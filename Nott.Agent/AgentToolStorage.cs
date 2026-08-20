using System.Reflection;
using Nott.Tool;
using OpenAI.Chat;

namespace Nott.Agent;

public sealed class AgentToolStorage
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<IAgentTool> Tools => _tools.Values;
    public IReadOnlyCollection<ChatTool> ChatTools => _tools.Values.Select(tool => tool.GetChatTool()).ToArray();
    
    public int Count => _tools.Count;

    public IReadOnlyList<IAgentTool> Load(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        
        var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<NottChatToolAttribute>() != null)
            .OrderBy(method => method.MetadataToken);

        object? instance = null;
        var loaded = new List<IAgentTool>();
        
        foreach (var method in methods)
        {
            if (!method.IsStatic)
            {
                instance ??= Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"Cannot create an instance of '{type.FullName}'.");
            }

            var wrapper = new OpenAIChatToolWrapper(method, instance);
            Register(wrapper);
            loaded.Add(wrapper);
        }

        return loaded;
    }

    public IReadOnlyList<IAgentTool> Load<T>()
    {
        return Load(typeof(T));
    }

    public void Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        
        var functionName = tool.GetChatTool().FunctionName;

        if (!_tools.TryAdd(functionName, tool))
        {
            throw new InvalidOperationException($"Duplicate chat tool name '{functionName}'.");
        }
    }

    public bool TryGet(string functionName, out IAgentTool tool)
    {
        return _tools.TryGetValue(functionName, out tool!);
    }

    public bool Remove(string functionName)
    {
        return _tools.Remove(functionName);
    }

    public void Clear()
    {
        _tools.Clear();
    }
}
