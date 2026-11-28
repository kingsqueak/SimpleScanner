// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// Makes a P25 Phase 2 (TDMA) outbound voice channel for SimpleScanner's tests: both logical channels, each with a
// call of its own, as 48 kHz complex baseband. There is no Phase 2 signal to record here, so this is the only one
// the receiver has seen; its parts (p25p2_*.c) are the receiver's own, which is why their codes and tables were
// checked against op25's and sdrtrunk's code (tests/SimpleScanner.Tests/Data/README.md).
//
//   p25p2gen OUT.raw DMR.raw [--wacn W] [--sysid S] [--nac N] [--tg1 T] [--src1 U] [--tg2 T] [--src2 U]
//            [--voice N] [--first1 K] [--first2 K] [--unscrambled] [--float] [--noise ESN0_DB] [--offset HZ]
//            [--seed N] [--frames-out FILE] [--alg1 A] [--key1 K] [--alg2 A] [--key2 K] [--svc1 S] [--svc2 S]
//            [--late1] [--late2] [--ext1] [--ext2] [--hang]
//
// Each superframe (360 ms, 12 timeslots) both channels send the same kind of thing: one superframe idle (FACCH
// MAC_IDLE in the five voice places, SACCH MAC_IDLE in the sixth: timeslot 11 for channel 0, 10 for channel 1), one
// with MAC_PTT (FACCH, its offset to the first 4V counted to the next superframe; and the SACCH), N of voice (4V 4V
// 4V 4V 2V, the ESS-B fragments in the 4Vs and the ESS-A in the 2V, SACCH MAC_ACTIVE with a group voice user
// message), one of MAC_END_PTT, one idle. Scrambled with the sequence of WACN, system and NAC (defaults 0xBEE00,
// 0x123, 0x293); --unscrambled sends FACCH and SACCH with the unscrambled DUIDs instead (voice is scrambled still).
// The I-ISCHs say voice channel, location and superframe count. FACCH's bits 138-179, which neither reference reads,
// are sent as zeros.
//
// Per channel (1: channel 0, 2: channel 1): --alg, --key: the call's encryption algorithm and key id (0x80, clear, and
// 0 when not given), in the MAC_PTT and the ESS, and the encryption bit of the service options with any algorithm but
// 0x80; --svc: the service options of the group voice channel user message (0x80 emergency, 0x40 encrypted); --late:
// the MAC_PTT superframe is idle on that channel, so its call begins with voice, as a receiver that missed the PTT
// hears it; --ext: MAC_ACTIVE carries the extended group voice channel user message (opcode 0x21, the source's full
// identity WACN, system, radio) instead of the abbreviated one. --hang: the last superframe is MAC_HANGTIME carrying
// the group voice channel user message, not MAC_IDLE.
//
// The speech: the AMBE+2 frames of a DMR call, read from DMR.raw (a dmr_repeater_24k.raw: 24 kHz discriminator,
// 6553.6 counts per kHz) by the DMR receiver itself; channel 0 sends frames from --first1 on (default 0), channel 1
// from --first2 (default 36), so a frame given to the wrong channel shows. --frames-out writes them as sent, a line
// each: the channel, then the 49 bits as 0s and 1s.
//
// The air: each dibit a phase step (P25's mapping: 01 +3 pi / 4, 00 +pi / 4, 10 -pi / 4, 11 -3 pi / 4), a raised
// cosine pulse (roll-off 0.2) on I and Q at 6000 symbols a second, amplitude 0.5 at the symbol centres; 50 ms of
// nothing after. The pulse is the one p25gen --cqpsk uses for Phase 1: what Phase 2 transmitters really use is not
// checked (the references' receivers have only a flat low-pass). Interleaved signed 16-bit I/Q, 16384 = 1.0, or
// float32 with --float. --offset: the carrier that many Hz up; --noise: white noise at that Es/N0 (dB, the symbol
// energy of the mean signal power over the noise density), from --seed.
//
// Build: cc -O2 -std=gnu11 -I.. -I../radiofoundry/core -o p25p2gen p25p2gen.c ../p25p2_sync.c ../p25p2_burst.c \
//           ../p25p2_rs.c ../p25p2_vf.c ../dmrrx.c ../radiofoundry/core/air/air_softmodem.c \
//           ../radiofoundry/core/air/air_framer.c ../radiofoundry/core/dmr/fec/dmrFec.c \
//           ../radiofoundry/core/dmr/fec/dmrCach.c ../radiofoundry/core/dmr/call/dmr_lc.c \
//           ../radiofoundry/core/dmr/ambe/ambeFec.c ../radiofoundry/core/fec/gf64.c ../radiofoundry/core/fec/golay.c \
//           ../radiofoundry/core/fec/hamming.c ../radiofoundry/core/fec/rs63.c ../radiofoundry/core/fec/bch63.c \
//           ../radiofoundry/core/fec/trellis.c -DCONFIG_AIR=1 -DFXP_FORCE_PLAIN -lm
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "p25p2.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define RATE 48000
#define SPS 8
#define SPAN 8                       // raised cosine: symbols each side
#define AMP 0.5
#define TAIL_SYMS 300
#define MAX_FRAMES 1024

