using System.Diagnostics;
using System.ComponentModel;
using Nott.Tool;

namespace Nott.Tool.Builtin;

public class ExecCommand
{
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

    [NottChatTool("exec-command", "Run a command in a system shell.")]
    public async Task<object> ExecuteAsync(
        [Description("Command to execute.")] string command,
        CancellationToken cancellationToken)
    {
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

        return new
        {
            exitCode = process.ExitCode,
            stdout,
            stderr
        };
    }
}
