using System.Text.Json;
using OpenAI.Chat;

namespace Nott.Agent;

public class AgentToolArgument(JsonDocument argJsonDoc)
{
    public JsonDocument Document { get; } = argJsonDoc ?? throw new ArgumentNullException(nameof(argJsonDoc));

    private JsonElement? TryGetArgElement(string argName)
    {
        if (Document.RootElement.ValueKind == JsonValueKind.Object &&
            Document.RootElement.TryGetProperty(argName, out var argElement))
        {
            return argElement;
        }
        
        return null;
    }

    public string? GetStringArg(string argName)
    {
        return TryGetArgElement(argName)?.GetString() ?? null;
    }

    public string ToArgumentString()
    {
        return string.Join(" ", Document.RootElement
            .EnumerateObject()
            .Select(x => JsonSerializer.Serialize(x.Value)));
    }

    public bool TryGetValue(string name, out JsonElement value)
    {
        if (Document.RootElement.ValueKind == JsonValueKind.Object &&
            Document.RootElement.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}

public interface IAgentTool
{
    public ChatTool GetChatTool();
    public Task<string> ExecuteAsync(AgentToolArgument args, CancellationToken cancellationToken);
}
