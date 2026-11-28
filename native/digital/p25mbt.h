// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// Multi-block trunking messages of a P25 control channel (p25mbt.c).
#ifndef SS_P25MBT_H
#define SS_P25MBT_H

#include <stdbool.h>
#include <stdint.h>
#include "radiofoundry/core/p25/p25_tsbk.h"

// A packet data unit of nsyms symbols. True when it is an alternate format multi-block trunking message: m as
// p25_tsbk_parse gives a single block's message (kind P25_TSBK_OTHER for an opcode not decoded here, P25_TSBK_MFR
// for a manufacturer's), hdr its header block and data0 its first data block. *crc_ok false: the packet CRC failed,
// and m holds only the opcode and manufacturer
bool p25mbt_unit(const uint8_t *unit, uint16_t nsyms, p25_tsbk_t *m, uint8_t hdr[P25_TSBK_BYTES],
                 uint8_t data0[P25_TSBK_BYTES], bool *crc_ok);

#endif
