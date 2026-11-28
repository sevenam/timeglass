using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Timeglass.Core.Domain;
using Timeglass.Core.Ports;
using Timeglass.Core.Reporting;
using Timeglass.Core.Settings;

namespace Timeglass.Storage;

/// <summary>
/// Stores intervals in SQLite and reads them back by time range.
/// </summary>
/// <remarks>
/// <para>
/// Writes go through a single background writer: <see cref="ApplyAsync"/> only queues the changes,
/// so the tracker never waits for the disk. Whatever has queued up since the last write is written
/// in one transaction. <see cref="FlushAsync"/> waits until everything queued so far is stored.
/// </para>
/// <para>
/// A failed write is logged and dropped. Every change carries the full interval, so the next
/// checkpoint of an open interval repairs it.
/// </para>
/// <para>
/// At most one interval is open (enforced by a unique index). Storing an open interval closes
/// any other one at its last checkpoint first.
/// </para>
/// <para>
/// <see cref="DeleteRangeAsync"/> goes through the same writer, so it is ordered with the
/// changes queued before it. It runs with <c>secure_delete</c> on, so deleted titles are
/// overwritten in the file rather than left in free pages.
/// </para>
/// </remarks>
public sealed class IntervalRepository : IIntervalSink, IReportingQueries, IAsyncDisposable
{
    private const string UpsertApp = """
        INSERT INTO apps (key, display_name) VALUES (@Key, @DisplayName)
        ON CONFLICT (key) DO UPDATE SET display_name = excluded.display_name
        RETURNING id;
        """;

    private const string UpsertInterval = """
        INSERT INTO intervals (id, start_utc, end_utc, kind, app_id, exe_path, window_title, is_open)
        VALUES (@Id, @StartUtc, @EndUtc, @Kind, @AppId, @ExePath, @Title, @IsOpen)
        ON CONFLICT (id) DO UPDATE SET
            start_utc = excluded.start_utc,
            end_utc = excluded.end_utc,
            kind = excluded.kind,
            app_id = excluded.app_id,
            exe_path = excluded.exe_path,
            window_title = excluded.window_title,
            is_open = excluded.is_open;
        """;

    private const string CloseOthers = """
        UPDATE intervals SET is_open = 0 WHERE is_open = 1 AND id <> @Id;
        """;

    // Intervals never overlap, so of those starting at or before @From only the latest can reach
    // into the range. Starting the index scan there keeps a day query fast however much history
    // there is, instead of scanning every earlier interval.
    private const string SelectRange = """
        SELECT i.id AS Id, i.start_utc AS StartUtc, i.end_utc AS EndUtc, i.kind AS Kind, i.is_open AS IsOpen,
               i.exe_path AS ExePath, i.window_title AS Title, a.key AS AppKey, a.display_name AS AppName
        FROM intervals i
        LEFT JOIN apps a ON a.id = i.app_id
        WHERE i.start_utc < @To
          AND i.start_utc >= COALESCE((SELECT MAX(start_utc) FROM intervals WHERE start_utc <= @From), @From)
          AND (i.end_utc > @From OR i.is_open = 1)
          AND (@AppKey IS NULL OR a.key = @AppKey OR (@AppKey = @UnknownKey AND i.app_id IS NULL AND i.kind = 0))
        ORDER BY i.start_utc, i.end_utc;
        """;

    private const string SelectOverlapping = """
        SELECT i.id AS Id, i.start_utc AS StartUtc, i.end_utc AS EndUtc, i.kind AS Kind, i.is_open AS IsOpen,
               i.exe_path AS ExePath, i.window_title AS Title
        FROM intervals i
        WHERE i.is_open = 0 AND i.start_utc < @To AND (i.end_utc > @From OR i.start_utc >= @From);
        """;

    private const string SelectRolledUp = """
        SELECT d.day AS Day, d.kind AS Kind, a.key AS AppKey, a.display_name AS AppName,
               d.duration_ms AS DurationMs, d.first_utc AS FirstUtc, d.last_utc AS LastUtc
        FROM daily_app_totals d
        LEFT JOIN apps a ON a.id = d.app_id
        WHERE d.day >= @First AND d.day <= @Last;
        """;

