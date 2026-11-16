using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Timeglass.App.Updates;

namespace Timeglass.App.Tests;

public class UpdateFailuresTests
{
    public static TheoryData<Exception> UpdaterFailures =>
    [
        new HttpRequestException("offline"),
        new IOException("disk full"),
        new UnauthorizedAccessException(),
        new InvalidOperationException(),
        new TimeoutException(),
        new OperationCanceledException(),
        // What the updater throws when a patch fails: no type to catch by.
#pragma warning disable CA2201 // That is the point.
        new Exception("patch process failed"),
#pragma warning restore CA2201
    ];

    public static TheoryData<Exception> ProcessFailures =>
    [
        new OutOfMemoryException(),
        new InsufficientMemoryException(),
        new AccessViolationException(),
        new StackOverflowException(),
    ];

    [Theory]
    [MemberData(nameof(UpdaterFailures))]
    public void What_the_updater_can_throw_is_carried_on_after(Exception failure) =>
        UpdateFailures.IsRecoverable(failure).ShouldBeTrue();

    [Theory]
    [MemberData(nameof(ProcessFailures))]
    public void A_process_in_trouble_is_not_hidden(Exception failure) =>
        UpdateFailures.IsRecoverable(failure).ShouldBeFalse();

    [Fact]
    public void Starting_with_an_update_lets_a_process_failure_through()
    {
        var updater = new FakeAppUpdater(new FakeTimeProvider())
        {
            PendingVersion = "1.1.0",
            ApplyFailure = new OutOfMemoryException(),
        };

        Should.Throw<OutOfMemoryException>(() => InstalledApp.ApplyPendingUpdate(updater, restartedByUpdater: false, []));
    }

    [Fact]
    public async Task Restart_to_update_lets_a_process_failure_through()
    {
        var time = new FakeTimeProvider();
        var updater = new FakeAppUpdater(time) { PendingVersion = "1.1.0", ApplyFailure = new OutOfMemoryException() };
        using var service = new UpdateService(updater, Options.Create(new TimeglassOptions()), time, NullLogger<UpdateService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 1000 && service.ReadyVersion is null; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Should.Throw<OutOfMemoryException>(() => service.TryApplyAfterExit());

        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
