// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's DMR receiver (ETSI TS 102 361, Tier II conventional): FM discriminator samples
// in; what each of the two time slots carries and the voice frames of one call out.
//
// The parts are RadioFoundry's (MIT, see radiofoundry/): its soft modem finds the symbols, its
// framer cuts them into 30 ms slots on the sync patterns in the middle of the bursts, and its
// DMR code does the error correction and reads the link control. What RadioFoundry does not
// have is the layer between: its radios take DMR bursts from a modem chip. That layer is here:
// which slot a burst belongs to, where a voice superframe stands, when a call starts and ends.
//
// Speech is not decoded here. The caller gets the 49 parameter bits of each 20 ms AMBE+2 frame
// and hands them to a vocoder if it has one (native/ambe).
//
// One instance per channel listened to. Not thread-safe: call everything for an instance from
// one thread.
#include <stdlib.h>
#include <string.h>
#include "radiofoundry/core/air/air_softmodem.h"
#include "radiofoundry/core/air/air_framer.h"
#include "radiofoundry/hal/hal_radio.h"
#include "radiofoundry/core/dmr/fec/dmrFec.h"
#include "radiofoundry/core/dmr/fec/dmrCach.h"
#include "radiofoundry/core/dmr/call/dmr_lc.h"
#include "radiofoundry/core/dmr/ambe/ambeFec.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define COUNTS_PER_KHZ 4000          // discriminator scale handed to the soft modem
#define FEED_BLOCK 2048
#define SLOT_SYMS 144                // 30 ms: 12 symbols of CACH or guard time, then the 132 of a burst
#define BURST_AT 12
#define SYNC_AT 66                   // the sync pattern's first symbol in the slot
#define VOICE_QUEUE 128              // frames: 2.5 s
#define VOICE_ITEM 50                // 49 parameter bits, one per byte, then the error count
#define ERRS_LOST 7                  // a frame that did not arrive: the vocoder repeats or mutes
#define ERRS_END 255                 // not a frame: the call ended, the vocoder starts afresh
#define LATE_HOLD_BURSTS 18          // late entry: bursts kept silent while the link control is awaited
#define STALE_SLOTS 26               // a slot that has said nothing for this many slots is idle

enum { SRC_BS = 1, SRC_MS = 2, SRC_DIRECT = 3 };
enum { ST_IDLE = 0, ST_VOICE = 1, ST_DATA = 2, ST_CONTROL = 3 };
// Sync pattern numbers: bit 0 set = data, bits 2-1 = who sends it
enum { SY_BS = 0, SY_MS = 2, SY_TS1 = 4, SY_TS2 = 6 };
// Data types (TS 102 361-1 table 6.1)
enum { DT_PI = 0, DT_VOICE_HEADER = 1, DT_TERMINATOR = 2, DT_CSBK = 3, DT_IDLE = 9 };

// What the caller reads about one time slot. The call's details stay after it ends.
typedef struct {
    int32_t slot;                    // 1 or 2; 0 when the signal does not say (a mobile's own bursts)
    int32_t state;                   // ST_*
    int32_t cc;                      // color code, -1 not known yet
    int32_t have_lc;                 // the fields below are from the call's link control
    int32_t group;                   // 1 a talkgroup call, 0 a call to one radio
    int32_t dst, src;
    int32_t emergency, encrypted;
    int32_t late;                    // the call's start was not heard
    int32_t calls;                   // voice calls seen on this slot since the start
    char alias[32];                  // the talker alias, as far as it has arrived
} dmrrx_slot_status;

typedef struct {
    int32_t bursts;                  // bursts read without doubt since the start
    int32_t source;                  // SRC_*: a repeater, a mobile, or mobiles in two-slot direct mode
    int32_t play;                    // index (0 or 1) of the slot whose voice frames are handed out, -1 none
    dmrrx_slot_status s[2];
} dmrrx_info;

