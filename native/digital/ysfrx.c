// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's receiver for Yaesu System Fusion ("C4FM", the amateur radio digital voice mode):
// FM discriminator samples in; who is talking to whom and the voice frames out.
//
// RadioFoundry has no System Fusion, so only its soft modem, its error correction codes and its
// IMBE decoder are used here (MIT, see radiofoundry/). Everything above the symbols is in this
// file, written from Yaesu's published air interface ("Amateur Radio Digital Standards", version
// 1.0; the frame layout below):
//
//   a frame           100 ms, 480 symbols of two bits: a 40-bit sync, the frame information
//                     (FICH, 200 bits) and 720 bits of payload. Every frame starts with the
//                     sync, so a transmission can be joined at any frame
//   the FICH          32 bits and a CRC, as four Golay(24,12) words through a rate 1/2
//                     convolutional code and an interleaver: what kind of frame this is (header,
//                     traffic, the end), its number in the cycle and what the payload carries
//   header and end    two data channels of 360 bits: the callsigns of who is called and who
//                     transmits, then of the repeater's two sides
//   traffic, "DN"     (voice and data, type 2: the usual mode) five times 40 bits of data and
//                     104 of voice. The data channel takes the callsigns round, one field a frame
//   traffic, type 1   five times 72 bits of data and 72 of voice
//   traffic, "VW"     (voice full rate) five voice frames of 144 bits, of another codec
//
// In DN and type 1 the speech is not decoded here: the caller gets the 49 parameter bits of each
// 20 ms AMBE+2 frame, in the form dmrrx.c gives them, for a vocoder (native/ambe). Full-rate
// voice is the codec and the code word P25 Phase 1 uses (IMBE, 88 bits in 144): that is decoded
// here with RadioFoundry's decoder, as p25rx.c does, and read as speech.
//
// Which way a discriminator's output goes for a positive deviation differs between receivers,
// so both senses are looked for and the one found is kept.
//
// One instance per channel listened to. Not thread-safe: call everything for an instance from
// one thread.
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include "radiofoundry/core/air/air_softmodem.h"
#include "radiofoundry/core/air/air_syms.h"
#include "radiofoundry/core/dmr/ambe/ambeFec.h"
#include "radiofoundry/core/fec/conv.h"
#include "radiofoundry/core/fec/golay.h"
#include "radiofoundry/core/mbe/imbe.h"
#include "radiofoundry/core/p25/p25.h"
#include "radiofoundry/hal/hal_radio.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define COUNTS_PER_KHZ 4000          // discriminator scale handed to the soft modem
#define FEED_BLOCK 2048
#define FRAME_BITS 960
#define SYNC_BITS 40
#define FICH_BITS 200
#define PAYLOAD (SYNC_BITS + FICH_BITS)      // where the payload starts
#define VOICE_QUEUE 128              // frames: 2.5 s
#define VOICE_ITEM 50                // 49 parameter bits, one per byte, then the error count
#define ERRS_LOST 7                  // a frame not to be trusted: the vocoder repeats the last or mutes
#define ERRS_END 255                 // not a frame: the transmission ended, the vocoder starts afresh
#define PCM_QUEUE 24000              // samples of full-rate speech: 3 s
#define P25_VOICE_0 112              // the first voice code word of a P25 voice data unit: its first information bit

#define SYNC 0xD471C9634DULL
#define SYNC_MASK 0xFFFFFFFFFFULL
#define FLIP 0xAAAAAAAAAAULL         // the same bits with the deviation turned over: each symbol's first bit changes

// Frame information: what the frame is ...
enum { FI_HEADER, FI_TRAFFIC, FI_END, FI_TEST };
// ... and what its payload carries
enum { DT_VD1, DT_DATA, DT_VD2, DT_VOICE_FR };

// The whitening sequence, PN(9,5): a nine-bit shift register started at 1 1100 1001, fed back from its
// first and fifth stages; as it comes out, most significant bit first. It starts again for every field
static const uint8_t PN[20] = {
    0x93, 0xD7, 0x51, 0x21, 0x9C, 0x2F, 0x6C, 0xD0, 0xEF, 0x0F,
    0xF8, 0x3D, 0xF1, 0x73, 0x20, 0x94, 0xED, 0x1E, 0x7C, 0xD8
};