// The DMR receiver (dmrrx.c), as far as this needs it
void *dmrrx_new(int rate, float hz_per_unit);
void dmrrx_free(void *r);
void dmrrx_feed(void *r, const float *disc, int n);
int dmrrx_read_voice(void *r, uint8_t *out, int max);

enum { PH_IDLE, PH_PTT, PH_VOICE, PH_END, PH_HANG };
enum { MAC_PTT = 1, MAC_END_PTT = 2, MAC_IDLE = 3, MAC_ACTIVE = 4, MAC_HANGTIME = 6 };

static const uint8_t slot_chan[P25P2_SLOTS] = { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 0 };

static uint8_t frames[MAX_FRAMES][49];
static int nframes;
static uint8_t *dib;                 // the dibits sent, and how many
static size_t ndib, capdib;
static uint8_t seq[P25P2_SEQ_BITS / 8];
static uint32_t wacn = 0xBEE00, sysid = 0x123, nac = 0x293;
static int unscrambled;
static FILE *frames_out;

typedef struct {
	uint32_t tg, src;
	uint32_t alg, key, svc;          // encryption algorithm (0x80 clear) and key id; service options
	int late, ext;                   // --late, --ext
	int first, next;                 // frames: where it starts, and the next to send
	uint8_t mi[9];
	uint8_t ess[44];                 // this voice superframe's ESS, encoded
} call_t;
static call_t calls[2];

static void put_dibit(uint8_t d)
{
	if (ndib == capdib) dib = realloc(dib, capdib = capdib ? 2 * capdib : 65536);
	dib[ndib++] = d & 3;
}

static void read_dmr(const char *path)
{
	FILE *f = fopen(path, "rb");
	void *rx;
	int16_t s[4800];
	float x[4800];
	uint8_t items[64 * 50];
	size_t n;
	if (!f) {
		perror(path);
		exit(1);
	}
	rx = dmrrx_new(24000, 1000);
	while ((n = fread(s, sizeof *s, 4800, f)) > 0) {
		int k;
		for (size_t i = 0; i < n; i++) x[i] = (float)(s[i] / 6553.6);
		dmrrx_feed(rx, x, (int)n);
		while ((k = dmrrx_read_voice(rx, items, 64)) > 0)
			for (int i = 0; i < k; i++)
				if (items[i * 50 + 49] == 0 && nframes < MAX_FRAMES) memcpy(frames[nframes++], items + i * 50, 49);
	}
	fclose(f);
	dmrrx_free(rx);
}

// A MAC PDU of nbits (144 FACCH, 168 SACCH), its CRC added, to hexbits
static void mac_hexbits(uint8_t *oct, int nbits, uint8_t *hb)
{
	uint16_t crc = p25p2_crc12(oct, nbits);
	oct[nbits / 8] = (uint8_t)(crc >> 4);
	oct[nbits / 8 + 1] = (uint8_t)((crc & 15) << 4);
	p25p2_octets_to_hexbits(oct, (nbits + 12) / 6, hb);
}

