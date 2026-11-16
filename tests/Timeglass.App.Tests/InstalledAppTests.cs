using Microsoft.Extensions.Time.Testing;
using Timeglass.App.Updates;
using Timeglass.Windows.Autostart;

namespace Timeglass.App.Tests;

public sealed class InstalledAppTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeAppUpdater _updater = new(new FakeTimeProvider());
    private readonly FakeStartupRegistry _registry = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>The folder the setup program makes: launcher, updater and the app under <c>current</c>.</summary>
    private (string Launcher, string App) Install(bool launcher = true, bool updater = true)
    {
        var root = Path.Join(_folder.Path, InstalledApp.PackageId);
        var app = Path.Join(root, "current", "Timeglass.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllText(app, "app");
        if (launcher)
        {
            File.WriteAllText(Path.Join(root, "Timeglass.exe"), "launcher");
        }

        if (updater)
        {
            File.WriteAllText(Path.Join(root, "Update.exe"), "updater");
        }

        return (Path.Join(root, "Timeglass.exe"), app);
    }

    private AutostartRegistration Autostart(string exe) => new(_registry, exe);

    [Fact]
    public void An_installed_copy_starts_through_its_launcher()
    {
        var (launcher, app) = Install();

        InstalledApp.StableLauncher(app).ShouldBe(launcher);
    }

    [Fact]
    public void The_launcher_path_does_not_depend_on_the_version_in_current()
    {
        var (launcher, app) = Install();
        var before = InstalledApp.StableLauncher(app);

        // An update replaces the app folder as a whole.
        File.WriteAllText(app, "a newer app");

        InstalledApp.StableLauncher(app).ShouldBe(before);
        before.ShouldBe(launcher);
        launcher.ShouldNotContain("current");
    }

    [Fact]
    public void An_unzipped_copy_starts_itself()
    {
        var exe = Path.Join(_folder.Path, "Timeglass.exe");
        Directory.CreateDirectory(_folder.Path);
        File.WriteAllText(exe, "app");

        InstalledApp.StableLauncher(exe).ShouldBe(exe);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_folder_that_only_looks_installed_starts_the_exe_itself(bool launcher, bool updater)
    {
        // A build output that happens to be in a folder named "current".
        var (_, app) = Install(launcher, updater);

        InstalledApp.StableLauncher(app).ShouldBe(app);
    }

    [Fact]
    public void An_exe_in_a_drive_root_starts_itself() =>
        InstalledApp.StableLauncher(@"C:\Timeglass.exe").ShouldBe(@"C:\Timeglass.exe");

    [Fact]
    public void A_start_with_a_downloaded_update_hands_over_to_the_updater()
    {
        _updater.PendingVersion = "1.1.0";

        InstalledApp.ApplyPendingUpdate(_updater, restartedByUpdater: false, ["--Timeglass:LogDirectory=logs"]).ShouldBeTrue();

        _updater.Applied.ShouldHaveSingleItem().ShouldBe(["--Timeglass:LogDirectory=logs"]);
    }

    [Fact]
    public void A_start_without_an_update_goes_on()
    {
        InstalledApp.ApplyPendingUpdate(_updater, restartedByUpdater: false, []).ShouldBeFalse();

        _updater.Applied.ShouldBeEmpty();
    }

    [Fact]
    public void A_start_by_the_updater_does_not_ask_it_again()
    {
        // The update could not be installed and is still waiting: no restart loop.
        _updater.PendingVersion = "1.1.0";

        InstalledApp.ApplyPendingUpdate(_updater, restartedByUpdater: true, []).ShouldBeFalse();

        _updater.Applied.ShouldBeEmpty();
        _updater.WasAsked.ShouldBeFalse();
    }

    [Fact]
    public void A_copy_that_was_not_installed_never_applies_anything()
    {
        _updater.IsInstalled = false;
        _updater.PendingVersion = "1.1.0";

        InstalledApp.ApplyPendingUpdate(_updater, restartedByUpdater: false, []).ShouldBeFalse();

        _updater.Applied.ShouldBeEmpty();
    }

    [Fact]
    public void A_failing_updater_does_not_stop_the_app_from_starting()
    {
        _updater.PendingVersion = "1.1.0";
        _updater.ApplyFailure = new IOException("updater missing");

        InstalledApp.ApplyPendingUpdate(_updater, restartedByUpdater: false, []).ShouldBeFalse();
    }

    [Fact]
    public void First_run_moves_autostart_from_an_unzipped_copy_to_the_launcher()
    {
        var (launcher, _) = Install();
        _registry.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Tools\\Timeglass\\Timeglass.exe\"";

        InstalledApp.OnFirstRun(Autostart(launcher));

        _registry.Values[AutostartRegistration.DefaultValueName].ShouldBe($"\"{launcher}\"");
    }

    [Fact]
    public void First_run_does_not_turn_autostart_on()
    {
        var (launcher, _) = Install();

        InstalledApp.OnFirstRun(Autostart(launcher));

        _registry.Values.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uninstall_removes_autostart_for_this_copy(bool registeredForLauncher)
    {
        var (launcher, app) = Install();
        _registry.Values[AutostartRegistration.DefaultValueName] = $"\"{(registeredForLauncher ? launcher : app)}\"";
        _registry.Approved[AutostartRegistration.DefaultValueName] = [3, 0, 0, 0];

        InstalledApp.OnUninstall(Autostart(launcher), app);

        _registry.Values.ShouldBeEmpty();
        _registry.Approved.ShouldBeEmpty();
    }

    [Fact]
    public void Uninstall_leaves_autostart_of_another_copy_alone()
    {
        var (launcher, app) = Install();
        _registry.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Tools\\Timeglass\\Timeglass.exe\"";

        InstalledApp.OnUninstall(Autostart(launcher), app);

        _registry.Values.ShouldContainKey(AutostartRegistration.DefaultValueName);
    }

    [Fact]
    public void A_registry_that_cannot_be_written_does_not_fail_the_installer()
    {
        var (launcher, app) = Install();
        _registry.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Tools\\Timeglass\\Timeglass.exe\"";
        _registry.WriteFailure = new UnauthorizedAccessException();

        Should.NotThrow(() => InstalledApp.OnFirstRun(Autostart(launcher)));

        _registry.Values[AutostartRegistration.DefaultValueName] = $"\"{launcher}\"";
        Should.NotThrow(() => InstalledApp.OnUninstall(Autostart(launcher), app));
    }

    [Fact]
    public void The_install_folder_is_not_the_data_folder()
    {
        // Setup and uninstall replace and delete %LOCALAPPDATA%\<package id> as a whole; the
        // database, settings and logs are in %LOCALAPPDATA%\Timeglass.
        var dataFolder = Path.GetFileName(Path.GetDirectoryName(Storage.TimeglassDatabase.DefaultPath));

        dataFolder.ShouldBe("Timeglass");
        string.Equals(InstalledApp.PackageId, dataFolder, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Values { get; } = [];

        public Dictionary<string, byte[]> Approved { get; } = [];

        public Exception? WriteFailure { get; set; }

        public string? GetRunValue(string name) => Values.GetValueOrDefault(name);

        public void SetRunValue(string name, string command)
        {
            ThrowIfFailing();
            Values[name] = command;
        }

        public void DeleteRunValue(string name)
        {
            ThrowIfFailing();
            Values.Remove(name);
        }

        public byte[]? GetApprovedValue(string name) => Approved.GetValueOrDefault(name);

        public void DeleteApprovedValue(string name)
        {
            ThrowIfFailing();
            Approved.Remove(name);
        }

        private void ThrowIfFailing()
        {
            if (WriteFailure is { } failure)
            {
                throw failure;
            }
        }
    }
}
