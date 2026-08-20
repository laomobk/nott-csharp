using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace Nott.Agent;

public sealed class AgentSession
{
    private Guid _sessionGuid;
    private readonly ChatMessageStorage _messageStorage = new ();
    private readonly AgentLoop _agentLoop;
    
    private ChatClient _client;

    public ChatMessageStorage MessageStorage => _messageStorage;

    public AgentSession(Guid guid, ChatClient client)
    {
        _sessionGuid = guid;
        _client = client;
        _agentLoop = new AgentLoop();
    }

    public void AddChatMessage(ChatMessage message)
    {
        _messageStorage.AddChatMessage(message);
    }

    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        _messageStorage.AddChatMessageRange(messages);
    }

    public Task RunAsync(AgentEvent events, AgentToolStorage toolStorage, CancellationToken cancellationToken)
    {
        return _agentLoop.AgentLoopAsync(_client, _messageStorage, events, toolStorage, cancellationToken);
    }

    public void SerializeSession(BinaryWriter binaryWriter)
    {
        
    }

    public static void CreateSessionFromDeserialize(BinaryReader binaryReader)
    {
        
    }
}