    private const string SelectClosedBefore = """
        SELECT i.id AS Id, i.start_utc AS StartUtc, i.end_utc AS EndUtc, i.kind AS Kind, i.is_open AS IsOpen,
               i.exe_path AS ExePath, i.window_title AS Title, a.key AS AppKey, a.display_name AS AppName
        FROM intervals i
        LEFT JOIN apps a ON a.id = i.app_id
        WHERE i.is_open = 0 AND i.start_utc < @Before
        ORDER BY i.start_utc;
        """;

    private const string SelectAppId = "SELECT id FROM apps WHERE key = @Key;";

    private const string AddToTotal = """
        UPDATE daily_app_totals
        SET duration_ms = duration_ms + @DurationMs, first_utc = MIN(first_utc, @FirstUtc), last_utc = MAX(last_utc, @LastUtc)
        WHERE day = @Day AND kind = @Kind AND app_id IS @AppId;
        """;

    private const string InsertTotal = """
        INSERT INTO daily_app_totals (day, kind, app_id, duration_ms, first_utc, last_utc)
        VALUES (@Day, @Kind, @AppId, @DurationMs, @FirstUtc, @LastUtc);
        """;

    private const string DeleteClosedBefore = "DELETE FROM intervals WHERE is_open = 0 AND end_utc <= @Before AND start_utc < @Before;";

    private const string DayFormat = "yyyy-MM-dd";

    private const string DeleteInterval = "DELETE FROM intervals WHERE id = @Id;";

    private const string UpdateBounds = "UPDATE intervals SET start_utc = @StartUtc, end_utc = @EndUtc WHERE id = @Id;";

    private const string InsertPiece = """
        INSERT INTO intervals (id, start_utc, end_utc, kind, app_id, exe_path, window_title, is_open)
        SELECT @NewId, @StartUtc, @EndUtc, kind, app_id, exe_path, window_title, 0 FROM intervals WHERE id = @Id;
        """;

    private readonly TimeglassDatabase _database;
    private readonly ILogger _logger;
    private readonly Channel<WriteItem> _queue = Channel.CreateUnbounded<WriteItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly Dictionary<AppIdentity, long> _appIds = [];
    private readonly Task _writer;

    // Only touched by the writer loop.
    private Exception? _lastError;

