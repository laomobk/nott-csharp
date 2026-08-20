using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace Nott.Agent;

public class AgentLoop()
{
    public async Task AgentLoopAsync(
        ChatClient client, ChatMessageStorage messages, AgentEvent events, AgentToolStorage toolStorage, CancellationToken token)
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

        foreach (var tool in toolStorage.ChatTools)
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

                var completionUpdates = client.CompleteChatStreamingAsync(messages.GetChatMessageList(), chatOptions, token);

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
                                messages.AddChatMessage(new AssistantChatMessage(contentBuilder.ToString()));
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

                            messages.AddChatMessage(assistantMsg);

                            foreach (var toolCall in toolCalls)
                            {
                                if (toolStorage.TryGet(toolCall.FunctionName, out var tool))
                                {
                                    using var argJsonDoc = JsonDocument.Parse(toolCall.FunctionArguments);

                                    var arguments = new AgentToolArgument(argJsonDoc);

                                    UpdateAgentLoopState(new ToolCallingState($"{toolCall.FunctionName} {arguments.ToArgumentString()}"));
                                    var result = await tool.ExecuteAsync(arguments, token).ConfigureAwait(false);

                                    messages.AddChatMessage(new ToolChatMessage(toolCall.Id, result));
                                }
                                else
                                {
                                    messages.AddChatMessage(new ToolChatMessage(toolCall.Id,
                                        $"Unknown tool '{toolCall.FunctionName}'."));
                                }
                            
                                messages.AddChatToolCall(toolCall);
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

}
