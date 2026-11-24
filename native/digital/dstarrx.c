// SPDX-License-Identifier: GPL-3.0-or-later
// SimpleScanner's D-STAR receiver (JARL's D-STAR digital voice, "DV" mode): FM discriminator
// samples in; the callsigns of a transmission, its short text message and its voice frames out.
//
// D-STAR is GMSK at 4800 bit/s. RadioFoundry has no D-STAR, so only its soft modem is used here
// (MIT, see radiofoundry/): it gives the bits. Everything above the bits is in this file, written
// from the D-STAR specification:
//
//   a transmission    bit sync (1010...), frame sync (15 bits), the header (660 bits), then voice
//                     frames until the end pattern
//   the header        41 octets (3 flags, four callsigns of 8, a 4-character suffix, CRC), sent
//                     least significant bit first, through a rate 1/2 convolutional code
//                     (constraint length 3), an interleaver and a scrambler
//   a voice frame     96 bits, 20 ms: 72 of AMBE speech, 24 of slow data
//   every 21st frame  carries a 24-bit sync pattern where the slow data would be. A receiver that
//                     missed the header starts from one of these
//   slow data         two frames make a block of six octets: a type and five octets of a text
//                     message, of position data, or of the header again (for late comers)
//
// Speech is not decoded here. The caller gets each frame's 72 voice bits sorted into the four
// code words of the AMBE 3600x2400 codec (the mapping table is from DSD, ISC) for a vocoder
// (native/ambe).
//
// Which way the transmitter's deviation goes for a 1 differs between receivers' discriminators,
// so both senses are looked for and the one found is kept.
//
// One instance per channel listened to. Not thread-safe: call everything for an instance from
// one thread.
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include "radiofoundry/core/air/air_softmodem.h"
#include "radiofoundry/core/air/air_syms.h"
#include "radiofoundry/hal/hal_radio.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define COUNTS_PER_KHZ 4000          // discriminator scale handed to the soft modem
#define FEED_BLOCK 2048
#define FRAME_BITS 96
#define VOICE_BITS 72
#define SUPERFRAME 21                // frames from one sync pattern to the next
#define HEADER_BITS 660
#define HEADER_BYTES 41
#define VOICE_QUEUE 128              // frames: 2.5 s
#define VOICE_ITEM 97                // 96 code word bits (4 x 24), one per byte, then 0; or 255: the call ended
#define ITEM_END 255
#define HOLD_FRAMES 24               // frames kept back until a second sync confirms a late entry

#define DATA_SYNC 0xAAB468u          // 1010 1010 1011 0100 0110 1000, the first bit on air on the left
#define END_SYNC 0xAAAA135Eu         // the last 32 bits of the end pattern: 1010... then 0001 0011 0101 1110
#define HEAD_SYNC 0x55557650u        // the end of the bit sync and the frame sync: ...0101 0101 0111 0110 0101 0000

// The place of each of the 72 voice bits, in the order they are sent, in the four code words
// (row, then bit) that mbelib's AMBE 3600x2400 decoder takes. From DSD (dstar_const.h):
// Copyright (C) 2010 DSD Author, ISC license (the same terms as mbelib's, see native/ambe/mbelib/COPYRIGHT).
static const uint8_t ROW[VOICE_BITS] = {
     0,  0,  3,  2,  1,  1,  0,  0,  1,  1,  0,  0,
     3,  2,  1,  1,  3,  2,  1,  1,  0,  0,  3,  2,
     0,  0,  3,  2,  1,  1,  0,  0,  1,  1,  0,  0,
     3,  2,  1,  1,  3,  2,  1,  1,  0,  0,  3,  2,
     0,  0,  3,  2,  1,  1,  0,  0,  1,  1,  0,  0,
     3,  2,  1,  1,  3,  3,  2,  1,  0,  0,  3,  3
};
static const uint8_t COL[VOICE_BITS] = {
    10, 22, 11,  9, 10, 22, 11, 23,  8, 20,  9, 21,
    10,  8,  9, 21,  8,  6,  7, 19,  8, 20,  9,  7,
     6, 18,  7,  5,  6, 18,  7, 19,  4, 16,  5, 17,
     6,  4,  5, 17,  4,  2,  3, 15,  4, 16,  5,  3,
     2, 14,  3,  1,  2, 14,  3, 15,  0, 12,  1, 13,
     2,  0,  1, 13,  0, 12, 10, 11,  0, 12,  1, 13
};

