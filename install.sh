#!/usr/bin/env bash
# Installs TunerFish for the current user (no sudo):
#   ~/.local/opt/tunerfish/                           the published app (Release)
#   ~/.local/share/icons/hicolor/<size>/apps/         tunerfish.png (16-512 px) and scalable/tunerfish.svg
#   ~/.local/share/applications/tunerfish.desktop     the menu launcher
# Safe to run again: it replaces the previous install. Settings, sort names and the cover cache
# (~/.config, ~/.local/share, ~/.cache .../TunerFish) are never touched. Undo with uninstall.sh.
set -euo pipefail

APP_ID=tunerfish     # icon name, .desktop file name and install folder
EXE=TunerFish        # AssemblyName in TunerFish.Desktop.csproj
SIZES=(16 24 32 48 64 128 256 512)

[[ -n "${HOME:-}" ]] || { echo "HOME is not set" >&2; exit 1; }
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OPT_DIR="$HOME/.local/opt/$APP_ID"
ICON_DIR="$HOME/.local/share/icons/hicolor"
APPS_DIR="$HOME/.local/share/applications"

echo "Publishing (Release)..."
STAGING="$(mktemp -d "${TMPDIR:-/tmp}/$APP_ID-publish.XXXXXX")"
trap 'rm -rf "$STAGING"' EXIT
dotnet publish "$SRC/src/TunerFish.Desktop" -c Release -o "$STAGING/app" --nologo -v quiet

# Swap in the new build only once publishing has succeeded, so nothing stale is left behind.
mkdir -p "$(dirname "$OPT_DIR")"
rm -rf "$OPT_DIR"
mv "$STAGING/app" "$OPT_DIR"
echo "  app      -> $OPT_DIR"

for size in "${SIZES[@]}"; do
    install -Dm644 "$SRC/src/TunerFish.Desktop/Assets/icon/$APP_ID-$size.png" \
        "$ICON_DIR/${size}x${size}/apps/$APP_ID.png"
done
install -Dm644 "$SRC/branding/$APP_ID.svg" "$ICON_DIR/scalable/apps/$APP_ID.svg"
echo "  icons    -> $ICON_DIR/{${SIZES[0]}x${SIZES[0]}..${SIZES[-1]}x${SIZES[-1]},scalable}/apps/"

# The checked-in .desktop file has a bare Exec=; point it at this install.
mkdir -p "$APPS_DIR"
sed "s|^Exec=.*|Exec=$OPT_DIR/$EXE|" "$SRC/$APP_ID.desktop" > "$STAGING/$APP_ID.desktop"
install -Dm644 "$STAGING/$APP_ID.desktop" "$APPS_DIR/$APP_ID.desktop"
echo "  launcher -> $APPS_DIR/$APP_ID.desktop"

if command -v gtk-update-icon-cache >/dev/null; then
    gtk-update-icon-cache -f -t -q "$ICON_DIR" || true
fi
if command -v update-desktop-database >/dev/null; then
    update-desktop-database -q "$APPS_DIR" || true
fi

echo "Done. TunerFish is in the menu (Sound & Video)."
