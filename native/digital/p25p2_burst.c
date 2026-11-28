// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// P25 Phase 2 bursts (p25p2.h): the DUID, where the fields sit, the scrambling sequence, the MAC PDU's CRC. The
// layouts and codes are op25's and sdrtrunk's (GPL-3), named at each; the code is written for SimpleScanner.
#include "p25p2.h"

#include <string.h>

// DUID code words by value (sdrtrunk enumeration/DataUnitID.java; every one decodes to its value in op25's
// p25p2_duid.cc _duid_lookup, and so does each with one bit wrong)
static const uint8_t duid_cw[16] = {
	0x00, 0x17, 0x2E, 0x39, 0x4B, 0x5C, 0x65, 0x72, 0x8D, 0x9A, 0xA3, 0xB4, 0xC6, 0xD1, 0xE8, 0xFF
};
// The DUID's dibits in the burst (sdrtrunk Timeslot.DATA_UNIT_ID; op25 p25p2_duid::extract_duid)
static const uint8_t duid_at[4] = { 0, 37, 122, 159 };

// The fields: runs of burst bits [from, to), in the order sent. FACCH: sdrtrunk FacchTimeslot (INFO_1 at bits 2-7 ...
// PARITY_19 at 312-317, nothing at 138-179), op25 handle_acch_frame fast (dibits 11-46, 48-78, 100-131, 133-168 of its
// count, which starts 10 dibits earlier). SACCH: SacchTimeslot; op25 the slow form (11-46, 48-131, 133-168). ESS-B:
// Voice4Timeslot ESS_B_START 148, 24 bits; op25 handle_4V2V_ess (its dibits 84-95). ESS-A: Voice2Timeslot 148-243 and
// 246-317; op25 the same 84 dibits, the DUID's at 132 skipped
typedef struct {
	uint16_t from, to;
} run_t;
static const run_t facch_runs[] = { { 2, 74 }, { 76, 138 }, { 180, 244 }, { 246, 318 } };
static const run_t sacch_runs[] = { { 2, 74 }, { 76, 244 }, { 246, 318 } };
static const run_t essb_runs[] = { { 148, 172 } };
static const run_t essa_runs[] = { { 148, 244 }, { 246, 318 } };
static const struct {
	const run_t *runs;
	int nruns, hexbits;
} fields[4] = {
	{ facch_runs, 4, 45 }, { sacch_runs, 3, 52 }, { essb_runs, 1, 4 }, { essa_runs, 2, 28 }
};

// Voice frames (sdrtrunk Voice4Timeslot FRAME_1_START 2, 76, 172, 246 bits; op25 handle_packet 11, 48, 96, 133)
const uint8_t p25p2_vf_at[4] = { 1, 38, 86, 123 };

static int bit_get(const uint8_t *dib, int k) { return (dib[k >> 1] >> (1 - (k & 1))) & 1; }

static void bit_put(uint8_t *dib, int k, int v)
{
	uint8_t m = (uint8_t)(1u << (1 - (k & 1)));
	dib[k >> 1] = (uint8_t)(v ? dib[k >> 1] | m : dib[k >> 1] & ~m);
}

uint8_t p25p2_duid_encode(int v) { return duid_cw[v & 15]; }

static int popcount8(unsigned v)
{
	int n = 0;
	for (; v; v &= v - 1)
		n++;
	return n;
}

int p25p2_duid_decode(uint8_t cw, int *errs)
{
	int best = -1, best_e = 9, ties = 0;
	for (int v = 0; v < 16; v++) {
		int e = popcount8(cw ^ duid_cw[v]);
		if (e < best_e) {
			best_e = e;
			best = v;
			ties = 0;
		} else if (e == best_e) {
			ties++;
		}
	}
	// The (8,4,4) code corrects one error; two are taken only where no other code word is as near
	if (best_e > 2 || (best_e == 2 && ties))
		best = -1;
	if (errs)
		*errs = best < 0 ? -1 : best_e;
	return best;
}

uint8_t p25p2_duid_get(const uint8_t burst[P25P2_BURST_SYMS])
{
	return (uint8_t)((burst[duid_at[0]] << 6) | (burst[duid_at[1]] << 4) | (burst[duid_at[2]] << 2) | burst[duid_at[3]]);
}

void p25p2_duid_put(uint8_t burst[P25P2_BURST_SYMS], uint8_t cw)
{
	for (int i = 0; i < 4; i++)
		burst[duid_at[i]] = (cw >> (6 - 2 * i)) & 3;
}

int p25p2_duid_scrambled(int v)
{
	// sdrtrunk TimeslotFactory: voice and the S forms; op25 handle_packet descrambles 0, 6, 3, 9 (and 4, its
	// "scrambled LCCH", which sdrtrunk calls reserved)
	return v == P25P2_DUID_4V || v == P25P2_DUID_2V || v == P25P2_DUID_SACCH_S || v == P25P2_DUID_FACCH_S ||
	       v == P25P2_DUID_DATCH_S;
}

