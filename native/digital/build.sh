#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Build the digital voice library (P25 Phase 1, DMR, NXDN, D-STAR and System Fusion, on RadioFoundry) for this machine into
# native/digital/out. Needs gcc or clang. macOS: one library for both Apple silicon and Intel. Windows: run in Git Bash
# with MinGW's gcc (not MSVC).
set -eu
cd "$(dirname "$0")"
mkdir -p out
RF=radiofoundry/core
SRC="p25rx.c dmrrx.c nxdnrx.c dstarrx.c ysfrx.c $RF/nxdn/nxdn_frame.c $RF/nxdn/nxdn_rx.c $RF/nxdn/nxdn_call.c $RF/fec/conv.c
     $RF/dmr/fec/dmrFec.c $RF/dmr/fec/dmrCach.c $RF/dmr/call/dmr_lc.c $RF/dmr/ambe/ambeFec.c
     $RF/air/air_softmodem.c $RF/air/air_framer.c $RF/p25/p25_unit.c $RF/p25/p25_rx.c $RF/p25/p25_call.c
     $RF/p25/p25_tsbk.c $RF/fec/bch63.c $RF/fec/gf64.c $RF/fec/golay.c $RF/fec/hamming.c $RF/fec/rs63.c $RF/fec/trellis.c
     $RF/mbe/imbe_dec.c $RF/mbe/imbe_tab.c $RF/mbe/mbe_synth.c $RF/mbe/mbe_dsp.c $RF/mbe/mbe_tab.c"
FLAGS="-I$RF -O2 -std=gnu11 -DCONFIG_AIR=1 -DCONFIG_P25=1 -DCONFIG_P25_TRUNK=1 -DCONFIG_NXDN=1 -DFXP_FORCE_PLAIN"
case "$(uname -s)" in
  Darwin)
    export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-14.0}"
    ${CC:-cc} $FLAGS -fvisibility=hidden -arch arm64 -arch x86_64 -dynamiclib -o out/libss_digital.dylib $SRC ;;
  MINGW*|MSYS*|CYGWIN*)
    ${CC:-gcc} $FLAGS -D_USE_MATH_DEFINES -shared -static-libgcc -o out/ss_digital.dll $SRC ;;
  *)
    ${CC:-cc} $FLAGS -fvisibility=hidden -fPIC -shared -o out/libss_digital.so $SRC -lm ;;
esac
ls -l out
