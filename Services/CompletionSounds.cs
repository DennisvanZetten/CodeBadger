namespace CodeBadger.Services;

public static class CompletionSounds
{
    public static readonly string[] All =
        ["bell", "chime", "drop", "sparkle", "soft", "glass", "marimba", "rise", "warm", "ping"];

    public static string Normalize(string? sound) => All.Contains(sound, StringComparer.Ordinal) ? sound! : "bell";
}