// What the caller reads. The transmission's details stay after it ends.
typedef struct {
    int32_t frames;                  // voice frames received in step since the start
    int32_t in_call;
    int32_t have_header;             // the callsigns below are in
    int32_t late;                    // the start of the transmission was not heard
    int32_t data;                    // the header says this is a data transmission, not speech
    int32_t emergency;
    int32_t calls;                   // transmissions since the start
    char my[9], suffix[5];           // the station transmitting, and its four-character note
    char your[9];                    // who is called (CQCQCQ for anybody)
    char rpt1[9], rpt2[9];           // the repeater the station sends through, and where that passes it on
    char text[21];                   // the transmission's short message
} dstarrx_info;

enum { SEARCH, HEADER, VOICE };

typedef struct {
    air_softmodem_t sm;
    uint64_t samples;
    uint32_t rate;
    float scale;                     // discriminator units to modem counts
    dstarrx_info info;
    uint8_t state;
    uint8_t invert;                  // a 1 arrives as negative deviation
    uint8_t confirmed;               // a header with a good CRC, or two syncs in step: this is D-STAR
    uint8_t misses;                  // sync patterns in a row that were not where they belong
    uint32_t reg;                    // the last 32 bits, the newest in bit 0
    uint16_t nbits;                  // bits of the header or of the frame in hand
    uint8_t frame_idx;               // the frame in hand, 0 to 20: 0 carries the sync pattern
    uint8_t bits[HEADER_BITS];
    uint8_t slow[6];                 // a slow data block being put together
    uint8_t slow_n;
    uint8_t hdr[HEADER_BYTES];       // the header as slow data repeats it
    uint8_t hdr_n;
    uint8_t held[HOLD_FRAMES][VOICE_ITEM];
    uint8_t held_n;
    uint8_t voice[VOICE_QUEUE][VOICE_ITEM];
    uint32_t v_head, v_count;
} dstarrx;

static int pop32(uint32_t v)
{
    int n = 0;
    for (; v; v &= v - 1) n++;
    return n;
}

static void voice_push(dstarrx *r, const uint8_t *item)
{
    if (r->v_count == VOICE_QUEUE) {             // full: the oldest goes
        r->v_head = (r->v_head + 1) % VOICE_QUEUE;
        r->v_count--;
    }
    memcpy(r->voice[(r->v_head + r->v_count) % VOICE_QUEUE], item, VOICE_ITEM);
    r->v_count++;
}

// CRC-CCITT as D-STAR uses it: bits taken least significant first, register preset to ones, the result inverted
static uint16_t crc16(const uint8_t *p, int n)
{
    uint16_t crc = 0xFFFF;
    int i, k;
    for (i = 0; i < n; i++) {
        crc ^= p[i];
        for (k = 0; k < 8; k++) crc = (uint16_t)((crc & 1) ? (crc >> 1) ^ 0x8408 : crc >> 1);
    }
    return (uint16_t)~crc;
}

static void field(char *dst, const uint8_t *src, int n)
{
    int i;
    for (i = 0; i < n; i++) dst[i] = (char)(src[i] >= 0x20 && src[i] < 0x7F ? src[i] : ' ');
    dst[n] = 0;
}

// The 41 header octets, checked: flags, RPT2, RPT1, YOUR, MY, suffix, CRC (low octet first)
static int header_take(dstarrx *r, const uint8_t *h)
{
    if (crc16(h, 39) != (uint16_t)(h[39] | h[40] << 8)) return 0;
    r->info.have_header = 1;
    r->info.data = (h[0] & 0x80) != 0;
    r->info.emergency = (h[0] & 0x08) != 0;
    field(r->info.rpt2, h + 3, 8);
    field(r->info.rpt1, h + 11, 8);
    field(r->info.your, h + 19, 8);
    field(r->info.my, h + 27, 8);
    field(r->info.suffix, h + 35, 4);
    return 1;
}

