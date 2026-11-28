using System.Globalization;
using Dapper;
using Timeglass.Core.Domain;
using Timeglass.Core.Reporting;
using Timeglass.Core.Settings;

namespace Timeglass.Storage.Tests;

/// <summary>Retention (#41): old intervals rolled up into daily totals, against a real database.</summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
    private static readonly AppIdentity Code = new("code", "Code");
    private static readonly AppIdentity Outlook = new("outlook", "Outlook");

    // Wednesday 4 and Thursday 5 November 2026, Oslo (UTC+1), with an evening past midnight, and
    // the open interval on Monday the 9th.
    private static readonly ActivityInterval[] Seed =
    [
        Active("2026-11-04T07:00Z", "2026-11-04T08:00Z", Code, "secret.cs"),
        Active("2026-11-04T08:00Z", "2026-11-04T08:30Z", Outlook, "RE: Salary"),
        Away("2026-11-04T08:30Z", "2026-11-04T09:30Z"),
        Active("2026-11-04T09:30Z", "2026-11-04T09:40Z", null, null),
        Active("2026-11-04T22:00Z", "2026-11-05T00:00Z", Code, "late.cs"), // 23:00 to 01:00 local
        Active("2026-11-05T07:00Z", "2026-11-05T09:00Z", Outlook, "Inbox"),
        Active("2026-11-09T07:00Z", "2026-11-09T07:10Z", Code, "today.cs", open: true),
    ];

    private static readonly DateTimeOffset Now = Utc("2026-11-09T08:00Z");

    private readonly TempDatabase _temp = new();
    private TimeglassDatabase _database = null!;
    private IntervalRepository _repository = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = _temp.Open();
        _repository = new IntervalRepository(_database);
        await _repository.ApplyAsync(
            [.. Seed.Select(i => new IntervalChange(i.IsOpen ? IntervalChangeKind.Opened : IntervalChangeKind.Closed, i))], Ct);
        await _repository.FlushAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _repository.DisposeAsync();
        _database.Dispose();
        _temp.Dispose();
    }

    private static DateTimeOffset Utc(string at) => DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static DateOnly Day(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset StartOf(string day) => DaySummaries.Bounds(Day(day), Oslo).Start;

    private static ActivityInterval Active(string start, string end, AppIdentity? app, string? title, bool open = false) =>
        new(Guid.CreateVersion7(), IntervalKind.Active, Utc(start), Utc(end), open, app, app is null ? null : $@"C:\{app.Key}.exe", title);

    private static ActivityInterval Away(string start, string end) =>
        new(Guid.CreateVersion7(), IntervalKind.Away, Utc(start), Utc(end), false, null, null, null);

    private async Task<(DaySummary Wed, DaySummary Thu, RangeSummary Week)> SummariesAsync() => (
        await _repository.GetDaySummaryAsync(Day("2026-11-04"), Oslo, Now, cancellationToken: Ct),
        await _repository.GetDaySummaryAsync(Day("2026-11-05"), Oslo, Now, cancellationToken: Ct),
        await _repository.GetRangeSummaryAsync(Day("2026-11-02"), Day("2026-11-08"), Oslo, Now, cancellationToken: Ct));

    [Fact]
    public async Task Category_application_titles_include_retained_and_remaining_time()
    {
        await _repository.ApplyRetentionAsync(StartOf("2026-11-05"), Oslo, Ct);
        CategorySetting[] categories = [new() { Name = "Code work", Keywords = ["code"] }];
        var first = Day("2026-11-04");
        var last = Day("2026-11-05");

        var applications = await _repository.GetCategoryApplicationsAsync(first, last, Oslo, Now, categories, "Code work", Ct);
        var titles = await _repository.GetCategoryApplicationTitlesAsync(first, last, Oslo, Now, categories, "Code work", "code", Ct);

        titles.Total.ShouldBe(applications.Apps.Single(a => a.App.Key == "code").Duration);
        titles.Titles.Select(t => (t.Title, t.Duration.TotalMinutes)).ShouldBe(
            [(null, 120d), ("late.cs", 60d)]);
    }

    [Fact]
    public async Task Category_away_time_is_unchanged_by_retention()
    {
        var first = Day("2026-11-04");
        var last = Day("2026-11-05");
        var before = await _repository.GetCategoryRangeSummaryAsync(first, last, Oslo, Now, [], Ct);

        await _repository.ApplyRetentionAsync(StartOf("2026-11-05"), Oslo, Ct);

        var after = await _repository.GetCategoryRangeSummaryAsync(first, last, Oslo, Now, [], Ct);
        after.Apps.Single(a => a.App.Key == "away").Duration.ShouldBe(TimeSpan.FromHours(1));
        after.Apps.Single(a => a.App.Key == "away").ShouldBe(before.Apps.Single(a => a.App.Key == "away"));
        after.Days[0].Apps.Single(a => a.App.Key == "away").Duration.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task The_rollup_equals_the_intervals_it_replaces()
    {
        var before = await SummariesAsync();

        var rolledUp = await _repository.ApplyRetentionAsync(StartOf("2026-11-06"), Oslo, Ct);

        rolledUp.ShouldBe(Seed.Count(i => !i.IsOpen));
        var after = await SummariesAsync();
        after.Wed.ShouldBeEquivalentTo(before.Wed);
        after.Thu.ShouldBeEquivalentTo(before.Thu);
        after.Week.ShouldBeEquivalentTo(before.Week);

        // The rows add up to the intervals, per kind.
        var totals = await _repository.GetRolledUpAsync(Day("2026-11-01"), Day("2026-11-30"), Ct);
        var replaced = Seed.Where(i => !i.IsOpen).ToList();
        totals.Where(t => t.Kind == IntervalKind.Active).Sum(t => t.Duration.Ticks)
            .ShouldBe(replaced.Where(i => i.Kind == IntervalKind.Active).Sum(i => i.Duration.Ticks));
        totals.Where(t => t.Kind == IntervalKind.Away).Sum(t => t.Duration.Ticks)
            .ShouldBe(replaced.Where(i => i.Kind == IntervalKind.Away).Sum(i => i.Duration.Ticks));
    }

    [Fact]
    public async Task Titles_and_the_timeline_of_rolled_up_days_are_gone()
    {
        await _repository.ApplyRetentionAsync(StartOf("2026-11-06"), Oslo, Ct);

        (await _repository.GetRangeAsync(Utc("2026-11-01T00:00Z"), Utc("2026-11-06T00:00Z"), Ct)).ShouldBeEmpty();
        (await _repository.GetTimelineAsync(Day("2026-11-04"), Oslo, Now, cancellationToken: Ct)).Blocks.ShouldBeEmpty();

        using var connection = _database.Connect();
        connection.ExecuteScalar<long>("SELECT count(*) FROM intervals WHERE window_title LIKE '%Salary%';").ShouldBe(0);
    }

    [Fact]
    public async Task Recent_days_and_the_open_interval_are_kept()
    {
        await _repository.ApplyRetentionAsync(StartOf("2026-11-05"), Oslo, Ct);

        var kept = await _repository.GetRangeAsync(Utc("2026-11-01T00:00Z"), Utc("2026-11-10T00:00Z"), Ct);
        kept.Select(i => (i.Title, i.Start, i.End, i.IsOpen)).ShouldBe(
        [
            ("late.cs", StartOf("2026-11-05"), Utc("2026-11-05T00:00Z"), false), // cut at the cut-off
            ("Inbox", Utc("2026-11-05T07:00Z"), Utc("2026-11-05T09:00Z"), false),
            ("today.cs", Utc("2026-11-09T07:00Z"), Utc("2026-11-09T07:10Z"), true),
        ]);
    }

    [Fact]
    public async Task An_interval_cut_at_the_cut_off_still_counts_once()
    {
        var before = await SummariesAsync();

        // Midnight between Wednesday and Thursday, in the middle of the late evening.
        await _repository.ApplyRetentionAsync(StartOf("2026-11-05"), Oslo, Ct);
        var after = await SummariesAsync();

        after.Wed.ShouldBeEquivalentTo(before.Wed);
        after.Thu.ShouldBeEquivalentTo(before.Thu);
        after.Week.ShouldBeEquivalentTo(before.Week);
    }

    [Fact]
    public async Task Running_again_later_adds_to_the_totals()
    {
        var before = await SummariesAsync();

        // Halfway through Wednesday's Outlook interval, then the same again, then all of it.
        await _repository.ApplyRetentionAsync(Utc("2026-11-04T08:15Z"), Oslo, Ct);
        (await _repository.ApplyRetentionAsync(Utc("2026-11-04T08:15Z"), Oslo, Ct)).ShouldBe(0);
        await _repository.ApplyRetentionAsync(StartOf("2026-11-06"), Oslo, Ct);

        var after = await SummariesAsync();
        after.Wed.ShouldBeEquivalentTo(before.Wed);
        after.Thu.ShouldBeEquivalentTo(before.Thu);
        after.Week.ShouldBeEquivalentTo(before.Week);

        // Wednesday's Outlook row was written by the first run and added to by the last.
        var wednesday = await _repository.GetRolledUpAsync(Day("2026-11-04"), Day("2026-11-04"), Ct);
        var outlook = wednesday.Single(t => t.App == Outlook);
        outlook.Duration.ShouldBe(TimeSpan.FromMinutes(30));
        (outlook.First, outlook.Last).ShouldBe((Utc("2026-11-04T08:00Z"), Utc("2026-11-04T08:30Z")));
    }

    [Fact]
    public async Task Tracking_writes_queued_before_retention_are_rolled_up_too()
    {
        var late = Active("2026-11-03T10:00Z", "2026-11-03T11:00Z", Code, "queued.cs");
        await _repository.ApplyAsync([new IntervalChange(IntervalChangeKind.Closed, late)], Ct);

        (await _repository.ApplyRetentionAsync(StartOf("2026-11-04"), Oslo, Ct)).ShouldBe(1);

        var tuesday = await _repository.GetDaySummaryAsync(Day("2026-11-03"), Oslo, Now, cancellationToken: Ct);
        tuesday.Active.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Maintenance_runs_and_empties_the_write_ahead_log()
    {
        var result = await _repository.RunMaintenanceAsync(allowVacuum: false, Ct);

        result.Vacuumed.ShouldBeFalse();
        result.Pages.ShouldBeGreaterThan(0);
        new FileInfo(_database.Path + "-wal").Length.ShouldBe(0);
    }

    [Fact]
    public async Task A_mostly_empty_file_is_vacuumed_only_when_allowed()
    {
        // Lots of titles, then deleted: the file is left mostly unused pages.
        var start = Utc("2026-10-01T00:00Z");
        var bulk = Enumerable.Range(0, 5000)
            .Select(i => Active(
                start.AddMinutes(i).ToString("O", CultureInfo.InvariantCulture),
                start.AddMinutes(i + 1).ToString("O", CultureInfo.InvariantCulture),
                Code,
                new string('x', 500)))
            .Select(i => new IntervalChange(IntervalChangeKind.Closed, i))
            .ToList();
        await _repository.ApplyAsync(bulk, Ct);
        await _repository.DeleteRangeAsync(start, Utc("2026-11-01T00:00Z"), Ct);

        var kept = await _repository.RunMaintenanceAsync(allowVacuum: false, Ct);
        kept.Vacuumed.ShouldBeFalse();
        DatabaseMaintenance.ShouldVacuum(kept.FreePages, kept.Pages).ShouldBeTrue();
        var sizeBefore = new FileInfo(_database.Path).Length;

        var vacuumed = await _repository.RunMaintenanceAsync(allowVacuum: true, Ct);

        vacuumed.Vacuumed.ShouldBeTrue();
        new FileInfo(_database.Path).Length.ShouldBeLessThan(sizeBefore / 2);
        (await _repository.GetRangeAsync(Utc("2026-11-04T00:00Z"), Utc("2026-11-10T00:00Z"), Ct)).Count.ShouldBe(Seed.Length);
    }

    [Theory]
    [InlineData(0, 1000, false)]
    [InlineData(255, 300, false)] // most of a tiny file
    [InlineData(300, 2000, false)] // not a quarter
    [InlineData(500, 2000, true)]
    public void Vacuum_threshold(long free, long pages, bool expected) =>
        DatabaseMaintenance.ShouldVacuum(free, pages).ShouldBe(expected);
}
