using System.Reflection;
using Timeglass.App.Tray;

namespace Timeglass.App.Tests;

public class TrayTextTests
{
    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(59.9, "0 min")]
    [InlineData(45 * 60, "45 min")]
    [InlineData((3 * 60 + 5) * 60, "3 h 05 min")]
    [InlineData(25 * 3600, "25 h 00 min")]
    [InlineData(-30, "0 min")]
    public void Formats_durations_in_whole_minutes(double seconds, string expected) =>
        TrayText.Duration(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

    [Fact]
    public void Tooltip_shows_todays_active_time() =>
        TrayText.Tooltip(TimeSpan.FromMinutes(95), paused: false).ShouldBe("Timeglass: 1 h 35 min active today");

    [Fact]
    public void Tooltip_says_when_tracking_is_paused() =>
        TrayText.Tooltip(TimeSpan.FromMinutes(5), paused: true).ShouldBe("Timeglass: 5 min active today (paused)");

    [Fact]
    public void Tooltip_before_the_first_query_is_just_the_name() =>
        TrayText.Tooltip(null, paused: false).ShouldBe("Timeglass");

    [Fact]
    public void Tooltip_says_when_a_pause_ends() =>
        TrayText.Tooltip(TimeSpan.FromMinutes(5), paused: true, new DateTime(2026, 11, 5, 14, 30, 0))
            .ShouldBe("Timeglass: 5 min active today (paused until 14:30)");

    [Fact]
    public void A_pause_until_tomorrow_ends_at_midnight() =>
        TrayText.Tooltip(null, paused: true, new DateTime(2026, 11, 6)).ShouldBe("Timeglass (paused until midnight)");

    [Fact]
    public void Resume_item_says_when_the_pause_ends()
    {
        TrayText.ResumeItem(null).ShouldBe("&Resume tracking");
        TrayText.ResumeItem(new DateTime(2026, 11, 5, 9, 5, 0)).ShouldBe("&Resume tracking (paused until 09:05)");
    }

    [Fact]
    public void Tooltip_fits_the_notification_area_limit() =>
        TrayText.Tooltip(TimeSpan.MaxValue, paused: true, DateTime.MaxValue).Length.ShouldBeLessThanOrEqualTo(TrayText.MaxTooltipLength);

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+3f2a1b9c0d4e5f60718293a4b5c6d7e8f9012345", "0.1.0 (3f2a1b9)")]
    [InlineData("0.1.1-alpha.0.4+abc", "0.1.1-alpha.0.4 (abc)")]
    [InlineData("1.0.0+", "1.0.0")]
    [InlineData(null, "unknown version")]
    [InlineData("", "unknown version")]
    public void Version_drops_the_full_commit_hash(string? informational, string expected) =>
        TrayText.Version(informational).ShouldBe(expected);

    [Fact]
    public void Update_item_names_the_version() =>
        TrayText.UpdateItem("0.2.0").ShouldBe("Restart to &update to 0.2.0");

    [Fact]
    public void About_starts_with_the_name_and_version() =>
        TrayText.About("0.1.0+3f2a1b9c").ShouldStartWith("Timeglass 0.1.0 (3f2a1b9)\n");

    [Fact]
    public void The_app_is_versioned_from_git_tags()
    {
        var version = typeof(TrayText).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // MinVer stamps a SemVer version; without it the SDK default is 1.0.0.
        version.ShouldNotBeNull();
        version.ShouldMatch(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9a-f]+)?$");
        version.ShouldNotStartWith("1.0.0+");
    }
}
