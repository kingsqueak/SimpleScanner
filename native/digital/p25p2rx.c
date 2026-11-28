// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's P25 Phase 2 (TDMA) receiver, the outbound voice channel: complex baseband in; the superframe's
// timing, each burst's type, the MAC PDUs of its FACCHs and SACCHs (error corrected, CRC checked, not interpreted)
// and the ESS as events; and the voice frames of one of its two logical channels, as dmrrx_read_voice gives DMR's.
//
// The chain: cqpsk.c (the differential demodulator, here at 6000 symbols a second with a flat low-pass ahead of it,
// like op25's and sdrtrunk's for this channel), the symbols sliced to dibits, p25p2_sync.c's framer (sync pattern,
// I-ISCH, timeslot count), and per burst here: the DUID; for a scrambled type, descrambling with the system's
// sequence (so nothing scrambled is read until p25p2rx_set_system); then the fields of its type: Reed-Solomon, then
// the CRC, for FACCH and SACCH; Golay through RadioFoundry's AMBE+2 code for voice frames; the ESS from the four
// ESS-B fragments of a voice superframe's 4V bursts and the ESS-A of its 2V burst, Reed-Solomon corrected.
//
// What it does not do: the MAC messages' meaning (talkgroups, encryption, offsets: the caller's, from the octets);
// the inbound channel (H-CPM); LCCH bursts of a TDMA control channel (reported by type only); soft-decision decoding.
//
// One instance per channel. Not thread-safe: call everything for an instance from one thread.
#include <stdlib.h>
#include <string.h>
#include "p25p2.h"
#include "cqpsk.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define EVENT_QUEUE 256
#define VOICE_QUEUE 128              // frames: 2.5 s
#define VOICE_ITEM 50                // 49 parameter bits, one per byte, then the error count (as dmrrx.c)
#define ERRS_LOST 7                  // a frame that did not arrive
#define ERRS_END 255                 // not a frame: the call ended, the vocoder starts afresh
#define SOFT_OUTER 96                // cqpsk soft value of a phase step of 3 pi / 4
#define MAC_FACCH_BITS 144           // FACCH information less its CRC (sdrtrunk FacchTimeslot MAX_OCTET_INDEX 144)
#define MAC_SACCH_BITS 168           // SacchTimeslot MAX_OCTET_INDEX 168
#define MAC_END_PTT 2                // the MAC PDU type in the top three bits of octet 0 (op25 process_mac_pdu)

// The receive filter: flat to 3.6 kHz, 50 dB down at 5.1. op25 and sdrtrunk have a flat low-pass for this channel
// too, but to 6.25 or 6.5 kHz (p25_demodulator.py's cutoff, 6250 to 7500 Hz; P25P2DecoderHDQPSK
// BASEBAND_FILTER_PASS_BAND 6500, STOP_BAND 7800), wide enough for a carrier offset, since theirs is taken off after
// the filter. cqpsk.c takes it off before, so this one only has to pass the signal (its raised cosine pulse is within
// 3.6 kHz); against the generated channel with noise it decoded as many frames as 6.5 kHz does 2 dB stronger, and as
// many as a root raised cosine of roll-off 0.5 or 1 (which would also shape the pulse), with offsets to 1.8 kHz
#define RX_CUTOFF_HZ 3600.0f
#define RX_TRANSITION_HZ 1500.0f
#define RX_MAX_OFFSET_HZ 2000.0f

enum { EV_SYNC = 1, EV_LOST = 2, EV_BURST = 3, EV_ESS = 4 };

// What the caller reads. Fields that do not apply to an event are 0 (slot, ts and duid -1). Only ever add fields
// at the end: the C# side mirrors this, and checks p25p2rx_abi().
typedef struct {
	int32_t kind;                // EV_*
	int32_t slot;                // logical channel, 0 or 1
	int32_t ts;                  // timeslot in the superframe, 0-11
	int32_t duid;                // burst type (P25P2_DUID_*), -1 unreadable
	int32_t duid_errs;
	int32_t isch;                // the ISCH before the burst: an I-ISCH's 9 bits, -1 the sync pattern, -2 unreadable
	int32_t superframe;          // superframes since the sync was found
	int32_t scrambled;           // the type is a scrambled one
	int32_t readable;            // descrambled (or never scrambled), so its fields were read
	int32_t rs_errs;             // symbols corrected (FACCH, SACCH, ESS); -1 it could not, or the burst has no such code
	int32_t crc_ok;              // the MAC PDU's CRC held
	int32_t ok;                  // corrected and checked: octets are the MAC PDU (or the ESS)
	int32_t len;                 // octets: FACCH 18, SACCH 21 (the CRC left off), ESS 12 (algorithm, key id, MI)
	uint8_t octets[24];
	int32_t voice_errs;          // 4V and 2V: the bit errors corrected in its frames, all together
	uint32_t symbol;             // bursts: the number of the timeslot's first symbol (its ISCH's) since the start
	uint32_t voice_at;           // voice items queued (p25p2rx_read_voice) before this event, counted from the start: what
	                             // came before it and what after; a burst's own frames come after it
} p25p2rx_event;

