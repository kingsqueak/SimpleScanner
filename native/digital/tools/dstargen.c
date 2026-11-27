// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// Makes D-STAR test signals for SimpleScanner's tests: a voice transmission as FM discriminator
// samples (24 kHz, signed 16-bit, 6553.6 counts per kHz). Written from the D-STAR specification,
// the transmit side of what dstarrx.c receives.
//
//   dstargen OUT.raw [--my CALL] [--suffix TEXT] [--your CALL] [--rpt1 CALL] [--rpt2 CALL]
//            [--text MESSAGE] [--frames N] [--late] [--invert] [--emergency] [--bits OUT.bits]
//
// The speech is D-STAR's own silence frame, repeated: there is no open AMBE encoder to make
// anything else. --late leaves out the bit sync, frame sync and header, as if the receiver came
// to the transmission after its start. --invert turns the deviation over. --bits also writes the
// bits sent, one per byte.
//
// Build: cc -O2 -o dstargen dstargen.c -lm
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SPS 5
#define DEV_HZ 1200.0
#define COUNTS_PER_HZ 6.5536

static uint8_t *bits;
static size_t nbits, capbits;

static void put(int b)
{
    if (nbits == capbits) bits = realloc(bits, capbits = capbits ? 2 * capbits : 8192);
    bits[nbits++] = (uint8_t)(b & 1);
}

static void put_msb(uint32_t v, int n) { while (n-- > 0) put((int)(v >> n)); }
static void put_octet(uint8_t v) { for (int k = 0; k < 8; k++) put(v >> k); }      // least significant bit first

static uint16_t crc16(const uint8_t *p, int n)
{
    uint16_t crc = 0xFFFF;
    for (int i = 0; i < n; i++) {
        crc ^= p[i];
        for (int k = 0; k < 8; k++) crc = (uint16_t)((crc & 1) ? (crc >> 1) ^ 0x8408 : crc >> 1);
    }
    return (uint16_t)~crc;
}

static void call(uint8_t *dst, const char *s, int n)
{
    memset(dst, ' ', (size_t)n);
    memcpy(dst, s, strlen(s) > (size_t)n ? (size_t)n : strlen(s));
}

