using MudBlazor.Utilities;

namespace CodeBadger.Services;

public sealed class WindowTitleBarService
{
    public event Action? SettingsRequested;

#if WINDOWS
    private TitleBar? _titleBar;
    private ImageButton? _settingsButton;
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

    public void Configure(Window window)
    {
#if WINDOWS
        _settingsButton = new ImageButton
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Margin = new Thickness(0, 0, 12, 0),
            Padding = 8,
            BackgroundColor = Colors.Transparent,
            Source = new FontImageSource { FontFamily = "Segoe MDL2 Assets", Glyph = "\uE713", Size = 20 }
        };
        _settingsButton.Clicked += (_, _) => SettingsRequested?.Invoke();
        _titleBar = new TitleBar
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
                    new Label { Text = "CodeBadger", FontSize = 16, VerticalOptions = LayoutOptions.Center }
                }
            },
            TrailingContent = _settingsButton
        };
        window.TitleBar = _titleBar;
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

        if (_titleBar is null || _settingsButton is null)
            return;

        var surface = theme.IsDark ? theme.Theme.PaletteDark.Surface : theme.Theme.PaletteLight.Surface;
        var text = theme.IsDark ? theme.Theme.PaletteDark.TextPrimary : theme.Theme.PaletteLight.TextPrimary;
        _titleBar.BackgroundColor = Color.FromArgb(surface.ToString(MudColorOutputFormats.Hex));
        _titleBar.ForegroundColor = Color.FromArgb(text.ToString(MudColorOutputFormats.Hex));
        if (_titleBar.LeadingContent is HorizontalStackLayout brand && brand.Children[1] is Label label)
            label.TextColor = _titleBar.ForegroundColor;
        if (_settingsButton.Source is FontImageSource icon)
            icon.Color = _titleBar.ForegroundColor;
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

        if (_settingsButton is null)
            return;
        _settingsButton.IsEnabled = enabled;
        SemanticProperties.SetDescription(_settingsButton, description);
#endif
    }
}
