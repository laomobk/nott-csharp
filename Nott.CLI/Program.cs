
using System.ClientModel;

namespace Nott.CLI;

public static class Program
{
    public static Task Main(string[] args)
    {
        try
        {
            var apiKey = new ApiKeyCredential(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

            return new Application(apiKey).Run(args);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
    
} 