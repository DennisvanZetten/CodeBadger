using MudBlazor;

namespace CodeBadger.Services;

public sealed record LauncherTheme(string Id, string Name, bool IsDark, MudTheme Theme,
    string TerminalBackground, string TerminalForeground, string TerminalCursor, string TerminalSelection);

public static class LauncherThemes
{
    public const string DefaultId = "midnight";

    public static IReadOnlyList<LauncherTheme> All { get; } =
    (LauncherTheme[])
    [
        new(DefaultId, "Midnight", true, new MudTheme(), "#0b1220", "#e5e7eb", "#60a5fa", "#334155"),
        Dark("ocean", "Ocean", "#80baff", "#80cbc4", "#101c2d", "#192a40"),
        Dark("forest", "Forest", "#8cd6a2", "#c4d98a", "#14231c", "#203429"),
        Dark("plum", "Plum", "#d3a7f5", "#eea5c9", "#241a2c", "#35283e"),
        Dark("ember", "Ember", "#ffb47b", "#e9c46a", "#271b1a", "#3b2926"),
        Dark("slate", "Slate", "#a9c4e7", "#8bd4d1", "#1b222c", "#293442"),
        Light("daylight", "Daylight", "#3457a4", "#7450a8", "#f4f6fb", "#ffffff"),
        Light("sky", "Sky", "#006b9c", "#006c70", "#eef8fc", "#ffffff"),
        Light("garden", "Garden", "#276b42", "#775c18", "#f3f8f1", "#ffffff"),
        Light("sand", "Sand", "#9a491f", "#795a16", "#fbf6ed", "#ffffff")
    ];

    public static LauncherTheme Get(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    private static LauncherTheme Dark(string id, string name, string primary, string secondary, string background, string surface) =>
        new(id, name, true, new MudTheme
        {
            PaletteDark = new PaletteDark
            {
                Primary = primary, PrimaryContrastText = "#17202a", Secondary = secondary,
                SecondaryContrastText = "#17202a", Background = background, Surface = surface,
                AppbarBackground = surface, DrawerBackground = surface
            }
        }, background, "#e5e7eb", primary, "#526176");

    private static LauncherTheme Light(string id, string name, string primary, string secondary, string background, string surface) =>
        new(id, name, false, new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = primary, PrimaryContrastText = "#ffffff", Secondary = secondary,
                SecondaryContrastText = "#ffffff", Background = background, Surface = surface,
                AppbarBackground = surface, DrawerBackground = surface
            }
        }, background, "#1f2937", primary, "#c6d5e5");
}