// The header as received (660 bits in r->bits): take off the scrambler (x^7 + x^4 + 1, started at all ones),
// undo the interleaver, and decode the convolutional code (generators 1 + D + D^2 and 1 + D^2) with a
// Viterbi decoder. The 330 decoded bits are the 41 octets, least significant bit first, and two tail bits
static int header_decode(dstarrx *r)
{
    uint8_t path[HEADER_BITS / 2][4];              // on the stack: two decoders may run at once
    uint8_t d[HEADER_BITS], out[HEADER_BITS / 2], h[HEADER_BYTES];
    uint16_t metric[4] = { 0, 1000, 1000, 1000 }, next[4];
    uint8_t sr = 0x7F;
    int i, k = 0, s;

    for (i = 0; i < HEADER_BITS; i++) {
        uint8_t pn = (uint8_t)(((sr >> 6) ^ (sr >> 3)) & 1);
        sr = (uint8_t)(((sr << 1) | pn) & 0x7F);
        // The interleaver sends the bits of 24 columns one column after another; the first 12 columns are a bit longer
        d[k] = r->bits[i] ^ pn;
        k += 24;
        if (k >= 672) k -= 671;
        else if (k >= 660) k -= 647;
    }
    // State = the two bits before this one (the newer in bit 0). Input b from state s sends b^s1^s0 and b^s1... as
    // (g1, g2) with s1 the older bit
    for (i = 0; i < HEADER_BITS / 2; i++) {
        for (s = 0; s < 4; s++) next[s] = 0xFFFF;
        for (s = 0; s < 4; s++) {
            int b;
            for (b = 0; b < 2; b++) {
                int s0 = s & 1, s1 = s >> 1, g1 = b ^ s0 ^ s1, g2 = b ^ s1, to = ((s << 1) | b) & 3;
                uint16_t m = (uint16_t)(metric[s] + (g1 != d[2 * i]) + (g2 != d[2 * i + 1]));
                if (m < next[to]) {
                    next[to] = m;
                    path[i][to] = (uint8_t)s;
                }
            }
        }
        memcpy(metric, next, sizeof metric);
    }
    s = 0;                                       // the tail bits bring the encoder back to zero
    for (i = HEADER_BITS / 2 - 1; i >= 0; i--) {
        out[i] = (uint8_t)(s & 1);
        s = path[i][s];
    }
    memset(h, 0, sizeof h);
    for (i = 0; i < HEADER_BYTES * 8; i++) h[i >> 3] |= (uint8_t)(out[i] << (i & 7));
    return header_take(r, h);
}

static void call_end(dstarrx *r)
{
    uint8_t item[VOICE_ITEM] = { 0 };
    if (r->info.in_call && r->confirmed) {
        item[VOICE_ITEM - 1] = ITEM_END;
        voice_push(r, item);
    }
    r->info.in_call = 0;
    r->state = SEARCH;
    r->confirmed = 0;
    r->held_n = 0;
}

static void call_begin(dstarrx *r, int late)
{
    r->info.have_header = r->info.data = r->info.emergency = 0;
    r->info.my[0] = r->info.suffix[0] = r->info.your[0] = r->info.rpt1[0] = r->info.rpt2[0] = r->info.text[0] = 0;
    r->info.late = late;
    r->confirmed = 0;
    r->misses = 0;
    r->held_n = 0;
    r->slow_n = 0;
    r->hdr_n = 0;
}

// Now it is known to be D-STAR: the transmission counts, and the frames kept back go out
static void confirm(dstarrx *r)
{
    int i;
    if (r->confirmed) return;
    r->confirmed = 1;
    r->info.in_call = 1;
    r->info.calls++;
    r->info.frames += r->held_n;
    for (i = 0; i < r->held_n; i++) voice_push(r, r->held[i]);
    r->held_n = 0;
}

