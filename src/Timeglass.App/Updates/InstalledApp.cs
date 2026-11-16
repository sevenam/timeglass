using System.Security;
using Timeglass.Windows.Autostart;

namespace Timeglass.App.Updates;

/// <summary>
/// What is different about a copy installed by the setup program (#40): where it lives, what
/// <i>Start with Windows</i> starts, and what happens on its first run, on a start with a
/// downloaded update, and on uninstall.
/// </summary>
/// <remarks>
/// The setup program installs per user, without admin rights, into
/// <c>%LOCALAPPDATA%\sevenam.Timeglass</c>:
/// <code>
/// Timeglass.exe    the launcher: starts current\Timeglass.exe, and never changes path
/// Update.exe       the updater
/// current\         the app; replaced as a whole by an update
/// packages\        downloaded versions
/// </code>
/// The folder is named after the package id, on purpose not <c>Timeglass</c>: the database, the
/// settings and the logs are in <c>%LOCALAPPDATA%\Timeglass</c>, which no installer, update or
/// uninstall touches.
/// </remarks>
internal static class InstalledApp
{
    /// <summary>The package id <c>vpk pack</c> is run with (<c>ci.yml</c>), and the install folder's name.</summary>
    public const string PackageId = "sevenam.Timeglass";

    private const string AppFolder = "current";
    private const string Updater = "Update.exe";

    /// <summary>
    /// The exe to start the app with from outside (<i>Start with Windows</i>): the launcher next
    /// to the updater for an installed copy, and <paramref name="processPath"/> itself otherwise.
    /// </summary>
    /// <param name="processPath">Full path of the running exe.</param>
    public static string StableLauncher(string processPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        var folder = Path.GetDirectoryName(processPath);
        if (!string.Equals(Path.GetFileName(folder), AppFolder, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(folder) is not { Length: > 0 } root
            || !File.Exists(Path.Join(root, Updater)))
        {
            return processPath;
        }

        var launcher = Path.Join(root, Path.GetFileName(processPath));
        return File.Exists(launcher) ? launcher : processPath;
    }

    /// <summary>
    /// On a normal start: hands a downloaded update to the updater, which installs it once this
    /// process has exited and starts the new version.
    /// </summary>
    /// <param name="updater">The updater.</param>
    /// <param name="restartedByUpdater">
    /// Whether the updater started this process. It is then not asked again, so an update that
    /// can't be installed doesn't restart the app in a loop.
    /// </param>
    /// <param name="arguments">This process's command line, passed on to the new version.</param>
    /// <returns><see langword="true"/> if the caller must exit now.</returns>
    public static bool ApplyPendingUpdate(IAppUpdater updater, bool restartedByUpdater, IReadOnlyList<string> arguments)
    {
        if (restartedByUpdater)
        {
            return false;
        }

        try
        {
            if (!updater.IsInstalled || updater.PendingVersion is null)
            {
                return false;
            }

            updater.ApplyAfterExit(arguments);
            return true;
        }
        // Whatever is wrong with the update, the app must still start and track.
        catch (Exception ex) when (UpdateFailures.IsRecoverable(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// First run after setup: if <i>Start with Windows</i> was on for another copy (an unzipped
    /// one from before the installer), it now starts this one.
    /// </summary>
    public static void OnFirstRun(AutostartRegistration autostart) => Try(() => autostart.Adopt());

    /// <summary>
    /// Uninstall: removes <i>Start with Windows</i> if it starts this copy (through
    /// <paramref name="autostart"/>'s launcher or <paramref name="processPath"/>), so nothing
    /// points at a deleted file. The recorded data stays; the README says where it is.
    /// </summary>
    public static void OnUninstall(AutostartRegistration autostart, string processPath) => Try(() =>
    {
        if (autostart.RegisteredExecutable is { } registered
            && (IsSameFile(registered, autostart.ExecutablePath) || IsSameFile(registered, processPath)))
        {
            autostart.Disable();
        }
    });

    private static bool IsSameFile(string path, string other) =>
        string.Equals(path, Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);

    /// <summary>The installer waits for these hooks and there is no log yet: a registry that can't be written is not worth failing it.</summary>
    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Autostart stays as it was; the tray menu shows its real state and can change it.
        }
    }
}
