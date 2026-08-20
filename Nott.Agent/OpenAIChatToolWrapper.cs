using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI.Chat;
using Nott.Tool;

namespace Nott.Agent;

public sealed class OpenAIChatToolWrapper : IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly MethodInfo _method;
    private readonly object? _target;
    private readonly ChatTool _chatTool;

    public string FunctionName { get; }
    public string FunctionDescription { get; }
    
    public MethodInfo Method => _method;

    public OpenAIChatToolWrapper(MethodInfo method, object? target, NottChatToolAttribute metadata)
    {
        _method = method ?? throw new ArgumentNullException(nameof(method));
        
        ArgumentNullException.ThrowIfNull(metadata);
        
        _target = target;

        if (!_method.IsStatic && _target == null)
        {
            throw new ArgumentException("An instance is required for a non-static chat tool method.", nameof(target));
        }

        if (_method.ContainsGenericParameters || _method.GetParameters().Any(p => p.IsOut || p.ParameterType.IsByRef))
        {
            throw new ArgumentException(
                $"Chat tool method '{_method.Name}' must be non-generic and use value parameters.", nameof(method));
        }

        FunctionName = string.IsNullOrWhiteSpace(metadata.Name) ? method.Name : metadata.Name!;
        FunctionDescription = metadata.Description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
        
        _chatTool = ChatTool.CreateFunctionTool(FunctionName, FunctionDescription, BinaryData.FromString(BuildSchema().ToJsonString()));
    }

    public OpenAIChatToolWrapper(MethodInfo method, object? target = null)
        : this(method, target, method.GetCustomAttribute<NottChatToolAttribute>()
            ?? throw new ArgumentException("The method is not marked with NottChatToolAttribute.", nameof(method)))
    { }

    public ChatTool GetChatTool()
    {
        return _chatTool;
    }

    public async Task<string> ExecuteAsync(AgentToolArgument args, CancellationToken cancellationToken)
    {
        var parameters = _method.GetParameters();
        var invokeArgs = new object?[parameters.Length];
        
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                invokeArgs[i] = cancellationToken;
                continue;
            }
            if (!args.TryGetValue(parameter.Name!, out var value))
            {
                if (parameter.HasDefaultValue)
                {
                    invokeArgs[i] = parameter.DefaultValue; continue;
                }
                throw new ArgumentException($"Missing required tool argument '{parameter.Name}'.");
            }
            
            invokeArgs[i] = JsonSerializer.Deserialize(value.GetRawText(), parameter.ParameterType, SerializerOptions) 
                            ?? (parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null);
        }

        object? result;
        
        try
        {
            result = _method.Invoke(_target, invokeArgs);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
        
        result = await AwaitResultAsync(result).ConfigureAwait(false);
        
        return result as string ?? JsonSerializer.Serialize(result, SerializerOptions);
    }

    private static async Task<object?> AwaitResultAsync(object? result)
    {
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        if (result is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false); return null;
        }
        
        var resultType = result?.GetType();
        
        if (resultType is not null && resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = resultType.GetMethod("AsTask")!.Invoke(result, null) as Task;
            
            await asTask!.ConfigureAwait(false);
            
            return asTask.GetType().GetProperty("Result")?.GetValue(asTask);
        }
        
        return result;
    }

    private JsonObject BuildSchema()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        
        foreach (var parameter in _method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }
            
            properties[parameter.Name!] = TypeToSchema(parameter.ParameterType, parameter.GetCustomAttribute<DescriptionAttribute>()?.Description);
            
            if (!parameter.HasDefaultValue)
            {
                required.Add(parameter.Name);
            }
        }
        
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        
        return schema;
    }

    private static JsonObject TypeToSchema(Type type, string? description = null)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        
        JsonObject schema;
        
        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var value in type.GetEnumNames()!) values.Add(value);
            schema = new JsonObject { ["type"] = "string", ["enum"] = values };
        }
        else if (type == typeof(string) || type == typeof(char) || 
                 type == typeof(Guid) || type == typeof(DateTime) || 
                 type == typeof(DateTimeOffset))
        {
            schema = new JsonObject { ["type"] = "string" };
        }
        else if (type == typeof(bool))
        {
            schema = new JsonObject { ["type"] = "boolean" };
        }
        else if (type.IsIntegral())
        {
            schema = new JsonObject { ["type"] = "integer" };
        }
        else if (type.IsNumeric())
        {
            schema = new JsonObject { ["type"] = "number" };
        }
        else if (type.IsArray || (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
        {
            var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().FirstOrDefault() ?? typeof(object);
            schema = new JsonObject { ["type"] = "array", ["items"] = TypeToSchema(itemType) };
        }
        else
        {
            schema = new JsonObject { ["type"] = "object" };
        }
        
        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }
        
        return schema;
    }
}

internal static class TypeSchemaExtensions
{
    public static bool IsIntegral(this Type type) => 
        type == typeof(byte) || type == typeof(sbyte) || 
        type == typeof(short) || type == typeof(ushort) || 
        type == typeof(int) || type == typeof(uint) || 
        type == typeof(long) || type == typeof(ulong);
    
    public static bool IsNumeric(this Type type) => 
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}
