using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Timeglass.Core.Domain;
using Timeglass.Core.Settings;
using static Timeglass.Api.Tests.ApiHost;

namespace Timeglass.Api.Tests;

/// <summary>The endpoints that change things (#36): settings, pause and resume, and deleting a range.</summary>
public sealed class SettingsEndpointTests : IAsyncLifetime
{
    private static readonly AppIdentity Code = new("code", "Code");
    private static readonly AppIdentity Outlook = new("outlook", "Outlook");

    // Tuesday 3 to Thursday 5 November 2026 in Oslo (UTC+1). Now is 10:00 local on Thursday.
    private static readonly ActivityInterval[] Seed =
    [
        Active("2026-11-03T08:00Z", "2026-11-03T09:00Z", Code, "main.cs"),
        Active("2026-11-03T22:30Z", "2026-11-04T00:30Z", Code, "late.cs"), // into Wednesday
        Active("2026-11-04T08:00Z", "2026-11-04T09:00Z", Outlook, "Inbox"),
        Active("2026-11-04T22:00Z", "2026-11-04T23:30Z", Code, "later.cs"), // into Thursday
        Active("2026-11-05T07:00Z", "2026-11-05T08:00Z", Outlook, "Inbox"),
    ];

    private static readonly DateTimeOffset Now = Utc("2026-11-05T09:00Z");

    private ApiHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await StartAsync(Now, Seed, Ct);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static SettingsDto Defaults => new(300, [], [], true, [], [], false);

    private async Task<HttpResponseMessage> PutAsync(SettingsDto settings) =>
        await _host.Client.PutAsJsonAsync(new Uri("/api/settings", UriKind.Relative), settings, JsonSerializerOptions.Web, Ct);

