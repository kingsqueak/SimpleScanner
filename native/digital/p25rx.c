// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's P25 Phase 1 receiver: FM discriminator samples (or complex baseband, through cqpsk.c) in, frame
// events and decoded voice out. The work is RadioFoundry's (MIT; the IMBE dequantizer is from mbelib, ISC; see
// radiofoundry/): its soft modem finds the symbols, its framer the data units, its P25 layer
// the fields and the voice code words, and its fixed-point IMBE decoder the speech. This file
// only joins them and queues the results for a caller that polls.
//
// One instance per channel listened to. Not thread-safe: call everything for an instance from
// one thread.
#include <stdlib.h>
#include <string.h>
#include "radiofoundry/core/air/air_softmodem.h"
#include "radiofoundry/core/air/air_syms.h"
#include "radiofoundry/core/p25/p25.h"
#include "radiofoundry/core/p25/p25_tsbk.h"
#include "radiofoundry/core/mbe/imbe.h"
#include "p25mbt.h"
#include "cqpsk.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define COUNTS_PER_KHZ 4000          // discriminator scale handed to the soft modem
#define PCM_QUEUE 16384              // samples at 8 kHz: two seconds
#define EVENT_QUEUE 256
#define FEED_BLOCK 2048

enum { EV_UNIT = 1, EV_CALL_START = 2, EV_CALL_UPDATE = 3, EV_CALL_END = 4, EV_TSBK = 5 };

// What the caller reads. Fields that do not apply to an event are 0. Only ever add fields at the end: the C#
// side (Digital/P25Decoder.cs) mirrors this, and refuses a library whose p25rx_abi() is not its size.
typedef struct {
    int32_t kind;                    // EV_*
    int32_t nac, duid, nid_errs;     // EV_UNIT: every data unit received (nid_errs -1: placed by order)
    int32_t late, have_lc, have_alg; // calls
    int32_t lcf, mfid, emergency, tgid, src, dst; // calls; src and dst also a grant's (dst: unit to unit)
    int32_t algid, kid;              // algid 0x80 is clear; anything else is encrypted and gives no voice
    int32_t tsbk_opcode, tsbk_mfid, tsbk_kind; // EV_TSBK: a trunking message (kind P25_TSBK_*)
    int32_t wacn, sysid, rfss, site; // network, RFSS and adjacent status; RFSS and site also secondary channels
    // EV_TSBK, the message as it came: one block of a TSDU, or a multi-block packet's header and first data block
    uint8_t raw[12], raw_data[12];
    int32_t crc_ok;                  // its CRC held (multi-block: the packet CRC); if not, no field below is set
    int32_t mbt;                     // it came as a multi-block packet
    int32_t last, prot;              // the last block flag; protected (its arguments are encrypted, not read)
    // Grants: the channel, talkgroup (unit to unit: dst), service options when the form has them; the update
    // form's second grant in ch2 / tg2. Status: ch is the control channel; secondary channels: ch and ch2
    int32_t ch, ch2, tg, tg2, svc, has_svc, unit;
    // Channel identifiers as sent: base x 5 Hz, spacing and bandwidth x 125 Hz, transmit offset signed (the
    // 0x3D form x 250 kHz, the others x the spacing), channels per carrier, the TDMA form's channel type
    int32_t iden_id, iden_form;
    uint32_t iden_base;
    int32_t iden_spacing, iden_tx_off, iden_bw, iden_slots, iden_tdma, iden_type;
    int32_t lra, site_flags, ssc, ssc2; // status: LRA, the 4 flag bits ahead of the system, service classes
    int32_t svc_avail, svc_supp, svc_prio; // system service broadcast
} p25rx_event;

