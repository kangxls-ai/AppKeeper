#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
output="${1:-artifacts/mac}"
mkdir -p "$output/EasyAppKeeper.app/Contents/MacOS" "$output/EasyAppKeeper.app/Contents/Resources"
app="$output/EasyAppKeeper.app"
dotnet publish src/AppKeeper.Mac/AppKeeper.Mac.csproj -c Release -r osx-arm64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o "$app/Contents/MacOS"
xcrun swiftc packaging/mac/Native.swift -o "$app/Contents/MacOS/EasyAppKeeper.Native" -framework AppKit -framework Carbon -framework Security -target arm64-apple-macosx14.0 -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker packaging/mac/HelperInfo.plist
xcrun swiftc packaging/mac/StatusItem.swift -emit-library -o "$app/Contents/MacOS/EasyAppKeeper.StatusItem.dylib" -framework AppKit -target arm64-apple-macosx14.0
cp packaging/mac/Info.plist "$app/Contents/Info.plist"
cp THIRD-PARTY-NOTICES.txt "$app/Contents/Resources/THIRD-PARTY-NOTICES.txt"
python3 packaging/mac/licenses.py "$app/Contents/Resources"
# Source is the existing project icon; the Mac bundle needs an ICNS representation.
iconset="$output/AppIcon.iconset"
mkdir -p "$iconset"
sips -s format png packaging/store/Assets/Square150x150.png --out "$output/icon-source.png" >/dev/null
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$output/icon-source.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  twice=$((size*2))
  sips -z "$twice" "$twice" "$output/icon-source.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
# Ad-hoc signing satisfies Apple Silicon executable integrity; this is NOT Developer ID signing or notarization.
find "$app/Contents/MacOS" -type f \( -name '*.dylib' -o -name 'EasyAppKeeper.Native' \) -exec codesign --force --sign - {} \;
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"
"$app/Contents/MacOS/EasyAppKeeper" --self-test | tee "$output/self-test.txt"
ditto -c -k --sequesterRsrc --keepParent "$app" "$output/EasyAppKeeper-1.4.7-mac-arm64.zip"
mkdir -p "$output/dmg-content"
ditto "$app" "$output/dmg-content/EasyAppKeeper.app"
ln -sfn /Applications "$output/dmg-content/Applications"
hdiutil create -volname EasyAppKeeper -srcfolder "$output/dmg-content" -ov -format UDZO "$output/EasyAppKeeper-1.4.7-mac-arm64.dmg"