typedef struct {
	int32_t locked;
	int32_t syncs;               // times the superframe timing was found
	int32_t slots;               // timeslots taken while locked
	int32_t bursts;              // ... whose DUID was read
	int32_t mac_ok, mac_bad;     // FACCH and SACCH read: corrected and checked, or not
	int32_t voice_frames;        // voice frames decoded (both channels)
	int32_t ess_ok;
	int32_t have_system;
	float offset_hz, err_rad, power;
	int32_t shifts;              // timing moved by a dibit or two to keep the sync patterns in place
} p25p2rx_stats_t;

typedef struct {
	uint8_t frag[4][4];          // the last four ESS-B fragments, by the count of voice bursts
	uint8_t have[4];
	int count;                   // voice bursts since the last 2V (or since a burst of another kind)
	int in_voice;                // the last burst of this channel was 4V or 2V
} chan_t;

typedef struct {
	cqpsk_t cq;
	p25p2_framer_t fr;
	uint8_t seq[P25P2_SEQ_BITS / 8];
	int have_seq;
	int play;                    // the logical channel whose voice is queued, -1 none
	int superframe;
	uint32_t silent;             // symbols of exact zeros in a row
	chan_t ch[2];
	p25p2rx_event ev[EVENT_QUEUE];
	uint32_t ev_head, ev_count;
	uint8_t voice[VOICE_QUEUE][VOICE_ITEM];
	uint32_t v_head, v_count;
	uint32_t v_total;            // voice items queued since the start
	p25p2rx_stats_t st;
} p25p2rx;

// Timeslot to logical channel: 10 and 11 the other way round (op25 p25p2_tdma::handle_packet which_slot; sdrtrunk
// SuperFrameFragment.getChannel1Timeslots on the final fragment)
static const uint8_t slot_chan[P25P2_SLOTS] = { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 0 };

static p25p2rx_event *ev_add(p25p2rx *r, int kind)
{
	p25p2rx_event *e;
	if (r->ev_count == EVENT_QUEUE) {            // full: the oldest goes
		r->ev_head = (r->ev_head + 1) % EVENT_QUEUE;
		r->ev_count--;
	}
	e = &r->ev[(r->ev_head + r->ev_count) % EVENT_QUEUE];
	r->ev_count++;
	memset(e, 0, sizeof *e);
	e->kind = kind;
	e->slot = e->ts = e->duid = -1;
	e->superframe = r->superframe;
	e->voice_at = r->v_total;
	return e;
}

static void voice_push(p25p2rx *r, const uint8_t *bits, uint8_t errs)
{
	uint8_t *item;
	if (r->v_count == VOICE_QUEUE) {
		r->v_head = (r->v_head + 1) % VOICE_QUEUE;
		r->v_count--;
	}
	item = r->voice[(r->v_head + r->v_count) % VOICE_QUEUE];
	r->v_count++;
	r->v_total++;
	if (bits) memcpy(item, bits, 49);
	else memset(item, 0, 49);
	item[49] = errs;
}

static void voice_end(p25p2rx *r, int c)
{
	if (r->play == c && r->ch[c].in_voice) voice_push(r, NULL, ERRS_END);
	r->ch[c].in_voice = 0;
}

static void chan_clear(chan_t *c)
{
	memset(c->have, 0, sizeof c->have);
	c->count = 0;
	c->in_voice = 0;
}

