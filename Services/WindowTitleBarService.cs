using MudBlazor.Utilities;

namespace CodeBadger.Services;

public sealed class WindowTitleBarService
{
    public event Action? SettingsRequested;
    public event Action? HelpRequested;
    public event Action? IdeasRequested;

#if WINDOWS
    private readonly Dictionary<Window, (TitleBar TitleBar, ImageButton? SettingsButton, BoxView? SettingsUpdateDot, ImageButton? IdeasButton, ImageButton? HelpButton)> _titleBars = [];
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
        var settingsUpdateDot = new BoxView
        {
            WidthRequest = 8,
            HeightRequest = 8,
            CornerRadius = 4,
            BackgroundColor = Colors.OrangeRed,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 4, 5, 0),
            IsVisible = false,
            InputTransparent = true
        };
        var settingsButtonWithDot = new Grid
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Children = { settingsButton, settingsUpdateDot }
        };
        var ideasButton = new ImageButton
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Margin = new Thickness(0, 0, 12, 0),
            Padding = 0,
            BackgroundColor = Colors.Transparent,
            Source = new FontImageSource { FontFamily = "Segoe MDL2 Assets", Glyph = "\uE8FD", Size = 18 }
        };
        SemanticProperties.SetDescription(ideasButton, "Ideas and tasks");
        ideasButton.Clicked += (_, _) => IdeasRequested?.Invoke();
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
                Children = { settingsButtonWithDot, ideasButton, helpButton }
            } : null
        };
        _titleBars[window] = (titleBar, showSettings ? settingsButton : null, showSettings ? settingsUpdateDot : null, showSettings ? ideasButton : null, showSettings ? helpButton : null);
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
        foreach (var (titleBar, settingsButton, _, ideasButton, helpButton) in _titleBars.Values)
        {
            titleBar.BackgroundColor = Color.FromArgb(background.ToString(MudColorOutputFormats.Hex));
            titleBar.ForegroundColor = Color.FromArgb(text.ToString(MudColorOutputFormats.Hex));
            if (titleBar.LeadingContent is HorizontalStackLayout brand && brand.Children[1] is Label label)
                label.TextColor = titleBar.ForegroundColor;
            if (settingsButton?.Source is FontImageSource icon)
                icon.Color = titleBar.ForegroundColor;
            if (ideasButton?.Source is FontImageSource ideasIcon)
                ideasIcon.Color = titleBar.ForegroundColor;
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

        foreach (var (_, settingsButton, _, _, _) in _titleBars.Values)
        {
            if (settingsButton is null)
                continue;
            settingsButton.IsEnabled = enabled;
            SemanticProperties.SetDescription(settingsButton, description);
        }
#endif
    }

    public void SetAgentUpdateAvailable(bool available)
    {
#if WINDOWS
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetAgentUpdateAvailable(available));
            return;
        }

        foreach (var (_, _, updateDot, _, _) in _titleBars.Values)
            if (updateDot is not null)
                updateDot.IsVisible = available;
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

        foreach (var (_, _, _, _, helpButton) in _titleBars.Values)
        {
            if (helpButton is null)
                continue;
            SemanticProperties.SetDescription(helpButton, description);
            helpButton.IsEnabled = true;
        }
#endif
    }

    public void SetIdeasDescription(string description)
    {
#if WINDOWS
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => SetIdeasDescription(description));
            return;
        }

        foreach (var (_, _, _, ideasButton, _) in _titleBars.Values)
        {
            if (ideasButton is null)
                continue;
            SemanticProperties.SetDescription(ideasButton, description);
            ideasButton.IsEnabled = true;
        }
#endif
    }
}