typedef struct {
    dmrrx_slot_status st;
    int8_t voice_idx;                // the last voice burst's place in the superframe (0 = A), -1 none yet
    uint8_t fails;                   // voice bursts in a row that could not be read
    uint16_t call_bursts;
    uint32_t last;                   // slot counter at the last good burst
    uint32_t pi_at;                  // ... at a privacy header, 0 none
    dmr_emb_rx_t emb;
    dmr_ta_t ta;
} slot_t;

typedef struct {
    air_softmodem_t sm;
    air_framer_t fr;
    uint8_t hard[(SLOT_SYMS + 3) / 4];
    int8_t soft[SLOT_SYMS];
    uint64_t samples;
    uint32_t rate;
    float scale;                     // discriminator units to modem counts
    uint32_t slots;                  // slots since the framer locked; its lowest bit says which of the two
    int32_t bursts, source, play;
    slot_t slot[2];
    uint8_t voice[VOICE_QUEUE][VOICE_ITEM];
    uint32_t v_head, v_count;
} dmrrx;

static uint32_t bits_get(const uint8_t *b, int at, int n)
{
    uint32_t v = 0;
    for (; n > 0; n--, at++) v = (v << 1) | ((b[at >> 3] >> (7 - (at & 7))) & 1u);
    return v;
}

static void voice_push(dmrrx *r, const uint8_t *bits, uint8_t errs)
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

static void call_end(dmrrx *r, int i, int state)
{
    slot_t *s = &r->slot[i];
    s->st.state = state;
    s->voice_idx = -1;
    if (r->play == i) {
        voice_push(r, NULL, ERRS_END);
        r->play = -1;
    }
}

static void call_begin(dmrrx *r, int i, int late)
{
    slot_t *s = &r->slot[i];
    if (s->st.state == ST_VOICE) call_end(r, i, ST_VOICE);
    s->st.state = ST_VOICE;
    s->st.have_lc = s->st.group = s->st.dst = s->st.src = s->st.emergency = 0;
    // A privacy header comes between the voice header and the speech; one just heard is this call's
    s->st.encrypted = s->pi_at && r->slots - s->pi_at < 8;
    s->st.late = late;
    s->st.calls++;
    s->st.alias[0] = 0;
    s->voice_idx = -1;
    s->fails = 0;
    s->call_bursts = 0;
    dmr_emb_rx_reset(&s->emb);
    dmr_ta_reset(&s->ta);
}

static void lc_set(dmrrx *r, int i, const uint8_t lcb[DMR_LC_BYTES])
{
    slot_t *s = &r->slot[i];
    dmr_lc_t lc;
    dmr_lc_unpack(lcb, &lc);
    if (lc.flco == DMR_FLCO_GROUP_VOICE || lc.flco == DMR_FLCO_UNIT_VOICE) {
        s->st.have_lc = 1;
        s->st.group = lc.flco == DMR_FLCO_GROUP_VOICE;
        s->st.dst = (int32_t)lc.dst;
        s->st.src = (int32_t)lc.src;
        s->st.emergency = (lc.svc & DMR_SVC_EMERGENCY) != 0;
        if (lc.svc & DMR_SVC_PRIVACY) s->st.encrypted = 1;
    } else if (lc.flco >= DMR_FLCO_TA_HEADER && lc.flco <= DMR_FLCO_TA_BLOCK3) {
        if (dmr_ta_push(&s->ta, lcb)) {
            strncpy(s->st.alias, s->ta.text, sizeof s->st.alias - 1);
            s->st.alias[sizeof s->st.alias - 1] = 0;
        }
    }
    // An encrypted call gives no speech: leave the way free for the other slot
    if (s->st.encrypted && r->play == i) {
        voice_push(r, NULL, ERRS_END);
        r->play = -1;
    }
}

// Speech goes out for one slot at a time: the first with a call that is not known to be encrypted.
// A call entered late stays silent until its link control has said whether it is encrypted, or
// for LATE_HOLD_BURSTS when that never comes through.
static int plays(dmrrx *r, int i)
{
    slot_t *s = &r->slot[i];
    if (s->st.encrypted) return 0;
    if (r->play < 0) r->play = i;
    if (r->play != i) return 0;
    return !s->st.late || s->st.have_lc || s->call_bursts > LATE_HOLD_BURSTS;
}

