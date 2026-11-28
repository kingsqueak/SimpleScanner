// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// P25 Phase 2 (TDMA) outbound voice channel: the parts below the MAC layer, for p25p2rx.c and the test signal tool
// (tools/p25p2gen.c). p25p2_sync.c: the inter-slot signalling (ISCH) and the framer that finds the superframe;
// p25p2_burst.c: burst types (DUID), where each field sits in a burst, the scrambling sequence and the MAC PDU's CRC;
// p25p2_rs.c: the Reed-Solomon codes; p25p2_vf.c: voice frames.
//
// None of the constants here were taken from memory: each was read in op25 (boatbod/op25, gr-op25_repeater) and
// sdrtrunk (DSheirer/sdrtrunk, module/decode/p25/phase2), both GPL-3, named beside it, and the tables were checked
// against those programs' own code (see tests/SimpleScanner.Tests/Data/README.md).
//
// The channel, as both describe it: 6000 symbols a second (two bits each, H-DQPSK); a superframe of 12 timeslots,
// 360 ms; each timeslot 180 symbols, an ISCH of 20 symbols then a burst of 160 (op25 p25p2_tdma.cc BURST_SIZE 180,
// SUPERFRAME_SIZE; sdrtrunk SuperFrameFragment, TIMESLOT_A_START = 40 bits). Timeslots go in fragments of four: an
// I-ISCH before the first two, the sync pattern (S-ISCH) before the last two (sdrtrunk P25P2SuperFrameDetector; op25
// p25p2_sync::check_confidence, expected_sync). Even timeslots belong to logical channel 0 and odd ones to channel 1,
// except 10 and 11, the SACCHs, which are the other way round (op25 p25p2_tdma::handle_packet, which_slot; sdrtrunk
// SuperFrameFragment.getTimeslotC/D on the final fragment).
//
// A burst here is 160 dibits, one per byte (the first sent first), or its 320 bits, bit k the high bit of dibit k / 2
// for even k. Hexbits are 6-bit Reed-Solomon symbols, their first bit the most significant.
#ifndef SS_P25P2_H
#define SS_P25P2_H

#include <stdint.h>

#define P25P2_SYM_RATE 6000          // op25 p25_demodulator.py set_omega(6000); sdrtrunk P25P2DecoderHDQPSK.SYMBOL_RATE
#define P25P2_SLOT_SYMS 180          // ISCH and burst
#define P25P2_ISCH_SYMS 20
#define P25P2_BURST_SYMS 160
#define P25P2_SLOTS 12               // timeslots in a superframe
#define P25P2_SEQ_BITS 4320          // the scrambling sequence: one superframe

// Burst types: the DUID's value (sdrtrunk enumeration/DataUnitID; op25 p25p2_tdma::handle_packet's burst_type)
enum {
	P25P2_DUID_4V = 0, P25P2_DUID_SACCH_S = 3, P25P2_DUID_2V = 6, P25P2_DUID_FACCH_S = 9, P25P2_DUID_DATCH_S = 10,
	P25P2_DUID_SACCH_U = 12, P25P2_DUID_LCCH_U = 13, P25P2_DUID_FACCH_U = 15
};

// ---- p25p2_sync.c ----

// The sync pattern, the S-ISCH: 20 dibits, the first in the top two bits (op25 frame_sync_magics.h
// P25P2_FRAME_SYNC_MAGIC and p25p2_isch.cc's -2 entry; sdrtrunk P25P2SyncPattern, +3 +3 +3 -3 ...)
#define P25P2_SISCH 0x575D57F7FFull
// The I-ISCH's 9 bits, first sent first: LCH type (2: 0 voice channel, 1 data, 3 control), channel number (2: 0 or
// 1), ISCH location (2: 0 to 2, the fragment of the superframe), LCH flag (1), superframe count (2: in the ultraframe)
// (sdrtrunk message/isch/IISCH.java; op25 p25p2_sync::check_confidence cnt, fr, loc, chn)
#define P25P2_ISCH_LCH(v) (((v) >> 7) & 3)
#define P25P2_ISCH_CHAN(v) (((v) >> 5) & 3)
#define P25P2_ISCH_LOC(v) (((v) >> 3) & 3)
#define P25P2_ISCH_FLAG(v) (((v) >> 2) & 1)
#define P25P2_ISCH_COUNT(v) ((v) & 3)

// The 40-bit I-ISCH code word of the 9 bits v (the first bit sent in bit 39)
uint64_t p25p2_isch_encode(unsigned v);
// The 9 bits of the nearest code word (channel 0 or 1, location 0 to 2) if it is within 7 bit errors, else -1.
// errs: the bit errors found (may be NULL)
int p25p2_isch_decode(uint64_t cw, int *errs);
// Bit errors between 40 bits and the S-ISCH
int p25p2_sisch_errors(uint64_t cw);

// The framer: finds the S-ISCH, follows the timeslots from then on, and says which timeslot of the superframe each
// is from the I-ISCHs
#define P25P2_RING 1024              // dibits kept (a whole number, more than a timeslot and the shifts tried)

// One timeslot: ts its place in the superframe (0-11); burst its 160 dibits; isch the I-ISCH's 9 bits before it,
// -1 an S-ISCH, -2 unreadable; isch_errs its bit errors; at the number of its first dibit (that of its ISCH)
typedef void (*p25p2_slot_cb)(void *ctx, int ts, const uint8_t *burst, int isch, int isch_errs, uint64_t at);
// Superframe timing found (1) or lost (0)
typedef void (*p25p2_lock_cb)(void *ctx, int locked);

