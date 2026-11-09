#!/bin/bash
# 编译 macOS 版 Claude Tools（通用二进制 arm64 + x86_64）并打包 .dmg
# 用法：bash mac/build.sh        产物：mac/build/Claude Tools.app、mac/build/Claude-Tools-mac.dmg
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(cd .. && pwd)"
OUT="$PWD/build"
APP="$OUT/Claude Tools.app"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

SRCS=(Sources/*.swift)
for ARCH in arm64 x86_64; do
  echo "== 编译 $ARCH"
  swiftc -O -swift-version 5 -target "$ARCH-apple-macos12.0" -module-name ClaudeTools \
    -o "$OUT/ClaudeTools-$ARCH" "${SRCS[@]}" \
    -framework AppKit -framework WebKit -framework Security -framework UserNotifications
done
lipo -create -output "$APP/Contents/MacOS/ClaudeTools" "$OUT/ClaudeTools-arm64" "$OUT/ClaudeTools-x86_64"
lipo -info "$APP/Contents/MacOS/ClaudeTools"

cp Info.plist "$APP/Contents/Info.plist"
cp "$ROOT/src/ui.html" "$APP/Contents/Resources/ui.html"
cp "$ROOT/src/fonts/zcool-kuaile.woff2" "$APP/Contents/Resources/font.woff2"
cp "$ROOT/src/fonts/OFL.txt" "$APP/Contents/Resources/OFL.txt"
cp "$ROOT/docs/logo.png" "$APP/Contents/Resources/logo.png"
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE.txt"

# 图标
ICONSET="$OUT/AppIcon.iconset"
mkdir -p "$ICONSET"
for S in 16 32 128 256 512; do
  sips -z $S $S Resources/AppIcon-1024.png --out "$ICONSET/icon_${S}x${S}.png" >/dev/null
  D=$((S * 2))
  sips -z $D $D Resources/AppIcon-1024.png --out "$ICONSET/icon_${S}x${S}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# 本地签名（ad-hoc，没有开发者证书）
codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP"

# 打包 dmg：App + 「应用程序」快捷方式 + 首次打开说明
DMG_ROOT="$OUT/dmg"
mkdir -p "$DMG_ROOT"
cp -R "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/应用程序"
cp Resources/首次打开说明.txt "$DMG_ROOT/"
hdiutil create -volname "Claude Tools" -srcfolder "$DMG_ROOT" -ov -format UDZO "$OUT/Claude-Tools-mac.dmg"
ls -la "$OUT"
