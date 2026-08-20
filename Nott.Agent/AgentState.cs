namespace Nott.Agent;


public abstract record AgentState;

public record ActionState(string Description) : AgentState;
public record ToolCallingState(string Description) : AgentState;

public record ReplyingStreamingState() : AgentState;
    
public record LoopFinishedState() : AgentState;
