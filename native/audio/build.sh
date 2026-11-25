#!/bin/sh
# Build the audio output library (and the USB replug of ss_usb.c) for this machine into native/audio/out.
# Windows: build ss_audio.c as ss_audio.dll with MSVC or clang (cl /LD /O2 ss_audio.c ss_usb.c).
set -eu
cd "$(dirname "$0")"
mkdir -p out
case "$(uname -s)" in
  Darwin)
    cc -O2 -fvisibility=hidden -dynamiclib -o out/libss_audio.dylib ss_audio.c ss_usb.c \
      -framework CoreAudio -framework AudioToolbox -framework CoreFoundation -framework AudioUnit -framework IOKit ;;
  *)
    cc -O2 -fPIC -fvisibility=hidden -shared -o out/libss_audio.so ss_audio.c ss_usb.c -lpthread -lm -ldl ;;
esac
ls -l out
