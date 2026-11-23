#!/bin/sh
# Build the AMBE speech decoders (mbelib, ISC: AMBE+2 for DMR and NXDN, the older AMBE for D-STAR) for this machine into native/ambe/out.
# Optional: see ss_ambe.c for why it is a library of its own.
set -eu
cd "$(dirname "$0")"
mkdir -p out
SRC="ss_ambe.c mbelib/ambe3600x2450.c mbelib/ambe3600x2400.c mbelib/ecc.c mbelib/mbelib.c"
FLAGS="-O2 -std=gnu11 -fvisibility=hidden -w"
case "$(uname -s)" in
  Darwin) cc $FLAGS -dynamiclib -o out/libss_ambe.dylib $SRC ;;
  *)      cc $FLAGS -fPIC -shared -o out/libss_ambe.so $SRC -lm ;;
esac
ls -l out
