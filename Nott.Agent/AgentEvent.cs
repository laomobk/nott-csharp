namespace Nott.Agent;

// TODO: refractor to string based event channels.
public class AgentEvent
{
    public Action<string>? onToolCall;
    public Action<string>? onMessagePartReceived;
    public Action<AgentState>? onAgentStateChanged;
    public Action? onAgentLoopBreak;
}