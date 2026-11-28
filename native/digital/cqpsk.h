// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// A differential (CQPSK, LSM, H-DQPSK) demodulator: complex baseband in, one soft value per symbol out, the
// differential phase arg(y[k] conj(y[k-1])) of the symbol centres scaled so that a phase step of 3 pi / 4 reads 96
// (RadioFoundry's AIR_SOFT_OUTER: P25's +3) at the default scale. A C4FM signal can be received this way too: its
// phase moves by the same steps from one symbol centre to the next.
//
// The chain, per input sample: NCO (the carrier offset taken off) -> receive filter (root raised cosine, or a plain
// low-pass) -> history, and the squared phase step over a symbol summed against the symbol rate over a window of 32
// symbols (where that line's phase puts the symbol centres). Per symbol, half a window behind the newest sample:
// cubic interpolation at the symbol centre and half a symbol before it, the differential phases of both against the
// same points a symbol earlier, a constant phase step (what is left of the carrier offset) taken off, Gardner's timing
// error from the differential phases (a real 4-level signal, so it works for C4FM's constant envelope as well as
// CQPSK) and a PI loop on the symbol clock; or, while the caller says its framer is searching, the clock steered to
// the window's estimate instead (it has the symbols within a few, the loop in tens: the first frame sync of a
// transmission is not lost). Two loops keep the carrier: a slow one on the NCO from the mean phase step per sample of
// the filtered signal (wide: it pulls in an offset of a kilohertz or more, which the symbols alone cannot tell from a
// different symbol), and the fourth power of the symbols' phase steps for the remainder (unambiguous within an eighth
// of a turn per symbol).
//
// One instance per channel. Not thread-safe.
#ifndef SS_CQPSK_H
#define SS_CQPSK_H

#include <stdint.h>

#define CQPSK_TAPS_MAX	129
#define CQPSK_HIST	512		// filtered samples kept: the timing window's half and the interpolation's reach
#define CQPSK_WIN_SYMS	32		// symbols: the timing estimate's window
#define CQPSK_CHUNK	48		// symbols per output call at most (under RadioFoundry's AIR_CHUNK_SYMS_MAX, 144)

typedef struct {
	uint32_t rate;			// input samples per second: a whole multiple of sym_rate, 4 to 12 per symbol
	uint16_t sym_rate;		// 4800 (P25 Phase 1) or 6000 (Phase 2)
	float rolloff;			// root raised cosine receive filter's roll-off; 0: a flat low-pass instead
	float cutoff_hz;		// the low-pass: flat to here
	float transition_hz;		// ... and 50 dB down this much above it
	float soft_per_rad;		// soft value per radian of phase step (P25: 128 / pi, so 3 pi / 4 is 96); negative
					// turns the sign over. Phase 2's H-DQPSK has the same four steps at 6000 symbols a second
					// (8 samples to a symbol at 48 kHz); its own dibit mapping is the caller's
	float max_offset_hz;		// the carrier loop's reach
} cqpsk_cfg_t;

// Soft values of n symbols in air order; t_us: the time of the first one's centre, on the input sample clock. silent:
// the input was nothing at all (exact zeros) for all of them, so the values mean nothing: a chunk is all one or the other
typedef void (*cqpsk_out_t)(const int8_t *soft, uint16_t n, uint32_t t_us, int silent, void *ctx);

typedef struct {
	cqpsk_cfg_t cfg;
	cqpsk_out_t out;
	void *ctx;
	float sps, half;		// samples per symbol, and half of it
	float kp, ki;			// timing loop gains (samples per radian squared)
	int ntaps;
	float taps[CQPSK_TAPS_MAX];
	float in_re[2 * CQPSK_TAPS_MAX], in_im[2 * CQPSK_TAPS_MAX];	// input history twice over: one run to filter
	int in_at;
	double nco_ph;			// radians
	float nco_w, nco_max;		// radians per sample, and its limit
	float ac_re, ac_im;		// mean of y[n] conj(y[n-1]) of the filtered signal
	float fp_re, fp_im;		// the filtered sample before
	float pwr;			// mean power of the filtered signal
	float h_re[CQPSK_HIST], h_im[CQPSK_HIST];	// filtered samples, a ring
	int S, win;			// samples per symbol (whole), and in the timing window
	float d2[CQPSK_WIN_SYMS * 12];	// the squared phase step over a symbol at each filtered sample, a ring of win
	float cs[12], sn[12];		// e^-j 2 pi k / S
	double x_re, x_im;		// sum over the window of d2 e^-j 2 pi n / S
	int searching;			// the caller has no frame lock: the timing window steers the symbol clock
	uint64_t n;			// filtered samples so far
	double t_next;			// where the next symbol centre falls, in filtered samples
	float rate_adj;			// the timing loop's integrator: samples per symbol over or under nominal
	float yk_re, yk_im, ym_re, ym_im;	// the last symbol centre and the midpoint before it
	float dk_prev;
	float q4_re, q4_im;		// mean of the phase steps to the fourth power
	float c;			// the constant phase step taken off, radians
	float err;			// mean distance of the phase steps from the nearest of the four, radians
	uint32_t syms;
	int8_t soft[CQPSK_CHUNK];
	uint16_t nsoft;
	uint32_t t_chunk;
	int silent;			// the chunk being filled is of silent symbols
} cqpsk_t;

// False if the configuration is not one it can do
int cqpsk_init(cqpsk_t *d, const cqpsk_cfg_t *cfg, cqpsk_out_t out, void *ctx);
// Back to the start: no carrier offset known, the symbol clock free
void cqpsk_reset(cqpsk_t *d);
// n complex samples; calls out from inside for the symbols they complete
void cqpsk_feed(cqpsk_t *d, const float *re, const float *im, int n);
// The caller's framer has (0) or has not (1) found the signal's frames: while it has not, the symbol clock is
// steered to where the timing window puts the symbol centres, which finds them within a few symbols where the
// timing loop would take tens; once it has, the loop keeps them
void cqpsk_set_searching(cqpsk_t *d, int searching);
// What it measures: the carrier offset (Hz, positive: the signal is above where it was tuned), the mean distance of
// the phase steps from the nearest symbol (radians: near 0 clean, about 0.35 for noise), the filtered signal's power
void cqpsk_stats(const cqpsk_t *d, float *offset_hz, float *err_rad, float *power);

#endif
