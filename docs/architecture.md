# Architecture

Timeglass is one Windows process: a tray app that watches the foreground window, turns what it
sees into time intervals, stores them in SQLite, and serves them to a React UI through a local
HTTP API. This page describes how the pieces fit together. The reasons behind the big choices
are in the [ADRs](adr/).

Parts marked _(planned)_ don't exist yet; the issue number says where they are being built.

## Projects and the dependency rule

```text
                  ┌──────────────────────┐
                  │    Timeglass.App     │  tray host, composition root (Windows)
                  └──────────┬───────────┘
        ┌────────────────────┼──────────────────────┐
        ▼                    ▼                      ▼
┌───────────────┐   ┌─────────────────┐   ┌──────────────────┐
│ Timeglass.    │   │ Timeglass.      │   │ Timeglass.Api    │
│ Windows       │   │ Storage         │   │ (ASP.NET Core)   │
│ (Win32)       │   │ (SQLite)        │   │                  │
└───────┬───────┘   └────────┬────────┘   └────────┬─────────┘
        └────────────────────┼─────────────────────┘
                             ▼
                  ┌──────────────────────┐
                  │    Timeglass.Core    │  domain, ports, tracking engine
                  └──────────────────────┘
```

| Project             | Target            | Responsibility                                                                                |
| ------------------- | ----------------- | --------------------------------------------------------------------------------------------- |
| `Timeglass.Core`    | `net10.0`         | Domain records, ports (interfaces), the tracking engine and service, app identity resolution. |
| `Timeglass.Storage` | `net10.0`         | SQLite database, migrations, `IntervalRepository` (implements `IIntervalSink`).               |
| `Timeglass.Api`     | `net10.0`         | Local HTTP API for the UI: `MapTimeglassApi()` endpoints and the OpenAPI document.            |
| `Timeglass.Windows` | `net10.0-windows` | Win32 implementations of the ports: foreground watcher, idle detector, session events.        |
| `Timeglass.App`     | `net10.0-windows` | `Timeglass.exe`: generic host, composition root, tray icon, single instance.                  |
| `Timeglass.DevHost` | `net10.0`         | `tools/`: the API over demo data, for frontend work and end-to-end tests. Not shipped.        |
| `web/`              | Node / browser    | React + TypeScript UI, built with Vite and embedded in `Timeglass.exe`, which serves it.      |

The rule:

1. **Core depends on nothing of ours.** It only knows its own types and the ports it defines.
   Everything that touches the outside world (OS, disk, network) is behind a port.
2. **Storage, Api and Windows depend only on Core**, never on each other.
3. **Only App knows all of them.** It is the composition root: it creates the concrete
   adapters and hands them to Core.
4. **Portable code never references Windows-only assemblies.** Core, Storage and Api must build
   and test on Linux (`Timeglass.Portable.slnx`), so CI can run most tests on the fast runner.

Rules 1 and 4 are enforced by `tests/Timeglass.Core.Tests/ArchitectureTests.cs`.

### Time

Core never reads the clock directly. All time goes through `TimeProvider`, and
`BannedSymbols.txt` makes `DateTime.Now`, `DateTimeOffset.UtcNow`, `Task.Delay(TimeSpan)` and
`System.Threading.Timer` build errors in Core. Tests use a fake time provider to run hours of
tracking in milliseconds.

## Data flow

```text
 Win32                       Core                                   Storage            UI
─────────               ─────────────────────────────────         ──────────        ───────
WinEvent hooks ─┐
+ polling       │  ForegroundChanged
                ├───────────────────►┌─────────┐   ┌───────────┐  IntervalChange   ┌────────┐
GetLastInput ───┤  Tick (1 s timer)  │ channel ├──►│ Tracking  ├──────────────────►│ SQLite │
Info            │                    └─────────┘   │ Engine    │  (upsert by id)   │  file  │
                │  SessionChanged                  └───────────┘                   └───┬────┘
SystemEvents ───┘                                                                      │
                                                                           queries     ▼
                                                    React UI ◄── HTTP (127.0.0.1) ── Api
```

