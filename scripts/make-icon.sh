#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Make the app icons from src/SimpleScanner/Assets/icon.svg:
#   icon.png      1024 px, the window icon (and the Linux desktop icon)
#   AppIcon.icns  the macOS bundle's icon
#   icon.ico      the Windows program's icon
# Run it again after changing the SVG, and commit the results.
# Needs rsvg-convert (librsvg), magick (ImageMagick) and, for the .icns, macOS's iconutil.
set -euo pipefail
cd "$(dirname "$0")/../src/SimpleScanner/Assets"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# A PNG at a given size, with no metadata in it.
png() { rsvg-convert -w "$1" -h "$1" icon.svg -o "$tmp/raw.png" && magick "$tmp/raw.png" -strip "$2"; }

png 1024 icon.png

set="$tmp/AppIcon.iconset"
mkdir "$set"
for n in 16 32 128 256 512; do
  png "$n" "$set/icon_${n}x${n}.png"
  png $((n * 2)) "$set/icon_${n}x${n}@2x.png"
done
iconutil -c icns "$set" -o AppIcon.icns

for n in 16 32 48 64 128 256; do png "$n" "$tmp/ico-$n.png"; done
magick "$tmp"/ico-{256,128,64,48,32,16}.png icon.ico
ls -l icon.png AppIcon.icns icon.ico
