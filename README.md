# Resonance

A Windows desktop local-music player built with .NET 8 and WPF. Index your own audio files, search the library, create/import playlists, and match provider metadata to local recordings. **Resonance never downloads provider audio.** Unmatched entries remain references; opening one uses an external application/browser. A preview URL in imported metadata is not an in-app streaming feature.

The native application is in `Resonance.App`; there is no web server, npm build, or Cloudflare deployment.

## Build and run on Windows

Use a supported **Windows x64** installation (Windows 11 recommended) and the **.NET 8 SDK** from <https://dotnet.microsoft.com/download/dotnet/8.0>. The runtime alone cannot build this source. If using Visual Studio 2022 (17.8 or newer), select **.NET desktop development** and the .NET 8 SDK. The standalone .NET SDK installer for Windows includes the WPF build targets; there is no separate `dotnet workload install wpf` command.

Extract the source ZIP, then open PowerShell in the folder containing `Resonance.sln`:

```powershell
dotnet --list-sdks
dotnet restore .\Resonance.sln
dotnet build .\Resonance.sln -c Release
dotnet test .\Resonance.Tests\Resonance.Tests.csproj -c Release
dotnet run --project .\Resonance.App\Resonance.App.csproj -c Release
```

If `dotnet` is not recognized, install the SDK and open a new terminal. The first restore needs internet access to NuGet. Do not run Resonance as administrator. macOS/Linux can run the Core/Data tests and cross-compile, but **cannot launch WPF**.

## Publish a runnable Windows release

```powershell
dotnet publish .\Resonance.App\Resonance.App.csproj -c Release -r win-x64 -p:PublishProfile=Release
.\artifacts\publish\win-x64\Resonance.exe
```

The checked-in `Release.pubxml` produces a **self-contained**, untrimmed, multi-file build: recipients do not need to install .NET. Keep **all files in the publish folder together**, including native SQLite/NAudio/runtime dependencies. Do not copy just `Resonance.exe`. The ICO is embedded through `ApplicationIcon` in the project file; `app.manifest` separately configures asInvoker, DPI and long-path behavior (Win32 manifests have no application-icon element).

For build + tests + ZIP + SHA-256 in one step:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Windows.ps1
```

Review the script before running it. This execution-policy override affects only that process; it does not change machine policy. Output: `artifacts\Resonance-win-x64.zip` and `.zip.sha256`. Extract the entire ZIP and double-click `Resonance.exe`. The release is **unsigned**, not an MSI installer; Windows may show a publisher warning. Verify the origin and checksum rather than disabling SmartScreen globally. Rebuild self-contained releases when .NET security updates ship. .NET 8 support ends in November 2026; plan a supported-framework upgrade separately.

## First use

1. Open **Library**, choose **Add folder**, then let the scan finish. MP3, WAV, FLAC, M4A, AAC, WMA, AIFF/AIF and OGG are indexed; actual playback depends on available decoders. NAudio/WASAPI is used first, with Windows MediaPlayer fallback.
2. Select a track and use the play control. The bottom bar provides playback, seek, volume and sequential/repeat/shuffle modes; the queue is available in the shell.
3. Open **Import**, paste a provider URL or `Artist - Title` lines, or choose an M3U/M3U8/PLS/TXT file. Resolve, review the matches, select entries and save. Local imports need no provider account.
4. Use **Playlists** to play matched items, open references, re-sync a source, or delete a playlist. Removing a library folder or playlist does not delete your audio files.
5. In **Settings**, choose System/Light/Dark, reduced motion and provider configuration. Queue/current track, mode, volume, folders, appearance and window geometry persist. Restoring a session does **not** automatically start playback or restore elapsed playback time.

Imports are capped at 4 MiB and 20,000 logical lines/items; a final line separator counts toward the line guard. Relative audio paths must stay within the playlist folder. Absolute paths can index supported audio files selected through an import. Symlinks/reparse points are rejected. M3U EXTINF and PLS Title labels are used; EXTINF/PLS duration fields are not currently imported. Missing or unreadable files are reported rather than executed. A failed/offline folder enumeration is not authority to delete its database entries.

## Provider setup

Provider access, quotas, permitted users and API policies can change. Resonance cannot bypass access restrictions; imports require your own authorized credentials and live provider testing.

- **Spotify:** create an app in <https://developer.spotify.com/dashboard>, register exactly `http://127.0.0.1:43821/callback/`, enter the **public client ID** in Settings, save, then **Connect Spotify**. No client secret is needed. Authorization uses PKCE/state and a three-minute loopback listener. Your Spotify account must be permitted by the app's current developer-access rules. Import `https://open.spotify.com/playlist/...` (album/track links also work). If the callback fails, check the exact redirect URI and port availability; do not open that port to the internet.
- **YouTube:** enable **YouTube Data API v3** in your Google Cloud project, create an API key, restrict it to that API, set quotas, and save it in Settings. Paste a playlist URL containing `list=`. This implementation has no YouTube user OAuth; a key alone does not grant access to private playlists.
- **Apple Music:** create a MusicKit-capable Apple Developer configuration and generate a valid signed developer token outside Resonance. Store the token, never the signing private key, in Settings. Only catalog playlist URLs are supported; personal-library OAuth is not implemented. Replace expired tokens manually.
- **SoundCloud:** supply an authorized API access token and use a `/sets/` playlist URL. Without a token, import creates one source reference, **not** a resolved track list. Token acquisition/refresh is external to Resonance.

