# TunerFish

Album-first desktop music player for a Jellyfin server. Browse album artist (A–Z) → albums
(by title or year) → click to play the whole album. Jellyfin owns the library (scanning, tags,
artwork, streaming); this app never reads or writes the music files (see
the read-only rule below).

Developed on Linux Mint with .NET 10, Avalonia 12 and JetBrains Rider.

## Commands

```bash
dotnet build TunerFish.sln                          # build everything
dotnet run --project src/TunerFish.Desktop          # run the app
dotnet test TunerFish.sln                           # all tests (playback tests skip if libmpv2 is missing)
dotnet test TunerFish.sln --filter "Category!=Integration"   # skip the real-libmpv tests
dotnet list package --outdated                       # check for NuGet updates
```

Runtime dependency: `libmpv2` (system package). Without it the app runs but can't play.

## Layout

```
src/TunerFish.Core/       All behaviour. NO Avalonia or other UI packages, ever.
  Models/                  Jellyfin API response shapes
  Services/                JellyfinClient, MpvPlayer (libmpv P/Invoke), CoverCache, SettingsStore,
                           PlayReporter (play history: start/progress/stop reports to Jellyfin),
                           SortOrder (sort key = Jellyfin's SortName, plain-letter comparer, jump-bar groups),
                           Marks (heart / like / hide on artists and albums), LibraryViews (the views + PlayHistory),
                           Tone (bass and treble: levels, labels, mpv's af string),
                           Search (the search box: SearchScope, hits, narrowing the artist list),
                           QueueStore (the queue kept across restarts, SQLite),
                           AppPaths (every file's folder, per OS), AppLog (LogArea, keeping secrets out of the log)
  ViewModels/              MainViewModel (all app logic), row VMs, TrackColumns, tiny MVVM base
src/TunerFish.Desktop/    Avalonia UI only: App, Program (crash handler), AppInfo, AppLogFile (Serilog), Views/MainWindow, Views/AboutWindow
  AppInfo.cs               The ONE place for the display name, version (csproj <Version>), description, WM_CLASS
  Assets/                  PNGs only, embedded as avares://TunerFish/Assets/...
    icon/                  tunerfish-{16..512}.png: window icon (256) and, via install.sh, the icon theme
    logo/                  tunerfish-logo-512.png: sign-in screen (160 px) and About window (192 px)
branding/                 SVG masters (tunerfish.svg, tunerfish-logo.svg) + spare logo PNG exports. Not built.
tunerfish.desktop         Launcher: Icon=tunerfish, StartupWMClass=tunerfish. install.sh rewrites Exec=.
install.sh / uninstall.sh Per-user install to ~/.local/opt/tunerfish + hicolor icons + launcher (no sudo)
package-deb.sh            .deb into artifacts/: self-contained in /usr/lib/tunerfish, Depends: libmpv2 (never bundled)
tests/TunerFish.Tests/    xUnit v3: fake Jellyfin HTTP server fixture + real silent libmpv
  Support/                 FakeJellyfinServer, TestMedia (WAV tones + PNG made in code), TempDir,
                           VmHarness, Wait.UntilAsync, the "Audio engine" collection
```

The split is deliberate: anything testable goes in Core, and the Desktop project stays a thin
skin. Core gets UI-specific things through injection (e.g. the image decoder is a
`Func<string,int,object?>` passed to `MainViewModel`). Keep it that way.

## Rules

- **Linux first; Windows possibly; macOS left possible for others.** The owner runs Linux only, might add Windows
  for a public release, and has no Mac, but a macOS port mustn't need a fork. So per-OS choices live in one place:
  folders in `AppPaths` (Linux: XDG, as tested; Windows and macOS untested), libmpv's file names in `MpvPlayer`,
  and opening files or folders through Avalonia's `Launcher`, never `xdg-open`. Packaging (`install.sh` etc.) is per OS.
- **The library is read-only; your own user data isn't.** TunerFish never changes the music files,
  their tags (ID3 etc.), artwork or any library metadata, and keeps sort data out of it too. It may
  write the signed-in user's own Jellyfin user data: favourites, played/unplayed, play counts and
  playback reports. That data is per user and kept apart from the library. Agreed 2026-10-24. Written
  so far: playback reports (`PlayReporter`) and marks.
