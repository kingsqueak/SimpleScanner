// SPDX-License-Identifier: GPL-3.0-or-later
// Makes System Fusion (C4FM) test signals for SimpleScanner's tests: a voice transmission as FM
// discriminator samples (24 kHz, signed 16-bit, 6553.6 counts per kHz). Written from Yaesu's
// published air interface, the transmit side of what ysfrx.c receives, with RadioFoundry's
// convolutional and Golay encoders.
//
//   ysfgen OUT.raw VOICE [--mode dn|vd1|vw] [--dest CALL] [--src CALL] [--down CALL] [--up CALL]
//          [--group N] [--frames N] [--late] [--invert] [--no-end]
//
// --mode: dn is voice and data type 2 (the usual mode), vd1 voice and data type 1, vw full-rate
// voice. VOICE for dn and vd1: 9-byte AMBE+2 frames as DMR sends them (RadioFoundry's
// tests/data/voice/*.ambe); their 49 parameter bits are what goes out. For vw: 11-byte IMBE
// frames (the *.imbe files there). --frames: traffic frames of 100 ms, five voice frames each
// (in vw the first has two, after its data channel).
// --late leaves the header frame out, as if the receiver came after the start. --invert turns
// the deviation over. --no-end leaves the end frame out: the transmitter just drops.
//
// Build: cc -O2 -I../radiofoundry/core -DCONFIG_P25=1 -o ysfgen ysfgen.c ../radiofoundry/core/fec/conv.c \
//           ../radiofoundry/core/fec/golay.c ../radiofoundry/core/fec/hamming.c ../radiofoundry/core/fec/bch63.c \
//           ../radiofoundry/core/fec/gf64.c ../radiofoundry/core/fec/rs63.c ../radiofoundry/core/fec/trellis.c \
//           ../radiofoundry/core/p25/p25_unit.c ../radiofoundry/core/dmr/ambe/ambeFec.c -lm
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "dmr/ambe/ambeFec.h"
#include "fec/conv.h"
#include "fec/golay.h"
#include "p25/p25.h"

#define SPS 5
#define SPAN 8                       // root raised cosine: symbols each side
#define INNER_HZ 900.0               // deviation of the inner symbols; the outer ones are three times it
#define COUNTS_PER_HZ 6.5536
#define FRAME_BITS 960
#define PAYLOAD 240

enum { FI_HEADER, FI_TRAFFIC, FI_END };
enum { DT_VD1, DT_DATA, DT_VD2, DT_VOICE_FR };

static const uint8_t PN[20] = {
    0x93, 0xD7, 0x51, 0x21, 0x9C, 0x2F, 0x6C, 0xD0, 0xEF, 0x0F,
    0xF8, 0x3D, 0xF1, 0x73, 0x20, 0x94, 0xED, 0x1E, 0x7C, 0xD8
};
static const fec_conv_code_t CODE = { 0x19, 0x17 };

static float *levels;                // one per symbol: +-3, +-1, or 0 with the transmitter off
static size_t nlevels, caplevels;

static void put_level(float v)
{
    if (nlevels == caplevels) levels = realloc(levels, (caplevels = caplevels ? 2 * caplevels : 4096) * sizeof *levels);
    levels[nlevels++] = v;
}

static void put_frame(const uint8_t *bits)
{
    static const float map[4] = { 1, 3, -1, -3 };       // dibits 00 01 10 11
    for (int i = 0; i < FRAME_BITS; i += 2) put_level(map[bits[i] << 1 | bits[i + 1]]);
}

static uint16_t crc16(const uint8_t *p, int n)
{
    uint16_t crc = 0;
    for (int i = 0; i < n; i++) {
        crc ^= (uint16_t)(p[i] << 8);
        for (int k = 0; k < 8; k++) crc = (uint16_t)((crc & 0x8000) ? (crc << 1) ^ 0x1021 : crc << 1);
    }
    return (uint16_t)~crc;
}

