#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Build the audio output library (and the USB replug of ss_usb.c) for this machine into native/audio/out.
# macOS: one library for both Apple silicon and Intel. Windows: run in Git Bash with MinGW's gcc.
set -eu
cd "$(dirname "$0")"
mkdir -p out
case "$(uname -s)" in
  Darwin)
    export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-14.0}"
    ${CC:-cc} -O2 -fvisibility=hidden -arch arm64 -arch x86_64 -dynamiclib -o out/libss_audio.dylib ss_audio.c ss_usb.c \
      -framework CoreAudio -framework AudioToolbox -framework CoreFoundation -framework AudioUnit -framework IOKit ;;
  MINGW*|MSYS*|CYGWIN*)
    ${CC:-gcc} -O2 -shared -static-libgcc -o out/ss_audio.dll ss_audio.c ss_usb.c -lole32 ;;
  *)
    ${CC:-cc} -O2 -fPIC -fvisibility=hidden -shared -o out/libss_audio.so ss_audio.c ss_usb.c -lpthread -lm -ldl ;;
esac
ls -l out
