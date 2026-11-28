using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Timeglass.Core.Domain;
using Timeglass.Core.Settings;
using static Timeglass.Api.Tests.ApiHost;

namespace Timeglass.Api.Tests;

/// <summary>Every endpoint against a real, seeded database.</summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private static readonly AppIdentity Code = new("code", "Code");
    private static readonly AppIdentity Outlook = new("outlook", "Outlook");

    // The same days as the storage reporting tests: Wednesday 4 and Thursday 5 November 2026 in
    // Oslo (UTC+1), with a late evening in between. Now is 10:00 local on Thursday, with Code
    // open since 09:30.
    private static readonly ActivityInterval[] Seed =
    [
        Active("2026-11-04T07:00Z", "2026-11-04T08:00Z", Code, "main.cs"),
        Active("2026-11-04T08:00:03Z", "2026-11-04T08:30Z", Code, "tests.cs"),
        Active("2026-11-04T08:30Z", "2026-11-04T09:00Z", Outlook, "Inbox"),
        Away("2026-11-04T09:00Z", "2026-11-04T10:00Z"),
        Active("2026-11-04T10:00Z", "2026-11-04T10:20Z", Code, "main.cs"),
        Active("2026-11-04T10:20Z", "2026-11-04T10:30Z", Code, null),
        Active("2026-11-04T22:00Z", "2026-11-05T00:00Z", Code, "late.cs"),
        Active("2026-11-05T07:00Z", "2026-11-05T08:00Z", Outlook, "Inbox"),
        Active("2026-11-05T08:00Z", "2026-11-05T08:30Z", Outlook, "RE: Budget"),
        Active("2026-11-05T08:30Z", "2026-11-05T08:40Z", Code, "main.cs", open: true),
    ];

    private static readonly DateTimeOffset Now = Utc("2026-11-05T09:00Z");

    private ApiHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await StartAsync(Now, Seed, Ct);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static long Minutes(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    [Fact]
    public async Task After_hours_rule_updates_all_reports_without_deleting_intervals()
    {
        await using var host = await StartAsync(Now,
        [
            Active("2026-11-04T08:00Z", "2026-11-04T15:30Z", Code, "work"),
            Away("2026-11-04T15:30Z", "2026-11-04T19:00Z"),
            Active("2026-11-04T19:00Z", "2026-11-04T19:05Z", Code, "shutdown"),
        ], Ct);
        host.Settings.Current = new TimeglassSettings
        {
            DiscardAfterHoursReturns = true,
            Categories = [new CategorySetting { Name = "Code work", Keywords = ["code"] }],
        };

        var day = await host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);
        var range = await host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-11-04&to=2026-11-04", Ct);
        var timeline = await host.GetJsonAsync<TimelineDto>("/api/days/2026-11-04/timeline", Ct);
        var app = await host.GetJsonAsync<AppDayDto>("/api/days/2026-11-04/apps/code", Ct);
        var categoryTimeline = await host.GetJsonAsync<TimelineDto>("/api/days/2026-11-04/timeline?byCategory=true", Ct);
        var categories = await host.GetJsonAsync<RangeSummaryDto>("/api/range/categories?from=2026-11-04&to=2026-11-04", Ct);
        var categoryApps = await host.GetJsonAsync<RangeSummaryDto>("/api/range/category-applications?from=2026-11-04&to=2026-11-04&category=Code%20work", Ct);
        var groupTitles = await host.GetJsonAsync<CategoryAppTitlesDto>("/api/range/category-application-titles?from=2026-11-04&to=2026-11-04&category=Code%20work&appKey=code", Ct);

        day.ActiveMs.ShouldBe(Minutes(450));
        day.AwayMs.ShouldBe(0);
        day.LastActivity.ShouldBe(Utc("2026-11-04T15:30Z"));
        range.ActiveMs.ShouldBe(day.ActiveMs);
        range.AwayMs.ShouldBe(day.AwayMs);
        timeline.Blocks.Count.ShouldBe(1);
        app.TotalMs.ShouldBe(day.ActiveMs);
        app.Titles.Select(t => t.Title).ShouldBe(["work"]);
        categoryTimeline.Blocks.Count.ShouldBe(1);
        categories.ActiveMs.ShouldBe(day.ActiveMs);
        categories.AwayMs.ShouldBe(day.AwayMs);
        categories.Apps.Single().App.Key.ShouldBe("category-0");
        categories.Apps.Single().Color.ShouldNotBeNullOrWhiteSpace();
        categoryApps.ActiveMs.ShouldBe(day.ActiveMs);
        categoryApps.Apps.Single().DurationMs.ShouldBe(day.ActiveMs);
        groupTitles.TotalMs.ShouldBe(day.ActiveMs);
        groupTitles.Titles.Select(t => t.Title).ShouldBe(["work"]);

        host.Settings.Current.DiscardAfterHoursReturns = false;
        day = await host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);
        day.ActiveMs.ShouldBe(Minutes(455));
        day.AwayMs.ShouldBe(Minutes(210));
    }

    [Fact]
    public async Task Overnight_absence_is_excluded_from_every_day_it_spans()
    {
        await using var host = await StartAsync(Utc("2026-11-06T09:00Z"),
        [
            Active("2026-11-04T08:00Z", "2026-11-04T15:30Z", Code, "work"),
            Away("2026-11-04T15:30Z", "2026-11-05T19:00Z"),
            Active("2026-11-05T19:00Z", "2026-11-05T19:05Z", Code, "shutdown"),
        ], Ct);
        host.Settings.Current = new TimeglassSettings { DiscardAfterHoursReturns = true };

        var first = await host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);
        var second = await host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-05/summary", Ct);
        var range = await host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-11-04&to=2026-11-05", Ct);
        first.AwayMs.ShouldBe(0);
        second.AwayMs.ShouldBe(0);
        second.ActiveMs.ShouldBe(0);
        range.AwayMs.ShouldBe(0);
        range.ActiveMs.ShouldBe(first.ActiveMs);
    }

    [Fact]
    public async Task An_away_interval_immediately_after_a_return_at_midnight_is_not_a_shutdown()
    {
        await using var host = await StartAsync(Utc("2026-11-06T09:00Z"),
        [
            Away("2026-11-04T15:00Z", "2026-11-04T22:55Z"),
            Active("2026-11-04T22:55Z", "2026-11-04T23:00Z", Code, "work"),
            Away("2026-11-04T23:00Z", "2026-11-05T00:00Z"),
        ], Ct);
        host.Settings.Current = new TimeglassSettings { DiscardAfterHoursReturns = true };

        var day = await host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);
        var range = await host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-11-04&to=2026-11-05", Ct);
        day.AwayMs.ShouldBe(Minutes(475));
        day.ActiveMs.ShouldBe(Minutes(5));
        range.Days[0].AwayMs.ShouldBe(day.AwayMs);
        range.Days[0].ActiveMs.ShouldBe(day.ActiveMs);
    }

    [Fact]
    public async Task Day_summary()
    {
        var day = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);

        day.Date.ShouldBe(new DateOnly(2026, 11, 4));
        day.Start.ShouldBe(Utc("2026-11-03T23:00Z"));
        day.Start.Offset.ShouldBe(TimeSpan.FromHours(1)); // a day's bounds are in the user's zone
        day.End.ShouldBe(Utc("2026-11-04T23:00Z"));
        day.ActiveMs.ShouldBe(Minutes(210) - 3000);
        day.AwayMs.ShouldBe(Minutes(60));
        day.FirstActivity.ShouldBe(Utc("2026-11-04T07:00Z"));
        day.LastActivity.ShouldBe(Utc("2026-11-04T23:00Z"));
        day.WorkdayMs.ShouldBe(Minutes(8 * 60)); // 08:00 to midnight local, within 09:00 to 17:00
        day.Apps.Select(a => (a.App.Key, a.App.Name, a.DurationMs, a.Percent))
            .ShouldBe([("code", "Code", Minutes(180) - 3000, 86), ("outlook", "Outlook", Minutes(30), 14)]);
    }

    [Fact]
    public async Task Day_summary_of_an_empty_day()
    {
        var day = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-03/summary", Ct);

        day.ActiveMs.ShouldBe(0);
        day.FirstActivity.ShouldBeNull();
        day.WorkdayMs.ShouldBe(0);
        day.Apps.ShouldBeEmpty();
    }

    [Fact]
    public async Task Timeline_counts_the_open_interval_until_now()
    {
        var timeline = await _host.GetJsonAsync<TimelineDto>("/api/days/2026-11-05/timeline", Ct);

        timeline.Date.ShouldBe(new DateOnly(2026, 11, 5));
        timeline.Blocks.Select(b => (b.Kind, b.App?.Key, b.Start, b.End, b.DurationMs, b.IsOpen)).ShouldBe(
        [
            (ActivityKind.Active, "code", Utc("2026-11-04T23:00Z"), Utc("2026-11-05T00:00Z"), Minutes(60), false),
            (ActivityKind.Active, "outlook", Utc("2026-11-05T07:00Z"), Utc("2026-11-05T08:30Z"), Minutes(90), false),
            (ActivityKind.Active, "code", Utc("2026-11-05T08:30Z"), Now, Minutes(30), true),
        ]);
    }

    [Fact]
    public async Task Timeline_has_away_blocks_without_an_app()
    {
        var timeline = await _host.GetJsonAsync<TimelineDto>("/api/days/2026-11-04/timeline", Ct);

        var away = timeline.Blocks.Single(b => b.Kind == ActivityKind.Away);
        away.App.ShouldBeNull();
        away.DurationMs.ShouldBe(Minutes(60));
    }

    [Fact]
    public async Task Category_timeline_splits_title_changes_and_uses_current_settings()
    {
        _host.Settings.Current.Categories =
        [
            new CategorySetting { Name = "Main", Keywords = ["main.cs"] },
            new CategorySetting { Name = "Tests", Keywords = ["tests.cs"] },
        ];
        var timeline = await _host.GetJsonAsync<TimelineDto>("/api/days/2026-11-04/timeline?byCategory=true", Ct);

        timeline.Blocks.Take(2).Select(b => (b.App?.Key, b.Category?.Key, b.Category?.Name))
            .ShouldBe([("code", "category-0", "Main"), ("code", "category-1", "Tests")]);
        timeline.Blocks.Single(b => b.Kind == ActivityKind.Away).Category.ShouldBeNull();
    }

    [Fact]
    public async Task App_day_lists_time_by_title()
    {
        var app = await _host.GetJsonAsync<AppDayDto>("/api/days/2026-11-04/apps/code", Ct);

        app.Date.ShouldBe(new DateOnly(2026, 11, 4));
        app.App.ShouldBe(new AppDto("code", "Code"));
        app.TotalMs.ShouldBe(Minutes(180) - 3000);
        app.Titles.Select(t => (t.Title, t.DurationMs)).ShouldBe(
        [
            ("main.cs", Minutes(80)),
            ("late.cs", Minutes(60)),
            ("tests.cs", Minutes(30) - 3000),
            (null, Minutes(10)),
        ]);
        app.Titles.Sum(t => t.Percent).ShouldBe(100);
    }

    [Fact]
    public async Task App_day_of_an_app_without_time_is_empty()
    {
        var app = await _host.GetJsonAsync<AppDayDto>("/api/days/2026-11-04/apps/teams", Ct);

        app.App.ShouldBe(new AppDto("teams", "teams"));
        app.TotalMs.ShouldBe(0);
        app.Titles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Range_summary_has_every_day()
    {
        var range = await _host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-11-02&to=2026-11-08", Ct);

        range.From.ShouldBe(new DateOnly(2026, 11, 2));
        range.To.ShouldBe(new DateOnly(2026, 11, 8));
        range.Days.Select(d => d.Date.Day).ShouldBe([2, 3, 4, 5, 6, 7, 8]);
        range.Days.Select(d => d.ActiveMs).ShouldBe([0, 0, Minutes(210) - 3000, Minutes(180), 0, 0, 0]);
        range.Days[2].Apps.Select(a => a.App.Key).ShouldBe(["code", "outlook"]);
        range.ActiveMs.ShouldBe(Minutes(390) - 3000);
        range.AwayMs.ShouldBe(Minutes(60));
        range.Apps.Select(a => (a.App.Key, a.Percent)).ShouldBe([("code", 69), ("outlook", 31)]);
    }

    [Fact]
    public async Task App_colors_match_across_days_range_and_drilldown()
    {
        var day = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);
        var next = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-05/summary", Ct);
        var range = await _host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-11-04&to=2026-11-05", Ct);
        var detail = await _host.GetJsonAsync<AppDayDto>("/api/days/2026-11-05/apps/code", Ct);
        var color = day.Apps.Single(a => a.App.Key == "code").Color;

        next.Apps.Single(a => a.App.Key == "code").Color.ShouldBe(color);
        range.Apps.Single(a => a.App.Key == "code").Color.ShouldBe(color);
        range.Days.SelectMany(d => d.Apps).Where(a => a.App.Key == "code").Select(a => a.Color)
            .ShouldAllBe(c => c == color);
        detail.Color.ShouldBe(color);
        day.Apps.Select(a => a.Color).Distinct().Count().ShouldBe(day.Apps.Count);
    }

    [Fact]
    public async Task Status_while_tracking_names_the_current_app()
    {
        var status = await _host.GetJsonAsync<StatusDto>("/api/status", Ct);

        status.ShouldBe(new StatusDto(TrackingStateKind.Active, null, new AppDto("code", "Code"), Utc("2026-11-05T08:30Z"), new DateOnly(2026, 11, 5), Now));
    }

    [Fact]
    public async Task Status_while_paused()
    {
        _host.Tracking.IsPaused = true;
        _host.Tracking.PausedUntil = Utc("2026-11-05T09:30Z");

        var status = await _host.GetJsonAsync<StatusDto>("/api/status", Ct);

        status.State.ShouldBe(TrackingStateKind.Paused);
        status.PausedUntil.ShouldBe(Utc("2026-11-05T09:30Z"));
        status.CurrentApp.ShouldBeNull();
        status.Since.ShouldBeNull();
    }

    [Fact]
    public async Task Status_while_away()
    {
        await using var host = await StartAsync(Now, [Away("2026-11-05T08:00Z", "2026-11-05T08:50Z", open: true)], Ct);

        var status = await host.GetJsonAsync<StatusDto>("/api/status", Ct);

        status.State.ShouldBe(TrackingStateKind.Away);
        status.CurrentApp.ShouldBeNull();
        status.Since.ShouldBe(Utc("2026-11-05T08:00Z"));
    }

    [Fact]
    public async Task Status_with_nothing_open_is_untracked()
    {
        await using var host = await StartAsync(Now, [Active("2026-11-05T08:00Z", "2026-11-05T08:50Z", Code, "main.cs")], Ct);

        var status = await host.GetJsonAsync<StatusDto>("/api/status", Ct);

        status.State.ShouldBe(TrackingStateKind.Untracked);
        status.CurrentApp.ShouldBeNull();
        status.Since.ShouldBeNull();
    }

    [Fact]
    public async Task Json_uses_camel_case_iso_dates_and_lowercase_kinds()
    {
        var json = await _host.Client.GetFromJsonAsync<JsonObject>("/api/days/2026-11-05/timeline", Ct);

        json!["date"]!.GetValue<string>().ShouldBe("2026-11-05");
        var block = json["blocks"]![0]!.AsObject();
        block.Select(p => p.Key).ShouldBe(["kind", "app", "start", "end", "durationMs", "isOpen", "category"]);
        block["kind"]!.GetValue<string>().ShouldBe("active");
        block["app"]!["name"]!.GetValue<string>().ShouldBe("Code");

        var status = await _host.Client.GetFromJsonAsync<JsonObject>("/api/status", Ct);
        status!["state"]!.GetValue<string>().ShouldBe("active");
    }

    [Fact]
    public async Task Times_are_in_the_users_zone_so_the_clock_time_can_be_read_off_the_string()
    {
        // The dashboard shows "08:00" from "2026-11-05T08:00:00+01:00" without converting (#37).
        var day = await _host.Client.GetFromJsonAsync<JsonObject>("/api/days/2026-11-05/summary", Ct);
        day!["firstActivity"]!.GetValue<string>().ShouldBe("2026-11-05T00:00:00+01:00");
        day["lastActivity"]!.GetValue<string>().ShouldBe("2026-11-05T10:00:00+01:00");

        var timeline = await _host.Client.GetFromJsonAsync<JsonObject>("/api/days/2026-11-05/timeline", Ct);
        var open = timeline!["blocks"]!.AsArray()[^1]!;
        open["start"]!.GetValue<string>().ShouldBe("2026-11-05T09:30:00+01:00");
        open["end"]!.GetValue<string>().ShouldBe("2026-11-05T10:00:00+01:00");

        var status = await _host.Client.GetFromJsonAsync<JsonObject>("/api/status", Ct);
        status!["since"]!.GetValue<string>().ShouldBe("2026-11-05T09:30:00+01:00");
        status["now"]!.GetValue<string>().ShouldBe("2026-11-05T10:00:00+01:00");
    }

    [Theory]
    [InlineData("/api/days/2026-13-01/summary", "date")]
    [InlineData("/api/days/yesterday/timeline", "date")]
    [InlineData("/api/days/2026-2-1/apps/code", "date")]
    [InlineData("/api/days/2026-02-30/summary", "date")]
    [InlineData("/api/range/summary", "from")]
    [InlineData("/api/range/summary?from=2026-11-02", "to")]
    [InlineData("/api/range/summary?from=2026-11-02&to=11/08/2026", "to")]
    [InlineData("/api/range/summary?from=2026-11-08&to=2026-11-02", "to")]
    [InlineData("/api/range/summary?from=2024-01-01&to=2025-01-01", "to")] // 367 days: 2024 is a leap year
    [InlineData("/api/range/categories?from=9999-12-31&to=9999-12-31", "to")]
    public async Task Invalid_dates_are_a_problem_details_400(string url, string field)
    {
        using var response = await _host.Client.GetAsync(new Uri(url, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(JsonSerializerOptions.Web, Ct);
        problem!["status"]!.GetValue<int>().ShouldBe(400);
        problem["errors"]![field]!.AsArray().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_range_of_366_days_is_allowed()
    {
        var range = await _host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-01-01&to=2027-01-01", Ct);

        range.Days.Count.ShouldBe(366);
    }

    [Fact]
    public async Task Unknown_routes_are_404()
    {
        using var response = await _host.Client.GetAsync(new Uri("/api/days", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