static int bit(const uint8_t *p, int i) { return (p[i >> 3] >> (7 - (i & 7))) & 1; }
static void setbit(uint8_t *p, int i, int b) { if (b) p[i >> 3] |= (uint8_t)(0x80 >> (i & 7)); }

// ndibits - 4 bits of in (packed, most significant first) and four tail bits through the convolutional
// code, the pairs of coded bits interleaved in rows of cols, to out (one bit per byte)
static void field_encode(const uint8_t *in, int ndibits, int cols, uint8_t *out)
{
    uint8_t padded[23] = { 0 }, coded[45] = { 0 };
    memcpy(padded, in, (size_t)(ndibits - 4 + 7) / 8);
    fec_conv_encode(&CODE, padded, (uint16_t)ndibits, coded);
    for (int i = 0; i < ndibits; i++) {
        int n = (i % cols) * 40 + (i / cols) * 2;
        out[n] = (uint8_t)bit(coded, 2 * i);
        out[n + 1] = (uint8_t)bit(coded, 2 * i + 1);
    }
}

// The frame information: four octets and their CRC as four Golay(24,12) words
static void fich_encode(const uint8_t f[4], uint8_t *out)
{
    uint8_t b[6], words[12] = { 0 };
    uint16_t w[4];
    memcpy(b, f, 4);
    b[4] = (uint8_t)(crc16(f, 4) >> 8);
    b[5] = (uint8_t)crc16(f, 4);
    w[0] = (uint16_t)(b[0] << 4 | b[1] >> 4);
    w[1] = (uint16_t)((b[1] & 15) << 8 | b[2]);
    w[2] = (uint16_t)(b[3] << 4 | b[4] >> 4);
    w[3] = (uint16_t)((b[4] & 15) << 8 | b[5]);
    for (int i = 0; i < 4; i++) {
        uint32_t cw = fec_golay24_encode(w[i]);
        for (int k = 0; k < 24; k++) setbit(words, 24 * i + k, (int)(cw >> (23 - k)) & 1);
    }
    field_encode(words, 100, 5, out);
}

// A data channel: nbytes octets whitened, then their CRC
static void dch_encode(const uint8_t *data, int nbytes, uint8_t *out)
{
    uint8_t b[22];
    for (int i = 0; i < nbytes; i++) b[i] = data[i] ^ PN[i];
    b[nbytes] = (uint8_t)(crc16(b, nbytes) >> 8);
    b[nbytes + 1] = (uint8_t)crc16(b, nbytes);
    field_encode(b, nbytes == 20 ? 180 : 100, nbytes == 20 ? 9 : 5, out);
}

static void call(uint8_t *dst, const char *s)
{
    memset(dst, ' ', 10);
    memcpy(dst, s, strlen(s) > 10 ? 10 : strlen(s));
}

