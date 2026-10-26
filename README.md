# TunerFish

An album-first music player for Jellyfin. Browse the way you'd flip through a well-sorted
record collection: **album artist (A–Z) → albums (by title or year) → click to play the album.**

Jellyfin does the scanning, tag reading and storage. TunerFish never touches your files,
never scans folders, and doesn't care about Flatpak sandboxes or drive mount timing.

> **Platform:** TunerFish currently targets **Linux** (developed and tested on Linux Mint / X11).
> It's built on cross-platform pieces (.NET, Avalonia, libmpv), but Windows and macOS aren't
> supported yet: there's no packaging for them, no bundled libmpv, and it hasn't been tested there.

## What it does

- Album artists in the left pane, A–Z, with a search box and an A–Z jump bar
- **Search** artist names, album titles and track titles (the Artists · Albums · Tracks buttons under
  the box, any combination). The results are artists: one found by an album or track comes with just
  those albums and says what matched; its albums pane has **Show all**. In a view it searches that view.
- Artists and albums are filed by their **sort name in Jellyfin**, so "The Beatles" files under B.
  To file one differently (e.g. Elvis Costello under "Costello"), change its Sort name in
  Jellyfin's metadata editor. Display names never change. Accents are ignored for sorting
  (Björk files with Bjork), and names starting with a symbol or number come first, under `#`.
- Albums as cover tiles, sorted by Title, Year (oldest first) or Year (newest first)
- Double-click an album to play it (or switch on **Single click plays album**)
- Double-click a track to play the album from that track
- Track list with **your choice of columns**: Columns… button, or right-click any column header.
  Drag headers to reorder, drag edges to resize. All of it is remembered.
- **Play queue** in a right-hand pane: double-click an album to play it (replaces the queue), or right-click an album or track → Add to queue (which never starts playing: into an empty queue, the first track waits, paused, for ▶ or Space). Drag rows to reorder, ✕ or Delete to remove a track, right-click → Remove album to remove the whole album, Clear to empty it. Played tracks are greyed out.
- **Your own marks** on artists and albums, kept in Jellyfin as your user data: ♥ favourite, 👍 like,
  👎 hide (out of the way, never deleted). Right-click an artist or album, or use the buttons beside
  Add to queue. Hidden ones are left out unless Options → Show hidden is on.
- **Views** above the artist list: Library, Favourites, Liked, Recently played, Most played (albums played
  through most often) and Hidden. Favourites, Liked and Hidden shorten the artist list to the artists
  that are marked (with all their albums) or have marked albums (with just those, and **Show all**).
- Your play counts in the track list (Plays and Last played columns). Plays are reported to Jellyfin.
- Gapless playback via libmpv, streamed straight from your server in its original format
- **Bass and treble** (the button beside the volume): ±12 dB, heard at once, Flat to reset
- Space / media keys for play-pause, next, previous (while the window has focus)
- An **app log** for bug reports (Menu → Open log folder): connections, errors and crashes, never your
  token or password
- Remembers the queue, paused where you left off (or Options → Clear queue on exit), and window size,
  pane sizes, columns, sort, volume, bass and treble and the last artist you viewed

Compilations tagged with the album name as Album Artist show up as their own "artist",
filed alphabetically under their own name.

## Setup

Requirements: .NET 10 SDK and libmpv. On Debian, Ubuntu or Linux Mint:

```bash
sudo apt install libmpv2
```

Then clone and run:

```bash
git clone https://github.com/TobekSoft/TunerFish.git
cd TunerFish
dotnet run --project src/TunerFish.Desktop
```

Or open `TunerFish.sln` in Rider (or another .NET IDE) and run the **TunerFish.Desktop** project.

First run asks for your Jellyfin server address (e.g. `192.168.1.20:8096`), user name and password.
Only the session token is stored, never the password. The eye in the password box shows what you typed.

## Optional: Avalonia licence key

Not needed to build or run: core Avalonia is MIT. A key only matters for Avalonia's licensed
tooling. If you have one, copy `Licenses.props.example` to `.licenses/Licenses.props` in this folder
or any folder above it (or point the `DEV_LICENSES_DIR` environment variable at its folder) and paste
your key in. `Directory.Build.props` picks it up automatically. Never commit it.

## Solution layout

| Project | What's in it |
|---|---|
| `src/TunerFish.Core` | All behaviour: Jellyfin client, mpv playback, settings, sort order, view models. No UI packages. |
| `src/TunerFish.Desktop` | The Avalonia windows (main, About) and app startup. `AppInfo.cs` holds the display name. |
| `branding/` | SVG masters for the icon and logo (not part of the build). |
| `tests/TunerFish.Tests` | xUnit tests against a fake Jellyfin server and real (silent) libmpv. |