    /// <summary>Creates the repository and starts its background writer.</summary>
    public IntervalRepository(TimeglassDatabase database, ILogger<IntervalRepository>? logger = null)
    {
        _database = database;
        _logger = logger ?? NullLogger<IntervalRepository>.Instance;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queues changes for the background writer and returns immediately.</summary>
    /// <exception cref="ObjectDisposedException">The repository has been disposed.</exception>
    public ValueTask ApplyAsync(IReadOnlyList<IntervalChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Enqueue(new WriteItem(changes, Flushed: null));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAppColorsAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Connect();
        return Task.FromResult(AppColors.Get(connection, keys));
    }

    /// <summary>
    /// Deletes [<paramref name="from"/>, <paramref name="to"/>) after the changes queued so far,
    /// cutting intervals that overlap its edges (<see cref="IntervalRange.Delete"/>). The open
    /// interval is not touched. Completes once the deletion is stored; throws if it failed.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> is before <paramref name="from"/>.</exception>
    public async ValueTask DeleteRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            throw new ArgumentException("The range must not end before it starts.", nameof(to));
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteItem(Changes: null, done, new DeleteRange(from, to)));
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retention (#41): replaces the closed intervals before <paramref name="before"/> with their
    /// totals per local day, kind and app in <c>daily_app_totals</c> (<see cref="DailyTotals"/>),
    /// so the day and week summaries stay the same while window titles, executables and the
    /// timeline of those days are gone. An interval that crosses <paramref name="before"/> is cut
    /// there. Runs after the changes queued so far, in one transaction, with <c>secure_delete</c> on.
    /// </summary>
    /// <param name="before">Keep everything from here on; usually the start of a local day.</param>
    /// <param name="zone">The time zone the days of the totals are in.</param>
    /// <param name="cancellationToken">Stops waiting; the work still runs.</param>
    /// <returns>How many intervals were rolled up.</returns>
    public async Task<int> ApplyRetentionAsync(DateTimeOffset before, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retention = new Retention(before, zone);
        Enqueue(new WriteItem(Changes: null, done, Retain: retention));
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return retention.RolledUp;
    }

    /// <summary>
    /// Runs <see cref="DatabaseMaintenance.Run"/> after the changes queued so far, so no write of
    /// the tracker runs meanwhile.
    /// </summary>
    /// <param name="allowVacuum">Whether the file may be rebuilt now; see <see cref="DatabaseMaintenance.Run"/>.</param>
    /// <param name="cancellationToken">Stops waiting; the work still runs.</param>
    public async Task<MaintenanceResult> RunMaintenanceAsync(bool allowVacuum, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<MaintenanceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteItem(Changes: null, Flushed: null, Maintain: new Maintenance(allowVacuum, done)));
        return await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes once every change queued before this call has been written.
    /// Throws the error of the most recent write if that write failed.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteItem(Changes: null, flushed));
        await flushed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns intervals that overlap [<paramref name="from"/>, <paramref name="to"/>), ordered by start,
    /// including ones that start before the range. Open intervals are included if they start before
    /// <paramref name="to"/>, since they are still running. Intervals are not clipped to the range.
    /// </summary>
    public Task<IReadOnlyList<ActivityInterval>> GetRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        QueryRangeAsync(from, to, appKey: null, cancellationToken);

    /// <summary>
    /// Where the time went on local day <paramref name="day"/> in <paramref name="zone"/>: active
    /// and away totals, first and last activity, and time and share per app
    /// (<see cref="DaySummaries.Build"/>). Intervals crossing the day's edges are clipped.
    /// </summary>
    /// <param name="day">The local date.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="now">The current time; the open interval counts as running until then.</param>
    /// <param name="dayStart">Local time the day begins, for people who work past midnight. Midnight by default.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <param name="afterHours">Optional after-hours analysis rule.</param>
    public async Task<DaySummary> GetDaySummaryAsync(
        DateOnly day,
        TimeZoneInfo zone,
        DateTimeOffset now,
        TimeSpan dayStart = default,
        CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = DaySummaries.Bounds(day, zone, dayStart);
        var intervals = await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false);
        var rolledUp = await GetRolledUpAsync(day, day, cancellationToken).ConfigureAwait(false);
        return DaySummaries.Build(FilterAfterHours(intervals, zone, afterHours), day, zone, now, dayStart, rolledUp);
    }

    /// <summary>
    /// The totals that retention left of local days <paramref name="firstDay"/> to
    /// <paramref name="lastDay"/> (both included); see <see cref="ApplyRetentionAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<RolledUpTotal>> GetRolledUpAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Connect();
        var rows = await connection.QueryAsync<TotalRow>(new CommandDefinition(
            SelectRolledUp,
            new { First = DayText(firstDay), Last = DayText(lastDay) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToTotal())];
    }