// The scrambling sequence (sdrtrunk timeslot/LinearFeedbackShiftRegister: G(x) = x^44 + x^40 + x^35 + x^29 + x^24 +
// x^10 + 1 as a shift register whose output is bit 43, fed back with bits 43, 33, 19, 14, 8 and 3; loaded with
// WACN (20 bits), system (12) and NAC (12), all ones in place of all zeros. op25's apps/tdma/lfsr.py makes the same
// bits another way: checked against it for four systems)
void p25p2_scramble_seq(uint32_t wacn, uint32_t sysid, uint32_t nac, uint8_t seq[P25P2_SEQ_BITS / 8])
{
	const uint64_t mask = 0xFFFFFFFFFFFull;
	uint64_t r = ((uint64_t)(wacn & 0xFFFFF) << 24) | ((uint64_t)(sysid & 0xFFF) << 12) | (nac & 0xFFF);
	if (!r)
		r = mask;
	memset(seq, 0, P25P2_SEQ_BITS / 8);
	for (int i = 0; i < P25P2_SEQ_BITS; i++) {
		unsigned out = (unsigned)(r >> 43) & 1;
		unsigned fb = out ^ ((unsigned)(r >> 33) & 1) ^ ((unsigned)(r >> 19) & 1) ^ ((unsigned)(r >> 14) & 1) ^
			      ((unsigned)(r >> 8) & 1) ^ ((unsigned)(r >> 3) & 1);
		r = ((r << 1) & mask) | fb;
		if (out)
			seq[i >> 3] |= (uint8_t)(0x80u >> (i & 7));
	}
}

void p25p2_scramble_burst(uint8_t burst[P25P2_BURST_SYMS], int ts, const uint8_t seq[P25P2_SEQ_BITS / 8])
{
	// The sequence starts halfway through the ISCH of timeslot 0, so a burst's bit k takes bit 20 + 360 ts + k
	// (sdrtrunk ScramblingSequence.update; op25 handle_packet, tdma_xormask[slot * 180 + i] on its dibit 10 + i)
	int base = 20 + 360 * ts;
	for (int k = 0; k < 2 * P25P2_BURST_SYMS; k++) {
		int j = base + k;
		if ((seq[j >> 3] >> (7 - (j & 7))) & 1) {
			int d = k >> 1;
			if (d == duid_at[0] || d == duid_at[1] || d == duid_at[2] || d == duid_at[3])
				continue;
			burst[d] ^= (uint8_t)(1u << (1 - (k & 1)));
		}
	}
}

int p25p2_field_hexbits(int field) { return fields[field].hexbits; }

void p25p2_field_get(const uint8_t burst[P25P2_BURST_SYMS], int field, uint8_t *hb)
{
	int n = 0;
	memset(hb, 0, (size_t)fields[field].hexbits);
	for (int r = 0; r < fields[field].nruns; r++)
		for (int k = fields[field].runs[r].from; k < fields[field].runs[r].to; k++, n++)
			hb[n / 6] = (uint8_t)((hb[n / 6] << 1) | bit_get(burst, k));
}

void p25p2_field_put(uint8_t burst[P25P2_BURST_SYMS], int field, const uint8_t *hb)
{
	int n = 0;
	for (int r = 0; r < fields[field].nruns; r++)
		for (int k = fields[field].runs[r].from; k < fields[field].runs[r].to; k++, n++)
			bit_put(burst, k, (hb[n / 6] >> (5 - n % 6)) & 1);
}

// x^12 + x^11 + x^7 + x^4 + x^2 + x + 1, the remainder inverted (op25 p25p2_tdma.cc crc12, poly {1,1,0,0,0,1,0,0,1,0,
// 1,1,1}, crc ^ 0xfff; sdrtrunk CRCP25.crc12_FACCH, 0x1897 with the register starting at 0xFFF)
uint16_t p25p2_crc12(const uint8_t *oct, int nbits)
{
	uint16_t r = 0;
	for (int i = 0; i < nbits; i++) {
		unsigned b = (oct[i >> 3] >> (7 - (i & 7))) & 1;
		unsigned top = (r >> 11) & 1;
		r = (uint16_t)((r << 1) & 0xFFF);
		if (top ^ b)
			r ^= 0x897;
	}
	return r ^ 0xFFF;
}

void p25p2_hexbits_to_octets(const uint8_t *hb, int nhb, uint8_t *oct)
{
	memset(oct, 0, (size_t)(nhb * 6 + 7) / 8);
	for (int n = 0; n < nhb * 6; n++)
		if ((hb[n / 6] >> (5 - n % 6)) & 1)
			oct[n >> 3] |= (uint8_t)(0x80u >> (n & 7));
}

void p25p2_octets_to_hexbits(const uint8_t *oct, int nhb, uint8_t *hb)
{
	memset(hb, 0, (size_t)nhb);
	for (int n = 0; n < nhb * 6; n++)
		hb[n / 6] = (uint8_t)((hb[n / 6] << 1) | ((oct[n >> 3] >> (7 - (n & 7))) & 1));
}
