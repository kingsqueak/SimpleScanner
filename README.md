# SimpleScanner

A desktop communications scanner for the SDRplay RSPdx, for Windows, macOS and Linux
(.NET 10, Avalonia). Early days: an analogue receiver with a fast scan, and the first two
digital voice modes.

## What works now

- AM (airband, AM broadcast), narrow FM (public service, marine, weather, amateur) and
  broadcast FM, in stereo when the station sends it
- Tuning by typing a frequency, by step buttons, or by rolling the mouse wheel over a digit
- Mode, filter and step chosen from a United States band plan, or by hand
- Squelch, a level bar, and automatic front-end gain
- Stored channels in groups, a scan over them that looks at several megahertz at a time, and
  a search between two frequencies
- P25 Phase 1 (conventional): decoded speech, with the NAC, talkgroup and radio ID on the display
- DMR (Tier II conventional: repeaters, and mobiles heard directly): decoded speech, with the
  color code, time slot, talkgroup, radio ID and talker alias. It follows one of the two time
  slots at a time: the first with a call
- Encrypted calls are shown and stay silent
- Cellular telephone bands are blocked

Not yet: trunk following, P25 Phase 2, NXDN, D-STAR, C4FM, RadioReference import.

### DMR speech and the AMBE+2 codec

DMR speech is coded with AMBE+2, which is covered by a patent (US 8,359,197, listed as in force
until May 2028). The decoder (mbelib) is therefore a library of its own, `native/ambe`, and a
build can leave it out:

    dotnet build -p:IncludeAmbe=false
    NO_AMBE=1 scripts/make-dmg.sh

Without it the scanner still follows DMR and shows who is talking; the display says
"no voice codec" and the call is silent.

## What you need

- An SDRplay RSPdx or RSPdx-R2 (other single-tuner RSPs stream too)
- The SDRplay API 3.x, from <https://www.sdrplay.com/api/>. It is not included.

## Building

    dotnet build
    dotnet test
    dotnet run --project src/SimpleScanner            # add "-- --start" to switch the radio on at launch
    scripts/make-dmg.sh                                # macOS app and DMG

The native libraries are built by the first `dotnet build` on macOS and Linux; they need a C
compiler (gcc or clang): audio output (`native/audio`, miniaudio behind a small C shim),
digital voice (`native/digital`, P25 and DMR) and the AMBE+2 decoder (`native/ambe`). After
changing C code, run that library's `build.sh` again.

`tools/rsp-bench` measures the radio without the window: sample throughput and retune times,
`rsp-bench listen <MHz> [seconds]` to run the receiver on one frequency, or
`rsp-bench hunt <from MHz> <to MHz> [seconds] [squelch]` to list what is on the air in a range
and what kind of signal each is.

## Licence

GPL-3.0. miniaudio (`native/audio/miniaudio.h`) is public domain or MIT-0, its authors' choice.
The digital voice library is built on a part of RadioFoundry (`native/digital/radiofoundry`,
MIT, with an IMBE dequantizer from mbelib, ISC). The AMBE+2 decoder is mbelib
(`native/ambe/mbelib`, ISC).
