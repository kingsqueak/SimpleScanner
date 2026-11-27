// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's NXDN receiver (conventional voice, NXDN TS 1-A): FM discriminator samples in;
// what the call says about itself and its voice frames out.
//
// The work is RadioFoundry's (MIT, see radiofoundry/): its soft modem finds the symbols, its
// framer the frames, its NXDN layer the channels and the call. NXDN comes at two speeds that
// cannot be told apart before a frame is found: 4800 bit/s in a 6.25 kHz channel and 9600 bit/s
// in a 12.5 kHz one. Both receivers run side by side here on the same samples, and the one that
// finds frames is the one reported.
//
// Speech is not decoded here. The caller gets the 49 parameter bits of each 20 ms AMBE+2 frame,
// in the same form dmrrx.c gives them, for a vocoder if it has one (native/ambe). The 9600 bit/s
// full-rate voice mode (EFR) is a different codec with no decoder here: such calls are shown and
// give no frames. Encrypted calls give none either (RadioFoundry's call layer holds them back).
//
// One instance per channel listened to. Not thread-safe: call everything for an instance from
// one thread.
#include <stdlib.h>
#include <string.h>
#include "radiofoundry/core/air/air_softmodem.h"
#include "radiofoundry/core/nxdn/nxdn.h"
#include "radiofoundry/core/dmr/ambe/ambeFec.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define COUNTS_PER_KHZ 4000          // discriminator scale handed to the soft modems
#define FEED_BLOCK 2048
#define VOICE_QUEUE 128              // frames: 2.5 s
#define VOICE_ITEM 50                // 49 parameter bits, one per byte, then the error count
#define ERRS_END 255                 // not a frame: the call ended, the vocoder starts afresh

// What the caller reads. The call's details stay after it ends.
typedef struct {
    int32_t frames;                  // frames read since the start, both speeds
    int32_t rate;                    // 4800 or 9600: the speed of the last frames; 0 none yet
    int32_t ran;                     // radio access number, 0 to 63 (the system's tone squelch, so to say); -1 none yet
    int32_t in_call;
    int32_t have_call;               // the fields below are from the call's own message
    int32_t late;                    // the call's start was not heard
    int32_t call_type;               // NXDN_CALL_*: 1 a group call, 4 a call to one radio
    int32_t dst, src;
    int32_t emergency;
    int32_t cipher;                  // 0 clear; 1 scrambled, 2 DES, 3 AES
    int32_t voice_mode;              // NXDN_VOICE_*: 3 is the full-rate codec, which is not decoded
    int32_t calls;                   // voice calls since the start
} nxdnrx_info;

struct nxdnrx;

typedef struct {
    struct nxdnrx *owner;
    int32_t rate;
    air_softmodem_t sm;
    air_framer_t fr;
    nxdn_call_rx_t call;
    uint8_t hard[NXDN_FRAME_BYTES];
    int8_t soft[NXDN_FRAME_SYMS];
    uint32_t seen;                   // call.stats.frames at the last look
} side_t;

typedef struct nxdnrx {
    side_t side[2];                  // 4800 and 9600 bit/s
    nxdnrx_info info;
    uint64_t samples;
    uint32_t rate;
    float scale;                     // discriminator units to modem counts
    uint8_t voice[VOICE_QUEUE][VOICE_ITEM];
    uint32_t v_head, v_count;
} nxdnrx;

static void voice_push(nxdnrx *r, const uint8_t *bits, uint8_t errs)
{
    uint8_t *item;
    if (r->v_count == VOICE_QUEUE) {             // full: the oldest goes
        r->v_head = (r->v_head + 1) % VOICE_QUEUE;
        r->v_count--;
    }
    item = r->voice[(r->v_head + r->v_count) % VOICE_QUEUE];
    r->v_count++;
    if (bits) memcpy(item, bits, AMBE_PARAM_BITS);
    else memset(item, 0, AMBE_PARAM_BITS);
    item[AMBE_PARAM_BITS] = errs;
}

static void fill(side_t *s, const nxdn_call_t *c)
{
    nxdnrx_info *i = &s->owner->info;
    i->rate = s->rate;
    i->ran = c->ran;
    i->in_call = 1;
    i->late = c->late;
    i->have_call = c->have_vcall;
    if (c->have_vcall) {
        i->call_type = c->vcall.call_type;
        i->dst = c->vcall.dst;
        i->src = c->vcall.src;
        i->emergency = (c->vcall.cc_opt & NXDN_CC_EMERGENCY) != 0;
        i->cipher = c->iv && c->vcall.cipher == NXDN_CIPHER_NONE ? NXDN_CIPHER_DES : c->vcall.cipher;
        i->voice_mode = c->vcall.voice_opt & 7;
    }
}

static void on_start(const nxdn_call_t *c, void *ctx)
{
    side_t *s = (side_t *)ctx;
    nxdnrx_info *i = &s->owner->info;
    i->calls++;
    i->have_call = i->call_type = i->dst = i->src = i->emergency = i->cipher = i->voice_mode = 0;
    fill(s, c);
}