typedef struct {
    air_softmodem_t sm;
    cqpsk_t cq;                      // the front end of an instance made by p25rx_new_iq
    int iq;
    air_framer_t fr;
    p25_call_rx_t call;
    uint8_t hard[P25_UNIT_BYTES_MAX];
    int8_t soft[P25_UNIT_SYMS_MAX];
    imbe_dec_state_t dec_state;
    mbe_params_t params;
    mbe_state_t mbe_state;
    mbe_frame_t prev, cur;
    int32_t buf[MBE_BUF_WORDS];
    imbe_mem_t dec;
    uint64_t samples;
    uint32_t rate;
    float scale;                     // discriminator units to modem counts
    int16_t pcm[PCM_QUEUE];
    uint32_t pcm_head, pcm_count;
    p25rx_event events[EVENT_QUEUE];
    uint32_t ev_head, ev_count;
    uint32_t voice_frames, voice_bad;
} p25rx;

static p25rx_event *push_event(p25rx *r, int kind)
{
    p25rx_event *e;
    if (r->ev_count == EVENT_QUEUE) {            // full: the oldest goes
        r->ev_head = (r->ev_head + 1) % EVENT_QUEUE;
        r->ev_count--;
    }
    e = &r->events[(r->ev_head + r->ev_count) % EVENT_QUEUE];
    r->ev_count++;
    memset(e, 0, sizeof *e);
    e->kind = kind;
    return e;
}

static void fill_call(p25rx_event *e, const p25_call_t *c)
{
    e->nac = c->nac;
    e->late = c->late;
    e->have_lc = c->have_lc;
    e->have_alg = c->have_alg;
    e->lcf = c->lc.lcf;
    e->mfid = c->lc.mfid;
    e->emergency = c->lc.emergency;
    e->tgid = c->lc.tgid;
    e->src = (int32_t)c->lc.src;
    e->dst = (int32_t)c->lc.dst;
    e->algid = c->algid;
    e->kid = c->kid;
}

static void on_chunk(const air_chunk_t *c, void *ctx) { air_framer_rx(&((p25rx *)ctx)->fr, c); }

// The differential demodulator's symbols: sliced as the soft modem slices its own (+3 at AIR_SOFT_OUTER), so the
// framer gets the same kind of chunk from either front end
static void on_soft(const int8_t *soft, uint16_t n, uint32_t t_us, int silent, void *ctx)
{
    uint8_t hard[CQPSK_CHUNK / 4];
    air_chunk_t c;
    uint16_t i;
    memset(hard, 0, sizeof hard);
    for (i = 0; i < n; i++) {
        int v = soft[i];
        air_sym_put(hard, i, v >= 2 * AIR_SOFT_OUTER / 3 ? 1 : v >= 0 ? 0 : v >= -2 * AIR_SOFT_OUTER / 3 ? 2 : 3);
    }
    memset(&c, 0, sizeof c);
    c.t_us = t_us;
    c.nsyms = n;
    c.sync_at = -1;
    c.flags = silent ? AIR_CHUNK_NO_SIGNAL : 0;
    c.hard = hard;
    c.soft = soft;
    air_framer_rx(&((p25rx *)ctx)->fr, &c);
    cqpsk_set_searching(&((p25rx *)ctx)->cq, !air_framer_locked(&((p25rx *)ctx)->fr));
}

