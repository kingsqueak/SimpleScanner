// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// P25 Phase 2's Reed-Solomon codes (p25p2.h), on RadioFoundry's GF(64) arithmetic (fec/gf64.h: x^6 + x + 1, the
// field both references use). RadioFoundry's own decoder (rs63.c) has Phase 1's three codes only, and its
// Berlekamp-Massey takes at most 23 syndromes, so the decoder is written here: errors and erasures, by the
// Berlekamp-Massey algorithm started from the erasure locator, a Chien search and Forney's formula.
//
// The mother code is RS(63,35) over GF(64) with the generator's roots alpha^1 to alpha^28 (sdrtrunk
// edac/ReedSolomonErasureDecoder and ReedSolomonTestEncoder; op25's ezpwd::RS<63,35>: primitive 0x43, first root 1,
// step 1). As a polynomial, symbol j is the coefficient of x^j: parity at 0-27, information at 28-62; the symbol
// sent first is the highest one that is sent. Each code leaves its highest information symbols out (they are zero)
// and its lowest parity symbols unsent (punctured: decoded as erasures):
//
//   code   sent  information  parity sent  zero      not sent   (sdrtrunk; op25 erases both ends instead)
//   FACCH  45    26           19           54-62     0-8        FacchTimeslot: decode(input, 9, 53)
//   SACCH  52    30           22           58-62     0-5        SacchTimeslot: decode(input, 6, 57)
//   ESS    44    16           28           44-62     -          EncryptionSynchronizationSequenceProcessor
#include "p25p2.h"
#include "radiofoundry/core/fec/gf64.h"

#include <string.h>

#define N 63
#define NPAR 28

static const struct {
	int sent, info, top;                 // top: the symbol sent first
} codes[3] = { { 45, 26, 53 }, { 52, 30, 57 }, { 44, 16, 43 } };

int p25p2_rs_info(int code) { return codes[code].info; }
int p25p2_rs_sent(int code) { return codes[code].sent; }
int p25p2_rs_capacity(int code) { return (NPAR - (codes[code].top + 1 - codes[code].sent)) / 2; }

#define mul fec_gf64_mul
#define alpha fec_gf64_alpha

// g(x) = (x - alpha^1) ... (x - alpha^28), g[i] the coefficient of x^i
static void generator(uint8_t g[NPAR + 1])
{
	memset(g, 0, NPAR + 1);
	g[0] = 1;
	for (int i = 1; i <= NPAR; i++) {
		uint8_t r = alpha((unsigned)i);
		for (int j = i; j > 0; j--)
			g[j] = (uint8_t)(g[j - 1] ^ mul(g[j], r));
		g[0] = mul(g[0], r);
	}
}

static void encode63(uint8_t c[N])
{
	uint8_t g[NPAR + 1], r[N];
	generator(g);
	memcpy(r, c, N);
	memset(r, 0, NPAR);
	for (int i = N - 1; i >= NPAR; i--) {
		uint8_t q = r[i];
		if (q)
			for (int j = 0; j <= NPAR; j++)
				r[i - NPAR + j] ^= mul(g[j], q);
	}
	memcpy(c, r, NPAR);
}

void p25p2_rs_encode(int code, const uint8_t *info, uint8_t *sent)
{
	uint8_t c[N];
	int top = codes[code].top;
	memset(c, 0, N);
	for (int i = 0; i < codes[code].info; i++)
		c[top - i] = info[i] & 63;
	encode63(c);
	for (int i = 0; i < codes[code].sent; i++)
		sent[i] = c[top - i];
}

static uint8_t eval(const uint8_t *p, int len, uint8_t x)
{
	uint8_t v = 0;
	for (int i = len - 1; i >= 0; i--)
		v = (uint8_t)(mul(v, x) ^ p[i]);
	return v;
}

