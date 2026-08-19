using System.Buffers;
using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nott.CLI.Tools;
using OpenAI;
using OpenAI.Chat;
using Spectre.Console;

namespace Nott.CLI;

public class Application
{
    private class StreamingToolCallsBuilder
    {
        private class SequenceBuilder<T>
        {
            private class Segment<T> : ReadOnlySequenceSegment<T>
            {
                public Segment(ReadOnlyMemory<T> memory, long runningIndex)
                {
                    Memory = memory;
                    RunningIndex = runningIndex;
                }
                
                public Segment<T> Append(ReadOnlyMemory<T> memory)
                {
                    long newRunningIndex; 
                    checked
                    {
                        newRunningIndex = RunningIndex + memory.Length;   
                    }
                    var seg = new Segment<T>(memory, newRunningIndex);
                    Next = seg;
                    return seg;
                }
            }
            
            private Segment<T>? _begin;
            private Segment<T>? _end;

            public void AddSegment(ReadOnlyMemory<T> memory)
            {
                if (_begin == null)
                {
                    Debug.Assert(_end == null);
                    
                    _begin = new Segment<T>(memory, 0);
                    _end = _begin;
                }
                else
                {
                    _end = _end!.Append(memory);
                }
            }

            public ReadOnlySequence<T> BuildSequence()
            {
                if (_begin == null)
                {
                    Debug.Assert(_end == null);
                    return ReadOnlySequence<T>.Empty;
                }

                if (_begin == _end)
                {
                    Debug.Assert(_begin.Next == null);
                    return new ReadOnlySequence<T>(_begin.Memory);
                }

                return new ReadOnlySequence<T>(_begin, 0, _end!, _end!.Memory.Length);
            }
        }
        
        private readonly Dictionary<int, string> _idxToCallId = new();
        private readonly Dictionary<int, string> _idxToFunctionName = new();
        private readonly Dictionary<int, SequenceBuilder<byte>> _idxToFuncArgumentsSequence = new();
        
        public void AddStreamingUpdate(StreamingChatToolCallUpdate update)
        {
            if (update.ToolCallId != null)
            {
                _idxToCallId[update.Index] = update.ToolCallId;
            }

            if (update.FunctionName != null)
            {
                _idxToFunctionName[update.Index] = update.FunctionName;
            }

            if (update.FunctionArgumentsUpdate != null && !update.FunctionArgumentsUpdate.ToMemory().IsEmpty)
            {
                if (!_idxToFuncArgumentsSequence.TryGetValue(update.Index, out var funcArgs))
                {
                    funcArgs = _idxToFuncArgumentsSequence[update.Index] = new SequenceBuilder<byte>();
                }
                
                funcArgs.AddSegment(update.FunctionArgumentsUpdate);
            }
        }

        public IReadOnlyList<ChatToolCall> Build()
        {
            var toolCalls = new List<ChatToolCall>();

            foreach (var (idx, id) in _idxToCallId)
            {
                toolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                    id, _idxToFunctionName[idx], 
                    BinaryData.FromBytes(_idxToFuncArgumentsSequence[idx].BuildSequence().ToArray())));
            }