// A trunking message's fields into an event
static void fill_tsbk(p25rx_event *e, const p25_tsbk_t *t)
{
    e->tsbk_opcode = t->opcode;
    e->tsbk_mfid = t->mfid;
    e->tsbk_kind = t->kind;
    e->last = t->last;
    e->prot = t->prot;
    switch (t->kind) {
    case P25_TSBK_GRANT:
        e->unit = t->u.grant.unit;
        e->has_svc = t->u.grant.has_svc;
        e->svc = t->u.grant.svc;
        e->ch = t->u.grant.ch;
        e->ch2 = t->u.grant.ch2;
        e->tg = t->u.grant.tg;
        e->tg2 = t->u.grant.tg2;
        e->src = (int32_t)t->u.grant.src;
        e->dst = (int32_t)t->u.grant.dst;
        break;
    case P25_TSBK_IDEN:
        e->iden_id = t->u.iden.id;
        e->iden_form = t->u.iden.form;
        e->iden_base = t->u.iden.base;
        e->iden_spacing = t->u.iden.spacing;
        e->iden_tx_off = t->u.iden.tx_off;
        e->iden_bw = t->u.iden.bw_khz8;
        e->iden_slots = t->u.iden.slots;
        e->iden_tdma = t->u.iden.tdma;
        e->iden_type = t->u.iden.type;
        break;
    case P25_TSBK_NET:
        e->lra = t->u.net.lra;
        e->wacn = (int32_t)t->u.net.wacn;
        e->sysid = t->u.net.sysid;
        e->ch = t->u.net.ch;
        e->ssc = t->u.net.ssc;
        break;
    case P25_TSBK_RFSS:
    case P25_TSBK_ADJ:
        e->lra = t->u.site.lra;
        e->site_flags = t->u.site.flags;
        e->sysid = t->u.site.sysid;
        e->rfss = t->u.site.rfss;
        e->site = t->u.site.site;
        e->ch = t->u.site.ch;
        e->ssc = t->u.site.ssc;
        break;
    case P25_TSBK_SCCB:
        e->rfss = t->u.sccb.rfss;
        e->site = t->u.sccb.site;
        e->ch = t->u.sccb.ch[0];
        e->ssc = t->u.sccb.ssc[0];
        e->ch2 = t->u.sccb.ch[1];
        e->ssc2 = t->u.sccb.ssc[1];
        break;
    case P25_TSBK_SYS_SRV:
        e->svc_avail = (int32_t)t->u.svc.avail;
        e->svc_supp = (int32_t)t->u.svc.supp;
        e->svc_prio = t->u.svc.prio;
        break;
    default:
        break;
    }
}

// Every unit, before the call layer reads it. Trunking signaling comes only here: each block of a TSDU, and a
// multi-block trunking packet, as an EV_TSBK. A block whose CRC fails is passed on too, with crc_ok 0 and its
// octets only.
static void on_unit(const p25_rx_unit_t *u, void *ctx)
{
    p25rx *r = (p25rx *)ctx;
    p25rx_event *e = push_event(r, EV_UNIT);
    e->nac = u->nac;
    e->duid = u->duid;
    e->nid_errs = u->nid_errs;
    if (u->duid == P25_DUID_TSDU) {
        uint8_t k, blk[P25_TSBK_BYTES];
        for (k = 0; k < 3; k++) {
            p25_tsbk_t t;
            int ok = p25_block_get(u->air, k, blk) >= 0;
            e = push_event(r, EV_TSBK);
            e->nac = u->nac;
            memcpy(e->raw, blk, sizeof blk);
            if (!ok) continue;
            e->crc_ok = 1;
            memset(&t, 0, sizeof t);
            p25_tsbk_parse(blk, &t);
            fill_tsbk(e, &t);
            if (blk[0] & P25_TSBK_LB) break;
        }
    } else if (u->duid == P25_DUID_PDU) {
        p25_tsbk_t t;
        uint8_t hdr[P25_TSBK_BYTES], data0[P25_TSBK_BYTES];
        bool crc_ok;
        if (!p25mbt_unit(u->air, u->nsyms, &t, hdr, data0, &crc_ok)) return;
        e = push_event(r, EV_TSBK);
        e->nac = u->nac;
        e->mbt = 1;
        memcpy(e->raw, hdr, sizeof hdr);
        memcpy(e->raw_data, data0, sizeof data0);
        e->tsbk_opcode = t.opcode;
        e->tsbk_mfid = t.mfid;
        if (!crc_ok) return;
        e->crc_ok = 1;
        fill_tsbk(e, &t);
    }
}

static void on_start(const p25_call_t *c, void *ctx) { fill_call(push_event((p25rx *)ctx, EV_CALL_START), c); }
static void on_update(const p25_call_t *c, void *ctx) { fill_call(push_event((p25rx *)ctx, EV_CALL_UPDATE), c); }
static void on_end(void *ctx) { push_event((p25rx *)ctx, EV_CALL_END); }

