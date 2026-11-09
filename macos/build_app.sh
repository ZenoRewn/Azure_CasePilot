#!/usr/bin/env bash
# 构建原生 CasePilot.app 与发布 zip: ./build_app.sh v1.1.0
set -euo pipefail
cd "$(dirname "$0")"
VER=${1:-v0.0.0}; NUM=${VER#v}
swift build -c release --arch arm64 --arch x86_64
BIN=$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/CasePilot
APP=dist/CasePilot.app
rm -rf dist build && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" build/icon.iconset
cp "$BIN" "$APP/Contents/MacOS/CasePilot"
for s in 16 32 64 128 256; do sips -z $s $s ../static/logo.png --out build/icon.iconset/icon_${s}x${s}.png >/dev/null; done
iconutil -c icns build/icon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>CasePilot</string>
  <key>CFBundleDisplayName</key><string>CasePilot</string>
  <key>CFBundleIdentifier</key><string>com.zenorewn.casepilot</string>
  <key>CFBundleExecutable</key><string>CasePilot</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${NUM}</string>
  <key>CFBundleVersion</key><string>${NUM}</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>用于语音输入问题描述与补充信息 (经 Azure Speech / Azure OpenAI 转写)。</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict></plist>
PLIST
codesign --force --deep --sign "${SIGN_ID:-Apple Development: zeno.ren@me.com (W96G7VCMTX)}" "$APP" 2>/dev/null || codesign --force --deep --sign - "$APP"
(cd dist && ditto -c -k --keepParent CasePilot.app "CasePilot-${VER}-macOS-universal.zip")
echo "dist/CasePilot-${VER}-macOS-universal.zip"
