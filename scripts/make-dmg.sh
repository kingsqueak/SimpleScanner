#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Build SimpleScanner.app and a DMG for macOS.
# Usage: scripts/make-dmg.sh [rid]   (osx-arm64 for Apple silicon, the default; osx-x64 for Intel)
# Output: artifacts/mac/SimpleScanner.app and artifacts/SimpleScanner-<version>-<arch>.dmg
#
# Self-contained (no .NET install needed on the Mac it runs on). The SDRplay API is not
# included: it comes from SDRplay's own installer (sdrplay.com/api). Ad-hoc signed, not
# notarized: the first time, open it with right-click > Open, or allow it in
# System Settings > Privacy & Security.
#
# NO_AMBE=1 leaves the AMBE+2 speech decoder out (see the README): DMR calls are then shown, not heard.
set -euo pipefail
cd "$(dirname "$0")/.."
# Homebrew's dotnet bundles its own runtime, linked to Homebrew's libraries, which
# wouldn't run on other Macs. Use Microsoft's when it's installed.
[ -x /usr/local/share/dotnet/dotnet ] && export PATH="/usr/local/share/dotnet:$PATH"
rid="${1:-osx-arm64}"
arch="${rid#osx-}"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleScanner/SimpleScanner.csproj)
out=artifacts/mac
app="$out/SimpleScanner.app"
stage="$out/stage"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$stage"

# The native libraries are built again here for the oldest macOS the app says it runs on
# (LSMinimumSystemVersion below). Built without this they carry this Mac's own macOS version
# as their minimum, and an older macOS refuses to load them.
export MACOSX_DEPLOYMENT_TARGET=14.0
echo "== native libraries (macOS $MACOSX_DEPLOYMENT_TARGET and later, Apple silicon and Intel)"
for lib in audio digital $([ -n "${NO_AMBE:-}" ] || echo ambe); do
  sh "native/$lib/build.sh" >/dev/null
done
case "$arch" in x64) mach=x86_64 ;; *) mach="$arch" ;; esac

echo "== app ($rid)"
rm -rf "src/SimpleScanner/obj/Release/net10.0/$rid" "src/SimpleScanner/bin/Release/net10.0/$rid"
# DebugType=none: no debug record in the program, which would carry the build folder's path.
# The intermediate folder is removed first, since a change of this setting alone does not compile again.
dotnet publish src/SimpleScanner/SimpleScanner.csproj -c Release -r "$rid" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:UseAppHost=true -p:IncludeAmbe="$([ -n "${NO_AMBE:-}" ] && echo false || echo true)" -o "$app/Contents/MacOS"
find "$app/Contents" -name '*.pdb' -delete

cp LICENSE THIRD-PARTY-NOTICES.txt src/SimpleScanner/Assets/AppIcon.icns "$app/Contents/Resources/"

# Every native library must hold code for the Mac this DMG is for.
for lib in "$app/Contents/MacOS/"*.dylib; do
  lipo -archs "$lib" | tr ' ' '\n' | grep -qx "$mach" || { echo "$lib has no $mach code: $(lipo -archs "$lib")" >&2; exit 1; }
done

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SimpleScanner</string>
  <key>CFBundleDisplayName</key><string>SimpleScanner</string>
  <key>CFBundleIdentifier</key><string>com.kc2rgw.simplescanner</string>
  <key>CFBundleExecutable</key><string>SimpleScanner</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright (C) 2026 KC2RGW Chris. GPL-3.0-or-later.</string>
</dict>
</plist>
PLIST

echo "== sign (ad-hoc)"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

echo "== dmg"
ditto "$app" "$stage/SimpleScanner.app"
ln -s /Applications "$stage/Applications"
dmg="artifacts/SimpleScanner-$version-$arch.dmg"
rm -f "$dmg"
hdiutil create -volname "SimpleScanner $version" -srcfolder "$stage" -format UDZO -ov "$dmg" >/dev/null
rm -rf "$stage"
du -sh "$app" "$dmg"
echo "dmg -> $dmg"
