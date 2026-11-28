// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// P25 Phase 2 voice frames (p25p2.h). A frame is 72 bits, 36 dibits in a 4V or 2V burst, of the AMBE+2 half-rate
// vocoder DMR uses: four code words, C0 extended Golay (24,12), C1 Golay (23,12) whitened with a sequence seeded from
// C0's data, C2 and C3 unprotected, 49 parameter bits in all. op25 reads them with p25p2_vf::extract_vcw, a table of
// where each air bit goes; that table is the same as DMR's interleave (air bit 4k + q is bit 18q + k of C0 C1 C2 C3 in
// a row), checked air bit by air bit against RadioFoundry's ambeDeinterleave, and op25's whitening (process_vcw,
// pr[n] = 173 pr[n-1] + 13849 mod 65536 from 16 u0) is ambeC1Mask's. So a frame is read with RadioFoundry's DMR
// AMBE+2 code, and its 49 bits come out in the order dmrrx_read_voice gives them.
#include "p25p2.h"
#include "radiofoundry/core/dmr/ambe/ambeFec.h"

#include <string.h>

int p25p2_vf_decode(const uint8_t dib[36], uint8_t bits[49])
{
	uint8_t frame[AMBE_FRAME_BYTES];
	memset(frame, 0, sizeof frame);
	for (int i = 0; i < 36; i++)
		frame[i >> 2] |= (uint8_t)((dib[i] & 3) << (6 - 2 * (i & 3)));
	return ambeFecDecode(frame, bits, NULL);
}

void p25p2_vf_encode(const uint8_t bits[49], uint8_t dib[36])
{
	uint8_t frame[AMBE_FRAME_BYTES];
	ambeFecEncode(bits, frame);
	for (int i = 0; i < 36; i++)
		dib[i] = (frame[i >> 2] >> (6 - 2 * (i & 3))) & 3;
}
