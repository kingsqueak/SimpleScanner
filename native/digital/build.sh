#!/bin/sh
# Build the digital voice library (P25 Phase 1, from RadioFoundry) for this machine into
# native/digital/out. Needs gcc or clang; on Windows use clang or MinGW, not MSVC.
set -eu
cd "$(dirname "$0")"
mkdir -p out
RF=radiofoundry/core
SRC="p25rx.c $RF/air/air_softmodem.c $RF/air/air_framer.c $RF/p25/p25_unit.c $RF/p25/p25_rx.c $RF/p25/p25_call.c
     $RF/p25/p25_tsbk.c $RF/fec/bch63.c $RF/fec/gf64.c $RF/fec/golay.c $RF/fec/hamming.c $RF/fec/rs63.c $RF/fec/trellis.c
     $RF/mbe/imbe_dec.c $RF/mbe/imbe_tab.c $RF/mbe/mbe_synth.c $RF/mbe/mbe_dsp.c $RF/mbe/mbe_tab.c"
FLAGS="-O2 -std=gnu11 -fvisibility=hidden -DCONFIG_AIR=1 -DCONFIG_P25=1 -DCONFIG_P25_TRUNK=1 -DFXP_FORCE_PLAIN"
case "$(uname -s)" in
  Darwin) cc $FLAGS -dynamiclib -o out/libss_digital.dylib $SRC ;;
  *)      cc $FLAGS -fPIC -shared -o out/libss_digital.so $SRC ;;
esac
ls -l out
