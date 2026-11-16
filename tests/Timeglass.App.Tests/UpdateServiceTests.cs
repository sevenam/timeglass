using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Timeglass.App.Updates;

namespace Timeglass.App.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeAppUpdater _updater;
    private readonly TimeglassOptions _options = new();
    private readonly UpdateService _service;

    public UpdateServiceTests()
    {
        _updater = new FakeAppUpdater(_time);
        _service = new UpdateService(_updater, Options.Create(_options), _time, NullLogger<UpdateService>.Instance);
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task Checks_shortly_after_start_not_at_once()
    {
        await StartAsync();

        _time.Advance(UpdateService.StartupDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _updater.Checks.ShouldBeEmpty();

        await AdvanceUntilAsync(() => _updater.Checks.Count == 1, TimeSpan.FromSeconds(1));

        (_updater.Checks[0] - T0).ShouldBeGreaterThanOrEqualTo(UpdateService.StartupDelay);
        await StopAsync();
    }

    [Fact]
    public async Task Checks_again_a_day_later()
    {
        await StartAndWaitForFirstCheckAsync();

        // Not during the day.
        for (var i = 0; i < 23; i++)
        {
            _time.Advance(Hour);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        _updater.Checks.Count.ShouldBe(1);

        await AdvanceUntilAsync(() => _updater.Checks.Count == 2, Hour);

        var between = _updater.Checks[1] - _updater.Checks[0];
        between.ShouldBeGreaterThanOrEqualTo(UpdateService.CheckInterval);
        between.ShouldBeLessThan(UpdateService.CheckInterval + (3 * Hour));
        await StopAsync();
    }

    [Fact]
    public async Task A_check_that_came_due_during_sleep_runs_after_waking()
    {
        await StartAndWaitForFirstCheckAsync();

        // Sleep: the wall clock moves on by days, and one check follows, not one per missed day.
        _time.Advance(TimeSpan.FromDays(3));
        await AdvanceUntilAsync(() => _updater.Checks.Count == 2, Hour);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _updater.Checks.Count.ShouldBe(2);
        (_updater.Checks[1] - _updater.Checks[0]).ShouldBeLessThan(TimeSpan.FromDays(3) + (3 * Hour));
        await StopAsync();
    }

    [Fact]
    public async Task A_failed_check_is_tried_again_soon()
    {
        _updater.CheckFailure = new HttpRequestException("offline");
        await StartAndWaitForFirstCheckAsync();

        _updater.CheckFailure = null;
        _updater.Available = "1.1.0";
        await AdvanceUntilAsync(() => _updater.Checks.Count == 2, TimeSpan.FromMinutes(10));

        (_updater.Checks[1] - _updater.Checks[0]).ShouldBeLessThan(3 * Hour);
        await WaitUntilAsync(() => _service.ReadyVersion == "1.1.0");
        await StopAsync();
    }

    [Fact]
    public async Task A_downloaded_version_is_ready_for_the_next_start()
    {
        _updater.Available = "1.1.0";
        _service.ReadyVersion.ShouldBeNull();

        await StartAndWaitForFirstCheckAsync();
        await WaitUntilAsync(() => _service.ReadyVersion is not null);

        _service.ReadyVersion.ShouldBe("1.1.0");
        // Downloading installs nothing.
        _updater.Applied.ShouldBeEmpty();
        await StopAsync();
    }

    [Fact]
    public async Task Nothing_is_ready_while_up_to_date()
    {
        await StartAndWaitForFirstCheckAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _service.ReadyVersion.ShouldBeNull();
        _service.TryApplyAfterExit().ShouldBeFalse();
        _updater.Applied.ShouldBeEmpty();
        await StopAsync();
    }

    [Fact]
    public async Task A_version_downloaded_by_an_earlier_run_is_ready_from_the_start()
    {
        _updater.PendingVersion = "1.1.0";

        await StartAsync();
        await WaitUntilAsync(() => _service.ReadyVersion is not null);

        _service.ReadyVersion.ShouldBe("1.1.0");
        _updater.Checks.ShouldBeEmpty();
        await StopAsync();
    }

    [Fact]
    public async Task Restart_to_update_hands_the_ready_version_to_the_updater()
    {
        _updater.Available = "1.1.0";
        await StartAndWaitForFirstCheckAsync();
        await WaitUntilAsync(() => _service.ReadyVersion is not null);

        _service.TryApplyAfterExit().ShouldBeTrue();

        _updater.Applied.ShouldHaveSingleItem().ShouldBeEmpty();
        await StopAsync();
    }

    [Fact]
    public async Task Restart_to_update_reports_a_failure_instead_of_throwing()
    {
        _updater.Available = "1.1.0";
        _updater.ApplyFailure = new IOException("updater missing");
        await StartAndWaitForFirstCheckAsync();
        await WaitUntilAsync(() => _service.ReadyVersion is not null);

        _service.TryApplyAfterExit().ShouldBeFalse();
        await StopAsync();
    }

    [Fact]
    public async Task A_copy_that_was_not_installed_never_checks()
    {
        _updater.IsInstalled = false;
        _updater.Available = "1.1.0";

        await StartAsync();
        await _service.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromDays(2));

        _updater.Checks.ShouldBeEmpty();
        _service.ReadyVersion.ShouldBeNull();
        await StopAsync();
    }

    [Fact]
    public async Task Turned_off_it_never_asks_the_updater_anything()
    {
        _options.CheckForUpdates = false;
        _updater.Available = "1.1.0";

        await StartAsync();
        await _service.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromDays(2));

        _updater.WasAsked.ShouldBeFalse();
        _updater.Checks.ShouldBeEmpty();
        await StopAsync();
    }

    [Fact]
    public void Update_checks_are_on_by_default() => new TimeglassOptions().CheckForUpdates.ShouldBeTrue();

    [Fact]
    public async Task Stops_with_the_host_while_waiting()
    {
        await StartAndWaitForFirstCheckAsync();

        await StopAsync();

        _service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private Task StartAsync() => _service.StartAsync(TestContext.Current.CancellationToken);

    private Task StopAsync() => _service.StopAsync(TestContext.Current.CancellationToken);

    private async Task StartAndWaitForFirstCheckAsync()
    {
        await StartAsync();
        await AdvanceUntilAsync(() => _updater.Checks.Count == 1, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Moves the fake clock on in steps until the service, which runs on another thread, has done
    /// what the test waits for.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            _time.Advance(step);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue();
    }
}