// FACCH or SACCH: Reed-Solomon, then the CRC
static void mac_read(p25p2rx *r, p25p2rx_event *e, const uint8_t *burst, int sacch)
{
	uint8_t hb[52], oct[24];
	int code = sacch ? P25P2_RS_SACCH : P25P2_RS_FACCH, nbits = sacch ? MAC_SACCH_BITS : MAC_FACCH_BITS;
	p25p2_field_get(burst, sacch ? P25P2_F_SACCH : P25P2_F_FACCH, hb);
	e->rs_errs = p25p2_rs_decode(code, hb, NULL);
	if (e->rs_errs >= 0) {
		p25p2_hexbits_to_octets(hb, p25p2_rs_info(code), oct);
		uint16_t crc = (uint16_t)(((oct[nbits / 8] << 4) | (oct[nbits / 8 + 1] >> 4)) & 0xFFF);
		e->crc_ok = crc == p25p2_crc12(oct, nbits);
		e->len = nbits / 8;
		memcpy(e->octets, oct, (size_t)e->len);
	}
	e->ok = e->rs_errs >= 0 && e->crc_ok;
	if (e->ok) r->st.mac_ok++;
	else r->st.mac_bad++;
}

// The ESS of a voice superframe, at its 2V burst: ESS-B fragments missing (or from before) are erasures
static void ess_read(p25p2rx *r, int c, int ts, const uint8_t *burst)
{
	chan_t *ch = &r->ch[c];
	uint8_t hb[44], er[44];
	int nb = 0;
	p25p2rx_event *e;
	memset(er, 0, sizeof er);
	for (int i = 0; i < 4; i++) {
		int k = ch->count - 4 + i, have = k >= 0 && ch->have[k & 3];
		if (have) memcpy(hb + 4 * i, ch->frag[k & 3], 4);
		else memset(hb + 4 * i, 0, 4), memset(er + 4 * i, 1, 4);
		nb += have;
	}
	if (!nb) return;
	p25p2_field_get(burst, P25P2_F_ESS_A, hb + 16);
	e = ev_add(r, EV_ESS);
	e->slot = c;
	e->ts = ts;
	e->rs_errs = p25p2_rs_decode(P25P2_RS_ESS, hb, er);
	e->ok = e->rs_errs >= 0;
	if (e->ok) {
		e->len = 12;
		p25p2_hexbits_to_octets(hb, 16, e->octets);
		r->st.ess_ok++;
	}
}

static void voice_read(p25p2rx *r, p25p2rx_event *e, int c, const uint8_t *burst, int nframes)
{
	for (int k = 0; k < nframes; k++) {
		uint8_t bits[49];
		int errs = p25p2_vf_decode(burst + p25p2_vf_at[k], bits);
		e->voice_errs += errs;
		r->st.voice_frames++;
		if (r->play == c) voice_push(r, bits, (uint8_t)errs);
	}
}

static void on_slot(void *ctx, int ts, const uint8_t *raw, int isch, int isch_errs, uint64_t at)
{
	p25p2rx *r = (p25p2rx *)ctx;
	uint8_t burst[P25P2_BURST_SYMS];
	int c = slot_chan[ts], v;
	chan_t *ch = &r->ch[c];
	p25p2rx_event *e;
	(void)isch_errs;

	r->st.slots++;
	if (ts == 0) r->superframe++;
	memcpy(burst, raw, sizeof burst);
	e = ev_add(r, EV_BURST);
	e->slot = c;
	e->ts = ts;
	e->isch = isch;
	e->rs_errs = -1;
	e->symbol = (uint32_t)at;
	v = e->duid = p25p2_duid_decode(p25p2_duid_get(burst), &e->duid_errs);
	if (v < 0) {
		// A burst that cannot be read in a call still takes its place: four frames lost
		if (ch->in_voice) {
			if (r->play == c)
				for (int k = 0; k < 4; k++) voice_push(r, NULL, ERRS_LOST);
			ch->have[ch->count & 3] = 0;
			ch->count++;
		}
		return;
	}
	r->st.bursts++;
	e->scrambled = p25p2_duid_scrambled(v);
	e->readable = !e->scrambled || r->have_seq;
	if (!e->readable) {
		if (v != P25P2_DUID_4V && v != P25P2_DUID_2V) chan_clear(ch);
		return;
	}
	if (e->scrambled) p25p2_scramble_burst(burst, ts, r->seq);
	switch (v) {
	case P25P2_DUID_4V:
		p25p2_field_get(burst, P25P2_F_ESS_B, ch->frag[ch->count & 3]);
		ch->have[ch->count & 3] = 1;
		ch->count++;
		ch->in_voice = 1;
		voice_read(r, e, c, burst, 4);
		break;
	case P25P2_DUID_2V:
		ch->in_voice = 1;
		voice_read(r, e, c, burst, 2);
		ess_read(r, c, ts, burst);
		memset(ch->have, 0, sizeof ch->have);
		ch->count = 0;
		break;
	case P25P2_DUID_FACCH_S:
	case P25P2_DUID_FACCH_U:
	case P25P2_DUID_SACCH_S:
	case P25P2_DUID_SACCH_U: {
		int sacch = v == P25P2_DUID_SACCH_S || v == P25P2_DUID_SACCH_U;
		mac_read(r, e, burst, sacch);
		// The SACCH comes in the middle of a call; a FACCH takes a voice burst's place, so the ESS starts afresh
		if (!sacch) {
			memset(ch->have, 0, sizeof ch->have);
			ch->count = 0;
		}
		if (e->ok && (e->octets[0] >> 5) == MAC_END_PTT) voice_end(r, c);
		break;
	}
	default:
		chan_clear(ch);
		break;
	}
}

