#!/usr/bin/env bash
set -euo pipefail

# Builds the upload archive. The Mods/ prefix is not a taste question: RedLoader's own install page
# tells players to extract the zip into the GAME folder and end up with a dll plus a same-named
# folder inside Mods/, and Red Manager on sotf-mods.com installs from these archives unattended.
# Verified against a published mod rather than the docs alone - stackmod 1.4.0 downloaded from
# sotf-mods.com contains exactly Mods/StackMod.dll and Mods/StackMod/manifest.json. An archive
# without the prefix installs to the wrong place for anyone who follows the standard instructions.
# Version comes from manifest.json so the file name can never disagree with what the loader reports.

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT_DIR"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"

VERSION="$(python3 -c 'import json; print(json.load(open("manifest.json"))["version"])')"
PACKAGE_NAME="MasonLine-$VERSION"
STAGE_DIR="dist/$PACKAGE_NAME"
ARCHIVE="dist/$PACKAGE_NAME.zip"

rm -rf "$STAGE_DIR" "$ARCHIVE"
dotnet build -c Release | tail -3
mkdir -p "$STAGE_DIR/Mods/MasonLine"
cp bin/Release/MasonLine.dll "$STAGE_DIR/Mods/MasonLine.dll"
cp manifest.json "$STAGE_DIR/Mods/MasonLine/manifest.json"

(
    cd "$STAGE_DIR"
    zip -qr "../$PACKAGE_NAME.zip" Mods
)
rm -rf "$STAGE_DIR"

echo
echo "Created $ARCHIVE"
unzip -l "$ARCHIVE"
