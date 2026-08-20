
using Nott.Agent;

namespace Nott.CLI;

public static class Program
{
    public static Task Main(string[] args)
    {
        try
        {
            AgentSession session;
            Guid sessionGuid;
            
            if (args.Length > 0 && args[0] == "--session")
            {
                if (args.Length < 2)
                {
                    throw new ArgumentException("The --session option requires a session GUID.");
                }

                if (!Guid.TryParse(args[1], out sessionGuid))
                {
                    throw new ArgumentException($"'{args[1]}' is not a valid session GUID.");
                }

                args = args[2..];
            }
            else
            {
                sessionGuid = Guid.NewGuid();
            }

            return new Application(sessionGuid).Run(args);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
    
}
