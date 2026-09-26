#!/bin/sh
set -eu
repo='https://github.com/isaiahpettingill/LevelBuilder/releases/latest/download'
asset='LevelBuilder-linux-x64.zip'
prefix="${XDG_DATA_HOME:-$HOME/.local/share}/LevelBuilder"
launcher="${HOME}/.local/bin/levelbuilder"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT INT TERM
curl -fLsS "$repo/$asset" -o "$stage/$asset"
curl -fLsS "$repo/$asset.sha256" -o "$stage/$asset.sha256"
(cd "$stage" && sha256sum -c "$asset.sha256")
mkdir -p "$prefix" "$(dirname "$launcher")"
unzip -oq "$stage/$asset" -d "$prefix"
chmod +x "$prefix/LevelBuilder.App"
ln -sfn "$prefix/LevelBuilder.App" "$launcher"
printf 'Installed LevelBuilder: %s\n' "$launcher"
