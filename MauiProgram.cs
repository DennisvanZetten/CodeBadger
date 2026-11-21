using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using CodeBadger.Services;

namespace CodeBadger;

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
		builder.Services.AddMudServices(configuration =>
		{
			configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomLeft;
			configuration.SnackbarConfiguration.VisibleStateDuration = 1000;
			configuration.SnackbarConfiguration.HideTransitionDuration = 150;
		});
		builder.Services.AddSingleton<LocalizationService>();
		builder.Services.AddSingleton<WindowTitleBarService>();
		builder.Services.AddSingleton<RepositoryDetachedWindowService>();
		builder.Services.AddSingleton<LauncherSettingsService>();
		builder.Services.AddSingleton<IdeaListService>();
		builder.Services.AddSingleton<PowerShellLauncher>();
		builder.Services.AddSingleton<WindowsTerminalSessionService>();
		builder.Services.AddSingleton<WindowsSandboxSessionService>();
		builder.Services.AddSingleton<SandboxPermissionService>();
		builder.Services.AddSingleton<SandboxNetworkBroker>();
		builder.Services.AddSingleton<SandboxApprovalService>();
		builder.Services.AddSingleton<SandboxOpenCodeProviderService>();
		builder.Services.AddSingleton<OpenCodeActivityService>();
		builder.Services.AddSingleton<SlackNotificationService>();
		builder.Services.AddSingleton<GitBranchService>();
		builder.Services.AddSingleton<TicketMcpService>();
		builder.Services.AddSingleton<RepositoryFolderPicker>();
		builder.Services.AddSingleton<AiCommitMessageService>();
		builder.Services.AddSingleton<AgentConfigService>();
		builder.Services.AddSingleton<CodexConfigService>();
		builder.Services.AddSingleton<ClaudeRepositoryConfigService>();
		builder.Services.AddSingleton<OpenCodeRepositoryConfigService>();
		builder.Services.AddSingleton<StartupDetectionService>();
		builder.Services.AddSingleton<UpdateService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
