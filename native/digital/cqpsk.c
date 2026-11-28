// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// The differential demodulator (cqpsk.h). Written for SimpleScanner from the textbook methods: Gardner's timing
// error detector (F. M. Gardner, "A BPSK/QPSK timing-error detector for sampled receivers", IEEE Trans. Comm. 1986),
// here on the differential phase, which is a 4-level signal with no intersymbol interference at the symbol centres
// for both P25 modulations; Oerder and Meyr's feedforward timing estimate (the phase of the symbol-rate line in the
// squared signal, IEEE Trans. Comm. 1988), on the same signal, for finding the symbols before the loop has them;
// cubic Lagrange interpolation; the fourth-power phase estimator. The structure (a receive filter, then Gardner
// timing, then the differential phase as the symbol) is the one op25's CQPSK receiver uses (gr-op25_repeater
// p25_demodulator.py, gardner_costas_cc); no code is taken from it.
#include "cqpsk.h"

#include <math.h>
#include <string.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define SPAN		4		// root raised cosine receive filter: symbols each side of its centre
#define LOOP_BW		0.008f		// timing loop: noise bandwidth x symbol period
#define DAMP		0.707f
// Gardner's error on the differential phase near lock, in radians squared per sample of timing error, for the loop
// gains. Set by trying: half or twice this decodes as many blocks of an off-air capture, a quarter of it fewer
#define TED_SLOPE	0.25f
#define AC_TC		0.005f		// seconds: the carrier loop's average of the phase step per sample
#define NCO_TC		0.25f		// seconds: the carrier loop's time constant
// For the first ACQ_S seconds after a reset, while the offset is still over FAST_HZ, the loop runs at NCO_FAST_TC:
// an offset over 600 Hz (an eighth of a turn per symbol at 4800) is one the symbols' fourth power reads wrong, and
// the sooner the NCO is past it the fewer blocks are lost. Faster for longer, it follows simulcast's fades
#define NCO_FAST_TC	0.05f
#define ACQ_S		0.3
#define FAST_HZ		400.0f
#define Q4_SYMS		128.0f		// symbols: the fourth power's average
#define ERR_SYMS	64.0f
#define FF_K		0.3		// while searching: the part of the timing window's correction taken per symbol

static float wrap(float a)
{
	while (a > (float)M_PI)
		a -= 2.0f * (float)M_PI;
	while (a < -(float)M_PI)
		a += 2.0f * (float)M_PI;
	return a;
}

// Root raised cosine at t symbols from the centre
static double rrc(double t, double a)
{
	double x = 4.0 * a * t;

	if (fabs(t) < 1e-9)
		return 1.0 - a + 4.0 * a / M_PI;
	if (fabs(fabs(x) - 1.0) < 1e-9)
		return a / sqrt(2.0) * ((1 + 2 / M_PI) * sin(M_PI / (4 * a)) + (1 - 2 / M_PI) * cos(M_PI / (4 * a)));
	return (sin(M_PI * t * (1 - a)) + x * cos(M_PI * t * (1 + a))) / (M_PI * t * (1 - x * x));
}

static double bessel_i0(double x)
{
	double sum = 1, term = 1;

	for (int k = 1; k < 40; k++) {
		term *= (x / (2 * k)) * (x / (2 * k));
		sum += term;
	}
	return sum;
}

// The receive filter: a root raised cosine over SPAN symbols each side (Hann window), or a low-pass flat to below
// cutoff_hz, down 50 dB a transition_hz above it (Kaiser window); unity gain at DC. False if it does not fit
static int design(cqpsk_t *d)
{
	const cqpsk_cfg_t *c = &d->cfg;
	int half, i;
	double sum = 0, beta = 4.55;	// Kaiser: 50 dB

	if (c->rolloff > 0)
		half = (int)lrintf(SPAN * d->sps);
	else
		half = (int)ceil(42.0 / (2.285 * 2 * M_PI * c->transition_hz / c->rate) / 2);
	d->ntaps = 2 * half + 1;
	if (d->ntaps > CQPSK_TAPS_MAX)
		return 0;
	for (i = 0; i < d->ntaps; i++) {
		double t = (double)(i - half) / d->sps, r = (double)(i - half) / (half + 1.0);

		if (c->rolloff > 0) {
			d->taps[i] = (float)(rrc(t, c->rolloff) * (0.5 + 0.5 * cos(M_PI * r)));
		} else {
			double fc = (c->cutoff_hz + c->transition_hz / 2) / c->rate, x = 2.0 * M_PI * fc * (i - half);

			d->taps[i] = (float)((i == half ? 2.0 * fc : sin(x) / (M_PI * (i - half))) *
					     bessel_i0(beta * sqrt(1 - r * r)) / bessel_i0(beta));
		}
		sum += d->taps[i];
	}
	for (i = 0; i < d->ntaps; i++)
		d->taps[i] = (float)(d->taps[i] / sum);
	return 1;
}