static void mac_pdu(uint8_t *oct, int type, int offset, const call_t *c, int sacch)
{
	memset(oct, 0, 24);
	oct[0] = (uint8_t)(type << 5 | offset << 2);
	switch (type) {
	case MAC_PTT:                    // op25 handle_mac_ptt; sdrtrunk PushToTalk
		memcpy(oct + 1, c->mi, 9);
		oct[10] = (uint8_t)c->alg;
		oct[11] = (uint8_t)(c->key >> 8);
		oct[12] = (uint8_t)c->key;
		break;
	case MAC_END_PTT:                // op25 handle_mac_end_ptt; sdrtrunk EndPushToTalk
		oct[1] = (uint8_t)((nac >> 8) & 15);
		oct[2] = (uint8_t)nac;
		break;
	case MAC_ACTIVE:
	case MAC_HANGTIME:
		// A group voice channel user message: abbreviated (opcode 0x01, 7 octets) or extended (0x21, 14): service
		// options, group, source; the extended one then the source's WACN (20 bits), system (12) and radio (24)
		// (sdrtrunk GroupVoiceChannelUserAbbreviated / Extended, MacStructureGroupVoiceService; op25 tk_p25.py
		// decode_tdma_msg). The rest of the PDU is null information (opcode 0)
		oct[1] = c->ext ? 0x21 : 0x01;
		oct[2] = (uint8_t)(c->svc | (c->alg != 0x80 ? 0x40 : 0));
		oct[3] = (uint8_t)(c->tg >> 8);
		oct[4] = (uint8_t)c->tg;
		oct[5] = (uint8_t)(c->src >> 16);
		oct[6] = (uint8_t)(c->src >> 8);
		oct[7] = (uint8_t)c->src;
		if (c->ext) {
			oct[8] = (uint8_t)(wacn >> 12);
			oct[9] = (uint8_t)(wacn >> 4);
			oct[10] = (uint8_t)((wacn & 15) << 4 | (sysid >> 8));
			oct[11] = (uint8_t)sysid;
			oct[12] = (uint8_t)(c->src >> 16);
			oct[13] = (uint8_t)(c->src >> 8);
			oct[14] = (uint8_t)c->src;
		}
		return;
	default:
		return;
	}
	oct[13] = (uint8_t)(c->src >> 16);
	oct[14] = (uint8_t)(c->src >> 8);
	oct[15] = (uint8_t)c->src;
	oct[16] = (uint8_t)(c->tg >> 8);
	oct[17] = (uint8_t)c->tg;
	(void)sacch;
}

static void signalling(uint8_t *burst, int sacch, int type, int offset, const call_t *c)
{
	uint8_t oct[24], hb[52];
	int code = sacch ? P25P2_RS_SACCH : P25P2_RS_FACCH;
	mac_pdu(oct, type, offset, c, sacch);
	mac_hexbits(oct, sacch ? 168 : 144, hb);
	p25p2_rs_encode(code, hb, hb);
	p25p2_field_put(burst, sacch ? P25P2_F_SACCH : P25P2_F_FACCH, hb);
	p25p2_duid_put(burst, p25p2_duid_encode(unscrambled ? (sacch ? P25P2_DUID_SACCH_U : P25P2_DUID_FACCH_U)
							   : (sacch ? P25P2_DUID_SACCH_S : P25P2_DUID_FACCH_S)));
}

static void voice(uint8_t *burst, int ch, int pos)
{
	call_t *c = &calls[ch];
	int n = pos < 4 ? 4 : 2;
	if (pos == 0) {
		// The ESS for this voice superframe: algorithm (0x80, clear), key id, MI (op25 handle_4V2V_ess)
		uint8_t oct[12], info[16];
		memset(oct, 0, sizeof oct);
		oct[0] = (uint8_t)c->alg;
		oct[1] = (uint8_t)(c->key >> 8);
		oct[2] = (uint8_t)c->key;
		memcpy(oct + 3, c->mi, 9);
		p25p2_octets_to_hexbits(oct, 16, info);
		p25p2_rs_encode(P25P2_RS_ESS, info, c->ess);
	}
	for (int k = 0; k < n; k++) {
		const uint8_t *bits = frames[c->next++ % nframes];
		p25p2_vf_encode(bits, burst + p25p2_vf_at[k]);
		if (frames_out) {
			fprintf(frames_out, "%d ", ch);
			for (int i = 0; i < 49; i++) fputc('0' + bits[i], frames_out);
			fputc('\n', frames_out);
		}
	}
	if (pos < 4) p25p2_field_put(burst, P25P2_F_ESS_B, c->ess + 4 * pos);
	else p25p2_field_put(burst, P25P2_F_ESS_A, c->ess + 16);
	p25p2_duid_put(burst, p25p2_duid_encode(pos < 4 ? P25P2_DUID_4V : P25P2_DUID_2V));
}