// Rate 1/2, constraint length 5: 1 + D^3 + D^4, then 1 + D + D^2 + D^4
static const fec_conv_code_t CODE = { 0x19, 0x17 };

// What the caller reads. The transmission's details stay after it ends.
typedef struct {
    int32_t frames;                  // frames with a good FICH since the start
    int32_t in_call;
    int32_t calls;                   // transmissions since the start
    int32_t mode;                    // DT_*: what the traffic frames carry; -1 not known yet
    int32_t have_calls;              // the destination and source below are in
    int32_t late;                    // the header frame was not heard
    int32_t group_id;                // the digital group ID (or squelch code), 0 to 127
    int32_t internet;                // the transmission came over the network, not from a radio nearby
    int32_t narrow;                  // sent at half deviation
    char dest[11], src[11];          // who is called ("ALL" for anybody) and who transmits
    char down[11], up[11];           // the repeater's callsign on its output and on its input side
} ysfrx_info;

typedef struct {
    air_softmodem_t sm;
    uint64_t samples;
    uint32_t rate;
    float scale;                     // discriminator units to modem counts
    ysfrx_info info;
    uint64_t reg;                    // the last 40 bits, the newest in bit 0
    uint8_t locked;                  // frames are being counted off from a sync
    uint8_t invert;                  // positive deviation arrives negative
    uint8_t misses;                  // frames in a row whose sync was not where it belongs
    uint8_t fich_bad;                // frames in a row whose FICH did not check
    uint8_t fi, dt, fn, ft;          // the last good FICH (fn moved on by one for each frame after it)
    uint16_t nbits;                  // bits of the frame in hand
    uint8_t bits[FRAME_BITS];
    uint8_t voice[VOICE_QUEUE][VOICE_ITEM];
    uint32_t v_head, v_count;
    // Full-rate voice: the decoder's memory and what it has made
    imbe_dec_state_t dec_state;
    mbe_params_t params;
    mbe_state_t mbe_state;
    mbe_frame_t prev, cur;
    int32_t buf[MBE_BUF_WORDS];
    imbe_mem_t dec;
    int16_t pcm[PCM_QUEUE];
    uint32_t pcm_head, pcm_count;
} ysfrx;

static int pop64(uint64_t v)
{
    int n = 0;
    for (; v; v &= v - 1) n++;
    return n;
}

static void voice_push(ysfrx *r, const uint8_t *bits, uint8_t errs)
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

// CRC-CCITT as System Fusion uses it: most significant bit first, register started at zero, the result inverted
static uint16_t crc16(const uint8_t *p, int n)
{
    uint16_t crc = 0;
    int i, k;
    for (i = 0; i < n; i++) {
        crc ^= (uint16_t)(p[i] << 8);
        for (k = 0; k < 8; k++) crc = (uint16_t)((crc & 0x8000) ? (crc << 1) ^ 0x1021 : crc << 1);
    }
    return (uint16_t)~crc;
}

// A convolutionally coded field: ndibits pairs of coded bits, interleaved in rows of cols (pair i sits
// at bit (i mod cols) x 40 + (i div cols) x 2 of the field). in: the field's bits, one per byte. out: the
// decoded bits packed, most significant first (ndibits of them, the last four the tail)
static void field_decode(const uint8_t *in, int ndibits, int cols, uint8_t *out)
{
    uint8_t coded[45];
    int8_t soft[360];
    uint16_t dec[180];
    int i;
    memset(coded, 0, sizeof coded);
    for (i = 0; i < ndibits; i++) {
        int n = (i % cols) * 40 + (i / cols) * 2;
        air_bit_put(coded, (uint16_t)(2 * i), in[n]);
        air_bit_put(coded, (uint16_t)(2 * i + 1), in[n + 1]);
    }
    fec_conv_soft_from_bits(coded, (uint16_t)(2 * ndibits), soft);
    fec_conv_decode(&CODE, soft, (uint16_t)ndibits, dec, out);
}