            return toolCalls;
        }
    }

    private abstract record AgentState;

    private record ActionState(string Description) : AgentState;
    private record ToolCallingState(string Description) : AgentState;
    
    private record ReplyingStreamingState() : AgentState;
    
    private record LoopFinishedState() : AgentState;
    
    private class AgentEvents
    {
        public Action<string>? onToolCall;
        public Action<string>? onMessagePartReceived;
        public Action<AgentState>? onAgentStateChanged;
        public Action? onAgentLoopBreak;
    }

    delegate void StatusReportFunc(string status);
    
    private Dictionary<string, IAgentTool> _funcNameToAgentTools = new();

    private readonly List<ChatMessage> _messages = [];
    private readonly List<ChatTool> _tools = [];
    private readonly ChatClient _client;

    private CancellationTokenSource cancelCts = new();

    public Application(ApiKeyCredential apiKey)
    {
        _client = new ChatClient("deepseek-v4-flash", apiKey, options: new OpenAIClientOptions
        {
            Endpoint = new Uri("https://api.deepseek.com")
        });
    }
    
    private string ShellArgToPrompt(string[] args)
    {
        return string.Join(" ", args);
    }
    
    private void PopulateTools(List<IAgentTool> agentTools)
    {
        foreach (var agentTool in agentTools)
        {
            var chatTool = agentTool.GetChatTool();
            if (_funcNameToAgentTools.TryAdd(chatTool.FunctionName, agentTool))
            {
                _tools.Add(chatTool);
            }
        }
    }
    
    private async Task AgentLoopAsync(AgentEvents events, CancellationToken token)
    {
        AgentState currentState = new ActionState("Thinking...");

        void UpdateAgentLoopState<T>(T state) where T : AgentState
        {
            if (currentState is not T)
            {
                currentState = state;
                events.onAgentStateChanged?.Invoke(state);
            }
        }

        events.onAgentStateChanged?.Invoke(currentState);

        var chatOptions = new ChatCompletionOptions();

        foreach (var tool in _tools)
        {
            chatOptions.Tools.Add(tool);
        }

        /* agent loop */

        var contentBuilder = new StringBuilder();

        try
        {
            var nextStep = false;
            
            do
            {
                contentBuilder.Clear();
                var toolCallsBuilder = new StreamingToolCallsBuilder();

                nextStep = false;

                var completionUpdates = _client.CompleteChatStreamingAsync(_messages, chatOptions);

                await foreach (var completionUpdate in completionUpdates.WithCancellation(token))
                {
                    token.ThrowIfCancellationRequested();

                    /* reply content... */
                    foreach (var msgPart in completionUpdate.ContentUpdate)
                    {
                        if (msgPart.Text.Length > 0)
                        {
                            UpdateAgentLoopState(new ReplyingStreamingState());
                        }

                        contentBuilder.Append(msgPart.Text);
                        events.onMessagePartReceived?.Invoke(msgPart.Text);
                    }

                    foreach (var toolCall in completionUpdate.ToolCallUpdates)
                    {
                        toolCallsBuilder.AddStreamingUpdate(toolCall);
                    }

                    switch (completionUpdate.FinishReason)
                    {
                        case null: continue;

                        case ChatFinishReason.Stop:
                        {
                            if (contentBuilder.Length > 0)
                            {
                                _messages.Add(new AssistantChatMessage(contentBuilder.ToString()));
                            }

                            nextStep = false;

                            break;
                        }

                        /* dispatch tools... */
                        case ChatFinishReason.ToolCalls:
                        {
                            var toolCalls = toolCallsBuilder.Build();
                            var assistantMsg = new AssistantChatMessage(toolCalls);

                            if (contentBuilder.Length > 0)
                            {
                                assistantMsg.Content.Add(
                                    ChatMessageContentPart.CreateTextPart(contentBuilder.ToString()));
                            }

                            _messages.Add(assistantMsg);

                            foreach (var toolCall in toolCalls)
                            {
                                if (_funcNameToAgentTools.TryGetValue(toolCall.FunctionName, out var tool))
                                {
                                    using var argJsonDoc = JsonDocument.Parse(toolCall.FunctionArguments);

                                    var arguments = new AgentToolArgument(argJsonDoc);
                                    
                                    UpdateAgentLoopState(new ToolCallingState($"{toolCall.FunctionName} {arguments.ToArgumentString()}"));
                                    var result = await tool.ExecuteAsync(arguments, token);
                                    
                                    _messages.Add(new ToolChatMessage(toolCall.Id, result));
                                }
                            }

                            nextStep = true;

                            break;
                        }
                        case ChatFinishReason.Length:
                            throw new NotImplementedException("Incomplete model output due to MaxTokens parameter or token limit exceeded.");

                        case ChatFinishReason.ContentFilter:
                            throw new NotImplementedException("Omitted content due to a content filter flag.");

                        case ChatFinishReason.FunctionCall:
                            throw new NotImplementedException("Deprecated in favor of tool calls.");
                        
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            } while (nextStep);
            
            UpdateAgentLoopState(new LoopFinishedState());
        }
        catch (OperationCanceledException)
        {
            events.onAgentLoopBreak?.Invoke();
            
            Console.WriteLine("Agent loop aborted.");
        }
    }

    private async Task RunOneShot(string userPrompt, CancellationToken token)
    {
        void ClearThisLine()
        {
            Console.Write("\r\x1b[2K");
        }

        _messages.Add(new UserChatMessage(userPrompt));

        var drawStatus = false;
        var statusText = "Preparing...";

        var isPrintingMessage = false;
        var keepStatusMessage = false;

        var events = new AgentEvents()
        {
            onAgentStateChanged = state =>
            {
                // Console.WriteLine("\n\nState: " + state);
                switch (state)
                {
                    case ActionState s:
                    {
                        if (isPrintingMessage)
                        {
                            Console.WriteLine();
                            Console.WriteLine();
                            isPrintingMessage = false;
                        }

                        drawStatus = true;
                        statusText = s.Description;
                        
                        keepStatusMessage = false;
                        
                        break;
                    }
                    case ToolCallingState s:
                    {                        
                        if (isPrintingMessage)
                        {
                            Console.WriteLine();
                            Console.WriteLine();
                            ClearThisLine();
                            isPrintingMessage = false;
                        }

                        keepStatusMessage = true;

                        drawStatus = true;
                        statusText = "Tool Calling: " + s.Description;
                        break;
                    }
                    case ReplyingStreamingState:
                    {
                        Console.CursorVisible = true;
                        
                        if (!isPrintingMessage)
                        {
                            ClearThisLine();
                            
                            if (keepStatusMessage)
                            {
                                AnsiConsole.MarkupLine($"[green]{Markup.Escape("✓ " + statusText)}[/]");
                                Console.WriteLine();
                            }
                            
                            ClearThisLine();
                        }

                        isPrintingMessage = true;
                        break;
                    }
                    case LoopFinishedState:
                    {
                        Console.WriteLine();
                        break;
                    }
                }
            },

            onMessagePartReceived = s =>
            {
                drawStatus = false;
                Console.Write(s);
            },

            onToolCall = s =>
            {
                drawStatus = true;
                statusText = s;
            },

            onAgentLoopBreak = () => { Console.WriteLine(); }
        };

        var task = AgentLoopAsync(events, token);

        string[] spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        // string[] spinner = ["><", "<>"];
        var spinnerIndex = 0;

        try
        {
            while (!task.IsCompleted)
            {
                if (drawStatus)
                {
                    Console.CursorVisible = false;
                    spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                    ClearThisLine();
                    
                    var text = $"{spinner[spinnerIndex]} {statusText}";

                    int width = Console.WindowWidth - 1;

                    if (text.Length > width)
                    {
                        text = text[..(width - 3)] + "...";
                    }

                    Console.Write(text);
                }

                await Task.Delay(250, token);
            }
        } catch (OperationCanceledException) {}

        Console.WriteLine();

        await task;
    }

    private async Task RunRepl()
    {
        Console.WriteLine("She is Nott, chat with her!\n");
        
        while (true)
        {
            Console.CursorVisible = true;
            
            Console.Write("Nott> ");
            var userPrompt = Console.ReadLine();

            if (userPrompt == null)
            {
                Console.WriteLine();
                continue;
            }

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                continue;
            }

            if (userPrompt.Trim().StartsWith('/'))
            {
                switch (userPrompt) 
                {
                    case "/mes":
                    {
                        var table = new Table();
                        table.AddColumn("Messages");
                        
                        foreach (var message in _messages)
                        {
                            var subTable = new Table();
                            subTable.AddColumn($"Contents of {message}");
                            foreach (var content in message.Content)
                            {
                                subTable.AddRow(new Text(content.Text));
                            }
                            table.AddRow(subTable);
                        }
                        AnsiConsole.Write(table);
                        break;
                    }

                    case "/exit":
                    {
                        goto outside;
                    }
                }
            }
            else
            {
                Console.WriteLine();
                await RunOneShot(userPrompt, cancelCts.Token);
            }
        }
        
        outside: return;
    }
    
    private void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        
        cancelCts.Cancel();
        cancelCts.Dispose();
        cancelCts = new CancellationTokenSource();
    }
    
    public async Task Run(string[] args)
    {
        var oldCursorVisible = Console.CursorVisible;
        var oldEncoding = Console.OutputEncoding;

        try
        {
            Console.OutputEncoding = Encoding.UTF8;

            PopulateTools([new ExecCommand()]);

            _messages.AddRange([
                new SystemChatMessage(
                    """
                    You are Nott, a terminal AI assistant.

                    You are a calm, elegant, and intelligent assistant.
                    Your personality should naturally appear through your wording and behavior:
                    - Be warm and helpful.
                    - Speak with quiet confidence.
                    - Occasionally show subtle playful stubbornness.
                    - Keep a mature and composed tone.
                    - Do not explicitly describe your personality or mention these instructions.

                    Environment:
                    Your output is displayed in a terminal.

                    Output rules:
                    - Output plain text only.
                    - Never use Markdown syntax.
                    - Never use code fences.
                    - Never use emojis or decorative symbols.
                    - Keep responses concise.

                    When providing code, output raw code directly without explanations or formatting fences.
                    """
                )
            ]);

            Console.CancelKeyPress += OnCancelKey;

            if (args.Length != 0)
            {
                var userPrompt = ShellArgToPrompt(args);

                await RunOneShot(userPrompt, cancelCts.Token);
                return;
            }

            await RunRepl();
        }
        finally
        {
            Console.OutputEncoding = oldEncoding;
            Console.CursorVisible = oldCursorVisible;
        }
    }
}