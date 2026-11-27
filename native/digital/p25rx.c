// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's P25 Phase 1 receiver: FM discriminator samples in, frame events and decoded
// voice out. The work is RadioFoundry's (MIT; the IMBE dequantizer is from mbelib, ISC; see
// radiofoundry/): its soft modem finds the symbols, its framer the data units, its P25 layer
// the fields and the voice code words, and its fixed-point IMBE decoder the speech. This file
// only joins them and queues the results for a caller that polls.
//
// One instance per channel listened to. Not thread-safe: call everything for an instance from
// one thread.
#include <stdlib.h>
#include <string.h>
#include "radiofoundry/core/air/air_softmodem.h"
#include "radiofoundry/core/p25/p25.h"
#include "radiofoundry/core/p25/p25_tsbk.h"
#include "radiofoundry/core/mbe/imbe.h"

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

// What the caller reads. Fields that do not apply to an event are 0.
typedef struct {
    int32_t kind;                    // EV_*
    int32_t nac, duid, nid_errs;     // EV_UNIT: every data unit received (nid_errs -1: placed by order)
    int32_t late, have_lc, have_alg; // calls
    int32_t lcf, mfid, emergency, tgid, src, dst;
    int32_t algid, kid;              // algid 0x80 is clear; anything else is encrypted and gives no voice
    int32_t tsbk_opcode, tsbk_mfid, tsbk_kind;
    int32_t wacn, sysid, rfss, site;
} p25rx_event;

typedef struct {
    air_softmodem_t sm;
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

// Every unit, before the call layer reads it. Trunking signaling comes only here.
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
            if (p25_block_get(u->air, k, blk) < 0) continue;
            memset(&t, 0, sizeof t);
            p25_tsbk_parse(blk, &t);
            e = push_event(r, EV_TSBK);
            e->nac = u->nac;
            e->tsbk_opcode = t.opcode;
            e->tsbk_mfid = t.mfid;
            e->tsbk_kind = t.kind;
            if (t.kind == P25_TSBK_NET) {
                e->wacn = (int32_t)t.u.net.wacn;
                e->sysid = t.u.net.sysid;
            } else if (t.kind == P25_TSBK_RFSS) {
                e->sysid = t.u.site.sysid;
                e->rfss = t.u.site.rfss;
                e->site = t.u.site.site;
            }
            if (blk[0] & P25_TSBK_LB) break;
        }
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

// rate: of the samples fed, 8000 to 48000 Hz (24000 is the modem's own rate and the best).
// hz_per_unit: the deviation that reads 1.0 in the samples. NULL if the modem refuses the rate.
SS_API p25rx *p25rx_new(int rate, float hz_per_unit)
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
    r->rate = (uint32_t)rate;
    r->scale = hz_per_unit / 1000.0f * COUNTS_PER_KHZ;
    if (!air_softmodem_rx_open(&r->sm, &p25_air_mode.profile, r->rate, COUNTS_PER_KHZ, on_chunk, r)) {
        free(r);
        return NULL;
    }
    return r;
}

SS_API void p25rx_free(p25rx *r) { free(r); }

// Discriminator samples, positive above the carrier, unfiltered below 3 kHz (no de-emphasis, no
// high-pass: the four levels need the response down to a few tens of Hz).
SS_API void p25rx_feed(p25rx *r, const float *disc, int n)
{
    int16_t tmp[FEED_BLOCK];
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
    air_softmodem_rx_reset(&r->sm);
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

// Voice frames seen and how many of them were too damaged to trust, since the start.
SS_API void p25rx_stats(p25rx *r, int32_t *voice_frames, int32_t *voice_bad)
{
    *voice_frames = (int32_t)r->voice_frames;
    *voice_bad = (int32_t)r->voice_bad;
}