static void voice_burst(dmrrx *r, int i, const uint8_t *burst)
{
    slot_t *s = &r->slot[i];
    uint8_t frames[3][AMBE_FRAME_BYTES], bits[AMBE_PARAM_BITS];
    int k;
    s->call_bursts++;
    if (!plays(r, i)) return;
    if (!burst) {
        for (k = 0; k < 3; k++) voice_push(r, NULL, ERRS_LOST);
        return;
    }
    ambeFullBurstSplit(burst, frames);
    for (k = 0; k < 3; k++) {
        ambe_fec_result_t res;
        ambeFecDecode(frames[k], bits, &res);
        voice_push(r, bits, res.errors);
    }
}

static void label(dmrrx *r, int i, int slot)
{
    r->slot[i].st.slot = slot;
    r->slot[i ^ 1].st.slot = 3 - slot;
}

// A burst that was read without doubt: count it, and take the slot number from what goes with it
static void good(dmrrx *r, int i, const air_frame_t *f, int sync)
{
    slot_t *s = &r->slot[i];
    r->bursts++;
    s->last = r->slots;
    if (sync && (f->sync_id & 6) == SY_TS1) label(r, i, 1);
    else if (sync && (f->sync_id & 6) == SY_TS2) label(r, i, 2);
    else if (r->source == SRC_BS) {
        // A repeater says in the CACH ahead of each burst which slot follows
        uint8_t tact, at, tc, lcss;
        uint32_t payload;
        dmrCachDeinterleave(bits_get(f->hard, 0, 24), &tact, &payload);
        if (dmrCachTactDecode(tact, &at, &tc, &lcss) == 0) label(r, i, tc ? 2 : 1);
    }
}

static void data_burst(dmrrx *r, int i, const air_frame_t *f, const uint8_t *burst)
{
    slot_t *s = &r->slot[i];
    uint8_t cc, dt, payload[DMR_FEC_BPTC_PAYLOAD_BYTES], lc[DMR_LC_BYTES];
    if (!dmrFecSlotTypeDecode(burst, &cc, &dt)) return;
    good(r, i, f, 1);
    s->st.cc = cc;
    switch (dt) {
    case DT_VOICE_HEADER:
        if (dmrFecBptcDecode(burst, payload) < 0 || dmr_lc_decode_full(payload, DMR_RS_MASK_VOICE_HEADER, lc) < 0) break;
        // Repeaters send the header more than once: only the first starts the call
        if (s->st.state != ST_VOICE || s->st.late || s->voice_idx >= 0) call_begin(r, i, 0);
        lc_set(r, i, lc);
        break;
    case DT_TERMINATOR:
        if (s->st.state == ST_VOICE && !s->st.have_lc && dmrFecBptcDecode(burst, payload) >= 0
            && dmr_lc_decode_full(payload, DMR_RS_MASK_TERMINATOR, lc) >= 0)
            lc_set(r, i, lc);
        call_end(r, i, ST_IDLE);
        break;
    case DT_PI:
        s->pi_at = r->slots;
        if (s->st.state == ST_VOICE) {
            s->st.encrypted = 1;
            if (r->play == i) call_end(r, i, ST_VOICE);
        }
        break;
    case DT_IDLE:
        call_end(r, i, ST_IDLE);
        break;
    case DT_CSBK:
        call_end(r, i, ST_CONTROL);
        break;
    default:
        call_end(r, i, ST_DATA);
        break;
    }
}