// A voice frame of a clear call: the call layer withholds encrypted ones. On a late entry the
// first frames arrive before the encryption word has; those are held back too.
static void on_voice(const uint8_t imbe[P25_IMBE_BYTES], uint8_t errs, void *ctx)
{
    p25rx *r = (p25rx *)ctx;
    int16_t pcm[MBE_FRAME_LEN];
    int i;
    r->voice_frames++;
    if (P25_VOICE_ERRS_BAD(errs)) r->voice_bad++;
    imbe_dec_frame(&r->dec, imbe, errs, pcm);
    if (r->call.call.late && !r->call.call.have_alg) return;
    for (i = 0; i < MBE_FRAME_LEN; i++) {
        if (r->pcm_count == PCM_QUEUE) {
            r->pcm_head = (r->pcm_head + 1) % PCM_QUEUE;
            r->pcm_count--;
        }
        r->pcm[(r->pcm_head + r->pcm_count) % PCM_QUEUE] = pcm[i];
        r->pcm_count++;
    }
}

static const p25_call_sink_t sink = { on_start, on_update, on_voice, on_end, on_unit };

// One instance, the call layer and framer set up; the front end is the caller's
static p25rx *alloc_rx(void)
{
    p25rx *r = (p25rx *)calloc(1, sizeof(p25rx));
    if (!r) return NULL;
    r->dec.s = &r->dec_state;
    r->dec.p = &r->params;
    r->dec.mbe.s = &r->mbe_state;
    r->dec.mbe.prev = &r->prev;
    r->dec.mbe.cur = &r->cur;
    r->dec.mbe.buf = r->buf;
    imbe_dec_init(&r->dec);
    p25_call_rx_init(&r->call, P25_NAC_ANY, &sink, r);
    r->call.rx.trunk = true;
    air_framer_init(&r->fr, &p25_air_mode, &r->call.rx, r->hard, r->soft, P25_UNIT_SYMS_MAX);
    return r;
}

// rate: of the samples fed, 8000 to 48000 Hz (24000 is the modem's own rate and the best).
// hz_per_unit: the deviation that reads 1.0 in the samples. NULL if the modem refuses the rate.
SS_API p25rx *p25rx_new(int rate, float hz_per_unit)
{
    p25rx *r = alloc_rx();
    if (!r) return NULL;
    r->rate = (uint32_t)rate;
    r->scale = hz_per_unit / 1000.0f * COUNTS_PER_KHZ;
    if (!air_softmodem_rx_open(&r->sm, &p25_air_mode.profile, r->rate, COUNTS_PER_KHZ, on_chunk, r)) {
        free(r);
        return NULL;
    }
    return r;
}

// The differential front end's receive filter (cqpsk_cfg_t): a root raised cosine of roll-off 1. Its noise bandwidth
// is the symbol rate's, as a matched filter's is; against the raised cosine pulse P25 sends (roll-off 0.2: no
// intersymbol interference at the symbol centres without any filter) it adds less interference than the roll-off
// 0.2 root raised cosine does, and it decoded the most blocks from an off-air simulcast capture and from generated
// signals with an echo (good blocks of a weak 90 s capture, roll-off 0.2: 3055; 0.5: 3125; 1: 3164; a flat
// low-pass to 2.6 to 3.5 kHz, tried with an earlier timing loop: about 3100)
#define IQ_ROLLOFF 1.0f
#define IQ_MAX_OFFSET_HZ 2000.0f

// Complex baseband instead: rate samples a second (48000: 10 to a symbol), centred on the channel, any level. For
// CQPSK and LSM (simulcast) transmitters, and C4FM ones too. NULL if the rate is not a multiple of 4800 from 19200
// to 57600.
SS_API p25rx *p25rx_new_iq(int rate)
{
    cqpsk_cfg_t cfg;
    p25rx *r;
    if (rate <= 0) return NULL;
    if (!(r = alloc_rx())) return NULL;
    memset(&cfg, 0, sizeof cfg);
    cfg.rate = (uint32_t)rate;
    cfg.sym_rate = p25_air_mode.profile.sym_rate;
    cfg.rolloff = IQ_ROLLOFF;
    cfg.soft_per_rad = (float)(4 * (AIR_SOFT_OUTER / 3) / 3.14159265358979323846);
    cfg.max_offset_hz = IQ_MAX_OFFSET_HZ;
    if (!cqpsk_init(&r->cq, &cfg, on_soft, r)) {
        free(r);
        return NULL;
    }
    r->iq = 1;
    r->rate = (uint32_t)rate;
    return r;
}

