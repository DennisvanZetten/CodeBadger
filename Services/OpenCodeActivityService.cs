using System.Diagnostics;
using System.Text.Json;

namespace CodeBadger.Services;

public sealed class OpenCodeActivityService
{
    public sealed record ActivitySnapshot(HashSet<string> ActiveDirectories, HashSet<string> QuestionDirectories);

    // Only the managed OpenCode service is queried. Other tools, standalone
    // instances and explicitly selected servers do not expose the same state here.
    public static bool SupportsCommand(string command)
    {
        var trimmed = command.Trim();
        var end = trimmed.StartsWith('"') ? trimmed.IndexOf('"', 1) : trimmed.IndexOfAny([' ', '\t']);
        if (trimmed.StartsWith('"') && end < 0)
            return false;
        var executable = trimmed.StartsWith('"') ? trimmed[1..end] : end < 0 ? trimmed : trimmed[..end];
        var arguments = end < 0 ? string.Empty : trimmed[(end + 1)..];
        return Path.GetFileNameWithoutExtension(executable).Equals("opencode", StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains("--standalone", StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains("--server", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ActivitySnapshot?> GetActivityAsync(CancellationToken cancellationToken)
    {
        var activeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questionDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var active = await RequestAsync("/api/session/active", cancellationToken);
        if (active is null || !active.RootElement.TryGetProperty("data", out var sessions) || sessions.ValueKind != JsonValueKind.Object)
            return null;

        var ids = sessions.EnumerateObject().Select(session => session.Name)
            .Where(id => id.StartsWith("ses_", StringComparison.Ordinal) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .ToArray();
        var responses = await Task.WhenAll(ids.Select(async id =>
            (Session: await RequestAsync($"/api/session/{id}", cancellationToken),
             Questions: await RequestAsync($"/api/session/{id}/question", cancellationToken))));
        try
        {
            foreach (var (sessionResponse, questionResponse) in responses)
            {
                if (sessionResponse is null || !sessionResponse.RootElement.TryGetProperty("data", out var session)
                    || !session.TryGetProperty("location", out var location)
                    || !location.TryGetProperty("directory", out var directory)
                    || directory.ValueKind != JsonValueKind.String
                    || directory.GetString() is not { Length: > 0 } path
                    || NormalizePath(path) is not { } normalized)
                    return null;

                activeDirectories.Add(normalized);
                if (questionResponse?.RootElement.TryGetProperty("data", out var questions) == true &&
                    questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0)
                    questionDirectories.Add(normalized);
            }
            return new ActivitySnapshot(activeDirectories, questionDirectories);
        }
        finally
        {
            foreach (var (sessionResponse, questionResponse) in responses)
            {
                sessionResponse?.Dispose();
                questionResponse?.Dispose();
            }
        }
    }

    public static string? NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private static async Task<JsonDocument?> RequestAsync(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add($"opencode api get {path}");
        try
        {
            if (!process.Start())
                return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            return process.ExitCode == 0 ? JsonDocument.Parse(await output) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (JsonException) { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
