using System.Text.Json;
using OpenAI.Chat;

namespace Nott.Agent;

public class ChatMessageStorage
{
    private record SerializedChatMessage(string Role, string Content, string ToolCallId, List<string>? ToolCallIds = null);
    
    private record SerializedToolCall(string Id, string funcName, string funcArgsBase64);
    
    private record SerializedMessageStorage(List<SerializedChatMessage> Messages, List<SerializedToolCall> ToolCalls);
    
    private List<ChatMessage> _messages = new ();
    private Dictionary<string, ChatToolCall> _idToToolCalls = new();

    public IReadOnlyList<ChatMessage> GetChatMessageList()
    {
        return _messages;
    }

    public void AddChatMessage(ChatMessage message)
    {
        _messages.Add(message);
    }

    public void AddChatMessageRange(IEnumerable<ChatMessage> messages)
    {
        _messages.AddRange(messages);
    }

    public void AddChatToolCall(ChatToolCall call)
    {
        _idToToolCalls[call.Id] = call;
    }

    public void Serialize(BinaryWriter binaryWriter)
    {
        var serializedMessages = new List<SerializedChatMessage>();
        var serializedToolCalls = new List<SerializedToolCall>();
        
        foreach (var message in _messages)
        {
            var toolCallId = "";
            List<string>? toolCallIds = null;
            
            var role = message switch
            {
                SystemChatMessage => "System",
                ToolChatMessage => "Tool",
                AssistantChatMessage => "Assistant",
                UserChatMessage => "User",
                _ => throw new NotSupportedException($"Chat message type '{message.GetType().Name}' cannot be serialized.")
            };

            if (message is ToolChatMessage toolMessage)
            {
                toolCallId = toolMessage.ToolCallId;
            }
            else if (message is AssistantChatMessage assistantMessage)
            {
                toolCallIds = new List<string>(assistantMessage.ToolCalls.Count);
                
                foreach (var toolCall in assistantMessage.ToolCalls)
                {
                    _idToToolCalls[toolCall.Id] = toolCall;
                    toolCallIds.Add(toolCall.Id);
                }
            }

            var content = string.Concat(message.Content.Select(part => part.Text));
            
            serializedMessages.Add(new SerializedChatMessage(role, content, toolCallId, toolCallIds));
        }

        foreach (var toolCall in _idToToolCalls.Values)
        {
            var argumentsBase64 = Convert.ToBase64String(
                toolCall.FunctionArguments.ToArray());

            serializedToolCalls.Add(new SerializedToolCall(toolCall.Id, toolCall.FunctionName, argumentsBase64));
        }

        var serializedStorage = new SerializedMessageStorage(serializedMessages, serializedToolCalls);
        binaryWriter.Write(JsonSerializer.Serialize(serializedStorage));
    }

    public void Deserialize(BinaryReader binaryReader)
    {
        var serializedStorage = JsonSerializer.Deserialize<SerializedMessageStorage>(binaryReader.ReadString())
            ?? throw new InvalidDataException("The serialized message storage is empty.");

        if (serializedStorage.Messages is null || serializedStorage.ToolCalls is null)
        {
            throw new InvalidDataException("The serialized message storage is missing required fields.");
        }

        var toolCalls = new Dictionary<string, ChatToolCall>();
        foreach (var serializedToolCall in serializedStorage.ToolCalls)
        {
            if (string.IsNullOrWhiteSpace(serializedToolCall.Id) ||
                string.IsNullOrWhiteSpace(serializedToolCall.funcName))
            {
                throw new InvalidDataException("A serialized tool call is missing its ID or function name.");
            }

            byte[] arguments;
            try
            {
                arguments = Convert.FromBase64String(serializedToolCall.funcArgsBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    $"Tool call '{serializedToolCall.Id}' contains invalid Base64 arguments.", exception);
            }

            if (!toolCalls.TryAdd(
                    serializedToolCall.Id,
                    ChatToolCall.CreateFunctionToolCall(
                        serializedToolCall.Id,
                        serializedToolCall.funcName,
                        BinaryData.FromBytes(arguments))))
            {
                throw new InvalidDataException($"Duplicate tool call ID '{serializedToolCall.Id}'.");
            }
        }

        var messages = new List<ChatMessage>(serializedStorage.Messages.Count);
        for (var index = 0; index < serializedStorage.Messages.Count; index++)
        {
            var serializedMessage = serializedStorage.Messages[index];
            var content = serializedMessage.Content ?? string.Empty;

            ChatMessage message;
            switch (serializedMessage.Role)
            {
                case "System":
                    message = new SystemChatMessage(content);
                    break;

                case "User":
                    message = new UserChatMessage(content);
                    break;

                case "Tool":
                    if (string.IsNullOrWhiteSpace(serializedMessage.ToolCallId))
                    {
                        throw new InvalidDataException("A tool message is missing its tool call ID.");
                    }

                    message = new ToolChatMessage(serializedMessage.ToolCallId, content);
                    break;

                case "Assistant":
                {
                    var callsForMessage = new List<ChatToolCall>();
                    var toolCallIds = serializedMessage.ToolCallIds;

                    if (toolCallIds is null)
                    {
                        toolCallIds = [];
                        for (var toolMessageIndex = index + 1;
                             toolMessageIndex < serializedStorage.Messages.Count &&
                             serializedStorage.Messages[toolMessageIndex].Role == "Tool";
                             toolMessageIndex++)
                        {
                            toolCallIds.Add(serializedStorage.Messages[toolMessageIndex].ToolCallId);
                        }
                    }

                    foreach (var toolCallId in toolCallIds)
                    {
                        if (!toolCalls.TryGetValue(toolCallId, out var toolCall))
                        {
                            throw new InvalidDataException(
                                $"Tool message references unknown tool call ID '{toolCallId}'.");
                        }

                        callsForMessage.Add(toolCall);
                    }

                    if (callsForMessage.Count == 0)
                    {
                        message = new AssistantChatMessage(content);
                    }
                    else
                    {
                        var assistantMessage = new AssistantChatMessage(callsForMessage);
                        if (content.Length > 0)
                        {
                            assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(content));
                        }

                        message = assistantMessage;
                    }

                    break;
                }

                default:
                    throw new InvalidDataException(
                        $"Unknown serialized chat role '{serializedMessage.Role}'.");
            }

            messages.Add(message);
        }

        _messages = messages;
        _idToToolCalls = toolCalls;
    }
}