static void superframe(int sf, int phase)
{
	for (int ts = 0; ts < P25P2_SLOTS; ts++) {
		uint8_t burst[P25P2_BURST_SYMS];
		int ch = slot_chan[ts], pos = ts >= 10 ? 5 : ts / 2, sacch = pos == 5;
		uint64_t isch;
		const call_t *c = &calls[ch];
		if (ts % 4 < 2)
			isch = p25p2_isch_encode((unsigned)((ts % 4) << 5 | (ts / 4) << 3 | (sf & 3)));
		else
			isch = P25P2_SISCH;
		for (int i = 0; i < P25P2_ISCH_SYMS; i++) put_dibit((uint8_t)(isch >> (38 - 2 * i)));
		memset(burst, 0, sizeof burst);
		switch (phase) {
		case PH_IDLE:
			signalling(burst, sacch, MAC_IDLE, 7, c);
			break;
		case PH_PTT:
			// The offset: non-SACCH timeslots to the first 4V, less one (sdrtrunk Voice4VOffset)
			if (c->late) signalling(burst, sacch, MAC_IDLE, 7, c);
			else signalling(burst, sacch, MAC_PTT, sacch ? 0 : 4 - pos, c);
			break;
		case PH_VOICE:
			if (sacch) signalling(burst, 1, MAC_ACTIVE, 0, c);
			else voice(burst, ch, pos);
			break;
		case PH_HANG:
			signalling(burst, sacch, MAC_HANGTIME, 7, c);
			break;
		default:
			signalling(burst, sacch, MAC_END_PTT, 7, c);
			break;
		}
		if (p25p2_duid_scrambled(p25p2_duid_decode(p25p2_duid_get(burst), NULL)))
			p25p2_scramble_burst(burst, ts, seq);
		for (int i = 0; i < P25P2_BURST_SYMS; i++) put_dibit(burst[i]);
	}
}

static double rc(double t, double a)
{
	double sinc = fabs(t) < 1e-9 ? 1 : sin(M_PI * t) / (M_PI * t);
	double d = 1 - (2 * a * t) * (2 * a * t);
	return fabs(d) < 1e-9 ? sinc * M_PI / 4 : sinc * cos(M_PI * a * t) / d;
}

static uint64_t rng = 88172645463325252ull;

static double uniform(void)
{
	rng ^= rng << 13;
	rng ^= rng >> 7;
	rng ^= rng << 17;
	return ((rng >> 11) + 0.5) / 9007199254740992.0;
}

static double gauss(void) { return sqrt(-2 * log(uniform())) * cos(2 * M_PI * uniform()); }

