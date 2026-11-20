namespace CodeBadger;

public partial class MainPage : ContentPage
{
	public Guid? DetachedRepositoryId { get; }

	public MainPage(string startPath = "/")
	{
		InitializeComponent();
		blazorWebView.StartPath = startPath;
		if (Guid.TryParse(startPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out var repositoryId))
			DetachedRepositoryId = repositoryId;
	}
}
