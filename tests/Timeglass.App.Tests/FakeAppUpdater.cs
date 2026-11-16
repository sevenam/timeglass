using Timeglass.App.Updates;

namespace Timeglass.App.Tests;

/// <summary>An <see cref="IAppUpdater"/> that records what it was asked and answers as the test says.</summary>
internal sealed class FakeAppUpdater(TimeProvider time) : IAppUpdater
{
    private readonly Lock _lock = new();
    private readonly List<DateTimeOffset> _checks = [];
    private readonly List<IReadOnlyList<string>> _applied = [];

    public bool IsInstalled { get; set; } = true;

    /// <summary>The version the feed offers; <see langword="null"/> when the app is up to date.</summary>
    public string? Available { get; set; }

    public string? PendingVersion { get; set; }

    /// <summary>Thrown by the next checks, as when the PC is offline.</summary>
    public Exception? CheckFailure { get; set; }

    public Exception? ApplyFailure { get; set; }

    /// <summary>Whether <see cref="IsInstalled"/> was read.</summary>
    public bool WasAsked { get; private set; }

    /// <summary>When each check ran, on the fake clock.</summary>
    public IReadOnlyList<DateTimeOffset> Checks
    {
        get
        {
            lock (_lock)
            {
                return [.. _checks];
            }
        }
    }

    /// <summary>The restart arguments of each <see cref="ApplyAfterExit"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Applied
    {
        get
        {
            lock (_lock)
            {
                return [.. _applied];
            }
        }
    }

    bool IAppUpdater.IsInstalled
    {
        get
        {
            WasAsked = true;
            return IsInstalled;
        }
    }

    public Task<string?> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _checks.Add(time.GetUtcNow());
        }

        if (CheckFailure is { } failure)
        {
            return Task.FromException<string?>(failure);
        }

        if (Available is not null)
        {
            PendingVersion = Available;
        }

        return Task.FromResult(Available);
    }

    public void ApplyAfterExit(IReadOnlyList<string> restartArguments)
    {
        if (ApplyFailure is { } failure)
        {
            throw failure;
        }

        lock (_lock)
        {
            _applied.Add(restartArguments);
        }
    }
}