static void on_update(const nxdn_call_t *c, void *ctx) { fill((side_t *)ctx, c); }

// A voice channel of a clear call whose details are in: 72 bits, the same code words and
// interleaving as a DMR voice frame
static void on_voice(const uint8_t ehr[NXDN_EHR_BYTES], void *ctx)
{
    side_t *s = (side_t *)ctx;
    uint8_t bits[AMBE_PARAM_BITS];
    ambe_fec_result_t res;
    if (s->owner->info.voice_mode == NXDN_VOICE_9600_EFR) return;
    ambeFecDecode(ehr, bits, &res);
    voice_push(s->owner, bits, res.errors);
}

static void on_end(void *ctx)
{
    side_t *s = (side_t *)ctx;
    s->owner->info.in_call = 0;
    voice_push(s->owner, NULL, ERRS_END);
}

static const nxdn_call_sink_t sink = { on_start, on_update, on_voice, on_end };

static void on_chunk(const air_chunk_t *c, void *ctx)
{
    side_t *s = (side_t *)ctx;
    nxdnrx_info *i = &s->owner->info;
    air_framer_rx(&s->fr, c);
    if (s->call.stats.frames != s->seen) {
        i->frames += (int32_t)(s->call.stats.frames - s->seen);
        s->seen = s->call.stats.frames;
        i->rate = s->rate;
    }
}

static void info_clear(nxdnrx *r)
{
    memset(&r->info, 0, sizeof r->info);
    r->info.ran = -1;
    r->v_head = r->v_count = 0;
}

// rate: of the samples fed, 8000 to 24000 Hz (24000 is the best: ten samples to a symbol at the
// slower speed, five at the faster). hz_per_unit: the deviation that reads 1.0 in the samples.
// NULL if a modem refuses the rate.
SS_API nxdnrx *nxdnrx_new(int rate, float hz_per_unit)
{
    static const air_mode_t *const modes[2] = { &nxdn_air_mode, &nxdn_air_mode_9600 };
    nxdnrx *r = (nxdnrx *)calloc(1, sizeof(nxdnrx));
    int k;
    if (!r) return NULL;
    info_clear(r);
    r->rate = (uint32_t)rate;
    r->scale = hz_per_unit / 1000.0f * COUNTS_PER_KHZ;
    for (k = 0; k < 2; k++) {
        side_t *s = &r->side[k];
        s->owner = r;
        s->rate = k ? 9600 : 4800;
        nxdn_call_rx_init(&s->call, NXDN_RAN_ANY, &sink, s);
        // The framer's context is the call layer's receiver, which sits first in it
        air_framer_init(&s->fr, modes[k], &s->call.rx, s->hard, s->soft, NXDN_FRAME_SYMS);
        if (!air_softmodem_rx_open(&s->sm, &modes[k]->profile, r->rate, COUNTS_PER_KHZ, on_chunk, s)) {
            free(r);
            return NULL;
        }
    }
    return r;
}

SS_API void nxdnrx_free(nxdnrx *r) { free(r); }

// Discriminator samples, positive above the carrier, unfiltered below 3 kHz (no de-emphasis, no
// high-pass: the four levels need the response down to a few tens of Hz).
SS_API void nxdnrx_feed(nxdnrx *r, const float *disc, int n)
{
    int16_t tmp[FEED_BLOCK];
    while (n > 0) {
        int k = n < FEED_BLOCK ? n : FEED_BLOCK, i;
        uint32_t t = (uint32_t)(r->samples * 1000000u / r->rate);
        for (i = 0; i < k; i++) {
            float v = disc[i] * r->scale;
            tmp[i] = (int16_t)(v > 32767.0f ? 32767 : v < -32767.0f ? -32767 : v);
        }
        air_softmodem_rx(&r->side[0].sm, tmp, (uint16_t)k, t);
        air_softmodem_rx(&r->side[1].sm, tmp, (uint16_t)k, t);
        r->samples += (uint64_t)k;
        disc += k;
        n -= k;
    }
}

// The signal has gone (squelch closed, channel changed): end any call now and start afresh.
SS_API void nxdnrx_signal_lost(nxdnrx *r)
{
    uint8_t zero = 0;
    air_chunk_t c;
    int k;
    memset(&c, 0, sizeof c);
    c.nsyms = 1;
    c.sync_at = -1;
    c.flags = AIR_CHUNK_GAP | AIR_CHUNK_NO_SIGNAL;
    c.hard = &zero;
    for (k = 0; k < 2; k++) {
        air_framer_rx(&r->side[k].fr, &c);
        air_softmodem_rx_reset(&r->side[k].sm);
    }
    info_clear(r);
}

SS_API void nxdnrx_status(nxdnrx *r, nxdnrx_info *out) { *out = r->info; }

// Voice frames in order: VOICE_ITEM bytes each, the 49 AMBE+2 parameter bits one per byte and
// then the frame's bit errors (ERRS_END marks the end of a call and is not a frame). Returns the
// items copied.
SS_API int nxdnrx_read_voice(nxdnrx *r, uint8_t *out, int max)
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
