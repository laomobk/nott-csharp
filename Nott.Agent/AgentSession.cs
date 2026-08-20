using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI;
using OpenAI.Chat;

namespace Nott.Agent;

public sealed class AgentSession
{
    private Guid _guid;
    private readonly ChatMessageStorage _messageStorage = new ();
    private readonly AgentLoop _agentLoop;
    
    private ChatClient _client;

    public Guid Guid => _guid;
    public ChatMessageStorage MessageStorage => _messageStorage;

    public AgentSession(ChatClient client, Guid guid)
    {
        _guid = guid;
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
        binaryWriter.Write(_guid.ToByteArray());
        _messageStorage.Serialize(binaryWriter);
    }

    public static AgentSession CreateSessionFromDeserialize(ChatClient client, BinaryReader binaryReader)
    {
        var guidBytes = binaryReader.ReadBytes(16);
        if (guidBytes.Length != 16)
        {
            throw new InvalidDataException("The serialized session does not contain a valid GUID.");
        }

        var session = new AgentSession(client, new Guid(guidBytes));
        session._messageStorage.Deserialize(binaryReader);
        return session;
    }
}
