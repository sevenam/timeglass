using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Timeglass.Core.Settings;
using Timeglass.Storage.Settings;

namespace Timeglass.Storage.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "timeglass-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "nested", SettingsFile.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Reads the file the way the host does: JSON configuration bound to the settings class.</summary>
    private TimeglassSettings Load()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(SettingsPath, optional: false).Build();
        var settings = new TimeglassSettings();
        configuration.Bind(settings);
        return settings;
    }

    [Fact]
    public void Default_path_is_next_to_the_database()
    {
        Path.GetDirectoryName(SettingsFile.DefaultPath).ShouldBe(Path.GetDirectoryName(TimeglassDatabase.DefaultPath));
        Path.GetFileName(SettingsFile.DefaultPath).ShouldBe("settings.json");
    }

    [Fact]
    public void Saved_settings_read_back_through_configuration()
    {
        var saved = new TimeglassSettings
        {
            IdleThreshold = TimeSpan.FromMinutes(10),
            IdleExemptApps = ["vlc", "zoom"],
            MinimumInterval = TimeSpan.FromSeconds(3),
            CheckpointInterval = TimeSpan.FromSeconds(30),
            RecordWindowTitles = false,
            ExcludedApps = ["keepass"],
            AppsWithoutTitles = ["outlook"],
            AppAliases = [new AppAliasSetting { Key = "outlook", Name = "Mail" }],
            Categories =
            [
                new CategorySetting { Name = "Investigator", Keywords = ["investigator", "research"] },
                new CategorySetting { Name = "Prizm", Keywords = ["prizm"] },
            ],
            ApiPort = 6000,
            LogLevel = LogLevel.Debug,
        };

        SettingsFile.Save(SettingsPath, saved);
        var loaded = Load();

        SettingsFile.Serialize(loaded).ShouldBe(SettingsFile.Serialize(saved));
        loaded.LogLevel.ShouldBe(LogLevel.Debug);
        loaded.IdleThreshold.ShouldBe(TimeSpan.FromMinutes(10));
        loaded.AppAliases.ShouldHaveSingleItem().ToAlias().TitlePattern.ShouldBeNull();
        loaded.Categories.Select(g => (g.Name, string.Join(",", g.Keywords))).ShouldBe(
            [("Investigator", "investigator,research"), ("Prizm", "prizm")]);
    }

    [Fact]
    public void Idle_detection_turned_off_stays_off_after_a_round_trip()
    {
        SettingsFile.Save(SettingsPath, new TimeglassSettings { IdleThreshold = null });

        Load().IdleThreshold.ShouldBeNull();
    }

    [Fact]
    public void Defaults_round_trip()
    {
        SettingsFile.Save(SettingsPath, new TimeglassSettings());

        SettingsFile.Serialize(Load()).ShouldBe(SettingsFile.Serialize(new TimeglassSettings()));
    }

    [Fact]
    public void The_file_is_readable_json_with_enum_names_and_time_spans()
    {
        SettingsFile.Save(SettingsPath, new TimeglassSettings());

        var json = File.ReadAllText(SettingsPath);
        json.ShouldContain("\"logLevel\": \"Information\"");
        json.ShouldContain("\"idleThreshold\": \"00:05:00\"");
    }

    [Fact]
    public void Save_replaces_the_file_and_leaves_no_temp_files()
    {
        SettingsFile.Save(SettingsPath, new TimeglassSettings { ApiPort = 6001 });
        SettingsFile.Save(SettingsPath, new TimeglassSettings { ApiPort = 6002 });

        Load().ApiPort.ShouldBe(6002);
        Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!).ShouldHaveSingleItem().ShouldBe(SettingsPath);
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file_behind()
    {
        // A directory where the file should go makes the final rename fail.
        var blocked = Path.Combine(_folder, "blocked", SettingsFile.FileName);
        Directory.CreateDirectory(blocked);

        Should.Throw<Exception>(() => SettingsFile.Save(blocked, new TimeglassSettings()));

        Directory.GetFiles(Path.GetDirectoryName(blocked)!).ShouldBeEmpty();
        Directory.Exists(blocked).ShouldBeTrue();
    }

    [Fact]
    public void EnsureExists_writes_defaults_once()
    {
        SettingsFile.EnsureExists(SettingsPath).ShouldBeTrue();
        SettingsFile.Save(SettingsPath, new TimeglassSettings { ApiPort = 6003 });

        SettingsFile.EnsureExists(SettingsPath).ShouldBeFalse();
        Load().ApiPort.ShouldBe(6003);
    }
}