    /// <summary>
    /// The timeline of local day <paramref name="day"/> in <paramref name="zone"/>: active and away
    /// blocks in time order, with intervals of the same app a few seconds apart merged
    /// (<see cref="Timelines.ForDay"/>).
    /// </summary>
    /// <param name="day">The local date.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="now">The current time; the open interval counts as running until then.</param>
    /// <param name="dayStart">Local time the day begins. Midnight by default.</param>
    /// <param name="mergeGap">Merge gaps shorter than this; <see cref="Timelines.DefaultMergeGap"/> if not given.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <param name="afterHours">Optional after-hours analysis rule.</param>
    public async Task<DayTimeline> GetTimelineAsync(
        DateOnly day,
        TimeZoneInfo zone,
        DateTimeOffset now,
        TimeSpan dayStart = default,
        TimeSpan? mergeGap = null,
        CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = DaySummaries.Bounds(day, zone, dayStart);
        var intervals = await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false);
        return Timelines.ForDay(FilterAfterHours(intervals, zone, afterHours), day, zone, now, dayStart, mergeGap);
    }

    /// <inheritdoc />
    public async Task<DayTimeline> GetCategoryTimelineAsync(
        DateOnly day, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories, CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = DaySummaries.Bounds(day, zone);
        var intervals = await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false);
        return Timelines.ForDay(FilterAfterHours(intervals, zone, afterHours), day, zone, now, categories: categories);
    }

    /// <summary>
    /// One app's time grouped by window title over local days <paramref name="firstDay"/> to
    /// <paramref name="lastDay"/> (both included), every title listed (<see cref="TitleBreakdowns.Build"/>).
    /// For one day, pass the same date twice.
    /// </summary>
    /// <param name="appKey">The app's key, e.g. <c>outlook</c>.</param>
    /// <param name="firstDay">First local date.</param>
    /// <param name="lastDay">Last local date, included.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="now">The current time; the open interval counts as running until then.</param>
    /// <param name="dayStart">Local time each day begins. Midnight by default.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <param name="afterHours">Optional after-hours analysis rule.</param>
    public async Task<TitleBreakdown> GetTitleBreakdownAsync(
        string appKey,
        DateOnly firstDay,
        DateOnly lastDay,
        TimeZoneInfo zone,
        DateTimeOffset now,
        TimeSpan dayStart = default,
        CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appKey);
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone, dayStart);
        var intervals = afterHours?.Enabled == true
            ? await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false)
            : await QueryRangeAsync(from, to, appKey, cancellationToken).ConfigureAwait(false);
        return TitleBreakdowns.Build(FilterAfterHours(intervals, zone, afterHours), appKey, from, to, now);
    }

    /// <summary>
    /// Totals per day, the apps of each day and totals per app over local days
    /// <paramref name="firstDay"/> to <paramref name="lastDay"/> (both included), for the week
    /// view (<see cref="RangeSummaries.Build"/>).
    /// </summary>
    /// <param name="firstDay">First local date.</param>
    /// <param name="lastDay">Last local date, included. At most <see cref="RangeSummaries.MaxDays"/> days after the first.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="now">The current time; the open interval counts as running until then.</param>
    /// <param name="dayStart">Local time each day begins. Midnight by default.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <param name="afterHours">Optional after-hours analysis rule.</param>
    public async Task<RangeSummary> GetRangeSummaryAsync(
        DateOnly firstDay,
        DateOnly lastDay,
        TimeZoneInfo zone,
        DateTimeOffset now,
        TimeSpan dayStart = default,
        CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone, dayStart);
        var intervals = await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false);
        var rolledUp = await GetRolledUpAsync(firstDay, lastDay, cancellationToken).ConfigureAwait(false);
        return RangeSummaries.Build(FilterAfterHours(intervals, zone, afterHours), firstDay, lastDay, zone, now, dayStart, rolledUp);
    }

    private static IReadOnlyList<ActivityInterval> FilterAfterHours(
        IReadOnlyList<ActivityInterval> intervals, TimeZoneInfo zone, AfterHoursOptions? options) =>
        options is { Enabled: true }
            ? AfterHoursReturns.Filter(intervals, zone, options.WorkHours, options.MinimumAway, options.MaximumReturn)
            : intervals;

    private async Task<IReadOnlyList<ActivityInterval>> GetReportIntervalsAsync(
        DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, AfterHoursOptions? options, CancellationToken cancellationToken)
    {
        if (options?.Enabled != true)
        {
            return await GetRangeAsync(from, to, cancellationToken).ConfigureAwait(false);
        }

        var queryFrom = from > DateTimeOffset.MinValue.AddMilliseconds(1) ? from.AddMilliseconds(-1) : from;
        var queryTo = to < DateTimeOffset.MaxValue.AddMilliseconds(-1) ? to.AddMilliseconds(1) : to;
        var intervals = await GetRangeAsync(queryFrom, queryTo, cancellationToken).ConfigureAwait(false);
        var crossingAway = intervals.LastOrDefault(i => i.Kind == IntervalKind.Away && !i.IsOpen && i.Start < to && i.End >= to);
        if (crossingAway is not null)
        {
            var returnDay = DaySummaries.DayOf(crossingAway.End, zone);
            var extendedTo = DaySummaries.Bounds(returnDay, zone).End;
            if (extendedTo < DateTimeOffset.MaxValue.AddMilliseconds(-1))
            {
                extendedTo = extendedTo.AddMilliseconds(1);
            }

            if (extendedTo > queryTo)
            {
                intervals = await GetRangeAsync(queryFrom, extendedTo, cancellationToken).ConfigureAwait(false);
            }
        }

        return intervals;
    }

    /// <inheritdoc />
    public async Task<RangeSummary> GetCategoryRangeSummaryAsync(
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories, CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone);
        var intervals = await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false);
        var rolledUp = await GetRolledUpAsync(firstDay, lastDay, cancellationToken).ConfigureAwait(false);
        return Categories.Build(FilterAfterHours(intervals, zone, afterHours), rolledUp, firstDay, lastDay, zone, now, categories);
    }

    /// <inheritdoc />
    public async Task<RangeSummary> GetCategoryApplicationsAsync(
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories, string categoryName, CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone);
        var intervals = await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false);
        var rolledUp = await GetRolledUpAsync(firstDay, lastDay, cancellationToken).ConfigureAwait(false);
        return Categories.BuildForCategory(FilterAfterHours(intervals, zone, afterHours), rolledUp, firstDay, lastDay, zone, now, categories, categoryName);
    }

    /// <inheritdoc />
    public async Task<TitleBreakdown> GetCategoryApplicationTitlesAsync(
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories, string categoryName, string appKey, CancellationToken cancellationToken = default,
        AfterHoursOptions? afterHours = null)
    {
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone);
        var intervals = afterHours?.Enabled == true
            ? await GetReportIntervalsAsync(from, to, zone, afterHours, cancellationToken).ConfigureAwait(false)
            : await QueryRangeAsync(from, to, appKey, cancellationToken).ConfigureAwait(false);
        var rolledUp = await GetRolledUpAsync(firstDay, lastDay, cancellationToken).ConfigureAwait(false);
        return Categories.BuildCategoryAppTitles(FilterAfterHours(intervals, zone, afterHours), rolledUp, firstDay, lastDay, zone, now, categories, categoryName, appKey);
    }

    /// <summary>
    /// The intervals <see cref="GetRangeAsync"/> returns, read from the database one at a time as
    /// the caller asks for them, so an export of years never holds them all. The query sees the
    /// database as it was when the first interval was read.
    /// </summary>
    public async IAsyncEnumerable<ActivityInterval> StreamRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Connect();
        var rows = connection.QueryUnbufferedAsync<IntervalRow>(SelectRange, RangeParameters(from, to, appKey: null));

        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return row.ToInterval();
        }
    }

    /// <summary>Writes everything still queued, then stops the writer.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ActivityInterval>> QueryRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? appKey,
        CancellationToken cancellationToken)
    {
        await using var connection = _database.Connect();
        var rows = await connection.QueryAsync<IntervalRow>(new CommandDefinition(
            SelectRange,
            RangeParameters(from, to, appKey),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToInterval())];
    }

    /// <summary>The parameters of <see cref="SelectRange"/>; no <paramref name="appKey"/> means every app.</summary>
    private static object RangeParameters(DateTimeOffset from, DateTimeOffset to, string? appKey) =>
        new { From = from.ToUnixTimeMilliseconds(), To = to.ToUnixTimeMilliseconds(), AppKey = appKey, UnknownKey = AppIdentity.Unknown.Key };

    private void Enqueue(WriteItem item)
    {
        if (!_queue.Writer.TryWrite(item))
        {
            throw new ObjectDisposedException(nameof(IntervalRepository));
        }
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<WriteItem>();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (_queue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Any(i => i.Changes is not null || i.Delete is not null || i.Retain is not null))
            {
                try
                {
                    Write(batch);
                    _lastError = null;
                }
#pragma warning disable CA1031 // The writer must survive a failed write; the error goes to the log and to flushers.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _lastError = ex;
                    _appIds.Clear(); // ids from a rolled-back transaction are not real
                    _logger.LogError(ex, "Failed to write {Count} interval change(s)", batch.Sum(i => i.Changes?.Count ?? 0));
                }
            }

            foreach (var item in batch)
            {
                if (_lastError is null)
                {
                    item.Flushed?.TrySetResult();
                }
                else
                {
                    item.Flushed?.TrySetException(_lastError);
                }
            }

            foreach (var maintenance in batch.Select(i => i.Maintain).OfType<Maintenance>())
            {
                try
                {
                    using var connection = _database.Connect();
                    maintenance.Done.TrySetResult(DatabaseMaintenance.Run(connection, maintenance.AllowVacuum));
                }
#pragma warning disable CA1031 // Maintenance failing must not stop the writer; the caller gets the error.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    maintenance.Done.TrySetException(ex);
                }
            }
        }
    }

    private void Write(List<WriteItem> batch)
    {
        using var connection = _database.Connect();
        var deletes = batch.Any(i => i.Delete is not null || i.Retain is not null);
        if (deletes)
        {
            // Per connection, and not allowed inside a transaction.
            connection.Execute("PRAGMA secure_delete = ON;");
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            foreach (var item in batch)
            {
                if (item.Changes is not null)
                {
                    WriteChanges(connection, transaction, item.Changes);
                }

                if (item.Delete is { } delete)
                {
                    WriteDelete(connection, transaction, delete);
                }

                if (item.Retain is { } retention)
                {
                    WriteRetention(connection, transaction, retention);
                }
            }

            transaction.Commit();
        }
        finally
        {
            if (deletes)
            {
                // Connections are pooled; don't leave the setting behind.
                connection.Execute("PRAGMA secure_delete = OFF;");
            }
        }
    }

    private static void WriteDelete(SqliteConnection connection, SqliteTransaction transaction, DeleteRange delete)
    {
        var rows = connection.Query<IntervalRow>(SelectOverlapping, new
        {
            From = delete.From.ToUnixTimeMilliseconds(),
            To = delete.To.ToUnixTimeMilliseconds(),
        }, transaction).ToList();

        foreach (var row in rows)
        {
            var pieces = IntervalRange.Delete(row.ToInterval(), delete.From, delete.To, Guid.CreateVersion7);
            var id = row.Id;
            var keepsRow = pieces.Count > 0 && pieces[0].Id.ToString("D") == id;
            foreach (var piece in pieces.Skip(keepsRow ? 1 : 0))
            {
                connection.Execute(InsertPiece, new
                {
                    NewId = piece.Id.ToString("D"),
                    Id = id,
                    StartUtc = piece.Start.ToUnixTimeMilliseconds(),
                    EndUtc = piece.End.ToUnixTimeMilliseconds(),
                }, transaction);
            }

            if (keepsRow)
            {
                connection.Execute(UpdateBounds, new
                {
                    Id = id,
                    StartUtc = pieces[0].Start.ToUnixTimeMilliseconds(),
                    EndUtc = pieces[0].End.ToUnixTimeMilliseconds(),
                }, transaction);
            }
            else
            {
                connection.Execute(DeleteInterval, new { Id = id }, transaction);
            }
        }
    }

    private static void WriteRetention(SqliteConnection connection, SqliteTransaction transaction, Retention retention)
    {
        var before = retention.Before.ToUnixTimeMilliseconds();

        // Streamed, so only the totals are held however much history there is.
        var rows = connection.Query<IntervalRow>(SelectClosedBefore, new { Before = before }, transaction, buffered: false);
        var counted = 0;
        var totals = DailyTotals.Build(
            rows.Select(r =>
            {
                counted++;
                return r.ToInterval();
            }),
            retention.Before,
            retention.Zone);

        var appIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var total in totals)
        {
            long? appId = null;
            if (total.App is { } app)
            {
                if (!appIds.TryGetValue(app.Key, out var id))
                {
                    id = connection.ExecuteScalar<long>(SelectAppId, new { app.Key }, transaction);
                    appIds[app.Key] = id;
                }

                appId = id;
            }

            var parameters = new
            {
                Day = DayText(total.Day),
                Kind = (int)total.Kind,
                AppId = appId,
                DurationMs = (long)total.Duration.TotalMilliseconds,
                FirstUtc = total.First.ToUnixTimeMilliseconds(),
                LastUtc = total.Last.ToUnixTimeMilliseconds(),
            };
            if (connection.Execute(AddToTotal, parameters, transaction) == 0)
            {
                connection.Execute(InsertTotal, parameters, transaction);
            }
        }

        // Whole intervals in one statement; what is left reaches past the cut-off and is cut there.
        connection.Execute(DeleteClosedBefore, new { Before = before }, transaction);
        WriteDelete(connection, transaction, new DeleteRange(DateTimeOffset.UnixEpoch, retention.Before));
        retention.RolledUp = counted;
    }

    private static string DayText(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private void WriteChanges(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<IntervalChange> changes)
    {
        foreach (var change in changes)
        {
            var interval = change.Interval;
            var id = interval.Id.ToString("D");

            if (interval.IsOpen)
            {
                // Only one interval can be open. If the write that closed the previous one was lost,
                // it ends at its last checkpoint, the same as after a crash.
                connection.Execute(CloseOthers, new { Id = id }, transaction);
            }

            connection.Execute(UpsertInterval, new
            {
                Id = id,
                StartUtc = interval.Start.ToUnixTimeMilliseconds(),
                EndUtc = interval.End.ToUnixTimeMilliseconds(),
                Kind = (int)interval.Kind,
                AppId = interval.App is null ? (long?)null : GetAppId(connection, transaction, interval.App),
                interval.ExePath,
                interval.Title,
                IsOpen = interval.IsOpen ? 1 : 0,
            }, transaction);
        }
    }

    private long GetAppId(SqliteConnection connection, SqliteTransaction transaction, AppIdentity app)
    {
        if (!_appIds.TryGetValue(app, out var id))
        {
            id = connection.ExecuteScalar<long>(UpsertApp, new { app.Key, app.DisplayName }, transaction);
            _appIds[app] = id;
        }

        return id;
    }

    private sealed record WriteItem(
        IReadOnlyList<IntervalChange>? Changes,
        TaskCompletionSource? Flushed,
        DeleteRange? Delete = null,
        Retention? Retain = null,
        Maintenance? Maintain = null);

    private sealed record DeleteRange(DateTimeOffset From, DateTimeOffset To);

    private sealed record Retention(DateTimeOffset Before, TimeZoneInfo Zone)
    {
        /// <summary>Set by the writer before the item completes.</summary>
        public int RolledUp { get; set; }
    }

    private sealed record Maintenance(bool AllowVacuum, TaskCompletionSource<MaintenanceResult> Done);

    private sealed class TotalRow
    {
        public string Day { get; init; } = "";

        public long Kind { get; init; }

        public string? AppKey { get; init; }

        public string? AppName { get; init; }

        public long DurationMs { get; init; }

        public long FirstUtc { get; init; }

        public long LastUtc { get; init; }

        public RolledUpTotal ToTotal() => new(
            DateOnly.ParseExact(Day, DayFormat, CultureInfo.InvariantCulture),
            (IntervalKind)Kind,
            AppKey is null ? null : new AppIdentity(AppKey, AppName ?? AppKey),
            TimeSpan.FromMilliseconds(DurationMs),
            DateTimeOffset.FromUnixTimeMilliseconds(FirstUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(LastUtc));
    }

    private sealed class IntervalRow
    {
        public string Id { get; init; } = "";

        public long StartUtc { get; init; }

        public long EndUtc { get; init; }

        public long Kind { get; init; }

        public long IsOpen { get; init; }

        public string? ExePath { get; init; }

        public string? Title { get; init; }

        public string? AppKey { get; init; }

        public string? AppName { get; init; }

        public ActivityInterval ToInterval() => new(
            Id: Guid.Parse(Id),
            Kind: (IntervalKind)Kind,
            Start: DateTimeOffset.FromUnixTimeMilliseconds(StartUtc),
            End: DateTimeOffset.FromUnixTimeMilliseconds(EndUtc),
            IsOpen: IsOpen != 0,
            App: AppKey is null ? null : new AppIdentity(AppKey, AppName ?? AppKey),
            ExePath: ExePath,
            Title: Title);
    }
}