- **Plays are reported to Jellyfin** (`PlayReporter`, fed by `Tick`: start when a track actually
  plays, progress on pause/resume/seek and every 10 s, stop where it was when playback moves on,
  the queue ends, you sign out or the app closes), so the server keeps the user's play counts and
  last played; it decides what counts as played. Reports go one at a time, in order; a failed one is
  dropped (the desktop only plays while it can reach the server; the phone keeps offline plays).
- **Marks, on artists and albums only, never tracks** (`Marks`, a port of the Android app's `Marks.kt`, tests
  mirrored in `MarksTests`): heart = Jellyfin's `IsFavorite` (a perennial favourite); thumbs up = `Likes` true
  (into it now); thumbs down = `Likes` false (hidden: out of the way, never a delete). `Likes` is one field, so
  liked and hidden exclude each other. Right-click an artist or album for the menu; the album's heart and thumbs
  are also beside Add to queue. `MainViewModel.SetMarksAsync` shows a change at once and sends it (one at a time,
  in order); if the server refuses or can't be reached, the change is undone and the status bar says so. No
  offline store, unlike Android: the desktop works only while it can reach the server. Hidden things are left
  out unless **Show hidden** (`ShowHidden`, the Android key) is on.
- **Views** (`LibraryView`, the chooser above the artist filter; not saved, the app opens on Library): Favourites,
  Liked and Hidden filter the artist list (`LibraryViews.Filter`): a marked artist is listed with all its albums
  (even ones added later), an artist with marked albums with only those, and a hidden artist hides all its albums
  (unless Show hidden). The album pane lists the shown marked albums from across the library, filed by artist then
  title; picking an artist shows the view's albums of it, under "Favourites: 2 of 14 albums" with **Show all**
  (`ShowAllAlbums`, off again for the next artist), and "‹ view" goes back. The search box narrows a view too.
  Recently played and Most played list albums only (top 100), from the user's played tracks (`filters=IsPlayed`,
  up to 2000): an album's latest play, or its plays through as a whole (`PlayHistory.Most`). Jellyfin's
  `filters=Dislikes` matches everything not liked, so Hidden reads every album and picks out `Likes` false.
  Same rules as the Android app's `Views.kt` (`LibraryViewsTests` mirrors its tests); change both or neither.
- **Search** (`Search`, the box above the artist list, `MainViewModel.ArtistFilter`): scope buttons Artists · Albums ·
  Tracks, any combination, at least one on (`FilterScope`, words, default `Artists Albums`: the Android key). Album-first:
  the results are the artist list (`Search.Narrow`). A name match keeps what the list shows of the artist; an album or
  track match keeps only those albums, with a note under the name ("Breathe (The Dark Side of the Moon) +2") and
  "Matching: 1 of 6 albums" with **Show all** on its albums. Artist names filter as you type; titles from 2 letters,
  300 ms after typing stops, through Jellyfin's `searchTerm` (one request per type, `limit=300`). Inside a view by
  marks it searches only that view; from a played view it goes to the Library. Hidden albums stay out unless Show
  hidden. An artist filtered out while picked keeps its albums showing. Same rules as the Android app's `Search.kt`
  (`SearchTests` mirrors its tests); change both or neither.
- **Bass and treble** (`Tone`, settings `Bass`/`Treble`, the Android keys): whole dB, ±12, 0 = flat; a low
  shelf at 100 Hz and a high shelf at 10 kHz (RBJ, slope 1), the same as the Android app's `Tone.kt`. The
  phone computes the filters itself; the desktop asks mpv for ffmpeg's `bass`/`treble` through `af`
  (`MpvPlayer.SetTone`), never the system equaliser. Flat means no filter at all. Change both or neither.
- **The queue is kept across restarts** (Android 0.9.0): the entries in `QueueStore` (`~/.local/share/TunerFish/tunerfish.db`,
  table `queue_item`), the place in settings (`QueueIndex`, `QueuePositionMs`: the Android keys). Saved in `Tick` after a
  change and in `Dispose`; put back on start (and on signing in again to the same server), paused at the same track and
  time; signing out empties it. Never store stream or cover URLs: they hold one server address. **Clear queue on exit**
  (`ClearQueueOnExit`) throws the saved queue away on the next start. Schema migrations go through `PRAGMA user_version`
  (1 was the retired sort-name table, left in place unused; 2 added `queue_item`).