int main(int argc, char **argv)
{
	int nvoice = 2, fl = 0, sf = 0, hang = 0;
	double noise_db = 1e9, offset = 0, pw = 0;
	const char *frames_path = NULL;
	static const double step[4] = { M_PI / 4, 3 * M_PI / 4, -M_PI / 4, -3 * M_PI / 4 };
	double *ph, p = 0, *re, *im, sigma = 0;
	size_t nsyms, n;
	FILE *f;

	if (argc < 3) {
		fprintf(stderr, "p25p2gen OUT.raw DMR.raw [--wacn W] [--sysid S] [--nac N] [--tg1 T] [--src1 U] [--tg2 T] [--src2 U]\n"
				"         [--voice N] [--first1 K] [--first2 K] [--unscrambled] [--float] [--noise ESN0_DB]\n"
				"         [--offset HZ] [--seed N] [--frames-out FILE] [--alg1 A] [--key1 K] [--alg2 A] [--key2 K]\n"
				"         [--svc1 S] [--svc2 S] [--late1] [--late2] [--ext1] [--ext2] [--hang]\n");
		return 2;
	}
	calls[0] = (call_t){ .tg = 1001, .src = 2001001, .alg = 0x80, .first = 0 };
	calls[1] = (call_t){ .tg = 1002, .src = 2002002, .alg = 0x80, .first = 36, .mi = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99 } };
	for (int i = 3; i < argc; i++) {
		const char *a = argv[i], *v = i + 1 < argc ? argv[i + 1] : "0";
		if (!strcmp(a, "--unscrambled")) unscrambled = 1;
		else if (!strcmp(a, "--float")) fl = 1;
		else if (!strcmp(a, "--wacn")) wacn = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--sysid")) sysid = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--nac")) nac = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--tg1")) calls[0].tg = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--src1")) calls[0].src = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--tg2")) calls[1].tg = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--src2")) calls[1].src = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--voice")) nvoice = atoi(v), i++;
		else if (!strcmp(a, "--first1")) calls[0].first = atoi(v), i++;
		else if (!strcmp(a, "--first2")) calls[1].first = atoi(v), i++;
		else if (!strcmp(a, "--noise")) noise_db = atof(v), i++;
		else if (!strcmp(a, "--offset")) offset = atof(v), i++;
		else if (!strcmp(a, "--seed")) rng = strtoull(v, NULL, 0) * 2654435761ull + 1, i++;
		else if (!strcmp(a, "--frames-out")) frames_path = v, i++;
		else if (!strcmp(a, "--alg1") || !strcmp(a, "--alg2")) calls[a[5] - '1'].alg = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--key1") || !strcmp(a, "--key2")) calls[a[5] - '1'].key = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--svc1") || !strcmp(a, "--svc2")) calls[a[5] - '1'].svc = (uint32_t)strtoul(v, NULL, 0), i++;
		else if (!strcmp(a, "--late1") || !strcmp(a, "--late2")) calls[a[6] - '1'].late = 1;
		else if (!strcmp(a, "--ext1") || !strcmp(a, "--ext2")) calls[a[5] - '1'].ext = 1;
		else if (!strcmp(a, "--hang")) hang = 1;
		else return fprintf(stderr, "p25p2gen: %s?\n", a), 2;
	}
	read_dmr(argv[2]);
	if (!nframes) return fprintf(stderr, "p25p2gen: no voice frames in %s\n", argv[2]), 1;
	if (frames_path && !(frames_out = fopen(frames_path, "w"))) return perror(frames_path), 1;
	for (int c = 0; c < 2; c++) calls[c].next = calls[c].first;
	p25p2_scramble_seq(wacn, sysid, nac, seq);

	superframe(sf++, PH_IDLE);
	superframe(sf++, PH_PTT);
	for (int k = 0; k < nvoice; k++) superframe(sf++, PH_VOICE);
	superframe(sf++, PH_END);
	superframe(sf++, hang ? PH_HANG : PH_IDLE);
	if (frames_out) fclose(frames_out);

	// The air
	nsyms = ndib;
	n = (nsyms + TAIL_SYMS) * SPS;
	ph = malloc(nsyms * sizeof *ph);
	re = calloc(n, sizeof *re);
	im = calloc(n, sizeof *im);
	for (size_t k = 0; k < nsyms; k++) ph[k] = p += step[dib[k]];
	for (size_t i = 0; i < n; i++) {
		double at = (double)i / SPS;
		long k0 = (long)floor(at) - SPAN, k1 = (long)ceil(at) + SPAN;
		for (long k = k0 < 0 ? 0 : k0; k <= k1 && (size_t)k < nsyms; k++) {
			double h = rc(at - (double)k, 0.2) * AMP;
			re[i] += h * cos(ph[k]);
			im[i] += h * sin(ph[k]);
		}
		if (i < nsyms * SPS) pw += re[i] * re[i] + im[i] * im[i];
	}
	pw /= (double)(nsyms * SPS);
	if (noise_db < 1e8) sigma = sqrt(pw * SPS / pow(10, noise_db / 10) / 2);
	if (!(f = fopen(argv[1], "wb"))) return perror(argv[1]), 1;
	for (size_t i = 0; i < n; i++) {
		double w = 2 * M_PI * offset * (double)i / RATE, x = re[i] * cos(w) - im[i] * sin(w), y = re[i] * sin(w) + im[i] * cos(w);
		x += sigma * gauss();
		y += sigma * gauss();
		if (fl) {
			float s[2] = { (float)x, (float)y };
			fwrite(s, sizeof s, 1, f);
		} else {
			double a = x * 16384, b = y * 16384;
			int16_t s[2] = { (int16_t)lrint(a > 32767 ? 32767 : a < -32767 ? -32767 : a),
					 (int16_t)lrint(b > 32767 ? 32767 : b < -32767 ? -32767 : b) };
			fwrite(s, sizeof s, 1, f);
		}
	}
	fclose(f);
	fprintf(stderr, "p25p2gen: %d superframes, %zu symbols, %d DMR frames read (channel 0 from %d, channel 1 from %d), "
			"WACN 0x%X system 0x%X NAC 0x%X%s\n", sf, nsyms, nframes, calls[0].first, calls[1].first, wacn, sysid, nac,
		unscrambled ? ", FACCH and SACCH unscrambled" : "");
	return 0;
}