// The frame information: four octets, or 0 when it does not check
static int fich_decode(const uint8_t *in, uint8_t fich[4])
{
    uint8_t out[13], b[6];
    uint16_t w[4];
    int i, k;
    field_decode(in, 100, 5, out);
    for (i = 0; i < 4; i++) {
        uint32_t cw = 0;
        for (k = 0; k < 24; k++) cw = (cw << 1) | air_bit_get(out, (uint16_t)(24 * i + k));
        if (fec_golay24_decode(cw, &w[i]) < 0) return 0;
    }
    b[0] = (uint8_t)(w[0] >> 4);
    b[1] = (uint8_t)((w[0] << 4) | (w[1] >> 8));
    b[2] = (uint8_t)w[1];
    b[3] = (uint8_t)(w[2] >> 4);
    b[4] = (uint8_t)((w[2] << 4) | (w[3] >> 8));
    b[5] = (uint8_t)w[3];
    if (crc16(b, 4) != (uint16_t)(b[4] << 8 | b[5])) return 0;
    memcpy(fich, b, 4);
    return 1;
}

// A data channel: nbytes octets of data and a CRC, whitened. in: its 360 or 200 bits. 0 when the CRC fails
static int dch_decode(const uint8_t *in, int nbytes, uint8_t *data)
{
    uint8_t out[23];
    int i;
    field_decode(in, nbytes == 20 ? 180 : 100, nbytes == 20 ? 9 : 5, out);
    if (crc16(out, nbytes) != (uint16_t)(out[nbytes] << 8 | out[nbytes + 1])) return 0;
    for (i = 0; i < nbytes; i++) data[i] = out[i] ^ PN[i];
    return 1;
}

// A callsign field: ten characters. One of all asterisks is a call to anybody
static void callsign(char *dst, const uint8_t *src)
{
    int i, n = 10;
    for (i = 0; i < 10; i++) dst[i] = (char)(src[i] >= 0x20 && src[i] < 0x7F ? src[i] : ' ');
    while (n > 0 && dst[n - 1] == ' ') n--;
    dst[n] = 0;
    if (n > 0 && strspn(dst, "*") == (size_t)n) strcpy(dst, "ALL");
}

static void call_end(ysfrx *r)
{
    if (r->info.in_call) {
        voice_push(r, NULL, ERRS_END);
        imbe_dec_init(&r->dec);
    }
    r->info.in_call = 0;
}

static void call_begin(ysfrx *r, int late)
{
    ysfrx_info *i = &r->info;
    i->in_call = 1;
    i->calls++;
    i->late = late;
    i->mode = -1;
    i->have_calls = 0;
    i->dest[0] = i->src[0] = i->down[0] = i->up[0] = 0;
}

// The two data channels of a header or end frame, in pieces of 72 bits by turns: who is called and who
// transmits, then the repeater's two callsigns
static void header_take(ysfrx *r)
{
    uint8_t dch[2][360], d[20];
    int b, k;
    for (b = 0; b < 5; b++)
        for (k = 0; k < 2; k++) memcpy(dch[k] + 72 * b, r->bits + PAYLOAD + 144 * b + 72 * k, 72);
    if (dch_decode(dch[0], 20, d)) {
        callsign(r->info.dest, d);
        callsign(r->info.src, d + 10);
        r->info.have_calls = 1;
    }
    if (dch_decode(dch[1], 20, d)) {
        callsign(r->info.down, d);
        callsign(r->info.up, d + 10);
    }
}

