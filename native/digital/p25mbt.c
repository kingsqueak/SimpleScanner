// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// Multi-block trunking messages: the forms of a control channel's messages too long for one block, sent as a packet
// (alternate format, TIA-102.AABC) of a header block and data blocks. RadioFoundry (MIT, radiofoundry/) gives the
// header's fields, the blocks' trellis code, the packet CRC and the network status form; this file reads the rest
// into the same p25_tsbk_t a single block gives, so a caller handles both alike.
//
// The octet places were checked against two receivers, sdrtrunk (phase1/message/pdu/ambtc/osp) and op25
// (tk_p25.py, decode_mbt_data); only the forms on which both agree are read: the group voice grant, the network,
// RFSS and adjacent site status. Fields that only one of them reads (the service class of the RFSS and adjacent
// forms, the adjacent site's flags) are left 0. The unit to unit grants and the TDMA identifier (sdrtrunk only,
// the latter for another system) are passed on as P25_TSBK_OTHER.
#include <string.h>
#include "p25mbt.h"

#define DATA_MAX (P25_PDU_BLOCKS_MAX - 1)

static uint16_t u16(const uint8_t *b) { return (uint16_t)(b[0] << 8 | b[1]); }
static uint32_t u24(const uint8_t *b) { return (uint32_t)b[0] << 16 | (uint32_t)b[1] << 8 | b[2]; }

bool p25mbt_unit(const uint8_t *unit, uint16_t nsyms, p25_tsbk_t *m, uint8_t hdr[P25_TSBK_BYTES],
                 uint8_t data0[P25_TSBK_BYTES], bool *crc_ok)
{
    uint8_t d[DATA_MAX * P25_TSBK_BYTES];
    p25_mbt_t h;
    unsigned n, k, len;
    uint32_t crc;

    memset(m, 0, sizeof *m);
    memset(data0, 0, P25_TSBK_BYTES);
    *crc_ok = false;
    if (p25_block_get(unit, 0, hdr) < 0 || !p25_mbt_parse(hdr, &h) || h.format != P25_PDU_FMT_AMBTC) return false;
    n = h.blocks;
    if (n < 1 || n > DATA_MAX || nsyms < p25_blocks_syms((uint8_t)(n + 1))) return false;
    for (k = 0; k < n; k++) p25_block_decode(unit, (uint8_t)(k + 1), d + k * P25_TSBK_BYTES);
    memcpy(data0, d, P25_TSBK_BYTES);
    m->opcode = h.opcode;
    m->mfid = h.mfid;
    m->last = true;
    m->kind = P25_TSBK_OTHER;
    // The packet CRC is in the last four octets of the last block and covers every octet before them
    len = n * P25_TSBK_BYTES - 4;
    crc = (uint32_t)d[len] << 24 | (uint32_t)d[len + 1] << 16 | (uint32_t)d[len + 2] << 8 | d[len + 3];
    if (p25_packet_crc(d, len) != crc) return true;
    *crc_ok = true;
    if (h.mfid != 0) {
        m->kind = P25_TSBK_MFR;
        return true;
    }
    switch (h.opcode) {
    case P25_OP_GRP_V_GRANT:
        // Header: the source in the logical link ID (octets 3-5), service options (octet 8). Data: 2 reserved
        // octets, channel (T), channel (R) (not kept), group
        m->kind = P25_TSBK_GRANT;
        m->u.grant.has_svc = true;
        m->u.grant.svc = hdr[8];
        m->u.grant.src = u24(hdr + 3);
        m->u.grant.ch = u16(d + 2);
        m->u.grant.tg = u16(d + 6);
        break;
    case P25_OP_NET_STS_BCST:
        if (!p25_mbt_net_parse(hdr, d, m)) {
            memset(&m->u, 0, sizeof m->u);
            m->kind = P25_TSBK_OTHER;
        }
        break;
    case P25_OP_RFSS_STS_BCST:
        // Header: LRA (octet 3), the system in the low 12 bits of octets 4-5. Data: RFSS, site, channel (T),
        // channel (R) (not kept)
        m->kind = P25_TSBK_RFSS;
        m->u.site.lra = hdr[3];
        m->u.site.sysid = (uint16_t)((hdr[4] & 0x0F) << 8 | hdr[5]);
        m->u.site.rfss = d[0];
        m->u.site.site = d[1];
        m->u.site.ch = u16(d + 2);
        break;
    case P25_OP_ADJ_STS_BCST:
        // Header: LRA, system as above, RFSS (octet 8), site (octet 9). Data: channel (T), channel (R) (not kept)
        m->kind = P25_TSBK_ADJ;
        m->u.site.lra = hdr[3];
        m->u.site.sysid = (uint16_t)((hdr[4] & 0x0F) << 8 | hdr[5]);
        m->u.site.rfss = hdr[8];
        m->u.site.site = hdr[9];
        m->u.site.ch = u16(d);
        break;
    default:
        break;
    }
    return true;
}
