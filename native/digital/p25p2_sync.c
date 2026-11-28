// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// P25 Phase 2: the inter-slot signalling channel (ISCH) and the framer that follows the superframe (p25p2.h).
//
// The framer, written for SimpleScanner: it looks for the sync pattern (the S-ISCH before the third and fourth
// timeslot of each fragment) in the dibits; a match is confirmed by an I-ISCH, at most two timeslots on, whose
// place in the superframe agrees with where the sync patterns were. From then on it takes a timeslot every 180
// dibits and checks each ISCH: a sync pattern found a dibit or two from where it should be moves the timing (as
// sdrtrunk's P25P2SuperFrameDetector does, with its thresholds), an I-ISCH says or confirms the timeslot number,
// and six timeslots in a row without either is the signal lost.
#include "p25p2.h"

#include <string.h>

// The I-ISCH (40,9,16) code: a code word is the generator's rows for the bits set, XORed, then the offset
// (sdrtrunk message/isch/ISCHDecoder.java, GENERATOR and CODE_WORD_OFFSET; the code words of the 128 with LCH type
// 0 are op25's p25p2_isch.cc isch_map, and agree)
static const uint64_t isch_gen[9] = {
	0x8816CE36D7ull, 0x201DFD4F64ull, 0x100F4B1758ull, 0x0C00DED18Eull, 0x020807F7FFull,
	0x09048D9B72ull, 0x009DA3A171ull, 0x0058CBAA4Eull, 0x00343D8597ull
};
#define ISCH_OFFSET 0x184229D461ull
#define ISCH_MAX_ERRS 7              // the code corrects 7 (sdrtrunk MAX_CORRECTABLE_BIT_ERRORS; op25 isch_lookup)

#define SEARCH_ERRS 4                // sync pattern bit errors: finding it (sdrtrunk UN_SYNCHRONIZED_SYNC_MATCH_THRESHOLD)
#define LOCKED_ERRS 7                // ... and checking it once locked (SYNCHRONIZED_SYNC_MATCH_THRESHOLD)
#define CONFIRM_ERRS 5               // I-ISCH bit errors accepted for the first timing
#define CONFIRM_SLOTS 6              // timeslots looked at for that before searching again
#define RESYNC_ERRS 3                // locked: an I-ISCH this good that disagrees with the count moves the count
#define LOST_MISSES 6
#define LOOKAHEAD 2                  // dibits beyond a timeslot waited for: the sync pattern may be that much late

enum { SEARCH, CONFIRM, LOCKED };

static int popcount64(uint64_t v)
{
	int n = 0;
	while (v) {
		v &= v - 1;
		n++;
	}
	return n;
}

uint64_t p25p2_isch_encode(unsigned v)
{
	uint64_t cw = 0;
	for (int i = 0; i < 9; i++)
		if (v & (0x100u >> i))
			cw ^= isch_gen[i];
	return cw ^ ISCH_OFFSET;
}

int p25p2_isch_decode(uint64_t cw, int *errs)
{
	int best = -1, best_e = ISCH_MAX_ERRS + 1;
	cw &= 0xFFFFFFFFFFull;
	for (unsigned v = 0; v < 512; v++) {
		int e;
		if (P25P2_ISCH_LOC(v) == 3 || P25P2_ISCH_CHAN(v) > 1)
			continue;
		e = popcount64(cw ^ p25p2_isch_encode(v));
		if (e < best_e) {
			best_e = e;
			best = (int)v;
		}
	}
	if (errs)
		*errs = best < 0 ? -1 : best_e;
	return best;
}

int p25p2_sisch_errors(uint64_t cw) { return popcount64((cw ^ P25P2_SISCH) & 0xFFFFFFFFFFull); }

void p25p2_framer_init(p25p2_framer_t *f, p25p2_slot_cb on_slot, p25p2_lock_cb on_lock, void *ctx)
{
	memset(f, 0, sizeof *f);
	f->on_slot = on_slot;
	f->on_lock = on_lock;
	f->ctx = ctx;
	p25p2_framer_reset(f);
}

void p25p2_framer_reset(p25p2_framer_t *f)
{
	f->state = SEARCH;
	f->ts = -1;
	f->tries = 0;
	f->nss = 0;
	f->misses = 0;
	f->sr = 0;
}

int p25p2_framer_locked(const p25p2_framer_t *f) { return f->state == LOCKED; }

// The 40 bits of the ISCH at dibit a
static uint64_t isch_at(const p25p2_framer_t *f, uint64_t a)
{
	uint64_t cw = 0;
	for (int i = 0; i < P25P2_ISCH_SYMS; i++)
		cw = (cw << 2) | f->d[(a + (uint64_t)i) % P25P2_RING];
	return cw;
}