Blank secret boxes retain saved values; the explicit clear option clears them. Spotify Disconnect clears its saved access/refresh tokens. Credentials are never shipped in the source/release ZIP.

## Storage, privacy and backup

Everything local is under `%LOCALAPPDATA%\Resonance\`:

| File/folder | Contents |
| --- | --- |
| `library.db` (+ WAL/SHM while open) | Track metadata/paths, playlists, application settings/session |
| `art\` | Cached embedded artwork |
| `config.json` | Public Spotify client ID |
| `credentials.dpapi` | Provider secrets encrypted for the current Windows user |

The database and art are **not encrypted**. Default user-profile ACLs apply; do not move this folder into a public share. DPAPI protects credentials at rest, not against malicious programs already running as you. Decrypted credential strings remain in process memory while the application runs; temporary serialized byte buffers are zeroed.

There is no Resonance account, backend or telemetry implementation. Provider imports send requests to the selected service (YouTube sends its API key to Google); remote playlist imports contact the entered HTTPS host. Explicitly opening a reference contacts its destination in another app/browser. Do not share credential files, private library databases or logs containing private paths. The app cannot guarantee the safety of arbitrary external reference destinations.

For backup, **close Resonance first**, then copy the whole data directory. DPAPI credentials are tied to your Windows account and may not be usable on another PC; reconnect there. To reset, close the app and rename this directory so you can recover it. Uninstalling the portable app does not remove this data or your music.

## Tests and maintenance

```powershell
dotnet test .\Resonance.sln -c Release
dotnet build .\Resonance.sln -c Release -warnaserror
dotnet list .\Resonance.sln package --vulnerable --include-transitive
```

Tests use isolated temporary files/SQLite databases and seeded shuffle randomness; no provider credentials or network access are required. Coverage includes normalization/threshold/duration matching, M3U/PLS/plain/BOM/path/size guards, queue transitions/history/reordering, actual WAV scanning and cache reuse, schema initialization/reopen, scan upsert/prune, snapshot validation, foreign-key behavior, rollback and settings. Schema tests cover the shipped schema and version-zero initialization, **not migrations from unknown historical schemas**. Automated tests do not validate a real audio device, WPF interactions or live provider authorization.

The optional GitHub Actions template (`scripts/Windows-CI.yml`) is configured to build with warnings as errors, run tests on Windows, create the self-contained ZIP, audit transitive NuGet packages and scan Git history. **It is not active:** the connected GitHub app could not push workflow files because it lacks workflows permission. To enable it, a repository maintainer with appropriate permissions can run the commands below, then commit and push the resulting workflow using their own authorized account. No CI success is claimed for this handoff.

```powershell
New-Item -ItemType Directory -Force .\.github\workflows | Out-Null
Copy-Item .\scripts\Windows-CI.yml .\.github\workflows\windows.yml
```

The local `scripts/Publish-Windows.ps1` script works independently of CI. A successful build is not a substitute for the manual checklist below. NuGet auditing is explicitly enabled for all dependencies; vulnerability and audit-source warnings fail restore. The native SQLite bundle is pinned to 2.1.13 to replace the flagged 2.1.6 dependency (GHSA-2m69-gcr7-jv3q); retain this override until the parent dependency supplies a safe version.

### Secret scanning

Install [pre-commit](https://pre-commit.com/) and Git, then run:

```powershell
pre-commit install
pre-commit run --all-files
```

The pinned Gitleaks hook scans staged content before commits. For a full history audit with Gitleaks 8.24.3 or later:

```powershell
gitleaks git . --log-opts="--all" --redact
```

`.gitignore` excludes credentials, encrypted temporary files, databases/journals, local tools and build outputs. A clean scanner report reduces risk but cannot prove that no secret ever existed. If a real secret is found, revoke it before arranging any history cleanup; do not merely delete it from the latest commit.

## Security launch review

This is a review of the **existing implementation**, not a claim that every launch requirement has been hardened. Remaining limitations are deliberately recorded instead of silently rewriting the completed application layer.

| # | Requirement | Result / reason |
| --- | --- | --- |
| 1 | Hide keys | Done for normal storage: per-user DPAPI; only public Spotify ID in JSON. No bundled credentials. |
| 2 | Git secrets | Gitleaks history scan and pre-commit configuration; optional CI template not yet activated; runtime secrets and DBs ignored. See validation results below. |
| 3 | Public DB key | N/A: local SQLite, no remote database key. |
| 4 | Row-level security | N/A: single-user desktop app; profile ACLs are the boundary, not database row policies. |
| 5 | Sensitive encryption | Partial: DPAPI CurrentUser and zeroed byte buffers; managed plaintext secret strings live for the session. |
| 6 | Server authentication | N/A: no server. Provider JSON is bounded and validated in Core; OAuth response validation remains less strict. |
| 7 | Record access | N/A: profile filesystem access, no multi-tenant records. |
| 8 | Field tampering | Partial: strict playlist snapshot/setting shape and range checks; credential/config JSON and some missing provider fields are not strictly rejected. |
| 9 | Secure cookies | N/A: no session cookies. Spotify uses state, S256 PKCE and loopback timeout. |
| 10 | Password hashing | N/A: no passwords/accounts are stored. |
| 11 | Login rate limiting | N/A: no login server. Provider API retries 429/5xx at most twice with capped waits and cancellation. |
| 12 | Bot protection | N/A: no public service. |
| 13 | Parameterized SQL | Done: user values bind as SQLite parameters; writes use transactions. |
| 14 | Input validation | Partial: API host/scheme checks, file extension/size/path guards and reparse rejection exist. Generic HTTPS playlist hosts are not an allowlist and DNS/private-address destinations are not fully blocked. |
| 15 | Escape content | Done for current views: untrusted titles are plain WPF text, not parsed XAML/HTML. |
| 16 | File restrictions | Done: read-only playlist access, allowed extensions, 4 MiB/line/item limits. |
| 17 | Response bounds | Partial: Core provider/remote playlist responses cap at 4 MiB with pagination/item caps; Spotify token exchange still uses unbounded string reading. |
| 18 | Security headers | N/A: desktop application, no hosted website. |
| 19 | HTTPS | Done for provider API calls and remote playlist fetches; local OAuth callback is intentionally HTTP. ExternalLinks accepts http/https/spotify schemes but does **not** restrict destination hosts. |
| 20 | Dependency audit | Explicit all-dependency auditing with fail-on-advisory policy; unsafe SQLite bundle updated. See validation results below. |

Playback rechecks that each track is indexed and its path is a supported, non-reparse local audio file. References only open on explicit action via `ExternalLinks.Open`; they are not audio downloads. The existing helper is scheme-restricted, **not host-allowlisted**. Do not treat provider/playlist metadata as trusted or call this a fully security-hardened release while the partial items remain.

## Validation and Windows acceptance

Validated after the SQLite dependency upgrade, using .NET SDK 8.0.425 on Linux:

- Release solution build with `-warnaserror`: **0 warnings, 0 errors**.
- Regression tests: **82 passed, 0 failed, 0 skipped**.
- Self-contained `win-x64` publish with the Release profile: **succeeded**.
- Direct/transitive NuGet audit: **no known vulnerable packages reported** across all four projects using the current NuGet source. The original SQLite advisory is no longer reported.
- Gitleaks scan of reachable Git history: **no leaks detected**.

These are Linux tests and a Windows cross-build, not an executed Windows desktop acceptance test. Windows CI is supplied as an inactive template because of the permission restriction above. The PowerShell packaging script, WPF UI, audio-device playback, live provider OAuth and clean-Windows execution still require Windows validation before distribution.

Palette calculations (normal-text WCAG AA minimum 4.5:1) for TextBrush / SecondaryBrush on SurfaceBrush: Light **14.77 / 6.10**, Dark **14.72 / 7.79**. These are token-level checks, not a full accessibility certification.

Manual acceptance on Windows (not replaced by CI):

- Start as a standard user; check an empty library and add/scan a real audio folder. Rescan unchanged files and retag one file. Test offline/unreadable folders without losing their entries.
- Play representative supported formats on a real audio device; exercise pause, stop, seek, volume, Previous, Next, repeat and shuffle; disconnect the device and check error handling.
- Import an M3U and a permitted Spotify playlist using your own account; inspect matches/references, save/reopen/delete and re-sync. Test denial, expired credentials and an unavailable network without exposing tokens.
- Change Light/Dark/System and reduced motion at runtime; test keyboard navigation, resizing and 125–200% DPI. Restart and check queue/current track, mode, volume, folders, appearance and window placement (no automatic playback).
- Launch `Resonance.exe` from the fully extracted self-contained ZIP on a clean Windows machine without the SDK/runtime; verify icon, SQLite native loading and data location.

## Project layout and assumptions

- `Resonance.Core`: domain, scanner, matching, queue, importers and validation.
- `Resonance.Data`: serialized transactional SQLite persistence.
- `Resonance.App`: WPF shell/views/viewmodels, playback, credentials, themes and session persistence.
- `Resonance.Tests`: offline xUnit regression suite.
- `scripts/Publish-Windows.ps1`: repeatable native release packaging.

This handoff completes tests/packaging/documentation around the application already present; it does not add lyrics, an equalizer, scrobbling, streaming, an updater, installer or new product behavior. The target package is Windows x64; no ARM64 package is promised. See [LICENSE](LICENSE) (MIT); dependencies retain their respective licenses.