int main(int argc, char **argv)
{
    static const uint8_t sync[5] = { 0xD4, 0x71, 0xC9, 0x63, 0x4D };
    const char *dest = "**********", *src = "N0CALL", *down = "", *up = "";
    static uint8_t voice[99000][P25_IMBE_BYTES];
    uint8_t csd1[20], csd2[20], frame[FRAME_BITS], dch[360];
    int mode = DT_VD2, frames = 21, late = 0, invert = 0, group = 0, no_end = 0, nvoice = 0, v = 0;
    FILE *f;

    if (argc < 3) return fprintf(stderr, "ysfgen OUT.raw VOICE.ambe [options]\n"), 2;
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--late")) late = 1;
        else if (!strcmp(argv[i], "--invert")) invert = 1;
        else if (!strcmp(argv[i], "--no-end")) no_end = 1;
        else if (i + 1 >= argc) return fprintf(stderr, "ysfgen: %s?\n", argv[i]), 2;
        else if (!strcmp(argv[i], "--dest")) dest = argv[++i];
        else if (!strcmp(argv[i], "--src")) src = argv[++i];
        else if (!strcmp(argv[i], "--down")) down = argv[++i];
        else if (!strcmp(argv[i], "--up")) up = argv[++i];
        else if (!strcmp(argv[i], "--group")) group = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--frames")) frames = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--mode")) {
            const char *m = argv[++i];
            mode = !strcmp(m, "vd1") ? DT_VD1 : !strcmp(m, "vw") ? DT_VOICE_FR : DT_VD2;
        } else return fprintf(stderr, "ysfgen: %s?\n", argv[i]), 2;
    }
    if (!(f = fopen(argv[2], "rb"))) return perror(argv[2]), 1;
    {
        size_t size = mode == DT_VOICE_FR ? P25_IMBE_BYTES : AMBE_FRAME_BYTES;
        while (nvoice < 99000 && fread(voice[nvoice], 1, size, f) == size) nvoice++;
    }
    fclose(f);
    if (nvoice == 0) return fprintf(stderr, "ysfgen: no voice frames in %s\n", argv[2]), 1;

    call(csd1, dest);
    call(csd1 + 10, src);
    call(csd2, down);
    call(csd2 + 10, up);

    for (int i = 0; i < 40; i++) put_level(0);                   // the transmitter off
    {
        // Keying up: symbols at random, nothing a receiver frames
        static const float map[4] = { 1, 3, -1, -3 };
        uint32_t x = 12345;
        for (int i = 0; i < 96; i++) put_level(map[(x = x * 1664525u + 1013904223u) >> 30]);
    }

    // The header, the traffic frames, the end
    for (int n = late ? 0 : -1; n <= frames; n++) {
        int fi = n < 0 ? FI_HEADER : n == frames ? FI_END : FI_TRAFFIC, fn = fi == FI_TRAFFIC ? n % 8 : 0;
        uint8_t fich[4];
        if (fi == FI_END && no_end) break;
        memset(frame, 0, sizeof frame);
        for (int i = 0; i < 40; i++) frame[i] = (uint8_t)bit(sync, i);
        fich[0] = (uint8_t)(fi << 6 | 2 << 4);                   // callsign information 2, a call to a group
        fich[1] = (uint8_t)(fn << 3 | 7);                        // the frame's number, of 0 to 7
        if (mode == DT_VOICE_FR && fi == FI_TRAFFIC) fich[1] = (uint8_t)(n == 0 ? 1 : 0);     // full rate: 0 of 0 to 1, then 0 of 0
        fich[2] = (uint8_t)mode;                                 // wide deviation, no message route, from a radio
        fich[3] = (uint8_t)(group & 0x7F);
        fich_encode(fich, frame + 40);

        if (fi != FI_TRAFFIC) {
            // Two data channels in pieces of 72 bits by turns
            uint8_t two[2][360];
            dch_encode(csd1, 20, two[0]);
            dch_encode(csd2, 20, two[1]);
            for (int b = 0; b < 5; b++)
                for (int k = 0; k < 2; k++) memcpy(frame + PAYLOAD + 144 * b + 72 * k, two[k] + 72 * b, 72);
        } else if (mode == DT_VD2) {
            uint8_t d[10];
            memset(d, ' ', 10);
            if (fn < 4) memcpy(d, fn == 0 ? csd1 : fn == 1 ? csd1 + 10 : fn == 2 ? csd2 : csd2 + 10, 10);
            dch_encode(d, 10, dch);
            for (int b = 0; b < 5; b++) {
                uint8_t p[AMBE_PARAM_BITS], w[104] = { 0 };
                memcpy(frame + PAYLOAD + 144 * b, dch + 40 * b, 40);
                ambeFecDecode(voice[v++ % nvoice], p, NULL);
                // The 27 most important bits three times each, the other 22, a spare; whitened; four rows of 26
                // sent column by column
                for (int i = 0; i < 27; i++) w[3 * i] = w[3 * i + 1] = w[3 * i + 2] = p[i];
                memcpy(w + 81, p + 27, 22);
                for (int i = 0; i < 104; i++)
                    frame[PAYLOAD + 144 * b + 40 + (i % 26) * 4 + i / 26] = (uint8_t)(w[i] ^ bit(PN, i));
            }
        } else if (mode == DT_VD1) {
            uint8_t d[20];
            memset(d, ' ', 20);
            if (fn < 2) memcpy(d, fn == 0 ? csd1 : csd2, 20);
            dch_encode(d, 20, dch);
            for (int b = 0; b < 5; b++) {
                const uint8_t *a = voice[v++ % nvoice];
                memcpy(frame + PAYLOAD + 144 * b, dch + 72 * b, 72);
                for (int i = 0; i < 72; i++) frame[PAYLOAD + 144 * b + 72 + i] = (uint8_t)bit(a, i);
            }
        } else {
            // Five voice code words, each as P25 sends it (taken from a P25 voice data unit made up for it). The first
            // frame has a data channel (callsigns 3: nothing here) and a reserved field where three of them would be
            int first = 0;
            if (n == 0) {
                uint8_t d[20];
                memset(d, ' ', 20);
                dch_encode(d, 20, dch);
                memcpy(frame + PAYLOAD, dch, 360);
                first = 3;
            }
            for (int b = first; b < 5; b++) {
                uint8_t unit[40] = { 0 };
                p25_ldu_voice_put(unit, 0, voice[v++ % nvoice]);
                for (int i = 0; i < 144; i++) frame[PAYLOAD + 144 * b + i] = (uint8_t)p25_bits_get(unit, (uint16_t)(112 + i), 1);
            }
        }
        put_frame(frame);
    }
    for (int i = 0; i < 40; i++) put_level(0);

    // 4FSK: an impulse of the symbol's level at each symbol, through a root raised cosine (roll-off 0.2)
    {
        enum { TAPS = 2 * SPAN * SPS + 1 };
        double h[TAPS], e = 0, a = 0.2;
        size_t n = nlevels * SPS;
        int16_t *out = malloc(n * sizeof *out);
        for (int i = 0; i < TAPS; i++) {
            double t = (double)(i - SPAN * SPS) / SPS;
            if (fabs(t) < 1e-9) h[i] = 1 - a + 4 * a / M_PI;
            else if (fabs(fabs(4 * a * t) - 1) < 1e-9)
                h[i] = a / sqrt(2) * ((1 + 2 / M_PI) * sin(M_PI / (4 * a)) + (1 - 2 / M_PI) * cos(M_PI / (4 * a)));
            else
                h[i] = (sin(M_PI * t * (1 - a)) + 4 * a * t * cos(M_PI * t * (1 + a))) / (M_PI * t * (1 - (4 * a * t) * (4 * a * t)));
            e += h[i] * h[i];
        }
        for (int i = 0; i < TAPS; i++) h[i] *= sqrt(SPS / e);      // steady symbols come out at their own level
        for (size_t i = 0; i < n; i++) {
            double s = 0;
            for (int k = 0; k < TAPS; k++) {
                long j = (long)i + SPAN * SPS - k;
                if (j >= 0 && j % SPS == 0 && (size_t)(j / SPS) < nlevels) s += levels[j / SPS] * h[k];
            }
            out[i] = (int16_t)lrint((invert ? -s : s) * INNER_HZ * COUNTS_PER_HZ);
        }
        if (!(f = fopen(argv[1], "wb"))) return perror(argv[1]), 1;
        fwrite(out, sizeof *out, n, f);
        fclose(f);
        fprintf(stderr, "%s: %d traffic frames, %zu symbols, %.2f s\n", argv[1], frames, nlevels, n / 24000.0);
    }
    return 0;
}
