// SPDX-License-Identifier: GPL-3.0-or-later
// Makes DMR test signals for SimpleScanner's tests: a voice call as FM discriminator samples
// (24 kHz, signed 16-bit, 6553.6 counts per kHz), built with RadioFoundry's encoders.
//
//   dmrgen bs|ms|direct OUT.raw VOICE.ambe [--slot 1|2] [--cc N] [--dst N] [--src N] [--private]
//          [--late] [--privacy] [--emergency] [--alias TEXT]
//
// bs: a repeater's output, both slots without a break, the call on --slot and idle bursts on the
//     other, a CACH ahead of every burst.
// ms: a mobile's bursts, every other slot, nothing between. direct: the same with the two-slot
//     direct mode sync patterns of --slot.
// VOICE.ambe: 9-byte AMBE+2 frames as they go on air (RadioFoundry's tests/data/voice/*.ambe).
// --late leaves the voice header out, as if the receiver came to the call after its start.
// --privacy sets the privacy bit in the link control (the speech itself is not encrypted).
//
// Build: cc -O2 -I../radiofoundry/core -o dmrgen dmrgen.c ../radiofoundry/core/dmr/fec/dmrFec.c \
//           ../radiofoundry/core/dmr/fec/dmrCach.c ../radiofoundry/core/dmr/call/dmr_lc.c \
//           ../radiofoundry/core/dmr/ambe/ambeFec.c -lm
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "dmr/fec/dmrFec.h"
#include "dmr/fec/dmrCach.h"
#include "dmr/call/dmr_lc.h"
#include "dmr/ambe/ambeFec.h"

#define SLOT_SYMS 144
#define SPS 5
#define SPAN 8                       // root raised cosine: symbols each side
#define INNER_HZ 648.0               // deviation of the inner symbols; the outer ones are three times it
#define COUNTS_PER_HZ 6.5536

enum { BS, MS, DIRECT };
static const uint8_t SYNCS[4][2][6] = {          // [who][0 voice, 1 data] (TS 102 361-1 table 9.2)
    { { 0x75, 0x5F, 0xD7, 0xDF, 0x75, 0xF7 }, { 0xDF, 0xF5, 0x7D, 0x75, 0xDF, 0x5D } },   // repeater
    { { 0x7F, 0x7D, 0xD5, 0xDD, 0x57, 0xDF }, { 0xD5, 0xD7, 0xF7, 0x7F, 0xD7, 0x57 } },   // mobile
    { { 0x5D, 0x57, 0x7F, 0x77, 0x57, 0xFF }, { 0xF7, 0xFD, 0xD5, 0xDD, 0xFD, 0x55 } },   // direct, slot 1
    { { 0x7D, 0xFF, 0xD5, 0xF5, 0x5D, 0x5F }, { 0xD7, 0x55, 0x7F, 0x5F, 0xF7, 0xF5 } },   // direct, slot 2
};

static float *levels;                // one per symbol: +-3, +-1, or 0 with the transmitter off
static size_t nlevels, caplevels;
static int cach_n;

static void put_level(float v)
{
    if (nlevels == caplevels) levels = realloc(levels, (caplevels = caplevels ? 2 * caplevels : 4096) * sizeof *levels);
    levels[nlevels++] = v;
}

static void put_bits(const uint8_t *b, int nbits)
{
    static const float map[4] = { 1, 3, -1, -3 };       // dibits 00 01 10 11
    for (int i = 0; i < nbits; i += 2)
        put_level(map[((b[i >> 3] >> (6 - (i & 7))) & 3)]);
}

static void put_off(int nsyms) { while (nsyms-- > 0) put_level(0); }

static void set_bits(uint8_t *b, int at, int n, uint32_t v)
{
    for (int i = 0; i < n; i++, at++) {
        uint8_t m = (uint8_t)(0x80 >> (at & 7));
        if ((v >> (n - 1 - i)) & 1) b[at >> 3] |= m; else b[at >> 3] &= (uint8_t)~m;
    }
}