static void lost_all(p25p2rx *r)
{
	for (int c = 0; c < 2; c++) {
		voice_end(r, c);
		chan_clear(&r->ch[c]);
	}
}

static void on_lock(void *ctx, int locked)
{
	p25p2rx *r = (p25p2rx *)ctx;
	p25p2rx_event *e;
	if (locked) {
		r->superframe = 0;
		e = ev_add(r, EV_SYNC);
		e->ts = r->fr.ts;
	} else {
		lost_all(r);
		ev_add(r, EV_LOST);
	}
	cqpsk_set_searching(&r->cq, !locked);
}

static void on_soft(const int8_t *soft, uint16_t n, uint32_t t_us, int silent, void *ctx)
{
	p25p2rx *r = (p25p2rx *)ctx;
	(void)t_us;
	// Exact zeros (a source that has stopped) have no phase, and read as dibits 00: a burst of them is a 4V. An
	// ISCH's length of them is the signal gone; they are counted, not looked at
	if (!silent) {
		r->silent = 0;
	} else if ((r->silent += n) >= P25P2_ISCH_SYMS || !p25p2_framer_locked(&r->fr)) {
		if (p25p2_framer_locked(&r->fr)) {
			p25p2_framer_reset(&r->fr);
			on_lock(r, 0);
		}
		p25p2_framer_reset(&r->fr);
		p25p2_framer_skip(&r->fr, n);
		return;
	}
	for (uint16_t i = 0; i < n; i++) {
		int v = soft[i];
		// P25's mapping: a step of +3 pi / 4 is dibit 01 (op25 p25_demodulator.py rescale and slicer; sdrtrunk
		// P25P2SyncPattern, whose +3 is D01)
		p25p2_framer_push(&r->fr, (uint8_t)(v >= 2 * SOFT_OUTER / 3 ? 1 : v >= 0 ? 0 : v >= -2 * SOFT_OUTER / 3 ? 2 : 3));
	}
}

// rate: complex samples a second, a multiple of 6000 from 24000 to 72000 (48000 is the app's). NULL if not.
SS_API p25p2rx *p25p2rx_new(int rate)
{
	cqpsk_cfg_t cfg;
	p25p2rx *r;
	if (rate <= 0) return NULL;
	if (!(r = (p25p2rx *)calloc(1, sizeof *r))) return NULL;
	memset(&cfg, 0, sizeof cfg);
	cfg.rate = (uint32_t)rate;
	cfg.sym_rate = P25P2_SYM_RATE;
	cfg.rolloff = 0;
	cfg.cutoff_hz = RX_CUTOFF_HZ;
	cfg.transition_hz = RX_TRANSITION_HZ;
	cfg.soft_per_rad = (float)(4 * (SOFT_OUTER / 3) / 3.14159265358979323846);
	cfg.max_offset_hz = RX_MAX_OFFSET_HZ;
	if (!cqpsk_init(&r->cq, &cfg, on_soft, r)) {
		free(r);
		return NULL;
	}
	p25p2_framer_init(&r->fr, on_slot, on_lock, r);
	r->play = -1;
	return r;
}

SS_API void p25p2rx_free(p25p2rx *r) { free(r); }