- **App log** (Android 0.9.11): what helps with a bug report goes in daily files, a month kept (`AppLogFile`,
  Serilog, `~/.local/state/TunerFish/logs`): starts (version, OS, .NET, libmpv), sign-in and sign-out, address
  changes (`JellyfinClient.OnConnect`), everything the status bar reports as a problem (`MainViewModel.Fail`),
  tracks mpv couldn't play (`MpvPlayer.PlaybackFailed`), play reports the server didn't take, and crashes with
  their stack trace (`Program.OnCrash` → `MainViewModel.NoteCrash`; `CrashToShare`, the Android key, makes the next
  start say so once). Every message goes through `AppLog.Clean` (a port of Android's `AppLog.kt`, `AppLogTests`
  mirrors its tests), which strips the token and passwords. Menu → Open log folder. Unlike the phone (a table
  with Share), files on purpose: a desktop user can read, grep and attach them, and a crash can't take the log with it.
- **Run `dotnet test TunerFish.sln` after any change to Core.** Everything must pass. Add a test
  for new behaviour, in the test class for the thing it exercises.
- **Test conventions.** Anything that writes files uses its own `TempDir` (or `VmHarness`, which
  owns one); nothing may touch the real `~/.config`, `~/.cache` or `~/.local/share` TunerFish
  folders, so always pass explicit paths to `SettingsStore`, `CoverCache` and `QueueStore` (`VmHarness` does).
  Anything that creates an `MpvPlayer`, including every `MainViewModel`, goes in
  `[Collection(AudioEngineCollection.Name)]` (not parallel). Tests that need real playback call
  `LibMpv.SkipIfUnavailable()` first and carry `[Trait("Category", "Integration")]`. Poll with
  `Wait.UntilAsync`, never a bare `Task.Delay`. Test media is generated in code; don't add files.
  `FakeJellyfinServer` keeps the user's marks and plays (`SetMarks`, `SetPlays`, `MarksOf`); `VmHarness` clears
  them at the start of each test, so tests in the shared collection don't see each other's.
- **xUnit runs through VSTest.** The test project uses `xunit.v3.mtp-off` + `xunit.runner.visualstudio`
  + `Microsoft.NET.Test.Sdk`. Plain `xunit.v3` 4.x switches to Microsoft.Testing.Platform, which the
  .NET 10 SDK rejects under classic `dotnet test`. Don't swap it back without also opting the repo
  into the new `dotnet test` in `global.json`.
- **UI changes can't be unit tested.** Build, run the app, and describe what you checked.
- **Settings are stored as words, never numbers.** Enums are persisted with `.ToString()` and
  read back with `Enum.TryParse` plus a default. An unrecognised value must reset only that one
  setting. Never let one bad value wipe the whole settings file. Add new settings to
  `AppSettings` with a sensible default; never rename or reuse existing keys.
- **Filing follows Jellyfin's SortName** for album artists and album titles (it already drops leading
  articles; exceptions are set in Jellyfin's metadata editor). TunerFish never writes sort data to the
  server and keeps no sort data of its own. The key is `SortOrder.KeyFor` (SortName, else the name),
  and the list is ordered client-side with `SortOrder.Comparer`, not in the order the server returns.
  The comparer applies the plain-letter rule: accents ignored (É → E, plus a table for ß, Æ, Ø, ...),
  then symbols < digits < letters, position by position, so each A–Z jump-bar group (`SortOrder.GroupOf`:
  "#" or A–Z) is one unbroken block. The Android app (`../TunerFishAndroid`, `core/.../SortOrder.kt`, with
  the filing tests ported case for case) must reproduce it exactly; change both or neither.
  Non-Latin scripts count as symbols (not supported). Display names are never altered. The old article settings (`IgnoreArticles`, `SortArticles`,
  `IgnoreArticlesInAlbumTitles`) are retired keys: never reuse them.
- **Track column keys in `TrackColumns.All` are persisted.** Never rename a key. To add a column:
  property on `TrackVm`, fill it in `TrackVm.From`, one line in `TrackColumns.All`.
- **Package versions live only in `Directory.Packages.props`** (central package management).
  Projects reference packages without versions. Bump all core Avalonia packages via the
  `AvaloniaVersion` property. NuGet source is nuget.org only (`nuget.config`).
