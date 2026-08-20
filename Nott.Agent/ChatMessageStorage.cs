using System.Text.Json;
using OpenAI.Chat;

namespace Nott.Agent;

public class ChatMessageStorage
{
    private record SerializedChatMessage(string Role, string Content, string ToolCallId);
    
    private record SerializedToolCall(string Id, string funcName, string funcArgsBase64);
    
    private record SerializedMessageStorage(List<SerializedChatMessage> Messages, List<SerializedToolCall> ToolCalls);
    
    private List<ChatMessage> _messages = new ();
    private Dictionary<string, ChatToolCall> _idToTooCalls = new();

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
        _idToTooCalls[call.Id] = call;
    }

    public void Serialize(BinaryWriter binaryWriter)
    {
        var serializedMessages = new List<SerializedChatMessage>();
        var serializedToolCalls = new List<SerializedToolCall>();
        
        foreach (var message in _messages)
        {
            var toolCallId = "";
            var content = "";
            var role = "";

            switch (message)
            {
                case ToolChatMessage msg:
                {
                    role = "Tool";
                    toolCallId = msg.ToolCallId;

                    break;
                }

                case AssistantChatMessage msg:
                {
                    role = "Assistant";
                    break;
                }

                case UserChatMessage msg:
                {
                    role = "User";
                    break;
                }
            }

            content = message.Content[0].Text;
            
            serializedMessages.Add(new SerializedChatMessage(role, content, toolCallId));
        }

        foreach (var toolCall in _idToTooCalls.Values)
        {
            var argumentsBase64 = Convert.ToBase64String(
                toolCall.FunctionArguments.ToArray());

            serializedToolCalls.Add(new SerializedToolCall(toolCall.Id, toolCall.FunctionName, argumentsBase64));
        }
    }

    public void Deserialize(BinaryReader binaryReader)
    {
        
    }
}