int cqpsk_init(cqpsk_t *d, const cqpsk_cfg_t *cfg, cqpsk_out_t out, void *ctx)
{
	float wn, sps;

	if (!cfg->sym_rate || cfg->rate % cfg->sym_rate)
		return 0;
	sps = (float)cfg->rate / cfg->sym_rate;
	if (sps < 4 || sps > 12 || cfg->rolloff < 0 || cfg->rolloff > 1 ||
	    (cfg->rolloff == 0 && (cfg->cutoff_hz <= 0 || cfg->transition_hz <= 0)))
		return 0;
	memset(d, 0, sizeof *d);
	d->cfg = *cfg;
	d->out = out;
	d->ctx = ctx;
	d->sps = sps;
	d->half = sps / 2;
	d->S = (int)cfg->rate / cfg->sym_rate;
	d->win = CQPSK_WIN_SYMS * d->S;
	for (int k = 0; k < d->S; k++) {
		d->cs[k] = (float)cos(2 * M_PI * k / d->S);
		d->sn[k] = (float)-sin(2 * M_PI * k / d->S);
	}
	if (!design(d))
		return 0;
	// A second-order loop (per symbol): the usual gains from the noise bandwidth and damping
	wn = LOOP_BW / (DAMP + 1.0f / (4.0f * DAMP));
	d->kp = 4.0f * DAMP * wn / (1.0f + 2.0f * DAMP * wn + wn * wn) / TED_SLOPE;
	d->ki = 4.0f * wn * wn / (1.0f + 2.0f * DAMP * wn + wn * wn) / TED_SLOPE;
	d->nco_max = (float)(2.0 * M_PI * cfg->max_offset_hz / cfg->rate);
	cqpsk_reset(d);
	return 1;
}

void cqpsk_reset(cqpsk_t *d)
{
	memset(d->in_re, 0, sizeof d->in_re);
	memset(d->in_im, 0, sizeof d->in_im);
	memset(d->h_re, 0, sizeof d->h_re);
	memset(d->h_im, 0, sizeof d->h_im);
	d->in_at = 0;
	d->nco_ph = 0;
	d->nco_w = 0;
	d->ac_re = d->ac_im = 0;
	d->fp_re = d->fp_im = 0;
	d->pwr = 0;
	d->n = 0;
	d->t_next = (double)d->sps + 2;
	d->rate_adj = 0;
	d->yk_re = d->yk_im = d->ym_re = d->ym_im = 0;
	d->dk_prev = 0;
	d->q4_re = d->q4_im = 0;
	d->c = 0;
	d->err = (float)M_PI / 4;
	d->syms = 0;
	d->nsoft = 0;
	memset(d->d2, 0, sizeof d->d2);
	d->x_re = d->x_im = 0;
	d->searching = 1;
}

// The filtered signal at t (in filtered samples, the newest at n - 1): cubic Lagrange through the four around it
static void interp(const cqpsk_t *d, double t, float *re, float *im)
{
	int64_t i = (int64_t)floor(t);
	float mu = (float)(t - (double)i);
	float c0 = -mu * (mu - 1) * (mu - 2) / 6, c1 = (mu + 1) * (mu - 1) * (mu - 2) / 2;
	float c2 = -(mu + 1) * mu * (mu - 2) / 2, c3 = (mu + 1) * mu * (mu - 1) / 6;
	unsigned a = (unsigned)(i - 1) % CQPSK_HIST, b = (unsigned)i % CQPSK_HIST;
	unsigned e = (unsigned)(i + 1) % CQPSK_HIST, f = (unsigned)(i + 2) % CQPSK_HIST;

	*re = c0 * d->h_re[a] + c1 * d->h_re[b] + c2 * d->h_re[e] + c3 * d->h_re[f];
	*im = c0 * d->h_im[a] + c1 * d->h_im[b] + c2 * d->h_im[e] + c3 * d->h_im[f];
}