- **The mpv playlist mirrors `MainViewModel.Queue` entry for entry.** Every queue change goes through
  a `MainViewModel` method that makes the same change in `MpvPlayer` (append, remove, move, clear), and
  `Tick` maps mpv's `playlist-pos` straight to `Queue[pos]`. Never touch one without the other.
- **Bindings are reflection bindings** (`AvaloniaUseCompiledBindingsByDefault=false`), because
  some VM properties are `object` (cover bitmaps). Binding typos fail silently at runtime, so
  check names against the view model.
- **The display name lives only in `AppInfo.Name`** (Desktop). Core receives it as `MainViewModel`'s
  `appName` (window title, mpv's audio-client-name, Jellyfin's Client=). Don't hard-code "TunerFish" in
  anything user-visible. Exceptions on purpose: data folder names (`~/.config/TunerFish` etc.), namespaces,
  the executable name, and `Name=` in `tunerfish.desktop` (change that one by hand on a rename).
- **Artwork:** edit the SVGs in `branding/`, then re-export PNGs into `Assets/icon` and `Assets/logo`.
  Avalonia can't draw SVG; don't add an SVG package. Images use `RenderOptions.BitmapInterpolationMode="HighQuality"`.
- **WM_CLASS is fixed** to `AppInfo.WmClass` ("tunerfish") via `X11PlatformOptions` in `Program.cs`; it must
  equal `StartupWMClass` in `tunerfish.desktop` or Cinnamon's panel shows a generic icon. `xprop WM_CLASS`
  on the window reports `"TunerFish", "tunerfish"` (instance from the process name, class from us).
- **One server, several addresses** (`ServerUrls`, most preferred first, e.g. home LAN then NetBird for the
  laptop away from home; `ServerUrl` is kept equal to the first, for older files). Same token and user on all.
  `JellyfinClient.ConnectAsync` probes `/System/Info/Public` on every address at once and uses the first in
  order that answers with the saved `ServerId`; the token is never sent to an address with a different id.
  A request that can't reach the server reconnects and retries once. A network change (NetworkChange event,
  acted on in `Tick`) reconnects too, and if the address changed the mpv playlist is reloaded entry for entry
  with the new URLs (`RepointPlaylistIfServerMoved`), same track, time and pause state.
- **Never commit licences or tokens.** See Licences below.

## Licences

Kept outside the repo, in a `.licenses/` folder above it (can be shared by several solutions):

```
<parent folder>/.licenses/Licenses.props   Avalonia key (+ wiring)
<parent folder>/.licenses/sixlabors.lic    ImageSharp licence file
```

`Directory.Build.props` finds `.licenses/Licenses.props` by searching upward from the repo
(override with `DEV_LICENSES_DIR`). Template: `Licenses.props.example`. The build works without
it: core Avalonia (MIT) needs no key; the key is for Avalonia's licensed tooling. ImageSharp is not
currently used; if it's added (e.g. thumbnail generation), reference it from Core and the licence
file is picked up automatically.

## Jellyfin API notes

- Auth header: `Authorization: MediaBrowser Client="TunerFish", Device=..., DeviceId=..., Version=..., Token="..."`
- Use `/Items?userId=...` (not the older `/Users/{id}/Items`).
- Album artists: `/Artists/AlbumArtists`. Albums of an artist: `/Items?includeItemTypes=MusicAlbum&albumArtistIds=...`.
- Streams: `/Audio/{id}/stream?static=true&api_key=...` (original file; mpv decodes it).
- User data: `enableUserData=true` on artists, albums and tracks (`UserData`: `IsFavorite`, `Likes`, `PlayCount`,
  `LastPlayedDate`). Marks: `POST`/`DELETE /UserFavoriteItems/{id}?userId=`, `POST /UserItems/{id}/Rating?userId=&likes=`
  (`DELETE` clears).
- Covers: `/Items/{id}/Images/Primary?maxHeight=...&tag=...` (server resizes; cache key includes the tag).
- The owner tags compilations with the album name as Album Artist, so a compilation appears as
  its own "artist". Don't add special "Various Artists" handling that would break this.

## Avalonia 12 notes

- `TextBox.PlaceholderText` (not `Watermark`).
- The track grid uses the community `Avalonia.Controls.DataGrid`, whose development has stopped
  upstream. Its columns are built in code (`MainWindow.BuildTrackColumns`). If it ever breaks on
  an Avalonia update, replace it with a custom list rather than the paid TreeDataGrid.
