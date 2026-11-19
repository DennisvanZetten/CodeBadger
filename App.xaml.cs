namespace CodeBadger;

public partial class App : Application
{
	private readonly Services.WindowTitleBarService _titleBar;

	public App(Services.WindowTitleBarService titleBar)
	{
		InitializeComponent();
		_titleBar = titleBar;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "CodeBadger" };
		_titleBar.Configure(window);
		return window;
	}
}