// The 12 symbols ahead of a burst: a repeater's CACH (busy, the slot that follows, a null short LC in four
// pieces), or a mobile's guard time
static void put_lead(int who, int slot)
{
    static uint32_t rows[4];
    static const uint8_t lcss[4] = { DMR_LCSS_FIRST, DMR_LCSS_CONT, DMR_LCSS_CONT, DMR_LCSS_LAST };
    uint8_t b[3];
    uint32_t c;
    if (who != BS) {
        put_off(12);
        return;
    }
    if (cach_n == 0) dmrShortLcEncode(dmrShortLcNull(), rows);
    c = dmrCachBuild(1, (uint8_t)(slot - 1), lcss[cach_n & 3], rows[cach_n & 3]);
    cach_n++;
    b[0] = (uint8_t)(c >> 16);
    b[1] = (uint8_t)(c >> 8);
    b[2] = (uint8_t)c;
    put_bits(b, 24);
}

static void put_data(int who, int sync, int slot, int cc, int type, const uint8_t payload[12])
{
    uint8_t burst[DMR_FEC_BURST_BYTES] = { 0 };
    dmrFecBptcEncode(payload, burst);
    dmrFecSlotTypeEncode((uint8_t)cc, (uint8_t)type, burst);
    for (int i = 0; i < 6; i++) set_bits(burst, 108 + 8 * i, 8, SYNCS[sync][1][i]);
    put_lead(who, slot);
    put_bits(burst, 264);
}