static void flush(cqpsk_t *d)
{
	if (d->nsoft) {
		d->out(d->soft, d->nsoft, d->t_chunk, d->silent, d->ctx);
		d->nsoft = 0;
	}
}

static void symbol(cqpsk_t *d)
{
	float kr, ki, mr, mi, zr, zi, wr, wi, dk, dm, e, m, z2r, z2i, q, near, s;
	double t = d->t_next;

	interp(d, t, &kr, &ki);
	interp(d, t - d->half, &mr, &mi);
	// The phase steps from a symbol back: at the centre, and at the midpoint before it
	zr = kr * d->yk_re + ki * d->yk_im;
	zi = ki * d->yk_re - kr * d->yk_im;
	wr = mr * d->ym_re + mi * d->ym_im;
	wi = mi * d->ym_re - mr * d->ym_im;
	d->yk_re = kr;
	d->yk_im = ki;
	d->ym_re = mr;
	d->ym_im = mi;
	// What is left of the carrier offset: the fourth power takes the four symbols to one point (minus one)
	m = zr * zr + zi * zi;
	if (m > 0) {
		z2r = (zr * zr - zi * zi) / m;
		z2i = 2 * zr * zi / m;
		// A plain mean over the first symbols, so it is right from the start, then a running one
		float n4 = d->syms + 1 < Q4_SYMS ? (float)(d->syms + 1) : Q4_SYMS;

		d->q4_re += ((z2r * z2r - z2i * z2i) - d->q4_re) / n4;
		d->q4_im += (2 * z2r * z2i - d->q4_im) / n4;
		d->c = atan2f(-d->q4_im, -d->q4_re) / 4;
	}
	dk = wrap(atan2f(zi, zr) - d->c);
	dm = wrap(atan2f(wi, wr) - d->c);
	if (d->searching) {
		// Where the window says the centres are, against where this one was taken
		double tau = -atan2(d->x_im, d->x_re) * d->S / (2 * M_PI), ph = fmod(t, d->S), f = tau - ph;
		while (f >= d->S / 2.0)
			f -= d->S;
		while (f < -d->S / 2.0)
			f += d->S;
		d->dk_prev = dk;
		d->t_next = t + d->sps + FF_K * f;

		goto done;
	}
	// Gardner: positive when the strobes are late
	e = (dk - d->dk_prev) * dm;
	if (e > 4)
		e = 4;
	if (e < -4)
		e = -4;
	d->dk_prev = dk;
	d->rate_adj -= d->ki * e;
	if (d->rate_adj > d->sps * 0.005f)
		d->rate_adj = d->sps * 0.005f;
	if (d->rate_adj < -d->sps * 0.005f)
		d->rate_adj = -d->sps * 0.005f;
	d->t_next = t + d->sps + d->rate_adj - d->kp * e;
done:

	q = (float)M_PI / 4;
	near = dk >= 2 * q ? 3 * q : dk >= 0 ? q : dk >= -2 * q ? -q : -3 * q;
	d->err += (fabsf(dk - near) - d->err) / ERR_SYMS;
	d->syms++;

	s = dk * d->cfg.soft_per_rad;
	// Exact zeros (a source that has stopped, the silence of a generated signal) have no phase: without this they read
	// as a run of one symbol, which the framer can take for a unit (all zeros is a valid NID)
	if (d->nsoft && (m < 1e-30f) != d->silent)
		flush(d);
	if (!d->nsoft) {
		d->silent = m < 1e-30f;
		// t is in filtered samples; the filter delays by half its length
		double at = t - (d->ntaps - 1) / 2.0;

		d->t_chunk = (uint32_t)(uint64_t)((at < 0 ? 0 : at) * 1e6 / d->cfg.rate);
	}
	d->soft[d->nsoft++] = (int8_t)(s > 127 ? 127 : s < -127 ? -127 : lrintf(s));
	if (d->nsoft == CQPSK_CHUNK)
		flush(d);
}