Package versions are managed centrally in `Directory.Packages.props`. Rider's NuGet window edits
that file directly. Bump Avalonia by changing `AvaloniaVersion`.

## Tests

```bash
dotnet test TunerFish.sln
```

Or use Rider's test explorer. Test audio and artwork are generated in code, so nothing extra is needed.
Playback tests use real libmpv with sound output off; if libmpv isn't installed they're skipped
(with a message saying so), not failed. They're tagged `Category=Integration`, so you can leave
them out with `dotnet test TunerFish.sln --filter "Category!=Integration"`.

## Where things live at runtime

| What | Where |
|---|---|
| Settings | `~/.config/TunerFish/settings.json` (readable by you only) |
| Cover cache | `~/.cache/TunerFish/covers/` (safe to delete) |
| Saved queue | `~/.local/share/TunerFish/tunerfish.db` |
| App log | `~/.local/state/TunerFish/logs/`, one file a day, a month kept (Menu → Open log folder) |

Settings are plain, readable JSON. Choices are stored as words (`"AlbumSort": "YearAscending"`),
not numbers, and a value the app doesn't recognise resets **only that one setting**. If the file
is ever unreadable, it's copied aside as `settings.json.unreadable-<date>` rather than overwritten.

## Install to your desktop menu (Linux)

```bash
./install.sh      # build Release and install for your user (no sudo); run again to update
./uninstall.sh    # remove it again (keeps your settings and cover cache)
```

`install.sh` publishes the app to `~/.local/opt/tunerfish`, installs the icon into your icon theme
(`~/.local/share/icons/hicolor/<size>x<size>/apps/tunerfish.png` for 16–512 px, plus
`scalable/apps/tunerfish.svg`), installs `tunerfish.desktop` into `~/.local/share/applications/`
(with `Exec=` pointed at the install), and refreshes the icon cache and desktop database if
`gtk-update-icon-cache` / `update-desktop-database` are present. Each run replaces the previous install.

## Build a .deb (Debian, Ubuntu, Linux Mint)

```bash
./package-deb.sh          # artifacts/tunerfish_<version>_amd64.deb
./package-deb.sh beta.1   # artifacts/tunerfish_<version>~beta.1_amd64.deb (a pre-release)
sudo apt install ./artifacts/tunerfish_<version>_amd64.deb
```

The package installs the app to `/usr/lib/tunerfish` (self-contained, so .NET isn't needed), `tunerfish`
on the PATH, the launcher and the icons, and depends on the distribution's `libmpv2` (Debian 12,
Ubuntu 23.04, Linux Mint 22 or later). Remove it with `sudo apt remove tunerfish`; your settings are kept.
Don't keep an `install.sh` copy as well: its launcher has the same name and hides the package's
(run `./uninstall.sh` first).

The launcher uses `Icon=tunerfish` and `StartupWMClass=tunerfish`; the app sets its X11 WM_CLASS to
`tunerfish`, so the panel shows the TunerFish icon for the running window. If the menu still shows an
old icon, log out and back in.

## Artwork

| Where | What |
|---|---|
| `branding/` | SVG masters (`tunerfish.svg` icon, `tunerfish-logo.svg` logo with music-note bubbles) and spare logo PNG exports. Edit these. |
| `src/TunerFish.Desktop/Assets/icon/` | Icon PNGs, 16–512 px. The window icon (256 px) and the icons `install.sh` installs. |
| `src/TunerFish.Desktop/Assets/logo/` | `tunerfish-logo-512.png`, shown on the sign-in screen and in the About window. |

The app uses PNGs only (Avalonia doesn't draw SVG). After changing an SVG, re-export the PNGs at the
same sizes and names. The app name, version and description shown in the UI come from
`src/TunerFish.Desktop/AppInfo.cs`; the version is `<Version>` in `TunerFish.Desktop.csproj`.
**Options → About TunerFish…** shows them, along with the Avalonia and libmpv versions.

## Working on it with Claude Code

`CLAUDE.md` describes the project, commands and house rules, and `.claude/settings.json`
pre-approves routine `dotnet` build, run and test commands. Start Claude Code in the solution folder.

## Ideas for next steps

- MPRIS support, so desktop media keys and the panel's now-playing widget work when unfocused
- Show/hide whole panes (e.g. hide the artist pane and browse an all-albums grid)

## License

MIT, © 2026 Tobeksoft. See [LICENSE](LICENSE). The icon and logo (`branding/`, `src/TunerFish.Desktop/Assets/`)
were made with AI image tools and may be used on the same terms.

Playback uses [libmpv](https://mpv.io), which is loaded from your system at runtime and not distributed
with TunerFish; it has its own licence (GPL or LGPL, depending on how it was built).
