# Timeglass

Timeglass is a personal activity tracker for Windows. It runs in the tray, records which
application (and optionally which window title) is in the foreground, notices when you are
away (idle, locked, asleep), and shows where your day went in a local web UI.

Everything stays on your machine: data goes into a local SQLite file, and the UI talks to a
small HTTP API that only listens on `127.0.0.1`.

> **Status:** early development. The tray app tracks and stores your day; the HTTP API and the
> real UI are still being built, so there is nothing to look at yet. See the
> [issues](https://github.com/sevenam/timeglass/issues) and milestones for what's next.
> Screenshots will follow once there is a UI worth showing.

## Repository layout

| Path                      | What it is                                                           |
| ------------------------- | -------------------------------------------------------------------- |
| `src/Timeglass.Core`      | Domain model, ports (interfaces) and the tracking engine. No I/O.    |
| `src/Timeglass.Storage`   | SQLite schema, migrations and the interval repository.               |
| `src/Timeglass.Api`       | Local HTTP API (ASP.NET Core).                                       |
| `src/Timeglass.Windows`   | Win32 adapters: foreground watcher, idle and session sources.        |
| `src/Timeglass.App`       | The tray app (`Timeglass.exe`): wires everything together.           |
| `tools/Timeglass.DevHost` | The API over demo data, for frontend work on any OS.                 |
| `tests/`                  | Unit, storage, API and Windows integration tests, plus shared fakes. |
| `web/`                    | React + TypeScript frontend (Vite).                                  |
| `docs/`                   | [Architecture overview](docs/architecture.md) and [ADRs](docs/adr/). |

There are two solutions:

- `Timeglass.slnx` is everything. It needs Windows to build, because `Timeglass.Windows` and
  `Timeglass.App` target `net10.0-windows`.
- `Timeglass.Portable.slnx` is Core, Storage, Api and their tests. It builds and tests on any
  OS, and is what the Linux CI job runs.

## Download

Each [release](https://github.com/sevenam/timeglass/releases) has two downloads for 64-bit
Windows 10 or 11. Neither needs a .NET runtime.

- **`timeglass-<version>-win-x64-setup.exe`** installs Timeglass for your user, without admin
  rights, adds it to the Start menu and starts it. From then on it keeps itself up to date
  (below). Uninstall it under _Installed apps_ in Windows Settings.
- **`timeglass-<version>-win-x64.zip`** holds a single `Timeglass.exe`: unzip it anywhere and
  run it. Nothing is installed, and it does not update itself.

Neither is code-signed yet, so SmartScreen warns the first time (choose **More info**, then
**Run anyway**). The tray menu's **About Timeglass** shows which version you have. How releases
are made is in [CONTRIBUTING.md](CONTRIBUTING.md#releases).

### Updates

An installed Timeglass looks for a newer release half a minute after it starts and once a day
after that, and downloads it in the background (usually only the parts that changed). Nothing
changes while it runs: the new version is installed the next time Timeglass starts, for example
when you sign in. Once a version is downloaded the tray menu also offers **Restart to update**.

The check is the only time Timeglass uses the network. It asks GitHub for this repository's
list of releases and sends nothing about you or what was recorded. To turn it off, set the
environment variable `DOTNET_Timeglass__CheckForUpdates` to `false` (or use the zip).

The app is installed in `%LOCALAPPDATA%\sevenam.Timeglass`. Your data is in another folder
([below](#where-data-is-stored)), so updating, reinstalling and uninstalling all leave it alone;
delete that folder yourself if you want the recorded time gone too. **Start with Windows**
registers the installed launcher, whose path stays the same across updates, and uninstalling
removes it. If it was on for an unzipped copy before you installed, the first run moves it to
the installed one.

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (the exact feature band is pinned in
  `global.json`, with `rollForward: latestFeature`).
- [Node.js 22+](https://nodejs.org/) with npm, for the frontend and for Release builds of the
  app, which embed it (`web/.nvmrc` has the version CI uses).
- Windows 10 1809 or later to build and run the full app. Linux and macOS can build and test
  the portable projects and the frontend.

## Build and test

Clone and enter the repository:

```sh
git clone https://github.com/sevenam/timeglass.git
cd timeglass
```

### .NET

On Windows, the full solution:

```sh
dotnet build Timeglass.slnx
dotnet test Timeglass.slnx
```

On Linux or macOS, the portable projects:

```sh
dotnet build Timeglass.Portable.slnx
dotnet test Timeglass.Portable.slnx
```

Warnings are errors, and code style is enforced at build time. Before pushing, check
formatting the way CI does:

```sh
dotnet format Timeglass.Portable.slnx --verify-no-changes
```

The Windows integration tests (`tests/Timeglass.Windows.IntegrationTests`) create real
top-level windows and bring them to the front, so they need an interactive Windows session
(the GitHub `windows-latest` runner has one) and only run as part of `Timeglass.slnx`. Don't
type into other windows while they run.

### Frontend

```sh
cd web
npm ci
npm run lint
npm run format:check
npm run typecheck
npm test
npm run build      # output in web/dist
```

`npm run test:coverage` runs the tests with the coverage threshold CI enforces.

[CONTRIBUTING.md](CONTRIBUTING.md) has the full list of checks CI runs, including coverage.

## Run

On Windows, run the tray app:

```sh
dotnet run --project src/Timeglass.App
```

It puts an hourglass in the notification area and tracks until you choose **Quit** from its
menu. Only one copy runs per user session; starting it again asks the running one to open the
dashboard. **Start with Windows** in the menu adds Timeglass to
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no admin needed); the check mark is read
from the registry each time, so turning Timeglass off in Task Manager's _Startup apps_ shows up
here too, and turning it back on here clears that. To keep test data out of your real database, pass another file:
`dotnet run --project src/Timeglass.App -- --Timeglass:DatabasePath=C:\temp\test.db`.

The app serves its API on `127.0.0.1` only, behind a token that changes on every start; the
tray menu's _Open dashboard_ signs the browser in. Scripts find the port and token in
`api.json` (below) and send `Authorization: Bearer <token>`:

```powershell
$api = Get-Content "$env:LOCALAPPDATA\Timeglass\api.json" | ConvertFrom-Json
Invoke-RestMethod "http://127.0.0.1:$($api.port)/api/status" -Headers @{ Authorization = "Bearer $($api.token)" }
```

### Exporting your data

The _Settings_ page has an _Export recorded time_ section: pick the days, the rows (every
interval with its window title, or one total per day and app) and CSV or JSON. The same from a
script, with `$api` as above:

```powershell
Invoke-WebRequest "http://127.0.0.1:$($api.port)/api/export?from=2026-11-01&to=2026-11-30&format=csv&granularity=daily-apps" `
  -Headers @{ Authorization = "Bearer $($api.token)" } -OutFile november.csv
```

An export follows the privacy settings as they are now: apps under `excludedApps` are left out,
also time recorded before they were excluded, and so are the titles `recordWindowTitles` and
`appsWithoutTitles` turn off. The CSV is UTF-8 and opens in Excel; text that would run as a
formula there (a title starting with `=`, `+`, `-` or `@`) gets a leading apostrophe, while the
JSON has every value as it was recorded.

The dashboard is built into the app: a Release build (`dotnet build -c Release`, which needs
Node.js) runs `npm ci` and `npm run build` in `web/` and embeds the result, and the app serves it
on the same port as the API. A Debug build embeds `web/dist` only if you built it before. For
frontend work, on any OS, start the dev host, which serves the API with a week of realistic demo
data and needs no tracker (nor a token: it only has demo data, and still listens on `127.0.0.1`
only):

```sh
dotnet run --project tools/Timeglass.DevHost
```

and in another terminal the Vite dev server:

```sh
cd web
npm run dev
```

Vite proxies `/api` to `http://127.0.0.1:5381`, where the dev host listens. Set
`TIMEGLASS_API_PORT` for both to use another port. The dev host takes a few options after
`--`: `--seed 7` for other demo days, `--workdays 10` for more of them, `--paused true` to show
tracking as paused, `--database demo.db` to keep the database (by default it is a temp
file, deleted on exit), `--now 2026-11-05T13:00:00Z --timeZone Europe/Oslo` to stop the clock,
and `--web ../../web/dist` to serve a built dashboard too, as the end-to-end tests do (`dotnet run`
starts it in its project folder, so relative paths start there).

## Where data is stored

| What      | Where                                                  |
| --------- | ------------------------------------------------------ |
| Database  | `%LOCALAPPDATA%\Timeglass\timeglass.db` (SQLite)       |
| Log files | `%LOCALAPPDATA%\Timeglass\logs\timeglass-YYYYMMDD.log` |
| Settings  | `%LOCALAPPDATA%\Timeglass\settings.json`              |
| API info  | `%LOCALAPPDATA%\Timeglass\api.json` (while running)   |

The database uses WAL mode, so you will also see `timeglass.db-wal` and `timeglass.db-shm`
next to it while the app runs. Timestamps are stored as UTC Unix milliseconds. To start over,
quit the app and delete the folder. The installer puts the program in a folder of its own
(`%LOCALAPPDATA%\sevenam.Timeglass`) and never touches this one, not even on uninstall. The
updater logs to `%LOCALAPPDATA%\velopack\`; those logs hold versions and paths, nothing recorded.

Log files roll over daily (and past 10 MB) and are deleted after seven days. The tray menu's
_Open log folder_ opens them. The level is `Information` by default; set `"logLevel": "Debug"`
in the settings to also log tracking state changes. Window titles are never written to the log.

## Settings

Most settings can be changed on the dashboard's _Settings_ page, which also exports and deletes
the recorded time of a range of days. They are stored in `settings.json`, which you can also edit
by hand.

`settings.json` is created with the defaults on first start. Edits apply while the app runs;
an edit with invalid values is logged and ignored (the previous settings stay), while invalid
settings at startup stop the app with a message that names the problem.

| Setting              | Default         | Meaning                                                                         |
| -------------------- | --------------- | ------------------------------------------------------------------------------- |
| `idleThreshold`      | `"00:05:00"`    | No input for this long counts as away (30 s to 4 h). `null` turns it off.       |
| `idleExemptApps`     | `[]`            | App keys that never go idle in front, e.g. `["vlc", "zoom"]`.                   |
| `minimumInterval`    | `"00:00:01"`    | Shorter visits to a window count towards the previous one (up to 1 min).        |
| `checkpointInterval` | `"00:00:15"`    | How often the open interval is saved, bounding loss on a crash (1 s to 10 min). |
| `recordWindowTitles` | `true`          | Store window titles. `false` stores every interval without its title.           |
| `excludedApps`       | `[]`            | App keys that are not tracked at all: while one is in front, nothing is stored. |
| `appsWithoutTitles`  | `[]`            | App keys whose time is tracked but whose window titles are never stored.        |
| `appAliases`         | `[]`            | Renames and extra apps on top of the built-in aliases, see below.               |
| `apiPort`            | `5381`          | Port of the API on 127.0.0.1 (1024 to 65535); another if taken. Needs a restart |
| `logLevel`           | `"Information"` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` or `None`.      |

An app can be in `excludedApps` or `appsWithoutTitles`, not both. Privacy settings apply to the
open interval as soon as they change: an interval of a newly excluded app is closed, and a
title that may no longer be stored is removed from it.

## Pausing and privacy

The tray menu's _Pause tracking_ pauses for 30 minutes, an hour, until midnight or until you
resume; the tray icon switches to its paused look and the tooltip says until when. Paused time is not stored at all
(it is a gap, not an interval), and neither is time in an excluded app. A pause ends when the
app restarts. See [ADR 0005](docs/adr/0005-privacy-gaps-not-intervals.md).

An alias with the key of a built-in app renames it, or adds executables to it; any other key
adds an app. For example, to call Outlook "Mail" and count Cursor as VS Code:

```json
"appAliases": [
  { "key": "outlook", "name": "Mail" },
  { "key": "vscode", "exes": ["cursor.exe"] }
]
```

## Documentation

- [Architecture overview](docs/architecture.md): layers, dependency rule, data flow and
  threading model.
- [Architecture Decision Records](docs/adr/): why things are the way they are.
- [Contributing](CONTRIBUTING.md): branches, pull requests, commit messages and the full
  local test run.

## License

[MIT](LICENSE)
