using MudBlazor.Utilities;

namespace CodeBadger.Services;

public sealed class WindowTitleBarService
{
    public event Action? SettingsRequested;
    public event Action? HelpRequested;

#if WINDOWS
    private readonly Dictionary<Window, (TitleBar TitleBar, ImageButton? SettingsButton, ImageButton? HelpButton)> _titleBars = [];
#endif

    public bool UsesNativeTitleBar
    {
        get
        {
#if WINDOWS
            return true;
#else
            return false;
#endif
        }
    }

    public void Configure(Window window, bool showSettings = true, string? title = null)
    {
#if WINDOWS
        var settingsButton = new ImageButton
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Margin = new Thickness(0, 0, 12, 0),
            Padding = 8,
            BackgroundColor = Colors.Transparent,
            Source = new FontImageSource { FontFamily = "Segoe MDL2 Assets", Glyph = "\uE713", Size = 20 }
        };
        settingsButton.Clicked += (_, _) => SettingsRequested?.Invoke();
        var helpButton = new ImageButton
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Margin = new Thickness(0, 0, 2, 0),
            Padding = 0,
            BackgroundColor = Colors.Transparent,
            Source = new FontImageSource { FontFamily = "Segoe UI", Glyph = "?", Size = 22 }
        };
        SemanticProperties.SetDescription(helpButton, "Help menu");
        helpButton.Clicked += (_, _) => HelpRequested?.Invoke();
        var titleBar = new TitleBar
        {
            HeightRequest = 48,
            LeadingContent = new HorizontalStackLayout
            {
                Margin = new Thickness(16, 0, 0, 0),
                Spacing = 12,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Image { Source = "codebadger.png", WidthRequest = 32, HeightRequest = 32 },
                    new Label { Text = title ?? "CodeBadger", FontSize = 16, VerticalOptions = LayoutOptions.Center }
                }
            },
            TrailingContent = showSettings ? new HorizontalStackLayout
            {
                Spacing = 0,
                VerticalOptions = LayoutOptions.Center,
                Children = { settingsButton, helpButton }
            } : null
        };
        _titleBars[window] = (titleBar, showSettings ? settingsButton : null, showSettings ? helpButton : null);
        window.Destroying += (_, _) => _titleBars.Remove(window);
        window.TitleBar = titleBar;
        ApplyTheme(LauncherThemes.Get(null));
#endif
    }

    public void ApplyTheme(LauncherTheme theme)
    {
#if WINDOWS
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => ApplyTheme(theme));
            return;
        }

        var background = theme.IsDark ? theme.Theme.PaletteDark.Background : theme.Theme.PaletteLight.Background;
        var text = theme.IsDark ? theme.Theme.PaletteDark.TextPrimary : theme.Theme.PaletteLight.TextPrimary;
        foreach (var (titleBar, settingsButton, helpButton) in _titleBars.Values)
        {
            titleBar.BackgroundColor = Color.FromArgb(background.ToString(MudColorOutputFormats.Hex));
            titleBar.ForegroundColor = Color.FromArgb(text.ToString(MudColorOutputFormats.Hex));
            if (titleBar.LeadingContent is HorizontalStackLayout brand && brand.Children[1] is Label label)
                label.TextColor = titleBar.ForegroundColor;
            if (settingsButton?.Source is FontImageSource icon)
                icon.Color = titleBar.ForegroundColor;
            if (helpButton?.Source is FontImageSource helpIcon)
                helpIcon.Color = titleBar.ForegroundColor;
        }
#endif
    }

    public void SetSettingsState(bool enabled, string description)
    {
#if WINDOWS
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetSettingsState(enabled, description));
            return;
        }

        foreach (var (_, settingsButton, _) in _titleBars.Values)
        {
            if (settingsButton is null)
                continue;
            settingsButton.IsEnabled = enabled;
            SemanticProperties.SetDescription(settingsButton, description);
        }
#endif
    }

    public void SetHelpDescription(string description)
    {
#if WINDOWS
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetHelpDescription(description));
            return;
        }

        foreach (var (_, _, helpButton) in _titleBars.Values)
        {
            if (helpButton is null)
                continue;
            SemanticProperties.SetDescription(helpButton, description);
            helpButton.IsEnabled = true;
        }
#endif
    }
}