1. **OS events.** `Timeglass.Windows` reports three things through Core's ports:
   - `IForegroundWindowSource`: the foreground window or its title changed. Implemented by
     `Win32ForegroundWindowSource` with `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`,
     `EVENT_OBJECT_NAMECHANGE`) plus polling every 2 s to catch missed events. Each event carries
     a `WindowSnapshot` (process id, exe path and name, title, file description).
     `ProcessResolver` fills in the process details and caches them by process id and start
     time. UWP apps, whose windows belong to `ApplicationFrameHost.exe`, are resolved to the
     app's own process through the frame's `CoreWindow` child. Processes the user may not query
     (elevated or protected) get the exe name `unknown-elevated` and show up as "Elevated app"
     instead of failing.
   - `IIdleSource`: time of the last keyboard or mouse input.
   - `ISessionEventSource`: lock, unlock, suspend, resume, shutdown. `Win32SessionEventSource`
     listens to `SystemEvents`: a remote or console disconnect counts as a lock and a reconnect
     as an unlock; logoff and shutdown (`SessionEnding`) become `ShuttingDown`. It must be
     disposed, since the static `SystemEvents` otherwise keeps its handlers alive.
2. **Engine.** `TrackingService` turns those into `TrackingEvent`s (`ForegroundChanged`, `Tick`,
   `SessionChanged`, `TrackingStarted`, `TrackingStopped`) and feeds them to `TrackingEngine`,
   a pure state machine with no clock, I/O or locks. The engine:
   - maps each window to an `AppIdentity` through `IAppIdentityResolver` (the alias table in
     `Apps/AppAliases.json` merges e.g. classic and new Outlook);
   - ignores windows that stay in front for less than a second (Alt-Tab flicker), backdating
     the switch once it sticks so no time is lost;
   - closes the active interval at the last input time after 5 minutes without input, and on
     lock or sleep, and opens an `Away` interval;
   - applies the privacy settings: no interval at all while an app in `excludedApps` is in
     front, and no title for apps in `appsWithoutTitles` or when `recordWindowTitles` is off
     ([ADR 0005](adr/0005-privacy-gaps-not-intervals.md));
   - emits `IntervalChange`s (`Opened`, `Updated`, `Closed`), each carrying the full
     `ActivityInterval`. The open interval gets an `Updated` checkpoint every 15 s.

   The engine's XML docs list every rule in detail.

3. **Storage.** `IntervalRepository` implements `IIntervalSink` and upserts each change by
   interval id into the `intervals` table. Because every change carries the whole interval, a
   lost or failed write is repaired by the next checkpoint, and a crash loses at most one
   checkpoint interval. On startup, `TimeglassDatabase.Open` applies migrations and closes any
   interval left open by a crash at its last checkpoint. A unique index allows at most one
   open interval. `TrackingService.DeleteRangeAsync` deletes a time range (for the UI, through
   the API): it runs in order with the tracking events, closes the open interval first if the
   range reaches it, and the repository cuts intervals at the range edges with `secure_delete`
   on.
4. **API.** An ASP.NET Core minimal API that serves the reporting queries as JSON (below),
   hosted in the tray app on `127.0.0.1` behind Host and Origin checks and a per-run access
   token (see _API security_).