void cqpsk_feed(cqpsk_t *d, const float *re, const float *im, int n)
{
	const int nt = d->ntaps;
	const float ac_a = 1.0f / (AC_TC * d->cfg.rate), nco_g = 1.0f / (NCO_TC * d->cfg.rate);
	const float nco_fast = 1.0f / (NCO_FAST_TC * d->cfg.rate);
	const float fast_at = (float)(2 * M_PI * FAST_HZ / d->cfg.rate);
	const uint64_t acq_n = (uint64_t)(ACQ_S * d->cfg.rate);

	for (int i = 0; i < n; i++) {
		float c = cosf((float)d->nco_ph), s = sinf((float)d->nco_ph);
		float xr = re[i] * c + im[i] * s, xi = im[i] * c - re[i] * s, fr = 0, fi = 0;
		const float *hr, *hi;
		unsigned h;
		float ar, ai, p;

		// Turned back by the NCO's angle, which runs at the offset measured
		d->nco_ph += d->nco_w;
		if (d->nco_ph > M_PI)
			d->nco_ph -= 2 * M_PI;
		if (d->nco_ph < -M_PI)
			d->nco_ph += 2 * M_PI;
		d->in_re[d->in_at] = d->in_re[d->in_at + nt] = xr;
		d->in_im[d->in_at] = d->in_im[d->in_at + nt] = xi;
		if (++d->in_at == nt)
			d->in_at = 0;
		hr = d->in_re + d->in_at;
		hi = d->in_im + d->in_at;
		for (int k = 0; k < nt; k++) {
			fr += d->taps[k] * hr[k];
			fi += d->taps[k] * hi[k];
		}
		// The carrier loop: the mean phase step per sample of the filtered signal. Noise in the filter's band is
		// centred on the NCO as the signal is once the loop has settled, so it only slows the loop down
		ar = fr * d->fp_re + fi * d->fp_im;
		ai = fi * d->fp_re - fr * d->fp_im;
		d->fp_re = fr;
		d->fp_im = fi;
		p = fr * fr + fi * fi;
		d->pwr += (p - d->pwr) * ac_a;
		d->ac_re += (ar - d->ac_re) * ac_a;
		d->ac_im += (ai - d->ac_im) * ac_a;
		if (d->ac_re != 0 || d->ac_im != 0) {
			float r = atan2f(d->ac_im, d->ac_re);

			d->nco_w += r * (d->n < acq_n && fabsf(r) > fast_at ? nco_fast : nco_g);
			if (d->nco_w > d->nco_max)
				d->nco_w = d->nco_max;
			if (d->nco_w < -d->nco_max)
				d->nco_w = -d->nco_max;
		}
		h = (unsigned)(d->n % CQPSK_HIST);
		d->h_re[h] = fr;
		d->h_im[h] = fi;
		{
			// The phase step over a symbol ending here, squared: its mean over the window peaks at the centres
			unsigned b = (unsigned)((d->n + CQPSK_HIST - (unsigned)d->S) % CQPSK_HIST), w = (unsigned)(d->n % (unsigned)d->win);
			unsigned k = (unsigned)(d->n % (unsigned)d->S);
			float zr = fr * d->h_re[b] + fi * d->h_im[b], zi = fi * d->h_re[b] - fr * d->h_im[b];
			float a = atan2f(zi, zr), v = a * a, dv = v - d->d2[w];

			d->d2[w] = v;
			d->x_re += dv * d->cs[k];
			d->x_im += dv * d->sn[k];
		}
		d->n++;
		// A symbol centre with two filtered samples after it in hand, and half the timing window
		while (d->t_next + 2 + d->win / 2 <= (double)(d->n - 1))
			symbol(d);
	}
	flush(d);
}

void cqpsk_set_searching(cqpsk_t *d, int searching) { d->searching = searching; }

void cqpsk_stats(const cqpsk_t *d, float *offset_hz, float *err_rad, float *power)
{
	*offset_hz = (float)((d->nco_w + d->c / d->sps) * d->cfg.rate / (2 * M_PI));
	*err_rad = d->err;
	*power = d->pwr;
}
