#!/usr/bin/env bash
set -euo pipefail

# Builds the Nexus upload archive. Layout matches the install instructions in README.md and
# nexus-description.txt: the player unpacks INTO the game's Mods/ folder, so the zip carries no
# Mods/ prefix of its own (chronos/package.sh does the opposite, its docs say to unpack into the
# game root). Version comes from manifest.json so the file name can never disagree with what the
# loader reports.

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT_DIR"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"

VERSION="$(python3 -c 'import json; print(json.load(open("manifest.json"))["version"])')"
PACKAGE_NAME="MasonLine-$VERSION"
STAGE_DIR="dist/$PACKAGE_NAME"
ARCHIVE="dist/$PACKAGE_NAME.zip"

rm -rf "$STAGE_DIR" "$ARCHIVE"
dotnet build -c Release | tail -3
mkdir -p "$STAGE_DIR/MasonLine"
cp bin/Release/MasonLine.dll "$STAGE_DIR/MasonLine.dll"
cp manifest.json "$STAGE_DIR/MasonLine/manifest.json"

(
    cd "$STAGE_DIR"
    zip -qr "../$PACKAGE_NAME.zip" MasonLine.dll MasonLine
)
rm -rf "$STAGE_DIR"

echo
echo "Created $ARCHIVE"
unzip -l "$ARCHIVE"