5. **UI** _(views planned, #31 onward)_. React app served by the same process and port as the
   API (see _Serving the dashboard_). In development, Vite proxies `/api` to the backend.

### Storage model

Intervals, not samples (see [ADR 0002](adr/0002-sqlite-with-stored-intervals.md)). The schema
lives in numbered scripts in `src/Timeglass.Storage/Migrations/`, embedded in the assembly and
applied in order by `Migrator`.

| Table       | Columns (main)                                                                                                                   |
| ----------- | -------------------------------------------------------------------------------------------------------------------------------- |
| `apps`      | `id`, `key` (unique, e.g. `outlook`), `display_name`                                                                             |
| `intervals` | `id` (engine GUID), `start_utc`, `end_utc` (Unix ms), `kind` (0 active, 1 away), `app_id`, `exe_path`, `window_title`, `is_open` |

Intervals never overlap and `end_utc >= start_utc`. They are contiguous within a tracking run;
paused time and time in an excluded app are gaps. All times are UTC; converting to local
days (including DST) is a reporting concern, below.

To change the schema, add the next numbered `.sql` file. Never edit a script that has shipped.

### Reporting

`Core/Reporting` turns stored intervals into answers; `IntervalRepository` runs the range query
and hands the rows over.

- **Day summary** (`DaySummaries.Build`, `IntervalRepository.GetDaySummaryAsync`). For a local
  date and time zone: active and away totals, first and last activity, and per app the active
  time, its exact share and a whole percentage. A day runs from its local start to the next
  day's start, so it is 23 or 25 hours long on DST days. Intervals crossing the edges are
  clipped, and an open interval runs until the `now` the caller passes (Core never reads the
  clock). Away time is not part of the percentages, and gaps (paused, excluded, not running)
  are in neither total. Percentages use the largest remainder method, so they add up to
  exactly 100.
- **Day start.** A day can start later than midnight (up to 05:59, e.g. 04:00 for late
  workers) through the `dayStart` parameter. If that time is skipped by a DST change the day
  starts at the jump; if it happens twice, at the first. There is no setting for it yet; the
  API and UI will add one when they need it.
- **Timeline** (`Timelines.ForDay`, `IntervalRepository.GetTimelineAsync`). The day's active
  and away time as blocks in time order, clipped to the day. The tracker starts a new interval
  on every title change, so intervals of the same app (or away time) that touch or are less
  than 5 seconds apart are merged into one block; a block's `Duration` leaves out the merged
  gaps.
- **App drill-down** (`TitleBreakdowns`, `IntervalRepository.GetTitleBreakdownAsync`). One
  app's time over a day or a range of days, grouped by exact window title: the top N titles
  (10 by default) and the rest summed as "other", with percentages that add up to 100. Time
  without a recorded title is its own group with a `null` title.
- **Range summary** (`RangeSummaries`, `IntervalRepository.GetRangeSummaryAsync`). For the
  week view: one entry per day (every day, also empty ones) with active and away totals and
  the day's top apps, plus totals per app over the whole range. Each day is built by the day
  summary, so the two always agree. A range is at most 366 days.
- **Range query.** Intervals never overlap, so of the ones starting before a range only the
  latest can reach into it. `GetRangeAsync` starts its index scan there instead of at the
  first interval ever stored, which keeps a day query at a few milliseconds with a year of
  history (about 500,000 intervals; `ReportingPerformanceTests` checks it stays under 100 ms).

### API

`Timeglass.Api` is a library: a host calls `services.AddTimeglassApi()` and
`app.MapTimeglassApi()`, and registers an `IReportingQueries` (Core port, implemented by
`IntervalRepository`), an `ITrackingState` (whether tracking is paused, and pausing it), an
`ISettingsStore` (the user's settings), an `IDataDeletion` (deleting recorded time) and,
optionally, an `IAutostart` (_Start with Windows_). It only knows Core, so the tray app and the
dev host (below) host the same endpoints.

| Endpoint                             | Returns                                                                             |
| ------------------------------------ | ----------------------------------------------------------------------------------- |
| `GET /api/days/{date}/summary`       | Day summary: active and away totals, first and last activity, per app.              |
| `GET /api/days/{date}/timeline`      | Day timeline: merged active and away blocks.                                        |
| `GET /api/days/{date}/apps/{appKey}` | One app's time on that day by window title: top 10 and the rest.                    |
| `GET /api/range/summary?from=&to=`   | Range summary for the week view, at most 366 days.                                  |
| `GET /api/status`                    | `active`, `away`, `untracked` or `paused`, the current app and since when.          |
| `GET /api/settings`                  | The settings the dashboard edits (#36), and whether the app starts with Windows.    |
| `PUT /api/settings`                  | Validates and saves them; 400 with the errors per field, nothing saved.             |
| `POST /api/pause`, `/api/resume`     | Pauses (`{"duration": "oneHour"}`, as in the tray menu) or resumes; the new status. |
| `DELETE /api/intervals?from=&to=`    | Deletes the recorded time of those days. Body `{"confirm": "from..to"}`.            |
| `GET /api/export?from=&to=`          | The recorded time of those days as a JSON or CSV file (see _Export_).               |

The settings endpoints validate with the settings model's own rules (its data annotations and
`TimeglassSettings.Validate`, as when `settings.json` loads), and keep the settings the dashboard
doesn't show (port, log level, timing). In the tray app, `FileSettingsStore` writes
`settings.json` atomically and reloads the configuration at once, so the new settings are in
effect when the request returns; `RegistryAutostart` is the tray menu's _Start with Windows_ value.
Deleting goes through `TrackingService.DeleteRangeAsync`, which cuts the open interval too, so
it isn't written back at its next checkpoint. The confirmation in the body must repeat the range,
so a request with the wrong query string deletes nothing.

- Days are local days in the `TimeProvider`'s local zone, starting at midnight. Every time in
  a response carries that zone's offset (`2026-11-05T08:12:31+01:00`), so the dashboard reads
  the clock time off the string.
- Dates are ISO dates (`yyyy-MM-dd`). A missing or invalid date, a range that ends before it
  starts or one longer than 366 days gets a 400 Problem Details response
  (`application/problem+json`, with the field under `errors`).
- Responses are API records (`ApiModels.cs`), not the Core ones: camelCase, durations as whole
  milliseconds (`activeMs`), kinds as lowercase strings, numbers never as strings.
- The OpenAPI document is served at `/openapi/v1.json` and committed as
  `src/Timeglass.Api/openapi.json` for the frontend's typed client (#31).
  `OpenApiDocumentTests` fails when the two differ; run the API tests with
  `TIMEGLASS_UPDATE_OPENAPI=1` to regenerate the file, and commit it.
- `Timeglass.Api.Tests` hosts the endpoints on `TestServer` over a real temp database. The API
  has no entry point of its own, so there is no `WebApplicationFactory` for it (the dev host
  has one, below).

### Export

`GET /api/export?from=&to=&format=json|csv&granularity=intervals|daily-apps` gets the data out
for spreadsheets, scripts and timesheets (#39). `format` defaults to `json` and `granularity` to
`intervals`.

- **Rows.** `intervals` is one row per stored interval in time order, clipped to the range, the
  open one running until now. `daily-apps` is one row per local day and app with the app's
  active time, in the day summary's order and agreeing with it; days and apps without time have
  no row. The executable path is in neither.
- **Streamed.** `IntervalRepository.StreamRangeAsync` reads the range query row by row
  (unbuffered), `Core/Reporting/Exports` turns the rows into export rows one at a time, and the
  response is written as they come. Nothing holds the whole range, so there is no limit on its
  length. The per-day totals hand out a day as soon as an interval starting on a later day
  arrives, which relies on the query's order by start.
- **Privacy.** The export applies the privacy settings as they are _now_ (`ExportPrivacy`), not
  as they were when the time was recorded: time in an app under `excludedApps` is left out, and
  titles are left out for `appsWithoutTitles` and everywhere while `recordWindowTitles` is off.
  The database may still hold such rows from before the setting changed; the export doesn't
  hand them out. Deleted time is gone from the database, so it can't be exported.
- **JSON.** One object: `from`, `to`, `granularity` and either `intervals` or `dailyApps` (the
  other is `null`), in the API's usual shapes (`ExportModels.cs`). The list is an
  `IAsyncEnumerable`, which the serializer writes as it reads.
- **CSV.** RFC 4180: a header line, CRLF line ends, fields with a comma, quote, line break or
  outer space in quotes with quotes doubled. UTF-8 with a byte order mark, which Excel needs.
  Columns are `date,start,end,duration_ms,kind,app_key,app,title` (times with the zone's
  offset; `date` is the local day the interval starts on) or `date,app_key,app,duration_ms`.
- **CSV injection.** Titles and app names are text other programs choose, and a spreadsheet
  runs a cell starting with `=`, `+`, `-` or `@` as a formula. `Csv` puts an apostrophe in front
  of a field starting with one of those (or a tab, a line break or their full-width forms), so
  it shows as text. The JSON has the exact value.
- **A file.** Both formats are sent with `Content-Disposition: attachment` and a name like
  `timeglass-intervals-2026-11-01_2026-11-30.csv`, and `Cache-Control: no-store`. The
  dashboard's _Export_ button is a plain link, so the browser saves the stream to disk; the
  cookie and the same-origin checks below apply to it like to any other request.

### API security

Window titles are sensitive: no other machine and no website may read them (#29).
`TimeglassApiServer.Build` is what the tray app hosts, and `ApiSecurity` does the checks:

- **Loopback only.** Kestrel listens on `127.0.0.1` and nothing else, set in code so no
  argument, environment variable or `appsettings.json` can change it. No `Server` header.
- **Port.** The `ApiPort` setting (default 5381), or a free port from the OS if something
  else has it (`LoopbackPort.Choose`). A changed setting applies on the next start.
- **Host header.** Must be `127.0.0.1:<port>` or `localhost:<port>`, else 400. A website that
  points its own name at 127.0.0.1 (DNS rebinding) sends its own name and is turned away.
- **No CORS.** A browser never lets another page read an answer. On top of that, a request
  with an `Origin` other than the API's own, or with `Sec-Fetch-Site` other than
  `same-origin` or `none`, gets 403, so other pages can't trigger requests either (this also
  covers other ports on localhost, which count as the same site for cookies).
- **Access token.** A random 256-bit token per run (`ApiAccessToken`). _Open dashboard_ opens
  `/auth?token=…`, which sets it as the `timeglass_token` cookie (`HttpOnly`,
  `SameSite=Strict`) and redirects to `/`, so page scripts never see it. Every other request
  needs that cookie or an `Authorization: Bearer` header, else 401. After a restart the old
  cookie no longer works; the dashboard is opened from the tray again.
- **Runtime file.** `%LOCALAPPDATA%\Timeglass\api.json` holds the port, the token and the
  process id while the app runs (`ApiRuntimeFile`), so scripts can call the API with the
  bearer header. It is in the user's profile, readable by the same people who can read the
  database, and deleted on exit.
- If the server can't start, the error is logged and the app keeps tracking; only the
  dashboard is unavailable.
- `SecurityTests` covers each check on `TestServer`, `ApiServerTests` the real Kestrel binding.

### Serving the dashboard

The tray app is the only thing to install and run (#30), so it serves the dashboard itself:

- **Embedded.** `src/Timeglass.App/Dashboard.targets` embeds `web/dist` in `Timeglass.exe`
  under `wwwroot/`, with the manifest `ManifestEmbeddedFileProvider` reads (`DashboardFiles`).
  Release builds run `npm ci` and `npm run build` in `web/` first, when its sources changed,
  and fail if there is still no `web/dist/index.html`. `-p:TimeglassBuildWeb=false` skips npm
  and embeds the `web/dist` that is there: CI's Windows job puts the `web-dist` artifact there
  instead of building the frontend twice. Debug builds embed `web/dist` only if it exists;
  without it the app logs that it has no dashboard and serves the API alone.
- **Routes.** `TimeglassDashboard.MapTimeglassDashboard` serves the files after the security
  checks, so the dashboard needs the token like the API. Any other `GET` without a file
  extension gets `index.html` (SPA fallback), except under `/api` and `/openapi`, where an
  unknown path is a 404 Problem Details answer. A missing file (`/assets/old.js`) is a 404.
- **Caching.** Files under `/assets/` have a content hash in their name and are sent with
  `Cache-Control: public, max-age=31536000, immutable`; `index.html` and the rest with
  `no-cache`, so a new version shows on the next load.
- `DashboardTests` covers these on `TestServer`; the App tests check that a Release build has
  the dashboard embedded and that the running app serves `/`.

### Dashboard data layer

`web/src/api` is how the dashboard talks to the API (#31):

- **Types.** `schema.d.ts` is generated from `src/Timeglass.Api/openapi.json` by
  `openapi-typescript` (`npm run generate:api`), and `openapi-fetch` checks paths, parameters
  and responses against it. CI's `npm run check:api` fails when the file is out of date.
- **Fetching.** TanStack Query caches answers by `queryKeys` (`queries.ts`). Data is fresh for
  15 seconds and refetched when the window regains focus, so today's numbers keep up.
  Network errors and 5xx are retried twice; 4xx are not.
- **Errors.** Every non-success answer becomes an `ApiError` with the status and the Problem
  Details body; `errorMessage` turns any error into a sentence for the user.
- **401.** `ApiProvider` watches the query cache. When any query gets a 401 (the cookie is
  missing or from an earlier run), it replaces the dashboard with a note to open it from the
  tray icon, which signs the browser in again.
- **Tests.** MSW serves demo fixtures shaped like the dev host's `DemoData`
  (`web/src/test/fixtures.ts`, `handlers.ts`) to every test, typed from the schema so they
  break the typecheck when the API changes.

### Dashboard screens

The dashboard has a few screens, so `web/src/router.ts` is a small router on the history API
rather than a routing library. `<Link>` follows a plain click in place and leaves a modified
click (new tab) to the browser; the back button works, and the SPA fallback above makes every
route a deep link.

| Path                       | Screen                                                          |
| -------------------------- | --------------------------------------------------------------- |
| `/`                        | Today (`DayView`): totals, the timeline and the app share list  |
| `/day/{date}`              | The same for another day                                        |
| `/day/{date}/app/{appKey}` | App drill-down (`AppView`): the app's time by window title      |
| `/week`, `/week/{date}`    | Week overview (`WeekView`): this week, or the week of a date    |
| `/settings`                | Settings and privacy (`SettingsView`), export, deleting a range |

The header shows the tracker's state, a _Pause_ menu with the tray's choices (or _Resume_ while
paused) and the link to the settings. The settings page edits what used to need `settings.json`:
away detection, apps that never go idle, window titles, excluded apps, apps without titles, app
renames and _Start with Windows_. It checks the form before saving and then shows the API's
errors next to their fields. _Export recorded time_ (`ExportData`) picks a range, the rows and
the format, and links to `/api/export`. Deleting a range asks first, in a dialog that says how much active
time the range holds; focus starts on _Cancel_.

The drill-down lists the API's top 10 titles with time and share of the app's time, and the
rest as one "other" row. Its search box filters those titles in the browser; the titles folded
into "other" aren't sent, so it says how many it can't search. Time without a title shows as
"No title recorded", and when an app has no titles at all (title recording off, or the app
excluded) it says so instead of showing a one-row list.

### Dev host

`tools/Timeglass.DevHost` runs the API on any OS without the tracker, for frontend work and
end-to-end tests. It is never shipped.

- It listens on `127.0.0.1` only, on `TIMEGLASS_API_PORT` (the variable the Vite proxy reads)
  or 5381, so `npm run dev` in `web/` finds it with no setup.
- It opens a new database in a temp folder, fills it with `DemoData` before taking requests,
  and deletes it on exit. `--database <file>` keeps the file instead; an existing file is used
  as it is.
- `DemoData.Generate(now, zone, workdays, seed)` writes today up to `now` (the last interval
  still open) and the five workdays before it: Outlook, Teams, VS Code, Edge and Windows
  Terminal, a 09:30 standup, a lunch break, short idle breaks, title changes a few seconds
  apart (merged by the timeline) and a Tuesday evening that runs past midnight. It is
  deterministic: each date gets its own random sequence from the seed and the date, so a day
  looks the same whichever day is today.
- `--now 2026-11-05T13:00:00Z` stops the clock there and `--timeZone Europe/Oslo` sets the
  zone days are counted in, so every run sees the same data and the same today. `--web <dir>`
  also serves a built dashboard at `/`, as the tray app does. The end-to-end tests use all three.
- Tracking is never paused, unless `--paused true`. Pausing and resuming through the API only
  change the state it reports; settings and _Start with Windows_ are kept in memory.
- `Timeglass.Api.Tests` checks the demo data's shape (`DemoDataTests`) and starts the host
  through `WebApplicationFactory` with a fake clock (`DevHostTests`).

## The host

`Timeglass.App` builds a generic host (`Microsoft.Extensions.Hosting`) for dependency injection,
configuration, logging and lifetime. `TimeglassHost.AddTimeglass` is the composition root, the
only code that knows every project. The container validates the whole graph when it is built,
so a missing registration fails at startup and in `Timeglass.App.Tests`.

Startup, in `Program.Main` on the STA main thread:

0. **Installer hooks.** `VelopackApp.Run()` comes first: the setup program and the updater
   start the exe with their own arguments to run a hook, and expect it to exit at once (see
   _Installing and updating_). A normal start falls through.
1. **Single instance.** A named mutex in the session's `Local\` namespace. If another instance
   holds it, this launch sends `activate` over a named pipe (current user only, named per
   session) and exits; the running instance opens the dashboard. The first instance then
   installs a downloaded update, if one is waiting, and exits for the updater to restart it.
2. **Host start.** Resolving the services opens the database (migrations, crash recovery) and
   creates the Windows sources. The session source is resolved on the main thread first, so
   `SystemEvents` delivers its events through the tray's message loop. `TrackingController`, a
   hosted service, starts the `TrackingService` run.
3. **Tray.** `TrayApplicationContext` shows a `NotifyIcon` with _Open dashboard_,
   _Pause tracking_ (for 30 minutes, an hour, until midnight or until resumed; _Resume tracking_
   while paused), _Start with Windows_, _Settings_ (#22), _Open log folder_, _About_,
   _Restart to update_ (only once a new version is downloaded) and _Quit_, and
   `Application.Run` pumps messages until Quit or the host stops. The tooltip shows today's
   active time, refreshed every 30 s. _Start with Windows_ is a value in the current user's
   `Run` key (`AutostartRegistration` in `Timeglass.Windows`), which also honours the
   _disabled_ flag Task Manager writes under `StartupApproved\Run`. It is not in
   `settings.json`: the check mark is read from the registry each time the menu opens, so it
   can't drift. The value holds `TimeglassHost.AutostartExecutable`: the running exe, or the
   installer's launcher for an installed copy.
4. **Shutdown.** After the message loop ends, the host is stopped and disposed off the UI
   thread: tracking closes the open interval and flushes, and the repository drains its queue.
   On logoff or shutdown the same flush already happened in the `ShuttingDown` handler, since
   Windows may end the process before the message loop returns.

Pausing ends the current tracking run the same way (close and flush); resuming starts a new one,
so paused time is not stored. `TrackingController` resumes a timed pause by itself, checking the
wall clock at least every 30 s so the pause ends on time after sleep. A pause does not survive
a restart.
`ApiServer`, a hosted service started after tracking and stopped before it, runs the API
(see _API security_) with the app's `IntervalRepository`, `TrackingController` (as
`ITrackingState`) and clock, and writes the runtime file.

### Installing and updating

Releases carry a setup exe built with Velopack (#40,
[ADR 0006](adr/0006-velopack-installer-and-updates.md)). `src/Timeglass.App/Updates` is all the
app knows about it; Core, Storage and Api don't.

```text
%LOCALAPPDATA%\sevenam.Timeglass\      the install folder, named after the package id
  Timeglass.exe                        launcher: starts current\Timeglass.exe; its path never changes
  Update.exe                           Velopack's updater
  current\                             the app, replaced as a whole by an update
  packages\                            downloaded versions
%LOCALAPPDATA%\Timeglass\              database, settings, logs: never touched by setup, update or uninstall
```

- **Installed or not.** Only a copy the setup program installed updates itself
  (`IAppUpdater.IsInstalled`). The zip's exe and a build from source run the same code and do
  nothing: no check, no network.
- **Check and download.** `UpdateService`, the last hosted service, asks 30 s after start and
  then every 24 h, measured on the wall clock it looks at hourly, so a check that came due
  during sleep runs after waking. A failed check (offline) is logged and retried at the next
  look. `VelopackUpdater` asks GitHub for the repository's releases, reads
  `releases.win.json` from them and downloads the delta packages from the installed version to
  the newest, or the full package when the deltas can't be used. A stable build
  ignores pre-releases; a pre-release build takes them. The request carries no data about the
  user. `Timeglass:CheckForUpdates=false` (host option) turns it off.
- **Install at a start.** Downloading changes nothing that runs. On the next start,
  `InstalledApp.ApplyPendingUpdate` hands the download to `Update.exe` and the process exits
  before the host is built; the updater swaps `current\` and starts the app again, which takes
  a few seconds. It runs after the single instance check, so a second launch only opens the
  dashboard and never pulls the files from under the tracker. A process the updater started
  doesn't ask again (`OnRestarted`), so an update that fails to install can't loop. The tray's
  _Restart to update_ starts the updater and then quits through the normal shutdown, so the
  open interval is closed and stored first.
- **Autostart.** `InstalledApp.StableLauncher` maps `current\Timeglass.exe` to the launcher
  one folder up when `Update.exe` is next to it, and that is what _Start with Windows_
  registers. On the first run after setup (`OnFirstRun`), a value that starts another copy is
  pointed at the launcher (`AutostartRegistration.Adopt`, which keeps Task Manager's _off_
  flag). On uninstall the value is removed if it starts this copy. The recorded data is left
  in place.
- **Packaging.** CI's _Package_ job publishes twice: the single-file exe for the zip, and a
  publish with one file per assembly that `vpk pack` turns into the setup exe, the full
  package, the delta against the previous release (tag builds only) and the feed. `release.yml`
  attaches them all to the GitHub Release, which is the feed.
- **Tests.** `UpdateServiceTests` runs the schedule on a fake clock against a fake updater,
  `InstalledAppTests` covers the launcher path, the start-up rule and the hooks, and the
  composition test checks the real `VelopackUpdater` reports "not installed" in the test host.
  Velopack's own download and install are not exercised by the suite.

### Logging and crashes

Code logs through `Microsoft.Extensions.Logging` only. The host replaces the default providers
with a rolling file (Serilog's file sink, in `%LOCALAPPDATA%\Timeglass\logs`, daily files kept
seven days) and the debugger output. `TimeglassOptions.LogLevel` sets the file's level through
a level switch, so a configuration reload changes it without a restart. Framework categories
(`Microsoft.*`, `System.*`) are kept at Warning.

Levels: tracking state changes (interval opened or closed, with app and kind) at Debug,
checkpoints at Trace, recoverable failures at Warning or Error. **Window titles are never
logged**, at any level: log files end up attached to bug reports.

`CrashHandler` catches what nothing else did. A UI thread exception (`Application.ThreadException`)
is logged as Critical and the app quits the normal way, so the open interval is closed and
stored. An unobserved task exception is logged and marked observed. An exception on another
thread (`AppDomain.UnhandledException`) is logged, but the process ends regardless; the next
start closes the interval it left open. The file is written through on every entry, so the
last entry survives the crash.

## Threading model

```text
 hook thread (Win32 msg loop) ─┐
 SystemEvents thread ──────────┼─► Channel<TrackingEvent> ─► engine reader ─► Channel<writes> ─► SQLite writer
 TimeProvider timer (ticks) ───┘      (many writers)          (single task)      (many writers)    (single task)
                                                                                                       │
 Kestrel request threads ─────────────────────────────── own pooled connections, WAL reads ◄──────────┘
```

- **Ports may raise events on any thread.** Handlers do nothing but write to an unbounded
  `Channel` (`TrackingService`), so a slow consumer never blocks a Win32 callback.
- **One reader runs the engine.** Everything after the channel is sequential, so the engine
  needs no locks and its behaviour is deterministic for a given event order.
- **The foreground watcher owns a dedicated thread** that installs the hooks, runs the message
  loop Windows delivers the callbacks on, and runs the polling timer. Its change detection
  needs no locks either.
- **One writer touches the database for tracking.** `IntervalRepository.ApplyAsync` only
  queues; a background task writes whatever has queued up in one transaction. `FlushAsync`
  waits for everything queued so far. The tracker never waits on the disk.
- **Readers use their own connections.** SQLite runs in WAL mode, so API queries read while
  the writer writes.
- **Shutdown.** On `ShuttingDown`, the session handler blocks for up to 5 s while the open
  interval is closed and flushed, because Windows may end the process as soon as the handler
  returns. On normal exit, `RunAsync` drains the channel, closes the open interval and flushes.

## Testing

| Layer            | Project                                             | Runs on        |
| ---------------- | --------------------------------------------------- | -------------- |
| Engine and rules | `Timeglass.Core.Tests` (incl. property tests)       | Linux, Windows |
| SQLite           | `Timeglass.Storage.Tests` (real temp files)         | Linux, Windows |
| API, dev host    | `Timeglass.Api.Tests` (in-memory server, demo data) | Linux, Windows |
| Win32 adapters   | `Timeglass.Windows.IntegrationTests` (real windows) | Windows only   |
| Host and wiring  | `Timeglass.App.Tests` (DI graph, single instance)   | Windows only   |
| Frontend         | `web/src/**/*.test.tsx` (Vitest, MSW)               | Linux          |
| End to end       | `web/e2e/*.spec.ts` (Playwright, Chromium)          | Linux          |

The end-to-end tests (#37) run the built dashboard against the dev host, with the clock stopped
at Thursday 5 November 2026, 14:00 in Oslo, so the demo data is the same on every run. They
check what only the whole stack shows: the day's percentages match the API, the date bar, the
app drill-down, saving a setting and pausing. Each test that changes the dev host's state
(settings, pause) puts it back, since all tests share one host and run one at a time.

Shared fakes for the ports live in `tests/Timeglass.Testing`. Coverage thresholds:
Core 90 %, Storage 80 %, Api 80 % (lines), web 70 % (lines).