// Voice and data, type 2 (DN). Voice: 104 bits, sent in four rows of 26 read column by column, whitened; the
// first 81 are the codec's 27 most important bits three times each, then its other 22 and a spare
static void vd2_take(ysfrx *r)
{
    uint8_t dch[200], d[10], v[104], bits[AMBE_PARAM_BITS];
    int b, i;
    for (b = 0; b < 5; b++) {
        const uint8_t *vch = r->bits + PAYLOAD + 144 * b + 40;
        int errs = 0;
        memcpy(dch + 40 * b, r->bits + PAYLOAD + 144 * b, 40);
        for (i = 0; i < 104; i++) v[i] = vch[(i % 26) * 4 + i / 26] ^ ((PN[i >> 3] >> (7 - (i & 7))) & 1);
        for (i = 0; i < 27; i++) {
            int sum = v[3 * i] + v[3 * i + 1] + v[3 * i + 2];
            bits[i] = (uint8_t)(sum >= 2);
            errs += sum == 1 || sum == 2;
        }
        memcpy(bits + 27, v + 81, 22);
        // The vocoder's error count runs to 6; a frame with a quarter of its protected bits in doubt is as good as lost
        voice_push(r, bits, (uint8_t)(errs > 6 ? ERRS_LOST : errs));
    }
    if (!dch_decode(dch, 10, d)) return;
    // The data channel takes the fields round by frame number
    switch (r->fn) {
    case 0: callsign(r->info.dest, d); break;
    case 1:
        callsign(r->info.src, d);
        r->info.have_calls = 1;
        break;
    case 2: callsign(r->info.down, d); break;
    case 3: callsign(r->info.up, d); break;
    }
}

// Voice and data, type 1. Voice: 72 bits, an AMBE+2 frame with the code words and interleaving DMR uses
static void vd1_take(ysfrx *r)
{
    uint8_t dch[360], d[20], frame[AMBE_FRAME_BYTES], bits[AMBE_PARAM_BITS];
    ambe_fec_result_t res;
    int b, i;
    for (b = 0; b < 5; b++) {
        const uint8_t *vch = r->bits + PAYLOAD + 144 * b + 72;
        memcpy(dch + 72 * b, r->bits + PAYLOAD + 144 * b, 72);
        memset(frame, 0, sizeof frame);
        for (i = 0; i < 72; i++) air_bit_put(frame, (uint16_t)i, vch[i]);
        ambeFecDecode(frame, bits, &res);
        voice_push(r, bits, res.errors);
    }
    if (!dch_decode(dch, 20, d)) return;
    if (r->fn == 0) {
        callsign(r->info.dest, d);
        callsign(r->info.src, d + 10);
        r->info.have_calls = 1;
    } else if (r->fn == 1) {
        callsign(r->info.down, d);
        callsign(r->info.up, d + 10);
    }
}

// Full-rate voice (VW): five voice code words of 144 bits, each the one P25 Phase 1 sends (the same codes,
// scrambling and interleaving), so RadioFoundry's P25 layer takes it from a data unit made up for it. The
// first traffic frame instead has a data channel and two voice code words, at the end
static void vw_take(ysfrx *r)
{
    int b, i, first = r->fn == 0 && r->ft == 1 ? 3 : 0;
    for (b = first; b < 5; b++) {
        uint8_t unit[40], imbe[P25_IMBE_BYTES];
        int16_t pcm[MBE_FRAME_LEN];
        int errs;
        memset(unit, 0, sizeof unit);
        for (i = 0; i < 144; i++) p25_bits_put(unit, (uint16_t)(P25_VOICE_0 + i), 1, r->bits[PAYLOAD + 144 * b + i]);
        errs = p25_ldu_voice_get(unit, 0, imbe);
        imbe_dec_frame(&r->dec, imbe, (uint8_t)errs, pcm);
        for (i = 0; i < MBE_FRAME_LEN; i++) {
            if (r->pcm_count == PCM_QUEUE) {     // full: the oldest goes
                r->pcm_head = (r->pcm_head + 1) % PCM_QUEUE;
                r->pcm_count--;
            }
            r->pcm[(r->pcm_head + r->pcm_count) % PCM_QUEUE] = pcm[i];
            r->pcm_count++;
        }
    }
}