    [Fact]
    public async Task Categories_round_trip_and_category_title_time()
    {
        using var response = await PutAsync(Defaults with
        {
            Categories = [new("Code work", ["late"]), new("Mail", ["outlook"])],
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var saved = await _host.GetJsonAsync<SettingsDto>("/api/settings", Ct);
        saved.Categories!.Select(g => g.Name).ShouldBe(["Code work", "Mail"]);
        var result = await _host.GetJsonAsync<RangeSummaryDto>("/api/range/categories?from=2026-11-03&to=2026-11-04", Ct);
        result.Apps.Select(a => (a.App.Name, a.DurationMs)).ShouldBe(
            [("Code work", 10_800_000L), ("Mail", 3_600_000L), ("Ungrouped", 3_600_000L)]);
        var detail = await _host.GetJsonAsync<RangeSummaryDto>(
            "/api/range/category-applications?from=2026-11-03&to=2026-11-04&category=Code%20work", Ct);
        detail.ActiveMs.ShouldBe(10_800_000L);
        detail.Apps.Select(a => (a.App.Name, a.DurationMs)).ShouldBe([("Code", 10_800_000L)]);
        var titles = await _host.GetJsonAsync<CategoryAppTitlesDto>(
            "/api/range/category-application-titles?from=2026-11-03&to=2026-11-04&category=Code%20work&appKey=code", Ct);
        titles.TotalMs.ShouldBe(detail.ActiveMs);
        titles.Titles.Select(t => (t.Title, t.DurationMs)).ShouldBe(
            [("late.cs", 7_200_000L), ("later.cs", 3_600_000L)]);
    }

    [Fact]
    public async Task Category_range_includes_tracked_away_without_changing_active_totals()
    {
        await using var host = await StartAsync(Now,
        [
            Active("2026-11-03T08:00Z", "2026-11-03T09:00Z", Code, "main.cs"),
            Away("2026-11-03T09:00Z", "2026-11-03T09:30Z"),
        ], Ct);
        var result = await host.GetJsonAsync<RangeSummaryDto>("/api/range/categories?from=2026-11-03&to=2026-11-03", Ct);

        result.ActiveMs.ShouldBe(3_600_000L);
        result.AwayMs.ShouldBe(1_800_000L);
        result.Apps.Select(a => (a.App.Key, a.DurationMs, a.Percent))
            .ShouldBe([("ungrouped", 3_600_000L, 67), ("away", 1_800_000L, 33)]);
        result.Days.Single().Apps.Single(a => a.App.Key == "away").DurationMs.ShouldBe(1_800_000L);
    }

    [Fact]
    public async Task Unknown_category_is_rejected_rather_than_showing_another_category()
    {
        using var response = await _host.Client.GetAsync(
            new Uri("/api/range/category-applications?from=2026-11-03&to=2026-11-04&category=Missing", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Group_application_titles_require_an_application_key()
    {
        using var response = await _host.Client.GetAsync(
            new Uri("/api/range/category-application-titles?from=2026-11-03&to=2026-11-04&category=Ungrouped", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Categories_reject_duplicate_names_and_keep_existing_when_omitted()
    {
        _host.Settings.Current = new TimeglassSettings
        {
            Categories = [new CategorySetting { Name = "Existing", Keywords = ["code"] }],
        };
        using var invalid = await PutAsync(Defaults with
        {
            Categories = [new("same", ["one"]), new("SAME", ["two"])],
        });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var omitted = await PutAsync(Defaults);
        omitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Settings.Current.Categories.Single().Name.ShouldBe("Existing");
    }

    private async Task<JsonObject> ErrorsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(JsonSerializerOptions.Web, Ct);
        return problem!["errors"]!.AsObject();
    }

    [Fact]
    public async Task After_hours_settings_round_trip_and_old_clients_preserve_them()
    {
        using var response = await PutAsync(Defaults with
        {
            DiscardAfterHoursReturns = true,
            AfterHoursMinimumAwayMinutes = 90,
            AfterHoursMaximumReturnMinutes = 15,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var oldClientResponse = await PutAsync(Defaults);
        oldClientResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = await _host.GetJsonAsync<SettingsDto>("/api/settings", Ct);
        settings.DiscardAfterHoursReturns.ShouldBe(true);
        settings.AfterHoursMinimumAwayMinutes.ShouldBe(90);
        settings.AfterHoursMaximumReturnMinutes.ShouldBe(15);
    }

    [Fact]
    public async Task After_hours_thresholds_must_be_in_range()
    {
        using var response = await PutAsync(Defaults with
        {
            AfterHoursMinimumAwayMinutes = 0,
            AfterHoursMaximumReturnMinutes = 241,
        });
        var errors = await ErrorsAsync(response);
        errors["afterHoursMinimumAwayMinutes"].ShouldNotBeNull();
        errors["afterHoursMaximumReturnMinutes"].ShouldNotBeNull();
    }

    private async Task<HttpResponseMessage> DeleteAsync(string query, string? confirm)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/intervals{query}", UriKind.Relative))
        {
            Content = JsonContent.Create(new DeleteIntervalsRequest(confirm), options: JsonSerializerOptions.Web),
        };
        return await _host.Client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Get_settings_returns_what_is_in_effect()
    {
        _host.Settings.Current = new TimeglassSettings
        {
            IdleThreshold = TimeSpan.FromMinutes(10),
            ExcludedApps = ["keepass"],
            RecordWindowTitles = false,
            AppAliases = [new AppAliasSetting { Key = "outlook", Name = "Mail" }],
        };
        _host.Autostart.IsEnabled = true;

        var settings = await _host.GetJsonAsync<SettingsDto>("/api/settings", Ct);

        settings.IdleThresholdSeconds.ShouldBe(600);
        settings.ExcludedApps.ShouldBe(["keepass"]);
        settings.IdleExemptApps.ShouldBeEmpty();
        settings.RecordWindowTitles.ShouldBeFalse();
        settings.AppAliases.Select(a => (a.Key, a.Name, a.Exes.Count, a.TitlePattern)).ShouldBe([("outlook", "Mail", 0, null)]);
        settings.StartWithWindows.ShouldBe(true);
    }

    [Fact]
    public async Task Work_hours_round_trip_in_minutes_and_shape_the_workday()
    {
        using var response = await PutAsync(Defaults with { WorkHoursStartMinutes = 8 * 60 + 30, WorkHoursEndMinutes = 16 * 60 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Settings.Current.WorkHoursStart.ShouldBe(TimeSpan.FromHours(8.5));
        _host.Settings.Current.WorkHoursEnd.ShouldBe(TimeSpan.FromHours(16));
        var settings = await _host.GetJsonAsync<SettingsDto>("/api/settings", Ct);
        settings.WorkHoursStartMinutes.ShouldBe(8 * 60 + 30);
        settings.WorkHoursEndMinutes.ShouldBe(16 * 60);
    }

    [Fact]
    public async Task Leaving_out_work_hours_keeps_them()
    {
        _host.Settings.Current = new TimeglassSettings { WorkHoursStart = TimeSpan.FromHours(7) };

        using var response = await PutAsync(Defaults);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Settings.Current.WorkHoursStart.ShouldBe(TimeSpan.FromHours(7));
        _host.Settings.Current.WorkHoursEnd.ShouldBe(TimeSpan.FromHours(17));
    }

    [Fact]
    public async Task Work_hours_that_end_before_they_start_are_refused()
    {
        using var response = await PutAsync(Defaults with { WorkHoursStartMinutes = 17 * 60, WorkHoursEndMinutes = 9 * 60 });

        var errors = await ErrorsAsync(response);
        errors["workHoursEndMinutes"]!.AsArray().Single()!.GetValue<string>().ShouldBe("WorkHoursEnd must be after WorkHoursStart.");
    }

    [Fact]
    public async Task Get_settings_with_idle_detection_off()
    {
        _host.Settings.Current = new TimeglassSettings { IdleThreshold = null };

        var settings = await _host.GetJsonAsync<SettingsDto>("/api/settings", Ct);

        settings.IdleThresholdSeconds.ShouldBeNull();
    }

    [Fact]
    public async Task Put_settings_saves_them_and_keeps_what_the_dashboard_does_not_edit()
    {
        _host.Settings.Current = new TimeglassSettings { ApiPort = 6000, MinimumInterval = TimeSpan.FromSeconds(5) };

        using var response = await PutAsync(Defaults with
        {
            IdleThresholdSeconds = 120,
            IdleExemptApps = [" vlc ", "VLC", "zoom"],
            ExcludedApps = ["keepass"],
            AppsWithoutTitles = ["edge"],
            AppAliases = [new AppAliasDto("game", "My game", ["game.exe"], null)],
            StartWithWindows = true,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = _host.Settings.Current;
        saved.IdleThreshold.ShouldBe(TimeSpan.FromMinutes(2));
        saved.IdleExemptApps.ShouldBe(["vlc", "zoom"]);
        saved.ExcludedApps.ShouldBe(["keepass"]);
        saved.AppsWithoutTitles.ShouldBe(["edge"]);
        saved.AppAliases.Single().ToAlias().Exes.ShouldBe(["game.exe"]);
        saved.ApiPort.ShouldBe(6000);
        saved.MinimumInterval.ShouldBe(TimeSpan.FromSeconds(5));
        _host.Autostart.IsEnabled.ShouldBeTrue();

        var body = await response.Content.ReadFromJsonAsync<SettingsDto>(JsonSerializerOptions.Web, Ct);
        body!.IdleExemptApps.ShouldBe(["vlc", "zoom"]);
        body.StartWithWindows.ShouldBe(true);
    }

    [Fact]
    public async Task Put_settings_can_turn_idle_detection_off()
    {
        using var response = await PutAsync(Defaults with { IdleThresholdSeconds = null });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Settings.Current.IdleThreshold.ShouldBeNull();
    }

    [Fact]
    public async Task Put_settings_without_start_with_windows_leaves_autostart_alone()
    {
        _host.Autostart.IsEnabled = true;

        using var response = await PutAsync(Defaults with { StartWithWindows = null });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Autostart.IsEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(29)]
    [InlineData(4 * 3600 + 1)]
    public async Task Put_settings_rejects_an_idle_threshold_out_of_range(int seconds)
    {
        using var response = await PutAsync(Defaults with { IdleThresholdSeconds = seconds });

        var errors = await ErrorsAsync(response);
        errors.Select(e => e.Key).ShouldBe(["idleThresholdSeconds"]);
        _host.Settings.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Put_settings_rejects_empty_app_keys()
    {
        using var response = await PutAsync(Defaults with { ExcludedApps = ["keepass", "  "] });

        var errors = await ErrorsAsync(response);
        errors["excludedApps"]!.AsArray().Single()!.GetValue<string>().ShouldContain("empty app keys");
        _host.Settings.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Put_settings_rejects_an_app_both_excluded_and_without_titles()
    {
        using var response = await PutAsync(Defaults with { ExcludedApps = ["edge"], AppsWithoutTitles = ["Edge"] });

        var errors = await ErrorsAsync(response);
        errors.Select(e => e.Key).ShouldBe(["excludedApps", "appsWithoutTitles"], ignoreOrder: true);
        _host.Settings.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Put_settings_rejects_an_invalid_alias()
    {
        using var response = await PutAsync(Defaults with { AppAliases = [new AppAliasDto("New App", "New", [], "(")] });

        var errors = await ErrorsAsync(response);
        errors.Select(e => e.Key).ShouldBe(["appAliases"]);
        errors["appAliases"]!.AsArray().Count.ShouldBeGreaterThan(0);
        _host.Settings.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        using (var paused = await _host.Client.PostAsJsonAsync(new Uri("/api/pause", UriKind.Relative), new PauseRequest(PauseDuration.OneHour), JsonSerializerOptions.Web, Ct))
        {
            paused.StatusCode.ShouldBe(HttpStatusCode.OK);
            var status = await paused.Content.ReadFromJsonAsync<StatusDto>(JsonSerializerOptions.Web, Ct);
            status!.State.ShouldBe(TrackingStateKind.Paused);
            status.PausedUntil.ShouldBe(Now.AddHours(1));
            status.PausedUntil!.Value.Offset.ShouldBe(TimeSpan.FromHours(1)); // local clock time, for the header
        }

        _host.Tracking.PausedUntil.ShouldBe(Now.AddHours(1));

        using var resumed = await _host.Client.PostAsync(new Uri("/api/resume", UriKind.Relative), content: null, Ct);
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resumed.Content.ReadFromJsonAsync<StatusDto>(JsonSerializerOptions.Web, Ct))!.State.ShouldNotBe(TrackingStateKind.Paused);
        _host.Tracking.IsPaused.ShouldBeFalse();
    }

    [Theory]
    [InlineData("untilResumed", null)]
    [InlineData("thirtyMinutes", "2026-11-05T09:30Z")]
    [InlineData("untilTomorrow", "2026-11-05T23:00Z")]
    public async Task Pause_ends_when_the_duration_says(string duration, string? until)
    {
        using var content = new StringContent($$"""{"duration":"{{duration}}"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await _host.Client.PostAsync(new Uri("/api/pause", UriKind.Relative), content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _host.Tracking.IsPaused.ShouldBeTrue();
        _host.Tracking.PausedUntil.ShouldBe(until is null ? null : Utc(until));
    }

    [Fact]
    public async Task Delete_removes_whole_local_days_and_cuts_intervals_at_the_edges()
    {
        using var response = await DeleteAsync("?from=2026-11-04&to=2026-11-04", "2026-11-04..2026-11-04");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _host.Deletions.ShouldBe([(Utc("2026-11-03T23:00Z"), Utc("2026-11-04T23:00Z"))]);

        var wednesday = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-04/summary", Ct);
        wednesday.ActiveMs.ShouldBe(0);

        // The intervals that ran over midnight keep the parts outside Wednesday.
        var tuesday = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-03/summary", Ct);
        tuesday.ActiveMs.ShouldBe((long)TimeSpan.FromMinutes(90).TotalMilliseconds);
        var thursday = await _host.GetJsonAsync<DaySummaryDto>("/api/days/2026-11-05/summary", Ct);
        thursday.ActiveMs.ShouldBe((long)TimeSpan.FromMinutes(90).TotalMilliseconds);
    }

    [Fact]
    public async Task Delete_a_range_of_days()
    {
        using var response = await DeleteAsync("?from=2026-11-03&to=2026-11-05", "2026-11-03..2026-11-05");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _host.Deletions.ShouldBe([(Utc("2026-11-02T23:00Z"), Utc("2026-11-05T23:00Z"))]);
        var week = await _host.GetJsonAsync<RangeSummaryDto>("/api/range/summary?from=2026-11-02&to=2026-11-08", Ct);
        week.ActiveMs.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11-04")]
    [InlineData("2026-11-03..2026-11-04")]
    public async Task Delete_needs_the_range_confirmed(string? confirm)
    {
        using var response = await DeleteAsync("?from=2026-11-04&to=2026-11-04", confirm);

        var errors = await ErrorsAsync(response);
        errors["confirm"]!.AsArray().Single()!.GetValue<string>().ShouldContain("2026-11-04..2026-11-04");
        _host.Deletions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("?to=2026-11-04", "from")]
    [InlineData("?from=2026-11-04", "to")]
    [InlineData("?from=4.11.2026&to=2026-11-04", "from")]
    [InlineData("?from=2026-11-05&to=2026-11-04", "to")]
    [InlineData("?from=2026-11-05&to=9999-12-31", "to")]
    public async Task Delete_rejects_a_bad_range(string query, string field)
    {
        using var response = await DeleteAsync(query, "anything");

        var errors = await ErrorsAsync(response);
        errors.ContainsKey(field).ShouldBeTrue();
        errors.ContainsKey("confirm").ShouldBeFalse();
        _host.Deletions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_without_a_body_is_a_bad_request()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/intervals?from=2026-11-04&to=2026-11-04", UriKind.Relative));

        using var response = await _host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _host.Deletions.ShouldBeEmpty();
    }
}
