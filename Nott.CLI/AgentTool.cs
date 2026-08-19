using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace Nott.CLI;

public class AgentToolArgument
{
    private JsonDocument argJsonDoc;

    public AgentToolArgument(JsonDocument argJsonDoc)
    {
        this.argJsonDoc = argJsonDoc;
    }

    private JsonElement? TryGetArgElement(string argName)
    {
        if (argJsonDoc.RootElement.TryGetProperty(argName, out var argElement))
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
        return string.Join(" ",
            argJsonDoc.RootElement
                .EnumerateObject()
                .Select(x => JsonSerializer.Serialize(x.Value)));
    }
}

public interface IAgentTool
{
    public ChatTool GetChatTool();
    public Task<string> ExecuteAsync(AgentToolArgument args, CancellationToken cancellationToken);
}