// A whole frame is in r->bits
static void frame(ysfrx *r)
{
    uint8_t f[4];
    int good = fich_decode(r->bits + SYNC_BITS, f);

    if (good) {
        r->fich_bad = 0;
        r->fi = f[0] >> 6;
        r->fn = (f[1] >> 3) & 7;
        r->ft = f[1] & 7;
        r->dt = f[2] & 3;
        r->info.frames++;
        r->info.narrow = (f[2] >> 6) & 1;
        r->info.internet = (f[2] >> 2) & 1;
        r->info.group_id = f[3] & 0x7F;
    } else {
        // A frame or two without its information is taken to be what should follow the last good one
        if (!r->info.in_call || ++r->fich_bad > 2) {
            call_end(r);
            r->locked = 0;
            return;
        }
        if (r->fi == FI_HEADER) r->fi = FI_TRAFFIC, r->fn = 0;
        else r->fn = (uint8_t)(r->fn >= r->ft ? 0 : r->fn + 1);
    }

    if (r->fi == FI_END) {
        if (good && r->info.in_call) header_take(r);
        call_end(r);
        return;
    }
    if (r->fi == FI_TEST) return;
    if (!r->info.in_call) call_begin(r, r->fi != FI_HEADER);
    if (r->fi == FI_HEADER) {
        header_take(r);
        return;
    }
    r->info.mode = r->dt;
    if (r->dt == DT_VD2) vd2_take(r);
    else if (r->dt == DT_VD1) vd1_take(r);
    else if (r->dt == DT_VOICE_FR) vw_take(r);
}

static void dibit_in(ysfrx *r, uint8_t dibit)
{
    uint64_t reg;
    int at_sync;

    dibit ^= (uint8_t)(r->invert << 1);
    reg = r->reg = ((r->reg << 2) | dibit) & SYNC_MASK;

    if (!r->locked) {
        // Either sense of deviation
        if (pop64(reg ^ SYNC) > 3) {
            if (pop64(reg ^ SYNC ^ FLIP) > 3) return;
            r->invert ^= 1;
            reg = r->reg = reg ^ FLIP;
        }
        at_sync = 1;
    } else {
        // In a transmission the count of symbols says where the next sync is; one found a symbol early or
        // late (the modem has moved its timing) puts the count right
        r->bits[r->nbits++] = dibit >> 1;
        r->bits[r->nbits++] = dibit & 1;
        at_sync = r->nbits != SYNC_BITS && (r->nbits == SYNC_BITS - 2 || r->nbits == SYNC_BITS + 2) && pop64(reg ^ SYNC) <= 1;
    }
    if (at_sync) {
        int i;
        for (i = 0; i < SYNC_BITS; i++) r->bits[i] = (uint8_t)((reg >> (SYNC_BITS - 1 - i)) & 1);
        r->nbits = SYNC_BITS;
        r->misses = 0;
        if (!r->locked) r->fich_bad = 0;
        r->locked = 1;
        return;
    }
    if (r->nbits == SYNC_BITS && pop64(reg ^ SYNC) > 6 && ++r->misses >= 3) {
        call_end(r);
        r->locked = 0;
        return;
    }
    if (r->nbits == SYNC_BITS && pop64(reg ^ SYNC) <= 6) r->misses = 0;
    if (r->nbits < FRAME_BITS) return;
    r->nbits = 0;
    frame(r);
}

static void on_chunk(const air_chunk_t *c, void *ctx)
{
    ysfrx *r = (ysfrx *)ctx;
    uint16_t i;
    if (c->flags & AIR_CHUNK_GAP) {
        call_end(r);
        r->locked = 0;
        r->reg = 0;
    }
    for (i = 0; i < c->nsyms; i++) dibit_in(r, air_sym_get(c->hard, i));
}

static air_profile_t profile;

static void info_clear(ysfrx *r)
{
    memset(&r->info, 0, sizeof r->info);
    r->info.mode = -1;
    r->locked = 0;
    r->reg = 0;
    r->nbits = 0;
    r->v_head = r->v_count = 0;
    r->pcm_head = r->pcm_count = 0;
    imbe_dec_init(&r->dec);
}

// Filled once, whichever thread comes first: several receivers may be made at the same moment
static pthread_once_t profile_once = PTHREAD_ONCE_INIT;