// One 30 ms slot from the framer, wherever it found or counted it
static void on_frame(const air_frame_t *f, void *ctx)
{
    dmrrx *r = (dmrrx *)ctx;
    int i = (int)(++r->slots & 1u);
    slot_t *s = &r->slot[i];
    const uint8_t *burst = f->hard + BURST_AT / 4;
    int sync = !(f->flags & AIR_FRAME_NO_SYNC);

    if (f->nsyms != SLOT_SYMS || f->offset) return;
    if (sync) {
        r->source = (f->sync_id & 6) == SY_BS ? SRC_BS : (f->sync_id & 6) == SY_MS ? SRC_MS : SRC_DIRECT;
        if (f->sync_id & 1) {
            data_burst(r, i, f, burst);
            return;
        }
        // Voice burst A. Without a header before it (or with one from long ago) the call's start was missed
        if (s->st.state != ST_VOICE || r->slots - s->last > STALE_SLOTS) {
            call_begin(r, i, 1);
            s->st.cc = -1;
        }
        good(r, i, f, 1);
        s->voice_idx = 0;
        s->fails = 0;
        voice_burst(r, i, burst);
        return;
    }
    if (s->st.state != ST_VOICE || s->voice_idx < 0) return;

    // Voice bursts B to F have the EMB and a piece of embedded signaling where A has its sync. A burst that
    // cannot be read still takes its place in the superframe; two in a row and the call is over
    s->voice_idx = (int8_t)((s->voice_idx + 1) % 6);
    if (s->voice_idx != 0) {
        uint8_t cc, lcss, frag[4], lc[DMR_LC_BYTES];
        bool pi;
        uint16_t emb = (uint16_t)((bits_get(burst, 108, 8) << 8) | bits_get(burst, 148, 8));
        if (dmrFecEmbDecode(emb, &cc, &pi, &lcss) >= 0 && (s->st.cc < 0 || cc == s->st.cc)) {
            uint32_t e = bits_get(burst, 116, 32);
            s->st.cc = cc;
            s->fails = 0;
            good(r, i, f, 0);
            frag[0] = (uint8_t)(e >> 24);
            frag[1] = (uint8_t)(e >> 16);
            frag[2] = (uint8_t)(e >> 8);
            frag[3] = (uint8_t)e;
            if (dmr_emb_rx_push(&s->emb, frag, lcss, lc)) lc_set(r, i, lc);
            voice_burst(r, i, burst);
            return;
        }
    }
    if (++s->fails >= 2) call_end(r, i, ST_IDLE);
    else voice_burst(r, i, NULL);
}

static void on_lost(void *ctx)
{
    dmrrx *r = (dmrrx *)ctx;
    int i;
    for (i = 0; i < 2; i++) {
        call_end(r, i, ST_IDLE);
        r->slot[i].st.slot = 0;
        r->slot[i].pi_at = 0;
    }
    r->slots = 0;
}

#define DMR_SYNC(n, a, b, c, d, e, g) { .id = n, .nsyms = 24, .max_errs = 3, .frame_at = -SYNC_AT, .syms = { a, b, c, d, e, g } }

// TS 102 361-1 clause 10 (4FSK, 4800 symbols/s, 1944 Hz outer deviation) and table 9.2 (the sync patterns).
// A repeater sends without a break, slot after slot; mobiles send every other slot, so the modem is told to keep
// its timing across the gaps: in a voice call only one burst in six of a slot carries a sync.
static const air_mode_t dmr_air_mode = {
    .name = "DMR",
    .profile = {
        .sym_rate = 4800,
        .levels = 4,
        .shape = AIR_SHAPE_RRC,
        .shape_param = 20,
        .dev_outer_hz = 1944,
        .timing = AIR_TIMING_CONTINUOUS,
        .bw = RADIO_BW_12K5,
        .sync_every_syms = 12 * SLOT_SYMS,
        .idle = 0x5F,
        .nsyncs = 8,
        .sync = {
            DMR_SYNC(SY_BS, 0x75, 0x5F, 0xD7, 0xDF, 0x75, 0xF7),
            DMR_SYNC(SY_BS | 1, 0xDF, 0xF5, 0x7D, 0x75, 0xDF, 0x5D),
            DMR_SYNC(SY_MS, 0x7F, 0x7D, 0xD5, 0xDD, 0x57, 0xDF),
            DMR_SYNC(SY_MS | 1, 0xD5, 0xD7, 0xF7, 0x7F, 0xD7, 0x57),
            DMR_SYNC(SY_TS1, 0x5D, 0x57, 0x7F, 0x77, 0x57, 0xFF),
            DMR_SYNC(SY_TS1 | 1, 0xF7, 0xFD, 0xD5, 0xDD, 0xFD, 0x55),
            DMR_SYNC(SY_TS2, 0x7D, 0xFF, 0xD5, 0xF5, 0x5D, 0x5F),
            DMR_SYNC(SY_TS2 | 1, 0xD7, 0x55, 0x7F, 0x5F, 0xF7, 0xF5),
        },
    },
    .frame_syms = SLOT_SYMS,
    .head_syms = 0,
    .lost_frames = 14,               // a slot's voice superframe and a little: 12 slots between its syncs
    .head = NULL,
    .rx_frame = on_frame,
    .rx_lost = on_lost,
};