// c corrected in place; eras the erased positions. Returns the symbols changed outside them, -1 when it cannot.
// zero_from: positions from here up are known zero, and a correction there is a failure
static int decode63(uint8_t c[N], const int *eras, int ne, int zero_from)
{
	uint8_t s[NPAR], lam[NPAR + 1], b[NPAR + 1], t[NPAR + 1], om[NPAR], last = 1;
	int len = ne, shift = 1, any = 0, nroots = 0, fixed = 0, deg = 0;
	int at[NPAR];

	if (ne > NPAR)
		return -1;
	for (int i = 0; i < ne; i++)
		c[eras[i]] = 0;
	for (int i = 0; i < NPAR; i++) {
		s[i] = eval(c, N, alpha((unsigned)i + 1));
		any |= s[i];
	}
	if (!any)
		return 0;
	// The erasure locator, prod (1 + alpha^e x), to start from
	memset(lam, 0, sizeof lam);
	lam[0] = 1;
	for (int i = 0; i < ne; i++) {
		uint8_t x = alpha((unsigned)eras[i]);
		for (int j = i + 1; j > 0; j--)
			lam[j] ^= mul(lam[j - 1], x);
	}
	memcpy(b, lam, sizeof b);
	for (int r = ne; r < NPAR; r++) {
		uint8_t d = 0, q;
		for (int i = 0; i <= r; i++)
			d ^= mul(lam[i], s[r - i]);
		if (!d) {
			shift++;
			continue;
		}
		q = mul(d, fec_gf64_inv(last));
		memcpy(t, lam, sizeof t);
		for (int i = 0; i + shift <= NPAR; i++)
			lam[i + shift] ^= mul(q, b[i]);
		if (2 * len <= r + ne) {
			memcpy(b, t, sizeof b);
			len = r + 1 + ne - len;
			last = d;
			shift = 1;
		} else {
			shift++;
		}
	}
	for (int i = 0; i <= NPAR; i++)
		if (lam[i])
			deg = i;
	if (deg != len || 2 * (len - ne) + ne > NPAR)
		return -1;
	// Chien search: position j is in error where lam(alpha^-j) = 0
	for (int j = 0; j < N && nroots <= deg; j++)
		if (!eval(lam, deg + 1, alpha((unsigned)(N - j) % N))) {
			if (nroots == deg)
				return -1;
			at[nroots++] = j;
		}
	if (nroots != deg)
		return -1;
	// Forney (first root alpha^1): e = omega(X^-1) / lam'(X^-1), omega = s lam mod x^28
	for (int i = 0; i < NPAR; i++) {
		om[i] = 0;
		for (int k = 0; k <= i && k <= deg; k++)
			om[i] ^= mul(lam[k], s[i - k]);
	}
	for (int k = 0; k < nroots; k++) {
		uint8_t xi = alpha((unsigned)(N - at[k]) % N), num = eval(om, NPAR, xi), den = 0, e;
		int erased = 0;
		for (int i = 1; i <= deg; i += 2)
			den ^= mul(lam[i], fec_gf64_alpha((unsigned)(fec_gf64_log(xi) * (unsigned)(i - 1))));
		if (!den)
			return -1;
		e = mul(num, fec_gf64_inv(den));
		if (e && at[k] >= zero_from)
			return -1;
		c[at[k]] ^= e;
		for (int i = 0; i < ne; i++)
			erased |= eras[i] == at[k];
		if (e && !erased)
			fixed++;
	}
	for (int i = 0; i < NPAR; i++)
		if (eval(c, N, alpha((unsigned)i + 1)))
			return -1;
	return fixed;
}

int p25p2_rs_decode(int code, uint8_t *sent, const uint8_t *erased)
{
	uint8_t c[N];
	int eras[NPAR], ne = 0, top = codes[code].top, low = top + 1 - codes[code].sent, rc;
	memset(c, 0, N);
	for (int j = 0; j < low; j++)
		eras[ne++] = j;
	for (int i = 0; i < codes[code].sent; i++) {
		c[top - i] = sent[i] & 63;
		if (erased && erased[i]) {
			if (ne == NPAR)
				return -1;
			eras[ne++] = top - i;
		}
	}
	rc = decode63(c, eras, ne, top + 1);
	if (rc < 0)
		return -1;
	for (int i = 0; i < codes[code].sent; i++)
		sent[i] = c[top - i];
	return rc;
}
