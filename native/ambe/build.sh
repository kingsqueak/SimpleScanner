#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Build the AMBE speech decoders (mbelib, ISC: AMBE+2 for DMR and NXDN, the older AMBE for D-STAR) for this machine into native/ambe/out.
# Optional: see ss_ambe.c for why it is a library of its own.
# macOS: one library for both Apple silicon and Intel. Windows: run in Git Bash with MinGW's gcc.
set -eu
cd "$(dirname "$0")"
mkdir -p out
SRC="ss_ambe.c mbelib/ambe3600x2450.c mbelib/ambe3600x2400.c mbelib/ecc.c mbelib/mbelib.c"
FLAGS="-O2 -std=gnu11 -w"
case "$(uname -s)" in
  Darwin)
    export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-14.0}"
    ${CC:-cc} $FLAGS -fvisibility=hidden -arch arm64 -arch x86_64 -dynamiclib -o out/libss_ambe.dylib $SRC ;;
  MINGW*|MSYS*|CYGWIN*)
    ${CC:-gcc} $FLAGS -D_USE_MATH_DEFINES -shared -static -o out/ss_ambe.dll $SRC ;;
  *)
    ${CC:-cc} $FLAGS -fvisibility=hidden -fPIC -shared -o out/libss_ambe.so $SRC -lm ;;
esac
ls -l out
