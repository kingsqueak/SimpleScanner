// SPDX-License-Identifier: GPL-3.0-or-later
// SimpleScanner's AMBE+2 speech decoder (3600 x 2450 bit/s: DMR, NXDN, and others that carry the
// same 49 parameter bits). The decoder is mbelib's (ISC, see mbelib/); this file only gives it
// an instance and a plain call.
//
// This library is separate from the rest on purpose. The AMBE+2 codec is covered by a patent
// (US 8,359,197, listed as in force until 2028-05-20), so a build of the scanner can leave this
// library out: without it the scanner still follows DMR and shows who is talking, with no speech.
//
// One instance per voice stream. Not thread-safe per instance.
#include <stdint.h>
#include <stdlib.h>
#include "mbelib/mbelib.h"

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

#define UV_QUALITY 3                 // mbelib's unvoiced synthesis effort, as its users set it
#define GAIN 2.0f                    // mbelib's floats to 16 bits at the level a radio's own decoder gives

typedef struct {
    mbe_parms cur, prev, prev_enhanced;
} ss_ambe;

SS_API void ss_ambe_reset(ss_ambe *a) { mbe_initMbeParms(&a->cur, &a->prev, &a->prev_enhanced); }

SS_API ss_ambe *ss_ambe_new(void)
{
    ss_ambe *a = (ss_ambe *)calloc(1, sizeof(ss_ambe));
    if (a) ss_ambe_reset(a);
    return a;
}

SS_API void ss_ambe_free(ss_ambe *a) { free(a); }

// One 20 ms frame. bits: the 49 parameter bits after error correction, one per byte, the first
// parameter bit first. errors: bit errors the correction found in the frame; more than 3 (a lost
// frame: give 7) repeats the last good frame, and after three repeats the output is silence.
// pcm: 160 samples at 8 kHz.
SS_API void ss_ambe_decode(ss_ambe *a, const uint8_t *bits, int errors, int16_t *pcm)
{
    char d[49], err_str[64];
    float f[160];
    int errs = errors, errs2 = errors, i;
    if (errs2 > 32) errs2 = 32;      // err_str takes one character per error
    for (i = 0; i < 49; i++) d[i] = (char)(bits[i] & 1);
    mbe_processAmbe2450Dataf(f, &errs, &errs2, err_str, d, &a->cur, &a->prev, &a->prev_enhanced, UV_QUALITY);
    for (i = 0; i < 160; i++) {
        float v = f[i] * GAIN;
        pcm[i] = (int16_t)(v > 32767.0f ? 32767 : v < -32767.0f ? -32767 : v);
    }
}
