#!/usr/bin/env bash
# Reverses install.sh: removes the app, its icons and its launcher. Safe to run more than once.
# Your settings and cover cache are kept; their locations are printed at the end.
set -euo pipefail

APP_ID=tunerfish
SIZES=(16 24 32 48 64 128 256 512)

[[ -n "${HOME:-}" ]] || { echo "HOME is not set" >&2; exit 1; }
OPT_DIR="$HOME/.local/opt/$APP_ID"
ICON_DIR="$HOME/.local/share/icons/hicolor"
APPS_DIR="$HOME/.local/share/applications"

rm -rf "$OPT_DIR"
for size in "${SIZES[@]}"; do
    rm -f "$ICON_DIR/${size}x${size}/apps/$APP_ID.png"
done
rm -f "$ICON_DIR/scalable/apps/$APP_ID.svg"
rm -f "$APPS_DIR/$APP_ID.desktop"

if command -v gtk-update-icon-cache >/dev/null && [[ -d "$ICON_DIR" ]]; then
    gtk-update-icon-cache -f -t -q "$ICON_DIR" || true
fi
if command -v update-desktop-database >/dev/null && [[ -d "$APPS_DIR" ]]; then
    update-desktop-database -q "$APPS_DIR" || true
fi

echo "TunerFish removed. Your data was left in place:"
echo "  ~/.config/TunerFish/          settings"
echo "  ~/.local/share/TunerFish/     the saved queue (tunerfish.db)"
echo "  ~/.cache/TunerFish/           cover cache"
