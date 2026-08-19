using System.Diagnostics;
using System.Text.Json;
using OpenAI.Chat;

namespace Nott.CLI.Tools;

public class ExecCommand : IAgentTool
{
    public ChatTool GetChatTool()
    {
        return ChatTool.CreateFunctionTool(
            functionName: "exec-command",
            functionDescription: "Run a command in a system shell.",
            functionParameters: BinaryData.FromString(
                """
                  {
                    "type": "object",
                    "properties": {
                        "command": {
                            "type": "string",
                            "description": "command to execute"
                        }
                    },
                    "required": ["command"]
                  }
                """));
    }
    
    private static string FindShell()
    {
        if (OperatingSystem.IsWindows())
        {
            // PowerShell Core
            if (File.Exists(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PowerShell", "7", "pwsh.exe")))
            {
                return "pwsh.exe";
            }

            // Windows PowerShell
            var systemPowerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");

            if (File.Exists(systemPowerShell))
            {
                return systemPowerShell;
            }

            return "cmd.exe";
        }

        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    public async Task<string> ExecuteAsync(AgentToolArgument args, CancellationToken cancellationToken)
    {
        var command = args.GetStringArg("command") ?? null;
        if (string.IsNullOrEmpty(command))
        {
            return "<invalid command: empty command>";
        }
        
        var shell = FindShell();

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            if (Path.GetFileNameWithoutExtension(shell).Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                psi.Arguments = $"/c \"{command}\"";
            }
            else
            {
                psi.Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"";
            }
        }
        else
        {
            psi.Arguments = $"-c \"{command}\"";
        }

        using var process = new Process();
        process.StartInfo = psi;

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // ignored
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return JsonSerializer.Serialize(new
        {
            exitCode = process.ExitCode,
            stdout,
            stderr
        });
    }
}