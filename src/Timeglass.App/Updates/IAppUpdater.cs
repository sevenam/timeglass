namespace Timeglass.App.Updates;

/// <summary>
/// Finds, downloads and applies new versions of the app (#40). Behind an interface so the
/// schedule in <see cref="UpdateService"/> and the startup rule in <see cref="InstalledApp"/> are
/// tested without an installer or a network.
/// </summary>
internal interface IAppUpdater
{
    /// <summary>
    /// Whether this copy was installed by the setup program. A copy unzipped by hand or built from
    /// source is not, and never updates itself.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>The version that is downloaded and waiting for a restart, if any.</summary>
    string? PendingVersion { get; }

    /// <summary>
    /// Asks the release feed for a newer version and downloads it. Nothing is installed: the
    /// download becomes the <see cref="PendingVersion"/>.
    /// </summary>
    /// <returns>The version now waiting for a restart, or <see langword="null"/> if the app is up to date.</returns>
    Task<string?> DownloadLatestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts the updater, which waits for this process to exit, installs the
    /// <see cref="PendingVersion"/> and starts the app again. The caller then exits the normal way.
    /// </summary>
    /// <param name="restartArguments">Command line for the new version.</param>
    void ApplyAfterExit(IReadOnlyList<string> restartArguments);
}
