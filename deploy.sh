#!/bin/bash
set -e
GAME_DIR="/Users/cyace84/Library/Application Support/CrossOver/Bottles/Steam-2/drive_c/Program Files (x86)/Steam/steamapps/common/Sons Of The Forest"
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build | tail -8
mkdir -p "$GAME_DIR/Mods/MasonLine"
cp bin/Debug/MasonLine.dll "$GAME_DIR/Mods/MasonLine.dll"
cp manifest.json "$GAME_DIR/Mods/MasonLine/manifest.json"
echo "deployed -> $GAME_DIR/Mods/MasonLine.dll"