static void on_chunk(const air_chunk_t *c, void *ctx) { air_framer_rx(&((dmrrx *)ctx)->fr, c); }

static void slots_clear(dmrrx *r)
{
    int i;
    memset(r->slot, 0, sizeof r->slot);
    for (i = 0; i < 2; i++) {
        r->slot[i].st.cc = -1;
        r->slot[i].voice_idx = -1;
    }
    r->slots = 0;
    r->bursts = 0;
    r->source = 0;
    r->play = -1;
    r->v_head = r->v_count = 0;
}

// rate: of the samples fed, 8000 to 48000 Hz (24000 is the modem's own rate and the best).
// hz_per_unit: the deviation that reads 1.0 in the samples. NULL if the modem refuses the rate.
SS_API dmrrx *dmrrx_new(int rate, float hz_per_unit)
{
    dmrrx *r = (dmrrx *)calloc(1, sizeof(dmrrx));
    if (!r) return NULL;
    slots_clear(r);
    air_framer_init(&r->fr, &dmr_air_mode, r, r->hard, r->soft, SLOT_SYMS);
    r->rate = (uint32_t)rate;
    r->scale = hz_per_unit / 1000.0f * COUNTS_PER_KHZ;
    if (!air_softmodem_rx_open(&r->sm, &dmr_air_mode.profile, r->rate, COUNTS_PER_KHZ, on_chunk, r)) {
        free(r);
        return NULL;
    }
    return r;
}

SS_API void dmrrx_free(dmrrx *r) { free(r); }

// Discriminator samples, positive above the carrier, unfiltered below 3 kHz (no de-emphasis, no
// high-pass: the four levels need the response down to a few tens of Hz).
SS_API void dmrrx_feed(dmrrx *r, const float *disc, int n)
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

// The signal has gone (squelch closed, channel changed): forget everything and start afresh.
SS_API void dmrrx_signal_lost(dmrrx *r)
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
    slots_clear(r);
}

// True while the framer is following DMR slots.
SS_API int dmrrx_locked(dmrrx *r) { return air_framer_locked(&r->fr) ? 1 : 0; }

SS_API void dmrrx_status(dmrrx *r, dmrrx_info *out)
{
    int i;
    out->bursts = r->bursts;
    out->source = r->source;
    out->play = r->play;
    for (i = 0; i < 2; i++) {
        out->s[i] = r->slot[i].st;
        if (out->s[i].state != ST_IDLE && r->slots - r->slot[i].last > STALE_SLOTS) out->s[i].state = ST_IDLE;
    }
}

// Voice frames of the slot being played, in order: VOICE_ITEM bytes each, the 49 AMBE+2 parameter
// bits one per byte and then the frame's bit errors (ERRS_LOST for a frame that did not arrive;
// ERRS_END marks the end of a call and is not a frame). Returns the items copied.
SS_API int dmrrx_read_voice(dmrrx *r, uint8_t *out, int max)
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
