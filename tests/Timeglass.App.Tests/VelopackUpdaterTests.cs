using Timeglass.App.Updates;

namespace Timeglass.App.Tests;

public class VelopackUpdaterTests
{
    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.0+3f2a1b9c", false)]
    [InlineData("1.2.0+build-7", false)]
    [InlineData("0.2.0-beta.1", true)]
    [InlineData("0.1.1-alpha.0.5+3f2a1b9c", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_pre_release_build_also_takes_pre_release_updates(string? version, bool expected) =>
        VelopackUpdater.IsPrerelease(version).ShouldBe(expected);

    [Fact]
    public void A_copy_that_no_setup_program_installed_has_nothing_to_update()
    {
        // The test host runs from the build output, like an unzipped copy.
        var updater = new VelopackUpdater();

        updater.IsInstalled.ShouldBeFalse();
        updater.PendingVersion.ShouldBeNull();
    }

    [Fact]
    public void Updates_come_from_the_projects_releases() =>
        new Uri(VelopackUpdater.RepositoryUrl).Host.ShouldBe("github.com");
}
