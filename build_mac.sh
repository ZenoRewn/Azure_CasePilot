#!/usr/bin/env bash
# 构建 macOS .app 与 zip (需要: python3 -m pip install pyinstaller certifi)
set -euo pipefail
cd "$(dirname "$0")"
VER=${1:-dev}
PY=${PY:-python3}
rm -rf build dist
mkdir -p build/icon.iconset
for s in 16 32 64 128 256; do sips -z $s $s static/logo.png --out build/icon.iconset/icon_${s}x${s}.png >/dev/null; done
iconutil -c icns build/icon.iconset -o build/CasePilot.icns
"$PY" -m PyInstaller --noconfirm --windowed --name CasePilot --icon build/CasePilot.icns \
  --add-data "static:static" --hidden-import certifi --osx-bundle-identifier com.zenorewn.casepilot server.py
(cd dist && ditto -c -k --keepParent CasePilot.app "CasePilot-${VER}-macOS-$(uname -m).zip")
echo "dist/CasePilot-${VER}-macOS-$(uname -m).zip"
