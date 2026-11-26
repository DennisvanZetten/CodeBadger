using System.Diagnostics;
using System.Text;

namespace CodeBadger.Services;

/// <summary>Opens the macOS Terminal app in a repository and starts its configured agent.</summary>
public sealed class MacTerminalLauncher
{
    public Task OpenRepositoryAsync(string repositoryPath, string agentCommand)
    {
        if (!OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("Opening Terminal.app is only supported on Mac Catalyst.");
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException("The repository folder does not exist.");
        if (string.IsNullOrWhiteSpace(agentCommand) || agentCommand.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("The agent command is invalid.", nameof(agentCommand));

        var shellCommand = $"cd -- {QuoteForShell(Path.GetFullPath(repositoryPath))} && {QuoteForShell(agentCommand)}; exec /bin/zsh -l";
        var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(shellCommand));
        var appleScript = $"tell application \"Terminal\" to do script \"echo {encodedCommand} | base64 -D | /bin/zsh\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(appleScript);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not open Terminal.app.");
        return Task.CompletedTask;
    }

    private static string QuoteForShell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
