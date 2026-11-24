using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace CodeBadger.Services;

public sealed record AppUpdate(string Version, Uri PackageUri, Uri CertificateUri, Uri RuntimeDependencyUri);
public sealed record AppRelease(string Version, string Name, string Notes, Uri Url);

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/DennisvanZetten/CodeBadger/releases?per_page=100";
    private const string ExpectedPublisher = "CN=CodeBadger Local";
    private static readonly HttpClient UpdateCheckHttp = CreateHttpClient(TimeSpan.FromSeconds(12));
    private static readonly HttpClient DownloadHttp = CreateHttpClient(TimeSpan.FromMinutes(10));
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger) => _logger = logger;

    public async Task<AppUpdate?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        try
        {
            if (string.IsNullOrWhiteSpace(GetSigningThumbprint()))
                return null;

            var startedAt = Stopwatch.GetTimestamp();
            _logger.LogInformation("Checking application updates at {Endpoint}.", LatestReleaseApi);
            using var response = await UpdateCheckHttp.GetAsync(LatestReleaseApi, cancellationToken);
            _logger.LogInformation("Application update check returned HTTP {StatusCode} in {ElapsedMilliseconds} ms.",
                (int)response.StatusCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            _logger.LogInformation("Application update release response body: {ResponseBody}", await ReadLogExcerptAsync(response.Content));
            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is > 1024 * 1024)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var releases = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (releases.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<(JsonElement Release, string Tag, Version Version)>();
            foreach (var item in releases.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("prerelease", out var prerelease) || prerelease.GetBoolean() ||
                    !item.TryGetProperty("tag_name", out var tagName) ||
                    !item.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array ||
                    assetsElement.GetArrayLength() == 0)
                    continue;

                var tag = tagName.GetString();
                if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var version) || version.Build < 0)
                    continue;

                candidates.Add((item, tag!, version));
            }

            var release = candidates.OrderByDescending(item => item.Version).FirstOrDefault();

            if (release.Release.ValueKind != JsonValueKind.Object)
                return null;

            var root = release.Release;
            var latestVersion = release.Version;
            var versionText = release.Tag!.TrimStart('v', 'V');

            if ((root.TryGetProperty("draft", out var isDraft) && isDraft.GetBoolean()) ||
                (root.TryGetProperty("assets", out var releaseAssets) && releaseAssets.GetArrayLength() == 0))
                return null;

            var installed = Windows.ApplicationModel.Package.Current.Id.Version;
            var installedVersion = new Version(installed.Major, installed.Minor, installed.Build, installed.Revision);
            var packageVersion = new Version(latestVersion.Major, latestVersion.Minor, latestVersion.Build, 0);
            if (packageVersion <= installedVersion)
                return null;

            var assets = root.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.TryGetProperty("name", out _))
                .ToDictionary(asset => asset.GetProperty("name").GetString() ?? string.Empty, StringComparer.Ordinal);

            if (!TryGetGitHubAsset(assets, "CodeBadger.msix", out var packageUri) ||
                !TryGetGitHubAsset(assets, "CodeBadger.cer", out var certificateUri) ||
                !TryGetGitHubAsset(assets, "CodeBadger-WindowsAppRuntime-x64.msix", out var runtimeDependencyUri))
                return null;

            return new AppUpdate(versionText, packageUri, certificateUri, runtimeDependencyUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    public async Task<AppRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _logger.LogInformation("Loading latest application release from {Endpoint}.", LatestReleaseApi);
        using var response = await UpdateCheckHttp.GetAsync(LatestReleaseApi, cancellationToken);
        _logger.LogInformation("Application release request returned HTTP {StatusCode} in {ElapsedMilliseconds} ms.",
            (int)response.StatusCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        _logger.LogInformation("Application release response body: {ResponseBody}", await ReadLogExcerptAsync(response.Content));
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 1024 * 1024)
            throw new InvalidOperationException("The GitHub release response is unexpectedly large.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var releases = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (releases.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        return releases.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("prerelease", out var prerelease) && !prerelease.GetBoolean() &&
                item.TryGetProperty("draft", out var draft) && !draft.GetBoolean() &&
                item.TryGetProperty("tag_name", out var tag) &&
                Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out _))
            .OrderByDescending(item => Version.Parse(item.GetProperty("tag_name").GetString()!.TrimStart('v', 'V')))
            .Select(item =>
            {
                var urlText = item.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null;
                return Uri.TryCreate(urlText, UriKind.Absolute, out var url) && url.Host == "github.com"
                    ? new AppRelease(
                        item.GetProperty("tag_name").GetString()!.TrimStart('v', 'V'),
                        item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        item.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                        url)
                    : null;
            })
            .FirstOrDefault(release => release is not null);
    }

    public async Task DownloadAndStartInstallAsync(AppUpdate update, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        _logger.LogInformation("Starting application update download for version {Version}.", update.Version);
        var thumbprint = GetSigningThumbprint();

        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException("This build does not include the release signing certificate thumbprint.");

        var updateDirectory = Path.Combine(FileSystem.CacheDirectory, "updates");
        Directory.CreateDirectory(updateDirectory);
        var packagePath = Path.Combine(updateDirectory, "CodeBadger.msix");
        var certificatePath = Path.Combine(updateDirectory, "CodeBadger.cer");
        var dependencyPath = Path.Combine(updateDirectory, "CodeBadger-WindowsAppRuntime-x64.msix");

        await DownloadAssetAsync(update.PackageUri, packagePath, cancellationToken);
        await DownloadAssetAsync(update.CertificateUri, certificatePath, cancellationToken);
        await DownloadAssetAsync(update.RuntimeDependencyUri, dependencyPath, cancellationToken);

        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        if (certificate.Subject != ExpectedPublisher ||
            !string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) ||
            certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
        {
            throw new InvalidOperationException("The downloaded update certificate could not be verified.");
        }

        using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadOnly);
            var trustedCertificate = store.Certificates
                .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .FirstOrDefault();
            if (trustedCertificate is null)
                throw new InvalidOperationException("Windows does not yet trust the CodeBadger publisher certificate. Install the official release package once and explicitly trust its certificate before using in-app updates.");
            trustedCertificate.Dispose();
        }

        var parentProcessId = Environment.ProcessId;
        var escapedPackagePath = EscapePowerShellLiteral(packagePath);
        var escapedCertificatePath = EscapePowerShellLiteral(certificatePath);
        var escapedDependencyPath = EscapePowerShellLiteral(dependencyPath);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $parent = Get-Process -Id {{parentProcessId}} -ErrorAction SilentlyContinue
            if ($parent) { $parent.WaitForExit() }
            $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new('{{escapedCertificatePath}}')
            if ($certificate.Subject -ne '{{ExpectedPublisher}}' -or $certificate.Thumbprint -ne '{{thumbprint}}') { throw 'The update signing certificate does not match this CodeBadger build.' }
            $signature = Get-AuthenticodeSignature -FilePath '{{escapedPackagePath}}'
            if ($signature.Status -notin @('Valid', 'NotTrusted') -or -not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw 'The downloaded MSIX is not signed by the pinned CodeBadger certificate.' }
            $dependencySignature = Get-AuthenticodeSignature -FilePath '{{escapedDependencyPath}}'
            if ($dependencySignature.Status -ne 'Valid' -or $dependencySignature.SignerCertificate.Subject -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US') { throw 'The Windows App Runtime dependency signature could not be verified.' }
            $trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint
            if (-not $trusted) { throw 'The CodeBadger signing certificate is not trusted. Run the official first-time installer and explicitly trust its publisher certificate before updating.' }
            $signature = Get-AuthenticodeSignature -FilePath '{{escapedPackagePath}}'
            if ($signature.Status -notin @('Valid', 'NotTrusted')) { throw "Windows could not validate the MSIX signature: $($signature.Status)" }
            Add-AppxPackage -Path '{{escapedPackagePath}}' -DependencyPath '{{escapedDependencyPath}}'
            Write-Host 'CodeBadger update installed successfully. You can close this window and start CodeBadger again.' -ForegroundColor Green
            Read-Host 'Press Enter to close'
            """;

        var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("The Windows installer could not be started.");
        _logger.LogInformation("Started the Windows installer for application version {Version}.", update.Version);
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("In-app updates are only supported on Windows.");
#endif
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeBadger-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task<string> ReadLogExcerptAsync(HttpContent content)
    {
        const int maximumLength = 12000;
        var text = await content.ReadAsStringAsync();
        return text.Length <= maximumLength ? text : text[..maximumLength] + "… [truncated]";
    }

    private static string? GetSigningThumbprint() => typeof(UpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "CodeBadgerSigningCertificateThumbprint")?.Value;

    private static bool TryGetGitHubAsset(Dictionary<string, JsonElement> assets, string name, out Uri uri)
    {
        uri = null!;
        if (!assets.TryGetValue(name, out var asset) ||
            !Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "github.com")
            return false;

        uri = candidate;
        return true;
    }

    private async Task DownloadAssetAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
            throw new InvalidOperationException("The update download URL is not a GitHub HTTPS URL.");

        var startedAt = Stopwatch.GetTimestamp();
        using var response = await DownloadHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _logger.LogInformation("Update asset download returned HTTP {StatusCode} for {AssetName} in {ElapsedMilliseconds} ms.",
            (int)response.StatusCode, Path.GetFileName(destination), Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
