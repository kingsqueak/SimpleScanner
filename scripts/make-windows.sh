#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Build the Windows program folder: SimpleScanner.exe with its native libraries beside it. The installer
# (scripts/windows-installer.iss) is made from it.
# Usage: scripts/make-windows.sh   (Git Bash on Windows, with MinGW's gcc on the PATH for the C libraries)
# Output: artifacts/win/SimpleScanner-<version>
#
# win-x64, self-contained (no .NET install needed). The SDRplay API is not included: it comes from
# SDRplay's own installer (sdrplay.com/api). Not signed, so Windows warns on the first run.
# NO_AMBE=1 leaves the AMBE speech decoders out (see the README).
set -euo pipefail
cd "$(dirname "$0")/.."
rid=win-x64
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleScanner/SimpleScanner.csproj | tr -d '\r')
ambe=true
[ -n "${NO_AMBE:-}" ] && ambe=false
out=artifacts/win
app="$out/SimpleScanner-$version"

rm -rf "$out"
mkdir -p "$app"

echo "== native libraries"
gcc --version | head -1
for lib in audio digital $([ "$ambe" = true ] && echo ambe); do
  sh "native/$lib/build.sh"
done

echo "== app ($rid)"
# RadioReference's application key (see SimpleScanner.csproj): say whether this build carries it.
if [ -n "${RADIOREFERENCE_KEY:-}" ] || [ -s radioreference.key ]; then
  echo "RadioReference application key: built in"
else
  echo "${GITHUB_ACTIONS:+::warning::}No RadioReference application key (RADIOREFERENCE_KEY or radioreference.key): the RadioReference browser will not work in this build" >&2
fi
rm -rf "src/SimpleScanner/obj/Release/net10.0/$rid" "src/SimpleScanner/bin/Release/net10.0/$rid"
dotnet publish src/SimpleScanner/SimpleScanner.csproj -c Release -r "$rid" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -p:UseAppHost=true -p:IncludeAmbe="$ambe" -o "$app"
find "$app" -name '*.pdb' -delete
for f in SimpleScanner.exe ss_audio.dll ss_digital.dll $([ "$ambe" = true ] && echo ss_ambe.dll) LICENSE THIRD-PARTY-NOTICES.txt; do
  [ -f "$app/$f" ] || { echo "$f is missing from the program folder" >&2; exit 1; }
done

# What each C library needs from Windows: only system DLLs, nothing from MinGW.
if command -v objdump > /dev/null; then
  for dll in "$app"/ss_*.dll; do
    deps=$(objdump -p "$dll" | sed -n 's/^\s*DLL Name: //p' | tr '\n' ' ')
    echo "$(basename "$dll"): $deps"
    if echo "$deps" | grep -qiE 'libgcc|libwinpthread|libstdc'; then echo "$dll needs a MinGW DLL" >&2; exit 1; fi
  done
fi

du -sh "$app"
echo "folder -> $app"