typedef struct {
	uint8_t d[P25P2_RING];       // dibits, a ring by their number
	uint64_t n;                  // dibits so far
	uint64_t sr;                 // the last 20, the newest in the low bits
	int state;                   // searching, confirming a sync pattern, or locked
	uint64_t at;                 // the number of the next timeslot's first dibit
	int ts;                      // and its place in the superframe; -1 not known
	int tries;                   // confirming: timeslots looked at
	int ss_at[4], nss;           // confirming: the timeslots (counted from the sync pattern) that had an S-ISCH
	int misses;                  // locked: timeslots in a row whose ISCH was not what it should be
	uint32_t slots, syncs, shifts;
	p25p2_slot_cb on_slot;
	p25p2_lock_cb on_lock;
	void *ctx;
} p25p2_framer_t;

void p25p2_framer_init(p25p2_framer_t *f, p25p2_slot_cb on_slot, p25p2_lock_cb on_lock, void *ctx);
// Back to searching (without a call to on_lock)
void p25p2_framer_reset(p25p2_framer_t *f);
// One received dibit (0-3, P25's mapping: +3 is 1, +1 0, -1 2, -3 3)
void p25p2_framer_push(p25p2_framer_t *f, uint8_t dibit);
// n symbols with no signal (counted, not looked at; call p25p2_framer_reset first if locked)
void p25p2_framer_skip(p25p2_framer_t *f, uint32_t n);
int p25p2_framer_locked(const p25p2_framer_t *f);

// ---- p25p2_burst.c ----

// The DUID: the 8-bit code word of value v (0-15), the value first (sdrtrunk DataUnitID.getValueWithParity)
uint8_t p25p2_duid_encode(int v);
// Its value from the code word: the nearest within 1 bit error, or within 2 where only one is that near; else -1
int p25p2_duid_decode(uint8_t cw, int *errs);
// The DUID's dibits in a burst, in its 160 (sdrtrunk Timeslot.DATA_UNIT_ID bits 0-1, 74-75, 244-245, 318-319; op25
// p25p2_duid::extract_duid, 10 47 132 169 of the burst counted from the middle of the ISCH)
uint8_t p25p2_duid_get(const uint8_t burst[P25P2_BURST_SYMS]);
void p25p2_duid_put(uint8_t burst[P25P2_BURST_SYMS], uint8_t cw);
// A burst type that is scrambled on the air
int p25p2_duid_scrambled(int v);

// The scrambling sequence of a system, 4320 bits packed (the first in the top bit of seq[0])
void p25p2_scramble_seq(uint32_t wacn, uint32_t sysid, uint32_t nac, uint8_t seq[P25P2_SEQ_BITS / 8]);
// The burst of timeslot ts (0-11) scrambled or descrambled in place (the DUID's bits are left alone: they are sent
// as they are)
void p25p2_scramble_burst(uint8_t burst[P25P2_BURST_SYMS], int ts, const uint8_t seq[P25P2_SEQ_BITS / 8]);

// Hexbits in and out of a burst, in the order sent. FACCH: 45 (26 information, 19 parity); SACCH: 52 (30 and 22);
// a 4V burst's ESS-B: 4; a 2V burst's ESS-A: 28. Voice frames: 36 dibits from p25p2_vf_at[]
enum { P25P2_F_FACCH, P25P2_F_SACCH, P25P2_F_ESS_B, P25P2_F_ESS_A };
int p25p2_field_hexbits(int field);
void p25p2_field_get(const uint8_t burst[P25P2_BURST_SYMS], int field, uint8_t *hb);
void p25p2_field_put(uint8_t burst[P25P2_BURST_SYMS], int field, const uint8_t *hb);
// The first dibit of each voice frame: a 4V burst has all four, a 2V burst the first two
extern const uint8_t p25p2_vf_at[4];

// The MAC PDU's CRC: of the first nbits of oct (first bit the top of oct[0]); FACCH 144 bits, SACCH 168, the CRC
// in the 12 bits after them
uint16_t p25p2_crc12(const uint8_t *oct, int nbits);
// Hexbits to octets and back (nhb hexbits, a whole number of them filling the octets from the top)
void p25p2_hexbits_to_octets(const uint8_t *hb, int nhb, uint8_t *oct);
void p25p2_octets_to_hexbits(const uint8_t *oct, int nhb, uint8_t *hb);

// ---- p25p2_rs.c ----

// The three codes, all shortened from RS(63,35) (sdrtrunk ReedSolomonErasureDecoder.p25_63_35_29; op25 p25p2_tdma
// rs28, ezpwd::RS<63,35>): FACCH (45,26) with 9 parity symbols not sent, SACCH (52,30) with 6 not sent, ESS (44,16)
enum { P25P2_RS_FACCH, P25P2_RS_SACCH, P25P2_RS_ESS };
int p25p2_rs_info(int code);         // information hexbits: 26, 30, 16
int p25p2_rs_sent(int code);         // hexbits sent: 45, 52, 44
int p25p2_rs_capacity(int code);     // symbol errors it corrects with nothing erased: 9, 11, 14
// sent[0..info-1] the information, the parity after it, in the order sent
void p25p2_rs_encode(int code, const uint8_t *info, uint8_t *sent);
// Corrects sent in place. erased: NULL, or one flag per hexbit sent (an erasure costs half an error). Returns the
// symbol errors corrected (erasures not counted), -1 when it cannot
int p25p2_rs_decode(int code, uint8_t *sent, const uint8_t *erased);

// ---- p25p2_vf.c ----

// A voice frame's 36 dibits to the 49 AMBE+2 parameter bits (one per byte), returning the bit errors corrected as
// ambeFecDecode counts them (7: the first code word could not be corrected); and back
int p25p2_vf_decode(const uint8_t dib[36], uint8_t bits[49]);
void p25p2_vf_encode(const uint8_t bits[49], uint8_t dib[36]);

#endif