// A block of slow data: six octets from two frames, already unscrambled
static void slow_block(dstarrx *r, const uint8_t *b)
{
    int type = b[0] & 0xF0, n = b[0] & 0x0F, i;
    if (type == 0x40 && n < 4) {
        // The 20-character message, five characters a block
        if (!r->info.text[0]) memset(r->info.text, ' ', 20);
        for (i = 0; i < 5; i++) r->info.text[5 * n + i] = (char)(b[1 + i] >= 0x20 && b[1 + i] < 0x7F ? b[1 + i] : ' ');
        r->info.text[20] = 0;
    } else if (type == 0x50 && n >= 1 && n <= 5) {
        // The header again, five octets a block: the way a late comer learns the callsigns
        for (i = 0; i < n && r->hdr_n < HEADER_BYTES; i++) r->hdr[r->hdr_n++] = b[1 + i];
        if (r->hdr_n == HEADER_BYTES || n < 5) {
            if (r->hdr_n == HEADER_BYTES && !r->info.have_header) header_take(r, r->hdr);
            r->hdr_n = 0;
        }
        return;
    }
    r->hdr_n = 0;
}

// A whole voice frame is in r->bits: the 72 voice bits, then the 24 of slow data or sync
static void frame(dstarrx *r)
{
    uint8_t item[VOICE_ITEM] = { 0 };
    uint32_t tail = r->reg & 0xFFFFFFu;
    int i;

    if (r->frame_idx == 0) {
        if (pop32(tail ^ DATA_SYNC) <= 3) {
            r->misses = 0;
            confirm(r);                          // a late entry's second sync, in step with its first
        } else if (++r->misses >= (r->confirmed ? 3 : 1)) {
            call_end(r);
            return;
        }
        r->slow_n = 0;
    } else if (r->misses == 0) {
        // Slow data: least significant bit first, three octets a frame, scrambled with a fixed pattern
        static const uint8_t mask[3] = { 0x70, 0x4F, 0x93 };
        for (i = 0; i < 3; i++) {
            uint8_t v = 0;
            int k;
            for (k = 0; k < 8; k++) v |= (uint8_t)(r->bits[VOICE_BITS + 8 * i + k] << k);
            r->slow[r->slow_n++] = v ^ mask[i];
        }
        if (r->slow_n == 6) {
            slow_block(r, r->slow);
            r->slow_n = 0;
        }
    }
    for (i = 0; i < VOICE_BITS; i++) item[ROW[i] * 24 + COL[i]] = r->bits[i];
    if (r->confirmed) {
        r->info.frames++;
        voice_push(r, item);
    } else if (r->held_n < HOLD_FRAMES) {
        memcpy(r->held[r->held_n++], item, VOICE_ITEM);
    }
    r->frame_idx = (uint8_t)((r->frame_idx + 1) % SUPERFRAME);
}

static void bit_in(dstarrx *r, uint8_t raw)
{
    uint8_t b = raw ^ r->invert;
    r->reg = (r->reg << 1) | b;

    if (r->state == SEARCH) {
        // Either sense of deviation. reg holds the bits as the present sense reads them
        uint32_t reg = r->reg;
        int sense;
        for (sense = 0; sense < 2; sense++, reg = ~reg) {
            if (pop32(reg ^ HEAD_SYNC) <= 2) {
                call_begin(r, 0);
                r->state = HEADER;
            } else if (pop32((reg & 0xFFFFFFu) ^ DATA_SYNC) <= 1) {
                // The sync pattern closes frame 0 of a superframe
                call_begin(r, 1);
                r->state = VOICE;
                r->frame_idx = 1;
            } else continue;
            if (sense) {
                r->invert ^= 1;
                r->reg = ~r->reg;
            }
            r->nbits = 0;
            return;
        }
        return;
    }
    r->bits[r->nbits++] = b;
    if (r->state == HEADER) {
        if (r->nbits < HEADER_BITS) return;
        if (header_decode(r)) confirm(r);        // a good CRC is proof enough; a bad one waits for the first sync
        r->state = VOICE;
        r->frame_idx = 0;
        r->nbits = 0;
        return;
    }
    // The end of a transmission can fall anywhere in a frame's slow data
    if (r->confirmed && pop32(r->reg ^ END_SYNC) <= 2) {
        call_end(r);
        return;
    }
    if (r->nbits < FRAME_BITS) return;
    r->nbits = 0;
    frame(r);
}

