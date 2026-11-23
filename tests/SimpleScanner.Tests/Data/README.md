# Test signals

Made with RadioFoundry's `tools/bench/p25/p25tool` (MIT) from its synthesized test speech
(`tests/data/voice/test_transmission.imbe`); nobody's voice or traffic is in these files.

- `p25_call_24k.raw`: a P25 Phase 1 voice call as FM discriminator samples, 24 kHz signed
  16-bit, 6553.6 counts per kHz. NAC 0x293, talkgroup 1234, radio 5678, clear.
  (`p25tool gen ... --nac 0x293 --tg 1234 --src 5678 --voice-file ...; p25tool mod ... --rate 24000`)
- `p25_control_24k.raw`: a P25 control channel, same format. NAC 0x293, system 0x123, WACN 0xBEE00.
- `p25_call_voice_8k.raw`: the speech the call should decode to, 8 kHz signed 16-bit.

## DMR

Made with `native/digital/tools/dmrgen.c` (this repository; it uses RadioFoundry's encoders)
from RadioFoundry's synthesized test speech (`tests/data/voice/test_transmission.ambe`). Same
sample format as the P25 files.

- `dmr_repeater_24k.raw`: a repeater's output with a group call on slot 2 and idle bursts on
  slot 1. Color code 7, talkgroup 3133, radio 3133001, talker alias "KC2RGW Chris".
  (`dmrgen bs OUT VOICE.ambe --slot 2 --cc 7 --dst 3133 --src 3133001 --alias "KC2RGW Chris"`)
- `dmr_mobile_late_24k.raw`: a mobile's bursts (every other slot, nothing between), with the
  voice header left out. Color code 3, talkgroup 9, radio 1234567.
  (`dmrgen ms OUT VOICE.ambe --cc 3 --dst 9 --src 1234567 --late`)
- `dmr_direct_private_24k.raw`: two-slot direct mode, slot 2, a call to one radio with the
  privacy and emergency bits set. Color code 1, to radio 99, from radio 42.
  (`dmrgen direct OUT VOICE.ambe --slot 2 --cc 1 --dst 99 --src 42 --private --privacy --emergency`)
- `dmr_voice_reference_8k.raw`: RadioFoundry's reference decode of the same AMBE+2 frames
  (`test_transmission_decoded.wav` without its header), 8 kHz signed 16-bit. Our decoder is a
  different one (mbelib), so the tests compare loudness frame by frame, not samples.

## NXDN

Made with RadioFoundry's `tools/bench/nxdn/nxdntool` (MIT) from the same synthesized test
speech (`test_transmission.ambe`: NXDN carries the same AMBE+2 frames as DMR, so
`dmr_voice_reference_8k.raw` is the reference for these too). Same sample format.

- `nxdn_4800_24k.raw`: 4800 bit/s (6.25 kHz channel). RAN 5, group 200, radio 1001.
  (`nxdntool gen ... --ran 5 --group 200 --src 1001 --voice-frames 104 --voice-file ... --lead 200 --tail 200;
  nxdntool mod ... --rate 24000 --full 5000`)
- `nxdn_9600_24k.raw`: 9600 bit/s (12.5 kHz channel). RAN 12, a call to radio 77 from radio
  4321, emergency. (the same with `--9600 --ran 12 --unit 77 --src 4321 --emergency`)

## D-STAR

Made with `native/digital/tools/dstargen.c` (this repository, written from the D-STAR
specification). Same sample format. The speech is D-STAR's silence frame repeated: there is no
open encoder for its codec, so these files test the framing, the header, the slow data and that
voice frames reach the vocoder without bit errors, not what speech sounds like.

- `dstar_call_24k.raw`: a whole transmission, 105 voice frames. From KC2RGW/IC91 to CQCQCQ
  through NE1DS C and NE1DS G, message "SimpleScanner test".
  (`dstargen OUT --my KC2RGW --suffix IC91 --your CQCQCQ --rpt1 "NE1DS  C" --rpt2 "NE1DS  G" --text "SimpleScanner test"`)
- `dstar_late_inverted_24k.raw`: the same kind of transmission without its bit sync, frame sync
  and header, with the deviation turned over. From W1AW, message "late and inverted".
  (`dstargen OUT --my W1AW --late --invert --text "late and inverted"`)
