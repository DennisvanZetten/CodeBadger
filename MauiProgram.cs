using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using RepoLauncher.Services;

namespace RepoLauncher;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddMudServices();
		builder.Services.AddSingleton<LocalizationService>();
		builder.Services.AddSingleton<LauncherSettingsService>();
		builder.Services.AddSingleton<PowerShellLauncher>();
		builder.Services.AddSingleton<WindowsTerminalSessionService>();
		builder.Services.AddSingleton<GitBranchService>();
		builder.Services.AddSingleton<RepositoryFolderPicker>();
		builder.Services.AddSingleton<AiCommitMessageService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
