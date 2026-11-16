using System.Reflection;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Timeglass.App.Updates;

/// <summary>
/// <see cref="IAppUpdater"/> on Velopack, with the project's GitHub Releases as the feed. The
/// only code in the app that talks to the network: a check asks GitHub for the list of releases
/// and sends nothing about the user or what was recorded.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    /// <summary>The repository whose releases hold the update packages (<c>release.yml</c> uploads them).</summary>
    public const string RepositoryUrl = "https://github.com/sevenam/timeglass";

    // Created on first use: building the container must not touch the disk.
    private readonly Lazy<UpdateManager> _manager = new(CreateManager);

    /// <inheritdoc />
    public bool IsInstalled => _manager.Value.IsInstalled;

    /// <inheritdoc />
    public string? PendingVersion =>
        IsInstalled ? _manager.Value.UpdatePendingRestart?.Version.ToString() : null;

    /// <inheritdoc />
    public async Task<string?> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        var manager = _manager.Value;
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return null;
        }

        var version = update.TargetFullRelease.Version;
        if (manager.UpdatePendingRestart?.Version != version)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await manager.DownloadUpdatesAsync(update, null, cancellationToken).ConfigureAwait(false);
        }

        return version.ToString();
    }

    /// <inheritdoc />
    public void ApplyAfterExit(IReadOnlyList<string> restartArguments)
    {
        var manager = _manager.Value;
        var pending = manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("No downloaded update is waiting.");

        // Silent: the tray icon going away and coming back is all there is to see.
        manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, [.. restartArguments]);
    }

    /// <summary>
    /// Whether a version is a pre-release (<c>0.2.0-beta.1</c>). Such a build also takes
    /// pre-release updates; a stable one only stable releases.
    /// </summary>
    internal static bool IsPrerelease(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return false;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = plus < 0 ? informationalVersion : informationalVersion[..plus];
        return version.Contains('-', StringComparison.Ordinal);
    }

    private static UpdateManager CreateManager()
    {
        var version = typeof(VelopackUpdater).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // VelopackApp.Run() in Main sets the locator, which knows where (and whether) the app is
        // installed. Tests don't come through Main.
        var locator = VelopackLocator.IsCurrentSet
            ? VelopackLocator.Current
            : VelopackLocator.CreateDefaultForPlatform();
        return new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, IsPrerelease(version)), options: null, locator);
    }
}
