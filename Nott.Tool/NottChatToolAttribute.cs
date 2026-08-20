namespace Nott.Tool;

/// <summary>Marks a method as a function that can be exposed to a chat model.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NottChatToolAttribute : Attribute
{
    public NottChatToolAttribute()
    {
    }

    public NottChatToolAttribute(string name)
    {
        Name = name;
    }

    public NottChatToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    /// <summary>OpenAI function name. Defaults to the method name.</summary>
    public string? Name { get; set; }

    /// <summary>OpenAI function description.</summary>
    public string? Description { get; set; }
}
