# Test signals

Made with RadioFoundry's `tools/bench/p25/p25tool` (MIT) from its synthesized test speech
(`tests/data/voice/test_transmission.imbe`); nobody's voice or traffic is in these files.

- `p25_call_24k.raw`: a P25 Phase 1 voice call as FM discriminator samples, 24 kHz signed
  16-bit, 6553.6 counts per kHz. NAC 0x293, talkgroup 1234, radio 5678, clear.
  (`p25tool gen ... --nac 0x293 --tg 1234 --src 5678 --voice-file ...; p25tool mod ... --rate 24000`)
- `p25_control_24k.raw`: a P25 control channel, same format. NAC 0x293, system 0x123, WACN 0xBEE00.
- `p25_call_voice_8k.raw`: the speech the call should decode to, 8 kHz signed 16-bit.

## P25 trunking

Made with `native/digital/tools/p25gen.c` (this repository; it uses RadioFoundry's encoders)
from a script of control channel messages. Same sample format as the P25 files above.

- `p25_trunk_control_24k.raw`: a trunked site's control channel, from `p25_trunk_control.txt`
  (its comments give the frequencies). NAC 0x8A1, system 0x8A6, WACN 0xBEE00, RFSS 1, site 1:
  the RFSS and network status, the three channel identifier forms (800 MHz, VHF, TDMA), the
  system services, group grants in every form (one TDMA, one encrypted, one emergency), unit
  to unit grants, an adjacent site, both secondary control channel forms, a block with its
  CRC broken, a Motorola block, and four multi-block messages (group grant, network, RFSS and
  adjacent status): seven TSDUs and four packets.
  (`p25gen p25_trunk_control.txt OUT`)
- `p25_trunk_follow_24k.raw`: a smaller control channel for the trunk engine's tests, from
  `p25_trunk_follow.txt`: NAC 0x293, system 0x123, WACN 0xBEE00, RFSS 1, site 1, control
  channel 851.0125 MHz. Its grants: talkgroup 1234 on 851.5125 MHz (inside a 2.4 MS/s radio's
  span with the control channel), 1235 on 853.0125 (outside it), 5005 encrypted, 6006, and
  4004 on TDMA (851.5125, slot 1). The tests put `p25_call_24k.raw` on the granted frequency.
  (`p25gen p25_trunk_follow.txt OUT`)
- `p25_trunk_tdma_24k.raw`: a control channel granting Phase 2 calls, from `p25_trunk_tdma.txt`: the
  same site as `p25_trunk_follow_24k.raw` (NAC 0x293, system 0x123, WACN 0xBEE00), with only two
  grants, talkgroup 1001 on slot 0 and 1002 on slot 1 of 851.5125 MHz (TDMA identifier 3), the
  calls of `p25p2_voice_48k.raw`. The tests put a Phase 2 channel on that frequency.
  (`p25gen p25_trunk_tdma.txt OUT`)

### CQPSK (simulcast)

Made with the same tool and scripts as a CQPSK (LSM) transmitter sends them (`p25gen --cqpsk`): each
symbol a phase step of its level times pi/4, a raised cosine pulse (roll-off 0.2) on I and Q.
Complex baseband at 48 kHz, signed 16-bit I and Q interleaved, 16384 = 1.0, amplitude 0.5 at the
symbol centres, nothing (zeros) while the transmitter is off.

- `p25_trunk_control_cqpsk_48k.raw`: `p25_trunk_control.txt` as a CQPSK transmitter sends it.
  (`p25gen p25_trunk_control.txt OUT --cqpsk --iq16`)
- `p25_trunk_control_echo_48k.raw`: the same script twice, as a receiver between two simulcast
  transmitters hears it: a second copy 30 us later, 2 dB down and 3 Hz up.
  (`p25gen p25_trunk_control.txt OUT --cqpsk --iq16 --repeat 2 --echo 30 -2 3`)
- `p25_trunk_follow_simulcast_48k.raw`: `p25_trunk_follow.txt` with the same echo, for the trunk
  engine's test on a simulcast site. (`p25gen p25_trunk_follow.txt OUT --cqpsk --iq16 --echo 30 -2 3`)

The differential front end was also run against off-air recordings of a simulcast control channel
(`rsp-bench record`, kept in `artifacts/captures/`, which git ignores): `P25CqpskTests` has a test
that runs on one of them where the file is there and is skipped where it is not.

## P25 Phase 2 (TDMA)

There is no Phase 2 signal to record here, so the receiver's only signals are made by
`native/digital/tools/p25p2gen.c` (this repository; its build line is at its top), which is
built from the receiver's own parts. So that the two cannot share a mistake unseen, the codes and
tables were checked against op25's and sdrtrunk's own code (GPL-3), and the values below were
made by op25's code alone.

- `p25p2_reference.txt`: values made by op25's code (boatbod/op25, `gr-op25_repeater`), nothing of
  ours: the scrambling sequences of three systems (WACN, system, NAC: 0xBEE00 0x123 0x293,
  0x92A47 0x3F5 0x8A1, 0x1 0x18 0x293) from `apps/tdma/lfsr.py` (its `p25p2_lfsr` class, copied
  word for word into a small script and run); the 128 I-ISCH code words and the sync pattern of
  `lib/p25p2_isch.cc`; the DUID code words and the whole decoding table of `lib/p25p2_duid.cc`;
  Reed-Solomon code words of `ezpwd::RS<63,35>` as op25 uses it (FACCH, SACCH, ESS, four
  messages each); CRC-12 values of `crc12` in `lib/p25p2_tdma.cc`. Each line's kind is
  explained in the file's own comments. The same sequences come out of sdrtrunk's
  `LinearFeedbackShiftRegister` (put into C line by line: there is no Java here) for four
  systems, and its Reed-Solomon test encoder agrees too; both references' voice frame layout
  is DMR's interleave (op25's `extract_vcw` run against RadioFoundry's `ambeDeinterleave`).
- `p25p2_voice_48k.raw`: a voice channel, both logical channels in a call, scrambled for WACN
  0xBEE00, system 0x123, NAC 0x293. Six superframes (2.2 s): idle (FACCH and SACCH MAC_IDLE),
  MAC_PTT, two of voice (4V 4V 4V 4V 2V with the ESS, SACCH MAC_ACTIVE), MAC_END_PTT, idle;
  then 50 ms of nothing. Channel 0: talkgroup 1001, radio 2001001, MI zero; channel 1:
  talkgroup 1002, radio 2002002, MI 11 22 ... 99 (algorithm 0x80, clear, on both). 48 kHz complex
  baseband, signed 16-bit I/Q interleaved, 16384 = 1.0, a raised cosine pulse (roll-off 0.2).
  The speech is the AMBE+2 frames of `dmr_repeater_24k.raw` as the DMR receiver reads them:
  frames 0-35 on channel 0, 36-71 on channel 1.
  (`p25p2gen OUT dmr_repeater_24k.raw --frames-out p25p2_voice_frames.txt`)
- `p25p2_voice_frames.txt`: those frames as p25p2gen sent them, a line each: the channel, then
  the 49 bits.
- `p25p2_unscrambled_48k.raw`: another system (WACN 0x92A47, system 0x3F5, NAC 0x8A1) with its
  FACCH and SACCH sent unscrambled; one superframe of voice (still scrambled). Talkgroups 4001
  and 4002, radios 123 and 456. Five superframes (1.8 s).
  (`p25p2gen OUT dmr_repeater_24k.raw --unscrambled --voice 1 --wacn 0x92A47 --sysid 0x3F5
  --nac 0x8A1 --tg1 4001 --src1 123 --tg2 4002 --src2 456`)

- `p25p2_late_enc_48k.raw`: the same system and superframes as `p25p2_voice_48k.raw`, with
  HANGTIME (carrying the group voice channel user message) in place of the last idle superframe.
  Channel 0, talkgroup 1001, radio 2001001: its PUSH-TO-TALK superframe is idle, so its call begins
  with voice, as a receiver that missed the PUSH-TO-TALK hears it; ACTIVE carries the extended group
  voice channel user message (opcode 0x21) with the emergency bit. Channel 1, talkgroup 1002, radio
  2002002: encrypted, algorithm 0x84 key 0x1234 in its PUSH-TO-TALK and ESS, the encryption bit in its
  service options (its voice frames are the clear ones still). Frames as for `p25p2_voice_48k.raw`,
  in `p25p2_late_enc_frames.txt`.
  (`p25p2gen OUT dmr_repeater_24k.raw --late1 --ext1 --svc1 0x80 --alg2 0x84 --key2 0x1234 --hang
  --frames-out p25p2_late_enc_frames.txt`)

The MAC PDUs the parser is tested on (`P25Phase2Tests`) were also run through a harness made of op25's
own MAC code (`lib/p25p2_tdma.cc`: `mac_msg_len`, `decode_mac_msg` and the field arithmetic of
`handle_mac_ptt` and `handle_mac_end_ptt`, copied; `apps/tk_p25.py` `decode_tdma_msg` for opcodes
0x01 and 0x21), whose output is in the test beside each PDU; three of them are the MAC structures of
sdrtrunk's `MacMessageFactoryTest`.

The tests add the noise and carrier offsets themselves (p25p2gen can too: `--noise`, `--offset`).
The pulse a real Phase 2 transmitter uses is not checked: neither reference has a transmitter.

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

## System Fusion (C4FM)

Made with `native/digital/tools/ysfgen.c` (this repository, written from Yaesu's published air
interface, with RadioFoundry's encoders) from RadioFoundry's synthesized test speech. Same
sample format.

- `ysf_dn_24k.raw`: a whole transmission in DN mode (voice and data type 2): header, 21 traffic
  frames (105 voice frames of `test_transmission.ambe`), end frame. From KC2RGW to all, through
  W1XYZ-RPT, group ID 12. `dmr_voice_reference_8k.raw` is the reference for the speech.
  (`ysfgen OUT test_transmission.ambe --src KC2RGW --down W1XYZ-RPT --up KC2RGW --group 12`)
- `ysf_vd1_late_inverted_24k.raw`: voice and data type 1, without its header frame and with the
  deviation turned over. From W1AW to K1ABC.
  (`ysfgen OUT test_transmission.ambe --mode vd1 --src W1AW --dest K1ABC --late --invert`)
- `ysf_vw_24k.raw`: full-rate voice (VW), 102 voice frames of `test_transmission.imbe`, the
  frames the P25 call carries, so `p25_call_voice_8k.raw` is the reference for its first 54.
  (`ysfgen OUT test_transmission.imbe --mode vw --src KC2RGW --frames 21`)

The receiver was also run against three public recordings of real transmissions (not kept
here: they are other people's voices): two discriminator recordings of an FTM-400D in DN and VW
from qsl.net/kb9mwr/projects/dv/yaesu, and DSDcc's `samples/ysf_f5zoo.dis`.