int main(int argc, char **argv)
{
    const char *my = "N0CALL", *suffix = "", *your = "CQCQCQ", *rpt1 = "DIRECT", *rpt2 = "DIRECT", *text = "", *bits_path = NULL;
    int frames = 105, late = 0, invert = 0, emergency = 0;
    uint8_t h[41] = { 0 }, stream[64][6];
    int nblocks = 0;

    if (argc < 2) return fprintf(stderr, "dstargen OUT.raw [options]\n"), 2;
    for (int i = 2; i < argc; i++) {
        if (!strcmp(argv[i], "--late")) late = 1;
        else if (!strcmp(argv[i], "--invert")) invert = 1;
        else if (!strcmp(argv[i], "--emergency")) emergency = 1;
        else if (i + 1 >= argc) return fprintf(stderr, "dstargen: %s?\n", argv[i]), 2;
        else if (!strcmp(argv[i], "--my")) my = argv[++i];
        else if (!strcmp(argv[i], "--suffix")) suffix = argv[++i];
        else if (!strcmp(argv[i], "--your")) your = argv[++i];
        else if (!strcmp(argv[i], "--rpt1")) rpt1 = argv[++i];
        else if (!strcmp(argv[i], "--rpt2")) rpt2 = argv[++i];
        else if (!strcmp(argv[i], "--text")) text = argv[++i];
        else if (!strcmp(argv[i], "--frames")) frames = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--bits")) bits_path = argv[++i];
        else return fprintf(stderr, "dstargen: %s?\n", argv[i]), 2;
    }

    // The header: flags, RPT2, RPT1, YOUR, MY, suffix, CRC (low octet first)
    h[0] = (uint8_t)((strcmp(rpt1, "DIRECT") ? 0x40 : 0) | (emergency ? 0x08 : 0));
    call(h + 3, rpt2, 8);
    call(h + 11, rpt1, 8);
    call(h + 19, your, 8);
    call(h + 27, my, 8);
    call(h + 35, suffix, 4);
    h[39] = (uint8_t)crc16(h, 39);
    h[40] = (uint8_t)(crc16(h, 39) >> 8);

    for (int i = 0; i < 100; i++) put(i);                // a transmitter keying up: nothing a receiver frames
    if (!late) {
        uint8_t in[330] = { 0 }, c[660], sr = 0x7F;
        int s0 = 0, s1 = 0, k = 0;
        for (int i = 0; i < 64; i++) put(~i);            // bit sync: 1010...
        put_msb(0x7650, 15);                             // frame sync: 111 0110 0101 0000
        for (int i = 0; i < 328; i++) in[i] = (h[i >> 3] >> (i & 7)) & 1;
        // Rate 1/2, constraint length 3: 1 + D + D^2, then 1 + D^2
        for (int i = 0; i < 330; i++) {
            c[2 * i] = (uint8_t)(in[i] ^ s0 ^ s1);
            c[2 * i + 1] = (uint8_t)(in[i] ^ s1);
            s1 = s0;
            s0 = in[i];
        }
        // Interleaved, then scrambled (x^7 + x^4 + 1 from all ones)
        for (int i = 0; i < 660; i++) {
            uint8_t pn = (uint8_t)(((sr >> 6) ^ (sr >> 3)) & 1);
            sr = (uint8_t)(((sr << 1) | pn) & 0x7F);
            put(c[k] ^ pn);
            k += 24;
            if (k >= 672) k -= 671;
            else if (k >= 660) k -= 647;
        }
    }

    // Slow data, in turn: the message (four blocks) when there is one, the header again (nine blocks), a filler
    if (*text) {
        uint8_t t[20];
        call(t, text, 20);
        for (int n = 0; n < 4; n++) {
            stream[nblocks][0] = (uint8_t)(0x40 | n);
            memcpy(stream[nblocks++] + 1, t + 5 * n, 5);
        }
    }
    for (int at = 0; at < 41; at += 5) {
        int n = 41 - at < 5 ? 41 - at : 5;
        memset(stream[nblocks], 0x66, 6);
        stream[nblocks][0] = (uint8_t)(0x50 | n);
        memcpy(stream[nblocks++] + 1, h + at, (size_t)n);
    }
    memset(stream[nblocks++], 0x66, 6);

    {
        static const uint8_t silence[9] = { 0x9E, 0x8D, 0x32, 0x88, 0x26, 0x1A, 0x3F, 0x61, 0xE8 };
        static const uint8_t mask[3] = { 0x70, 0x4F, 0x93 };
        int block = 0;
        // A late comer's first whole frame is whichever comes next: start a few frames before a sync
        for (int f = late ? 17 : 0; f < frames + (late ? 17 : 0); f++) {
            int idx = f % 21;
            for (int i = 0; i < 9; i++) put_octet(silence[i]);
            if (idx == 0) put_msb(0xAAB468, 24);
            else {
                const uint8_t *b = stream[block % nblocks] + (idx % 2 ? 0 : 3);
                for (int i = 0; i < 3; i++) put_octet(b[i] ^ mask[i]);
                if (idx % 2 == 0) block++;
            }
        }
    }
    put_msb(0xAAAAAAAA, 32);                             // the end pattern
    put_msb(0x135E, 16);
    for (int i = 0; i < 100; i++) put(i);

    if (bits_path) {
        FILE *b = fopen(bits_path, "wb");
        if (!b) return perror(bits_path), 1;
        fwrite(bits, 1, nbits, b);
        fclose(b);
    }
    // GMSK: the bits as +-1200 Hz, five samples each, through a Gaussian filter of BT 0.5
    {
        enum { HALF = 10 };
        double g[2 * HALF + 1], sum = 0, sigma = sqrt(log(2.0)) / (2 * M_PI * 0.5) * SPS;
        size_t n = nbits * SPS;
        int16_t *out = malloc(n * sizeof *out);
        FILE *f;
        for (int i = -HALF; i <= HALF; i++) sum += g[i + HALF] = exp(-0.5 * i * i / (sigma * sigma));
        for (size_t i = 0; i < n; i++) {
            double v = 0;
            for (int k = -HALF; k <= HALF; k++) {
                long j = (long)i + k;
                if (j < 0) j = 0;
                if ((size_t)j >= n) j = (long)n - 1;
                v += (bits[j / SPS] ? 1.0 : -1.0) * g[k + HALF];
            }
            out[i] = (int16_t)lrint((invert ? -v : v) / sum * DEV_HZ * COUNTS_PER_HZ);
        }
        if (!(f = fopen(argv[1], "wb"))) return perror(argv[1]), 1;
        fwrite(out, sizeof *out, n, f);
        fclose(f);
        fprintf(stderr, "%s: %d voice frames, %zu bits, %.2f s\n", argv[1], frames, nbits, n / 24000.0);
    }
    return 0;
}