static void on_chunk(const air_chunk_t *c, void *ctx)
{
    dstarrx *r = (dstarrx *)ctx;
    uint16_t i;
    if (c->flags & AIR_CHUNK_GAP) {
        if (r->state != SEARCH) call_end(r);
        r->reg = 0;
    }
    for (i = 0; i < c->nsyms; i++) bit_in(r, (uint8_t)(air_sym_get(c->hard, i) == 1));
}

// A pattern for the modem to set its timing by: n bits of v (the first on air highest) as symbols, 1 = positive
static air_sync_t sync_of(uint8_t id, uint32_t v, int invert)
{
    air_sync_t s;
    int i;
    memset(&s, 0, sizeof s);
    s.id = id;
    s.nsyms = 24;
    s.max_errs = 2;
    for (i = 0; i < 24; i++) air_sym_put(s.syms, (uint16_t)i, (((v >> (23 - i)) & 1) ^ (uint32_t)invert) ? 1 : 3);
    return s;
}

static air_profile_t profile;

static void info_clear(dstarrx *r)
{
    memset(&r->info, 0, sizeof r->info);
    r->state = SEARCH;
    r->confirmed = 0;
    r->held_n = 0;
    r->reg = 0;
    r->v_head = r->v_count = 0;
}

// Filled once, whichever thread comes first: several receivers may be made at the same moment
static pthread_once_t profile_once = PTHREAD_ONCE_INIT;

static void profile_fill(void)
{
    profile.levels = 2;
    profile.shape = AIR_SHAPE_GAUSS;
    profile.shape_param = 50;
    profile.dev_outer_hz = 1200;             // GMSK at 4800 bit/s: a quarter of the bit rate
    profile.timing = AIR_TIMING_CONTINUOUS;
    profile.bw = RADIO_BW_12K5;
    profile.sync_every_syms = SUPERFRAME * FRAME_BITS;
    profile.idle = 0x77;
    profile.nsyncs = 4;
    profile.sync[0] = sync_of(0, DATA_SYNC, 0);
    profile.sync[1] = sync_of(1, DATA_SYNC, 1);
    profile.sync[2] = sync_of(2, HEAD_SYNC & 0xFFFFFFu, 0);
    profile.sync[3] = sync_of(3, HEAD_SYNC & 0xFFFFFFu, 1);
    profile.sym_rate = 4800;
}

// rate: of the samples fed, 8000 to 48000 Hz (24000 is the modem's own rate and the best).
// hz_per_unit: the deviation that reads 1.0 in the samples. NULL if the modem refuses the rate.
SS_API dstarrx *dstarrx_new(int rate, float hz_per_unit)
{
    dstarrx *r = (dstarrx *)calloc(1, sizeof(dstarrx));
    if (!r) return NULL;
    pthread_once(&profile_once, profile_fill);
    info_clear(r);
    r->rate = (uint32_t)rate;
    r->scale = hz_per_unit / 1000.0f * COUNTS_PER_KHZ;
    if (!air_softmodem_rx_open(&r->sm, &profile, r->rate, COUNTS_PER_KHZ, on_chunk, r)) {
        free(r);
        return NULL;
    }
    return r;
}

SS_API void dstarrx_free(dstarrx *r) { free(r); }

// Discriminator samples, unfiltered below 3 kHz (no de-emphasis, no high-pass).
SS_API void dstarrx_feed(dstarrx *r, const float *disc, int n)
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
SS_API void dstarrx_signal_lost(dstarrx *r)
{
    air_softmodem_rx_reset(&r->sm);
    info_clear(r);
}

SS_API void dstarrx_status(dstarrx *r, dstarrx_info *out) { *out = r->info; }

// Voice frames in order: VOICE_ITEM bytes each, the 96 bits of the four AMBE code words (4 rows
// of 24, one bit per byte) and then 0; a last byte of ITEM_END marks the end of a transmission
// and is not a frame. Returns the items copied.
SS_API int dstarrx_read_voice(dstarrx *r, uint8_t *out, int max)
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