int main(int argc, char **argv)
{
    int who, slot = 1, cc = 1, late = 0, privacy = 0, emergency = 0, private = 0, sync;
    uint32_t dst = 1, src = 1;
    const char *alias = "";
    uint8_t (*voice)[AMBE_FRAME_BYTES], lc[DMR_LC_BYTES], full[DMR_LC_FULL_BYTES], idle[12], frag[4][4];
    uint8_t ta[DMR_TA_LC_MAX][DMR_LC_BYTES];
    int nframes = 0, nbursts, nta, emb_n = 0;
    dmr_lc_t l = { 0 };
    FILE *f;

    if (argc < 4) return fprintf(stderr, "dmrgen bs|ms|direct OUT.raw VOICE.ambe [options]\n"), 2;
    who = !strcmp(argv[1], "bs") ? BS : !strcmp(argv[1], "ms") ? MS : DIRECT;
    for (int i = 4; i < argc; i++) {
        if (!strcmp(argv[i], "--late")) late = 1;
        else if (!strcmp(argv[i], "--privacy")) privacy = 1;
        else if (!strcmp(argv[i], "--emergency")) emergency = 1;
        else if (!strcmp(argv[i], "--private")) private = 1;
        else if (i + 1 >= argc) return fprintf(stderr, "dmrgen: %s?\n", argv[i]), 2;
        else if (!strcmp(argv[i], "--slot")) slot = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--cc")) cc = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--dst")) dst = (uint32_t)atol(argv[++i]);
        else if (!strcmp(argv[i], "--src")) src = (uint32_t)atol(argv[++i]);
        else if (!strcmp(argv[i], "--alias")) alias = argv[++i];
        else return fprintf(stderr, "dmrgen: %s?\n", argv[i]), 2;
    }
    sync = who == BS ? 0 : who == MS ? 1 : 1 + slot;

    // The speech, filled up to whole superframes (18 frames) with its last frame
    if (!(f = fopen(argv[3], "rb"))) return perror(argv[3]), 1;
    voice = malloc(100000 * AMBE_FRAME_BYTES);
    while (nframes < 99000 && fread(voice[nframes], 1, AMBE_FRAME_BYTES, f) == AMBE_FRAME_BYTES) nframes++;
    fclose(f);
    if (!nframes) return fprintf(stderr, "dmrgen: no frames in %s\n", argv[3]), 1;
    for (; nframes % 18; nframes++) memcpy(voice[nframes], voice[nframes - 1], AMBE_FRAME_BYTES);
    nbursts = nframes / 3;

    l.flco = private ? DMR_FLCO_UNIT_VOICE : DMR_FLCO_GROUP_VOICE;
    l.svc = (uint8_t)((privacy ? DMR_SVC_PRIVACY : 0) | (emergency ? DMR_SVC_EMERGENCY : 0));
    l.dst = dst;
    l.src = src;
    dmr_lc_pack(&l, lc);
    nta = dmr_ta_encode(alias, DMR_TA_7BIT, ta);
    for (int i = 0; i < 12; i++) idle[i] = (uint8_t)(0x53 + 29 * i);   // an idle burst's content means nothing

    // What goes out on the call's slot, one burst per entry: -1 idle, -2 header, -3 terminator, else a voice burst
    int total = 4 + (late ? 0 : 1) + nbursts + 1 + 4, at = 0;
    int *plan = malloc((size_t)total * sizeof *plan);
    for (int i = 0; i < 4; i++) plan[at++] = -1;
    if (!late) plan[at++] = -2;
    for (int i = 0; i < nbursts; i++) plan[at++] = i;
    plan[at++] = -3;
    for (int i = 0; i < 4; i++) plan[at++] = -1;

    put_off(who == BS ? 0 : 200);
    for (int k = 0; k < total; k++) {
        int p = plan[k];
        // The other slot first when the call is on slot 2, so slot 1 always leads
        for (int s = 1; s <= 2; s++) {
            if (s != slot) {
                if (who == BS) put_data(BS, 0, s, cc, 9, idle);
                else put_off(SLOT_SYMS);
                continue;
            }
            if (p == -1) {
                if (who == BS) put_data(BS, 0, s, cc, 9, idle);
                else put_off(SLOT_SYMS);
            } else if (p == -2) {
                dmr_lc_encode_full(lc, DMR_RS_MASK_VOICE_HEADER, full);
                put_data(who, sync, s, cc, 1, full);
            } else if (p == -3) {
                dmr_lc_encode_full(lc, DMR_RS_MASK_TERMINATOR, full);
                put_data(who, sync, s, cc, 2, full);
            } else {
                uint8_t burst[AMBE_FULL_BURST_BYTES] = { 0 };
                int idx = p % 6;
                ambeFullBurstJoin((const uint8_t (*)[AMBE_FRAME_BYTES])&voice[3 * p], burst);
                if (idx == 0) {
                    // Each superframe carries one embedded LC: the call's, then the talker alias pieces in turn
                    int which = emb_n++ % (1 + nta);
                    dmr_emb_lc_encode(which == 0 ? lc : ta[which - 1], frag);
                    for (int i = 0; i < 6; i++) set_bits(burst, 108 + 8 * i, 8, SYNCS[sync][0][i]);
                } else {
                    uint8_t lcss = dmr_emb_lcss((uint8_t)idx);
                    uint16_t emb = dmrFecEmbEncode((uint8_t)cc, false, lcss);
                    set_bits(burst, 108, 8, emb >> 8);
                    set_bits(burst, 148, 8, emb & 0xFF);
                    for (int i = 0; i < 4; i++) set_bits(burst, 116 + 8 * i, 8, idx <= 4 ? frag[idx - 1][i] : 0);
                }
                put_lead(who, s);
                put_bits(burst, 264);
            }
        }
    }
    put_off(who == BS ? 0 : 200);

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
            double v = 0;
            for (int k = 0; k < TAPS; k++) {
                long j = (long)i + SPAN * SPS - k;
                if (j >= 0 && j % SPS == 0 && (size_t)(j / SPS) < nlevels) v += levels[j / SPS] * h[k];
            }
            out[i] = (int16_t)lrint(v * INNER_HZ * COUNTS_PER_HZ);
        }
        if (!(f = fopen(argv[2], "wb"))) return perror(argv[2]), 1;
        fwrite(out, sizeof *out, n, f);
        fclose(f);
        fprintf(stderr, "%s: %d voice bursts, %zu symbols, %.2f s\n", argv[2], nbursts, nlevels, n / 24000.0);
    }
    return 0;
}