SS_API void p25rx_free(p25rx *r) { free(r); }

// Complex samples for an instance made by p25rx_new_iq (ignored by one made by p25rx_new, and the reverse).
SS_API void p25rx_feed_iq(p25rx *r, const float *re, const float *im, int n)
{
    if (!r->iq || n <= 0) return;
    cqpsk_feed(&r->cq, re, im, n);
    r->samples += (uint64_t)n;
}

// What the differential front end measures (all 0 for an FM instance): the carrier offset in Hz, the mean distance
// of the symbols' phase steps from the nearest of the four in radians (near 0 clean, about 0.35 for noise), the filtered
// signal's mean power.
SS_API void p25rx_iq_stats(p25rx *r, float *offset_hz, float *err_rad, float *power)
{
    *offset_hz = *err_rad = *power = 0;
    if (r->iq) cqpsk_stats(&r->cq, offset_hz, err_rad, power);
}

// Discriminator samples, positive above the carrier, unfiltered below 3 kHz (no de-emphasis, no
// high-pass: the four levels need the response down to a few tens of Hz).
SS_API void p25rx_feed(p25rx *r, const float *disc, int n)
{
    int16_t tmp[FEED_BLOCK];
    if (r->iq) return;
    while (n > 0) {
        int k = n < FEED_BLOCK ? n : FEED_BLOCK, i;
        for (i = 0; i < k; i++) {
            float v = disc[i] * r->scale;
            tmp[i] = (int16_t)(v > 32767.0f ? 32767 : v < -32767.0f ? -32767 : v);
        }
        // The modem wants a clock that runs with the samples; it wraps, which it allows.
        air_softmodem_rx(&r->sm, tmp, (uint16_t)k, (uint32_t)(r->samples * 1000000u / r->rate));
        r->samples += (uint64_t)k;
        disc += k;
        n -= k;
    }
}

// The signal has gone (squelch closed, channel changed): end any call now and start afresh.
SS_API void p25rx_signal_lost(p25rx *r)
{
    uint8_t zero = 0;
    air_chunk_t c;
    memset(&c, 0, sizeof c);
    c.nsyms = 1;
    c.sync_at = -1;
    c.flags = AIR_CHUNK_GAP | AIR_CHUNK_NO_SIGNAL;
    c.hard = &zero;
    air_framer_rx(&r->fr, &c);
    if (r->iq) cqpsk_reset(&r->cq);
    else air_softmodem_rx_reset(&r->sm);
    imbe_dec_init(&r->dec);
}

// True while the framer is following P25 data units.
SS_API int p25rx_locked(p25rx *r) { return air_framer_locked(&r->fr) ? 1 : 0; }

// Decoded voice, 8 kHz signed 16-bit. Returns the samples copied.
SS_API int p25rx_read_pcm(p25rx *r, int16_t *out, int max)
{
    int n = 0;
    while (n < max && r->pcm_count > 0) {
        out[n++] = r->pcm[r->pcm_head];
        r->pcm_head = (r->pcm_head + 1) % PCM_QUEUE;
        r->pcm_count--;
    }
    return n;
}

SS_API int p25rx_read_events(p25rx *r, p25rx_event *out, int max)
{
    int n = 0;
    while (n < max && r->ev_count > 0) {
        out[n++] = r->events[r->ev_head];
        r->ev_head = (r->ev_head + 1) % EVENT_QUEUE;
        r->ev_count--;
    }
    return n;
}

// The size of p25rx_event, so a caller can tell a library built from another version of this file.
SS_API int p25rx_abi(void) { return (int)sizeof(p25rx_event); }

// Voice frames seen and how many of them were too damaged to trust, since the start.
SS_API void p25rx_stats(p25rx *r, int32_t *voice_frames, int32_t *voice_bad)
{
    *voice_frames = (int32_t)r->voice_frames;
    *voice_bad = (int32_t)r->voice_bad;
}
