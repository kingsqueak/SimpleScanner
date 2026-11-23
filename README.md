# SimpleScanner

A desktop communications scanner for the SDRplay RSPdx, for Windows, macOS and Linux
(.NET 10, Avalonia). Early days: this is the analogue receiver the scanner will be built on.

## What works now

- AM (airband, AM broadcast), narrow FM (public service, marine, weather, amateur) and
  broadcast FM, in stereo when the station sends it
- Tuning by typing a frequency, by step buttons, or by rolling the mouse wheel over a digit
- Mode, filter and step chosen from a United States band plan, or by hand
- Squelch, a level bar, and automatic front-end gain
- Cellular telephone bands are blocked

Not yet: scanning, memories, digital voice (P25, DMR, NXDN, D-STAR, C4FM), RadioReference import.

## What you need

- An SDRplay RSPdx or RSPdx-R2 (other single-tuner RSPs stream too)
- The SDRplay API 3.x, from <https://www.sdrplay.com/api/>. It is not included.

## Building

    dotnet build
    dotnet test
    dotnet run --project src/SimpleScanner            # add "-- --start" to switch the radio on at launch
    scripts/make-dmg.sh                                # macOS app and DMG

The audio output library (miniaudio behind a small C shim, `native/audio`) is built by the
first `dotnet build` on macOS and Linux; it needs a C compiler.

`tools/rsp-bench` measures the radio without the window: sample throughput and retune times,
or `rsp-bench listen <MHz> [seconds]` to run the receiver on one frequency.

## Licence

GPL-3.0. miniaudio (`native/audio/miniaudio.h`) is public domain or MIT-0, its authors' choice.