// The system whose scrambling sequence the bursts are sent with (from the control channel's network status: WACN,
// system id; the site's NAC, the color code). Until it is set only unscrambled bursts are read.
SS_API void p25p2rx_set_system(p25p2rx *r, uint32_t wacn, uint32_t sysid, uint32_t nac)
{
	p25p2_scramble_seq(wacn, sysid, nac, r->seq);
	r->have_seq = 1;
}

// The logical channel (0 or 1; a grant's TDMA slot) whose voice frames are queued; -1 none
SS_API void p25p2rx_set_slot(p25p2rx *r, int slot)
{
	if (slot != r->play && r->play >= 0) voice_end(r, r->play);
	r->play = slot == 0 || slot == 1 ? slot : -1;
}

SS_API void p25p2rx_feed_iq(p25p2rx *r, const float *re, const float *im, int n)
{
	if (n > 0) cqpsk_feed(&r->cq, re, im, n);
}

// The signal has gone (channel changed): forget the timing and start afresh
SS_API void p25p2rx_signal_lost(p25p2rx *r)
{
	if (p25p2_framer_locked(&r->fr)) ev_add(r, EV_LOST);
	lost_all(r);
	p25p2_framer_reset(&r->fr);
	cqpsk_reset(&r->cq);
}

SS_API int p25p2rx_locked(p25p2rx *r) { return p25p2_framer_locked(&r->fr); }

SS_API int p25p2rx_read_events(p25p2rx *r, p25p2rx_event *out, int max)
{
	int n = 0;
	while (n < max && r->ev_count > 0) {
		out[n++] = r->ev[r->ev_head];
		r->ev_head = (r->ev_head + 1) % EVENT_QUEUE;
		r->ev_count--;
	}
	return n;
}

// Voice frames of the chosen channel in order: VOICE_ITEM bytes each, the 49 AMBE+2 parameter bits one per byte,
// then the frame's bit errors (ERRS_LOST: a frame that did not arrive; ERRS_END: the end of a call, not a frame)
SS_API int p25p2rx_read_voice(p25p2rx *r, uint8_t *out, int max)
{
	int n = 0;
	while (n < max && r->v_count > 0) {
		memcpy(out + n * VOICE_ITEM, r->voice[r->v_head], VOICE_ITEM);
		r->v_head = (r->v_head + 1) % VOICE_QUEUE;
		r->v_count--;
		n++;
	}
	return n;
}

SS_API void p25p2rx_stats(p25p2rx *r, p25p2rx_stats_t *out)
{
	*out = r->st;
	out->locked = p25p2_framer_locked(&r->fr);
	out->syncs = (int32_t)r->fr.syncs;
	out->shifts = (int32_t)r->fr.shifts;
	out->have_system = r->have_seq;
	cqpsk_stats(&r->cq, &out->offset_hz, &out->err_rad, &out->power);
}

// sizeof(p25p2rx_event), for the C# side's check; and the stats'
SS_API int p25p2rx_abi(void) { return (int)sizeof(p25p2rx_event); }
SS_API int p25p2rx_stats_abi(void) { return (int)sizeof(p25p2rx_stats_t); }

// The codes and the scrambling sequence for tests against reference values (p25p2.h has what they do)
SS_API void p25p2_test_scramble(uint32_t wacn, uint32_t sysid, uint32_t nac, uint8_t *seq) { p25p2_scramble_seq(wacn, sysid, nac, seq); }
SS_API uint64_t p25p2_test_isch_encode(int v) { return p25p2_isch_encode((unsigned)v); }
SS_API int p25p2_test_isch_decode(uint64_t cw, int *errs) { return p25p2_isch_decode(cw, errs); }
SS_API int p25p2_test_duid_encode(int v) { return p25p2_duid_encode(v); }
SS_API int p25p2_test_duid_decode(int cw, int *errs) { return p25p2_duid_decode((uint8_t)cw, errs); }
SS_API void p25p2_test_rs_encode(int code, const uint8_t *info, uint8_t *sent) { p25p2_rs_encode(code, info, sent); }
SS_API int p25p2_test_rs_decode(int code, uint8_t *sent, const uint8_t *erased) { return p25p2_rs_decode(code, sent, erased); }
SS_API int p25p2_test_crc12(const uint8_t *oct, int nbits) { return p25p2_crc12(oct, nbits); }
