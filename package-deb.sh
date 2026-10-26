#!/usr/bin/env bash
# Builds a .deb of TunerFish for Debian, Ubuntu, Linux Mint and the like (amd64), into artifacts/:
#   /usr/lib/tunerfish/                       the published app (Release, self-contained: no .NET needed)
#   /usr/bin/tunerfish                        a link to it
#   /usr/share/icons/hicolor/<size>/apps/     tunerfish.png (16-512 px) and scalable/tunerfish.svg
#   /usr/share/applications/tunerfish.desktop the menu launcher
# libmpv comes from the distribution (Depends: libmpv2), so it's never bundled. dpkg's triggers
# refresh the icon cache and the menu, so there are no install scripts.
#   ./package-deb.sh           tunerfish_<Version>_amd64.deb, <Version> from TunerFish.Desktop.csproj
#   ./package-deb.sh beta.4    tunerfish_<Version>~beta.4_amd64.deb (sorts before <Version> itself)
# Install with: sudo apt install ./artifacts/tunerfish_..._amd64.deb   Remove with: sudo apt remove tunerfish
# A per-user install.sh copy would hide this one in the menu (same launcher name): run uninstall.sh first.
set -euo pipefail

APP_ID=tunerfish     # package name, icon name, .desktop file name and install folder
EXE=TunerFish        # AssemblyName in TunerFish.Desktop.csproj
SIZES=(16 24 32 48 64 128 256 512)
RID=linux-x64
ARCH=amd64

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$SRC/src/TunerFish.Desktop/TunerFish.Desktop.csproj"
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$CSPROJ" | head -1)"
[[ -n "$VERSION" ]] || { echo "No <Version> in $CSPROJ" >&2; exit 1; }
[[ -n "${1:-}" ]] && VERSION="$VERSION~$1"
OUT_DIR="$SRC/artifacts"
DEB="$OUT_DIR/${APP_ID}_${VERSION}_${ARCH}.deb"

STAGING="$(mktemp -d "${TMPDIR:-/tmp}/$APP_ID-deb.XXXXXX")"
trap 'rm -rf "$STAGING"' EXIT
ROOT="$STAGING/root"

echo "Publishing (Release, $RID, self-contained)..."
dotnet publish "$SRC/src/TunerFish.Desktop" -c Release -r "$RID" --self-contained true \
    -o "$ROOT/usr/lib/$APP_ID" --nologo -v quiet
# No debug symbols, and no LTTng tracing hook (it needs liblttng-ust, which isn't a dependency).
rm -f "$ROOT/usr/lib/$APP_ID"/*.pdb "$ROOT/usr/lib/$APP_ID/libcoreclrtraceptprovider.so"

mkdir -p "$ROOT/usr/bin"
ln -s "../lib/$APP_ID/$EXE" "$ROOT/usr/bin/$APP_ID"

for size in "${SIZES[@]}"; do
    install -Dm644 "$SRC/src/TunerFish.Desktop/Assets/icon/$APP_ID-$size.png" \
        "$ROOT/usr/share/icons/hicolor/${size}x${size}/apps/$APP_ID.png"
done
install -Dm644 "$SRC/branding/$APP_ID.svg" "$ROOT/usr/share/icons/hicolor/scalable/apps/$APP_ID.svg"

# The checked-in .desktop file has a bare Exec=; point it at the link on the PATH.
mkdir -p "$ROOT/usr/share/applications"
sed "s|^Exec=.*|Exec=$APP_ID|" "$SRC/$APP_ID.desktop" > "$ROOT/usr/share/applications/$APP_ID.desktop"
chmod 644 "$ROOT/usr/share/applications/$APP_ID.desktop"

install -Dm644 "$SRC/LICENSE" "$ROOT/usr/share/doc/$APP_ID/copyright"

# Debian's modes whatever the umask: 755 for folders and programs, 644 for the rest.
find "$ROOT" -type d -exec chmod 755 {} +
find "$ROOT" -type f -exec chmod 644 {} +
chmod 755 "$ROOT/usr/lib/$APP_ID/$EXE" "$ROOT/usr/lib/$APP_ID/createdump" "$ROOT/usr/lib/$APP_ID"/*.so

# What .NET and Avalonia need from the system (Skia, HarfBuzz and SQLite come bundled).
# ICU and OpenSSL change package names between releases, hence the alternatives.
mkdir -p "$ROOT/DEBIAN"
cat > "$ROOT/DEBIAN/control" <<EOF
Package: $APP_ID
Version: $VERSION
Architecture: $ARCH
Maintainer: Tobeksoft <61861115+TobekSoft@users.noreply.github.com>
Installed-Size: $(du -sk --exclude=DEBIAN "$ROOT" | cut -f1)
Depends: libmpv2, libc6, libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3,
 libicu76 | libicu74 | libicu72 | libicu71 | libicu70,
 libx11-6, libice6, libsm6, libfontconfig1
Section: sound
Priority: optional
Homepage: https://github.com/TobekSoft/TunerFish
Description: Album-first music player for Jellyfin
 Browse album artists A-Z, then their albums by title or year, and play
 whole albums from your Jellyfin server. Jellyfin owns the library; this
 app never changes your music files.
EOF

mkdir -p "$OUT_DIR"
dpkg-deb --root-owner-group -Zxz --build "$ROOT" "$DEB" >/dev/null
echo "Done: $DEB"
