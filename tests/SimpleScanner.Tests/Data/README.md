# Test signals

Made with RadioFoundry's `tools/bench/p25/p25tool` (MIT) from its synthesized test speech
(`tests/data/voice/test_transmission.imbe`); nobody's voice or traffic is in these files.

- `p25_call_24k.raw`: a P25 Phase 1 voice call as FM discriminator samples, 24 kHz signed
  16-bit, 6553.6 counts per kHz. NAC 0x293, talkgroup 1234, radio 5678, clear.
  (`p25tool gen ... --nac 0x293 --tg 1234 --src 5678 --voice-file ...; p25tool mod ... --rate 24000`)
- `p25_control_24k.raw`: a P25 control channel, same format. NAC 0x293, system 0x123, WACN 0xBEE00.
- `p25_call_voice_8k.raw`: the speech the call should decode to, 8 kHz signed 16-bit.
