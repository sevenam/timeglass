# 0006: Velopack for the installer and updates

- **Status:** Accepted
- **Date:** 2026-11-16

## Context

Releases were a zip with one `Timeglass.exe` (#38). To get a new version the user had to
download and unzip it again, and _Start with Windows_ pointed at wherever they had put it. The
app should install once and stay up to date (#40), without admin rights, without a server of
ours, and without weakening two promises: the tracker loses no time, and nothing about the user
leaves the machine.

## Decision

**Package with [Velopack](https://velopack.io), with GitHub Releases as the update feed.**

- **Per user.** The setup exe installs into `%LOCALAPPDATA%\sevenam.Timeglass` without
  elevation. The package id is not `Timeglass`, because `%LOCALAPPDATA%\Timeglass` holds the
  database, settings and logs, and the installer replaces and removes its folder as a whole.
- **Feed.** `release.yml` attaches the full package, the delta from the previous release and
  `releases.win.json` to the GitHub Release. The app reads them anonymously; there is no update
  server and no account.
- **Download in the background, install at a start.** The app checks shortly after start and
  daily, and downloads what it finds. It never replaces its files while it tracks: the next
  start hands the download to the updater before tracking begins, and _Restart to update_ in
  the tray does the same through the normal exit, which stores the open interval first.
  Velopack's own apply-on-startup is off, because it also runs for a second launch (which
  should only open the dashboard) and would end the running tracker without that exit.
- **Two publishes.** The zip stays a single compressed exe. The installer is built from a
  publish with one file per assembly, so a delta only carries the assemblies that changed.
- **The zip stays**, for people who want no installer. It does not update itself.

## Consequences

- The app now makes network requests: one to GitHub's release list per check, plus downloads.
  They carry nothing about the user or the data. It can be turned off (`CheckForUpdates`), and
  a copy that was not installed never checks.
- The package id and the asset names are a contract with every installed copy. Renaming them,
  or deleting a release's assets, strands those copies on their version.
- `vpk` and the `Velopack` package must stay on the same version, and builds need a version of
  at least `0.0.1`, so tagless builds are `0.1.0-alpha.0.N`.
- Still unsigned: SmartScreen warns on the setup exe as it did on the zip. Velopack can sign
  when there is a certificate.
- A release is about three times as large as before (zip, setup exe, full package).
- The update itself is Velopack's code and is not covered by our automated tests; our schedule,
  the start-up rule and the autostart path are. A change to packaging needs a manual
  install-and-update check.