static void deliver(p25p2_framer_t *f, int isch, int errs)
{
	uint8_t burst[P25P2_BURST_SYMS];
	for (int i = 0; i < P25P2_BURST_SYMS; i++)
		burst[i] = f->d[(f->at + P25P2_ISCH_SYMS + (uint64_t)i) % P25P2_RING];
	f->slots++;
	f->on_slot(f->ctx, f->ts, burst, isch, errs, f->at);
}

// Locked: the timeslot at f->at, which should be ts
static void locked_slot(p25p2_framer_t *f)
{
	static const int shift[5] = { 0, -1, 1, -2, 2 }, limit[5] = { LOCKED_ERRS, LOCKED_ERRS - 1, LOCKED_ERRS - 1,
								    LOCKED_ERRS - 2, LOCKED_ERRS - 2 };
	int good = 0, isch = -2, errs = -1;

	if (f->ts % 4 >= 2) {
		for (int k = 0; k < 5; k++) {
			int e = p25p2_sisch_errors(isch_at(f, f->at + (uint64_t)(int64_t)shift[k]));
			if (e <= limit[k]) {
				if (shift[k]) {
					f->at += (uint64_t)(int64_t)shift[k];
					f->shifts++;
				}
				good = 1;
				isch = -1;
				errs = e;
				break;
			}
		}
	} else {
		int v = p25p2_isch_decode(isch_at(f, f->at), &errs);
		if (v >= 0) {
			int t = P25P2_ISCH_LOC(v) * 4 + P25P2_ISCH_CHAN(v);
			if (t == f->ts || errs <= RESYNC_ERRS) {
				f->ts = t;
				good = 1;
				isch = v;
			}
		}
	}
	if (good) {
		f->misses = 0;
	} else if (++f->misses >= LOST_MISSES) {
		p25p2_framer_reset(f);
		f->on_lock(f->ctx, 0);
		return;
	}
	deliver(f, isch, errs);
	f->ts = (f->ts + 1) % P25P2_SLOTS;
	f->at += P25P2_SLOT_SYMS;
}

// Confirming a sync pattern found at the timeslot tries = 0: the S-ISCHs go before timeslots 2 and 3 of each
// fragment of four, so an I-ISCH at timeslot k says where the ones before it were
static void confirm_slot(p25p2_framer_t *f)
{
	uint64_t cw = isch_at(f, f->at);
	int errs, v;

	if (p25p2_sisch_errors(cw) <= LOCKED_ERRS) {
		if (f->nss < 4)
			f->ss_at[f->nss++] = f->tries;
	} else if ((v = p25p2_isch_decode(cw, &errs)) >= 0 && errs <= CONFIRM_ERRS) {
		int t = P25P2_ISCH_LOC(v) * 4 + P25P2_ISCH_CHAN(v), ok = f->nss > 0;
		for (int i = 0; i < f->nss; i++) {
			int s = ((t - (f->tries - f->ss_at[i])) % P25P2_SLOTS + P25P2_SLOTS) % P25P2_SLOTS;
			if (s % 4 < 2)
				ok = 0;
		}
		if (ok) {
			f->state = LOCKED;
			f->ts = t;
			f->misses = 0;
			f->syncs++;
			f->on_lock(f->ctx, 1);
			deliver(f, v, errs);
			f->ts = (f->ts + 1) % P25P2_SLOTS;
			f->at += P25P2_SLOT_SYMS;
			return;
		}
	}
	if (++f->tries >= CONFIRM_SLOTS) {
		p25p2_framer_reset(f);
		return;
	}
	f->at += P25P2_SLOT_SYMS;
}

void p25p2_framer_skip(p25p2_framer_t *f, uint32_t n)
{
	f->n += n;
	f->sr = 0;
}

void p25p2_framer_push(p25p2_framer_t *f, uint8_t dibit)
{
	f->d[f->n % P25P2_RING] = dibit & 3;
	f->n++;
	f->sr = ((f->sr << 2) | (dibit & 3)) & 0xFFFFFFFFFFull;
	if (f->state == SEARCH) {
		if (f->n < P25P2_ISCH_SYMS || p25p2_sisch_errors(f->sr) > SEARCH_ERRS)
			return;
		f->state = CONFIRM;
		f->at = f->n - P25P2_ISCH_SYMS;
		f->ts = -1;
		f->tries = 0;
		f->nss = 0;
	}
	while (f->state != SEARCH && f->n >= f->at + P25P2_SLOT_SYMS + LOOKAHEAD) {
		if (f->state == LOCKED)
			locked_slot(f);
		else
			confirm_slot(f);
	}
}
