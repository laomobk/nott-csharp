using System.Text;
using Nott.Agent;
using Nott.Tool.Builtin;
using OpenAI;
using OpenAI.Chat;

using Spectre.Console;

namespace Nott.CLI;

public class Application
{
    private AgentSession _session;
    
    delegate void StatusReportFunc(string status);
    
    private CancellationTokenSource cancelCts = new();
    private volatile bool _cancelRequested;

    private AgentToolStorage _toolStorage = new();

    public Application(System.ClientModel.ApiKeyCredential apiKey)
    {
        _session = new AgentSession(Guid.NewGuid(), new ChatClient("deepseek-v4-flash", apiKey, new OpenAIClientOptions
        {
            Endpoint = new Uri("https://api.deepseek.com")
        }));
    }
    
    private string ShellArgToPrompt(string[] args)
    {
        return string.Join(" ", args);
    }
    
    private async Task RunOneShot(string userPrompt, CancellationToken token)
    {
        void ClearThisLine()
        {
            Console.Write("\r\x1b[2K");
        }

        _session.AddChatMessage(new UserChatMessage(userPrompt));

        var drawStatus = false;
        var statusText = "Preparing...";

        var isPrintingMessage = false;
        var keepStatusMessage = false;

        var events = new AgentEvent()
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

        var task = _session.RunAsync(events, _toolStorage, token);

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

                await Task.Delay(100, token);
            }
        } catch (OperationCanceledException) {}

        Console.WriteLine();

        await task;
    }

    private async Task RunRepl()
    {
        Console.WriteLine("She is Nott, chat with her!\n");

        var worked = false;
        
        while (true)
        {
            Console.CursorVisible = true;
            
            Console.Write("Nott> ");
            var userPrompt = Console.ReadLine();

            if (userPrompt == null)
            {
                if (worked)
                {
                    Console.WriteLine("\n(Ctrl-C again to leave the Nott alone.)");
                    worked = false;
                    continue;
                }

                Console.WriteLine();
                break;
            }

            _cancelRequested = false;

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                continue;
            }

            worked = true;
            if (userPrompt.Trim().StartsWith('/'))
            {
                switch (userPrompt) 
                {
                    case "/mes":
                    {
                        var table = new Table();
                        table.AddColumn("Messages");
                        
                        foreach (var message in _session.MessageStorage.GetChatMessageList())
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
        _cancelRequested = true;
        
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

            _toolStorage.Load(typeof(ExecCommand));

            _session.AddMessages([
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
