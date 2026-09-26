#!/bin/sh
set -eu
repo='https://github.com/isaiahpettingill/LevelBuilder/releases/latest/download'
asset='LevelBuilder-linux-x64.zip'
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
case "$data_home" in /*) ;; *) data_home="$HOME/.local/share" ;; esac
prefix="$data_home/LevelBuilder"
launcher="$HOME/.local/bin/levelbuilder"
desktop="$data_home/applications/org.levelbuilder.editor.desktop"
icon="$data_home/icons/hicolor/scalable/apps/org.levelbuilder.editor.svg"
mime="$data_home/mime/packages/org.levelbuilder.editor.xml"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT INT TERM
if [ "${1:-}" = '--archive' ]; then
    [ "$#" -eq 2 ] || { echo 'Usage: install.sh --archive file.zip' >&2; exit 1; }
    cp "$2" "$stage/$asset"
else
    [ "$#" -eq 0 ] || { echo 'Usage: install.sh [--archive file.zip]' >&2; exit 1; }
    curl -fLsS "$repo/$asset" -o "$stage/$asset"
    curl -fLsS "$repo/$asset.sha256" -o "$stage/$asset.sha256"
    (cd "$stage" && sha256sum -c "$asset.sha256")
fi
mkdir -p "$prefix" "$(dirname "$launcher")" "$(dirname "$desktop")" "$(dirname "$icon")" "$(dirname "$mime")"
unzip -oq "$stage/$asset" -d "$prefix"
chmod +x "$prefix/LevelBuilder.App"
ln -sfn "$prefix/LevelBuilder.App" "$launcher"
exec_path=$(printf '%s' "$launcher" | sed 's/\\/\\\\/g; s/"/\\"/g; s/\$/\\$/g; s/`/\\`/g; s/%/%%/g')
cat > "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=LevelBuilder
GenericName=2D Level Editor
Comment=Edit 2D game levels
Exec="$exec_path" %f
MimeType=application/vnd.levelbuilder.project;application/vnd.levelbuilder.bundle;
Icon=org.levelbuilder.editor
Terminal=false
Categories=Graphics;2DGraphics;
StartupNotify=true
StartupWMClass=org.levelbuilder.editor
EOF
cat > "$icon" <<'EOF'
<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
 <rect x="4" y="4" width="56" height="56" rx="10" fill="#273246"/>
 <path d="M13 13h38v38H13z" fill="#344d69"/>
 <path d="M13 13h18v18H13zm20 0h18v18H33zM13 33h18v18H13zm20 0h18v18H33z" fill="#7cacc4" stroke="#273246" stroke-width="2"/>
 <path d="M15 49l11-12 8 8 8-18 8 22" fill="none" stroke="#f8d17e" stroke-width="4"/>
</svg>
EOF
cat > "$mime" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
 <mime-type type="application/vnd.levelbuilder.project"><comment>LevelBuilder project</comment><glob pattern="*.level"/></mime-type>
 <mime-type type="application/vnd.levelbuilder.bundle"><comment>LevelBuilder bundle</comment><sub-class-of type="application/zip"/><glob pattern="*.levelz"/></mime-type>
</mime-info>
EOF
if command -v update-mime-database >/dev/null 2>&1; then update-mime-database "$data_home/mime" >/dev/null 2>&1 || true; fi
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database "$data_home/applications" >/dev/null 2>&1 || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -f -t "$data_home/icons/hicolor" >/dev/null 2>&1 || true; fi
for refresh in kbuildsycoca6 kbuildsycoca5; do
    if command -v "$refresh" >/dev/null 2>&1; then "$refresh" --noincremental >/dev/null 2>&1 || true; break; fi
done
printf 'Installed LevelBuilder: %s (also available in the application menu)\n' "$launcher"