static void profile_fill(void)
{
    int k, i;
    profile.levels = 4;
    profile.shape = AIR_SHAPE_RRC;
    profile.shape_param = 20;
    profile.dev_outer_hz = 2700;
    profile.timing = AIR_TIMING_CONTINUOUS;
    profile.bw = RADIO_BW_12K5;
    profile.sync_every_syms = 0;             // the sync is 20 symbols, short of the 24 a modem chip wants
    profile.sync_any_syms = FRAME_BITS / 2;
    profile.idle = 0x77;
    profile.nsyncs = 2;
    for (k = 0; k < 2; k++) {                // as sent, and with the deviation turned over
        profile.sync[k].id = (uint8_t)k;
        profile.sync[k].nsyms = SYNC_BITS / 2;
        profile.sync[k].max_errs = 2;
        for (i = 0; i < SYNC_BITS / 2; i++)
            air_sym_put(profile.sync[k].syms, (uint16_t)i, (uint8_t)(((SYNC >> (SYNC_BITS - 2 - 2 * i)) & 3) ^ (k ? 2 : 0)));
    }
    profile.sym_rate = 4800;
}

// rate: of the samples fed, 8000 to 48000 Hz (24000 is the modem's own rate and the best).
// hz_per_unit: the deviation that reads 1.0 in the samples. NULL if the modem refuses the rate.
SS_API ysfrx *ysfrx_new(int rate, float hz_per_unit)
{
    ysfrx *r = (ysfrx *)calloc(1, sizeof(ysfrx));
    if (!r) return NULL;
    pthread_once(&profile_once, profile_fill);
    r->dec.s = &r->dec_state;
    r->dec.p = &r->params;
    r->dec.mbe.s = &r->mbe_state;
    r->dec.mbe.prev = &r->prev;
    r->dec.mbe.cur = &r->cur;
    r->dec.mbe.buf = r->buf;
    info_clear(r);
    r->rate = (uint32_t)rate;
    r->scale = hz_per_unit / 1000.0f * COUNTS_PER_KHZ;
    if (!air_softmodem_rx_open(&r->sm, &profile, r->rate, COUNTS_PER_KHZ, on_chunk, r)) {
        free(r);
        return NULL;
    }
    return r;
}

SS_API void ysfrx_free(ysfrx *r) { free(r); }

// Discriminator samples, unfiltered below 3 kHz (no de-emphasis, no high-pass: the four levels
// need the response down to a few tens of Hz).
SS_API void ysfrx_feed(ysfrx *r, const float *disc, int n)
{
    int16_t tmp[FEED_BLOCK];
    while (n > 0) {
        int k = n < FEED_BLOCK ? n : FEED_BLOCK, i;
        for (i = 0; i < k; i++) {
            float v = disc[i] * r->scale;
            tmp[i] = (int16_t)(v > 32767.0f ? 32767 : v < -32767.0f ? -32767 : v);
        }
        air_softmodem_rx(&r->sm, tmp, (uint16_t)k, (uint32_t)(r->samples * 1000000u / r->rate));
        r->samples += (uint64_t)k;
        disc += k;
        n -= k;
    }
}

// The signal has gone (squelch closed, channel changed): forget everything and start afresh.
SS_API void ysfrx_signal_lost(ysfrx *r)
{
    air_softmodem_rx_reset(&r->sm);
    info_clear(r);
}

SS_API void ysfrx_status(ysfrx *r, ysfrx_info *out) { *out = r->info; }

// Voice frames in order: VOICE_ITEM bytes each, the 49 AMBE+2 parameter bits one per byte and
// then the frame's error count (ERRS_END marks the end of a transmission and is not a frame).
// Returns the items copied.
SS_API int ysfrx_read_voice(ysfrx *r, uint8_t *out, int max)
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

// Full-rate (VW) speech, decoded: 8 kHz signed 16-bit. Returns the samples copied.
SS_API int ysfrx_read_pcm(ysfrx *r, int16_t *out, int max)
{
    int n = 0;
    while (n < max && r->pcm_count > 0) {
        out[n++] = r->pcm[r->pcm_head];
        r->pcm_head = (r->pcm_head + 1) % PCM_QUEUE;
        r->pcm_count--;
    }
    return n;
}
