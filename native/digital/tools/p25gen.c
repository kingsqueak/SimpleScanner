// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// Makes P25 control channel test signals for SimpleScanner's tests: the messages of a script as FM
// discriminator samples (24 kHz, signed 16-bit, 6553.6 counts per kHz), built with RadioFoundry's encoders; or as
// complex baseband.
//
//   p25gen SCRIPT OUT.raw [--repeat N] [--cqpsk [--iq16] [--echo US DB HZ]]
//
// --cqpsk: a CQPSK (LSM) transmitter instead: the symbols as phase steps of level x pi / 4 (+3 is +3 pi / 4, as
// C4FM's phase moves), a raised cosine pulse (roll-off 0.2) on I and Q, written as 48 kHz complex baseband,
// interleaved float32 I/Q, amplitude 0.5 at the symbol centres, nothing while the transmitter is off.
// --iq16: that as signed 16-bit I/Q, 16384 = 1.0 (half the size, for test fixtures).
// --echo US DB HZ: a second copy of the complex signal, US microseconds later, DB dB from the first (negative:
// weaker) and HZ above it, as a simulcast site's second transmitter is heard.
//
// The script has one message to a line, its fields as name=value (decimal, or hex with 0x; frequencies in Hz);
// a field left out is 0, and # starts a comment. Single-block messages go out three to a TSDU, in order; "tsdu"
// ends a TSDU early. A multi-block message (mbt_*) is a packet of its own.
//
//   nac N                                      the NAC of all that follows (default 0x293)
//   net lra wacn sysid ch ssc                  network status broadcast
//   rfss lra flags sysid rfss site ch ssc      RFSS status broadcast
//   adj lra flags sysid rfss site ch ssc       adjacent site status
//   sccb rfss site ch ssc ch2 ssc2             secondary control channels
//   sccb_exp rfss site ch ch2 ssc              the same, explicit form (ch transmit, ch2 receive)
//   iden id base spacing offset bw             channel identifier (0x3D: offset a multiple of 250 kHz)
//   iden_vu id base spacing offset bw          VHF/UHF form (offset a multiple of the spacing; bw 6250 or 12500)
//   iden_tdma id type base spacing offset      TDMA form (type: the channel type)
//   grant svc ch tg src                        group voice grant
//   update ch tg ch2 tg2                       group voice grant update
//   update_exp svc ch ch2 tg                   the same, explicit form
//   uu_grant ch dst src / uu_update ch dst src unit to unit voice grant and update
//   sys_srv avail supp prio                    system service broadcast
//   raw o=OCTETS                               a block's first ten octets as 20 hex digits (the last block flag is
//                                              set here)
//   bad                                        the next block goes out with its CRC broken
//   mbt_grant svc src ch ch2 tg                group voice grant, multi-block (alternate format)
//   mbt_net lra sysid wacn ch ch2 ssc          network status, multi-block
//   mbt_rfss lra sysid rfss site ch ch2        RFSS status, multi-block
//   mbt_adj lra sysid rfss site ch ch2         adjacent site status, multi-block
//
// The multi-block layouts are those p25mbt.c reads (checked against sdrtrunk and op25 there); they are written here
// octet by octet, apart from the reader.
//
// Build: cc -O2 -std=gnu11 -DCONFIG_P25_TRUNK=1 -DCONFIG_P25=1 -I../radiofoundry/core -o p25gen p25gen.c \
//           ../radiofoundry/core/p25/p25_tsbk.c ../radiofoundry/core/p25/p25_unit.c \
//           ../radiofoundry/core/fec/trellis.c ../radiofoundry/core/fec/bch63.c ../radiofoundry/core/fec/gf64.c \
//           ../radiofoundry/core/fec/golay.c ../radiofoundry/core/fec/hamming.c ../radiofoundry/core/fec/rs63.c -lm
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "p25/p25.h"
#include "p25/p25_tsbk.h"
#include "air/air_syms.h"

#define SPS 5
#define SPAN 8                       // raised cosine: symbols each side
#define INNER_HZ 600.0               // deviation of the inner symbols; the outer ones are three times it
#define COUNTS_PER_HZ 6.5536
#define MBT_DATA_MAX 3
#define IQ_RATE 48000
#define IQ_SPS 10
#define IQ_AMP 0.5

static float *levels;                // one per symbol: +-3, +-1, or 0 with the transmitter off
static size_t nlevels, caplevels;
static int nac = 0x293, nunits, nblocks, npackets;

static void put_level(float v)
{
    if (nlevels == caplevels) levels = realloc(levels, (caplevels = caplevels ? 2 * caplevels : 4096) * sizeof *levels);
    levels[nlevels++] = v;
}

static void put_off(int nsyms) { while (nsyms-- > 0) put_level(0); }

static void put_unit(const uint8_t *unit, uint16_t nsyms)
{
    for (uint16_t i = 0; i < nsyms; i++) put_level(air_sym_level(air_sym_get(unit, i)));
    nunits++;
}

// ---- the script's fields ----

static char *fields[32];
static int nfields;

static long field(const char *name)
{
    size_t n = strlen(name);
    for (int i = 1; i < nfields; i++)
        if (!strncmp(fields[i], name, n) && fields[i][n] == '=') return strtol(fields[i] + n + 1, NULL, 0);
    return 0;
}

static const char *field_text(const char *name)
{
    size_t n = strlen(name);
    for (int i = 1; i < nfields; i++)
        if (!strncmp(fields[i], name, n) && fields[i][n] == '=') return fields[i] + n + 1;
    return "";
}

// v in units of `unit`, or the program stops: the air has no place for a part of one
static uint32_t in_units(long v, long unit, const char *what)
{
    if (unit <= 0 || v % unit) {
        fprintf(stderr, "p25gen: %s %ld is not a multiple of %ld\n", what, v, unit);
        exit(2);
    }
    return (uint32_t)(v / unit);
}

// Bit a of a block's arguments (octets 2-9), n bits, first bit first
static void arg_put(uint8_t *blk, unsigned a, unsigned n, uint32_t v)
{
    for (unsigned i = 0; i < n; i++) air_bit_put(blk, (uint16_t)(16 + a + i), (uint8_t)((v >> (n - 1 - i)) & 1));
}

// ---- TSDUs ----

static uint8_t pending[P25_TSDU_BLOCKS_MAX][P25_TSBK_BYTES];
static int npending, bad_next, bad[P25_TSDU_BLOCKS_MAX];

static void flush_tsdu(void)
{
    uint8_t unit[P25_UNIT_BYTES_MAX];
    uint16_t n;
    if (!npending) return;
    n = p25_blocks_syms((uint8_t)npending);
    p25_unit_begin(unit, n, (uint16_t)nac, P25_DUID_TSDU, P25_SS_IDLE);
    for (int k = 0; k < npending; k++) {
        if (k == npending - 1) pending[k][0] |= P25_TSBK_LB;
        else pending[k][0] &= (uint8_t)~P25_TSBK_LB;
        p25_tsbk_seal(pending[k]);
        if (bad[k]) pending[k][11] ^= 0x5A;
        p25_block_put(unit, (uint8_t)k, pending[k]);
    }
    put_unit(unit, n);
    nblocks += npending;
    npending = 0;
}

static void add_block(const uint8_t blk[P25_TSBK_BYTES])
{
    memcpy(pending[npending], blk, P25_TSBK_BYTES);
    bad[npending] = bad_next;
    bad_next = 0;
    if (++npending == P25_TSDU_BLOCKS_MAX) flush_tsdu();
}

static void add_tsbk(p25_tsbk_t *m)
{
    uint8_t blk[P25_TSBK_BYTES];
    p25_tsbk_make(m, blk);
    add_block(blk);
}

// ---- multi-block packets: a header and n data blocks, the packet CRC in the last four octets ----

static void add_packet(uint8_t opcode, uint8_t hdr[P25_TSBK_BYTES], uint8_t *data, int n)
{
    uint8_t unit[P25_UNIT_BYTES_MAX];
    p25_mbt_t h = { .format = P25_PDU_FMT_AMBTC, .sap = 61, .mfid = 0, .blocks = (uint8_t)n, .opcode = opcode };
    uint8_t made[P25_TSBK_BYTES];
    unsigned len = (unsigned)n * P25_TSBK_BYTES - 4;
    uint32_t crc;
    uint16_t nsyms = p25_blocks_syms((uint8_t)(n + 1));

    flush_tsdu();
    // The header's fixed octets from RadioFoundry; octets 3-5 and 8-9 are the message's
    p25_mbt_make(&h, made);
    made[1] |= 0xC0;
    memcpy(made + 3, hdr + 3, 3);
    memcpy(made + 8, hdr + 8, 2);
    p25_tsbk_seal(made);
    crc = p25_packet_crc(data, len);
    data[len] = (uint8_t)(crc >> 24);
    data[len + 1] = (uint8_t)(crc >> 16);
    data[len + 2] = (uint8_t)(crc >> 8);
    data[len + 3] = (uint8_t)crc;
    p25_unit_begin(unit, nsyms, (uint16_t)nac, P25_DUID_PDU, P25_SS_IDLE);
    p25_block_put(unit, 0, made);
    for (int k = 0; k < n; k++) p25_block_put(unit, (uint8_t)(k + 1), data + k * P25_TSBK_BYTES);
    put_unit(unit, nsyms);
    npackets++;
}

static void put16(uint8_t *b, long v) { b[0] = (uint8_t)(v >> 8); b[1] = (uint8_t)v; }

static int line(char *s)
{
    p25_tsbk_t m;
    uint8_t blk[P25_TSBK_BYTES], hdr[P25_TSBK_BYTES], data[MBT_DATA_MAX * P25_TSBK_BYTES];
    const char *w;
    char *c = strchr(s, '#');
    if (c) *c = 0;
    nfields = 0;
    for (char *t = strtok(s, " \t\r\n"); t && nfields < 32; t = strtok(NULL, " \t\r\n")) fields[nfields++] = t;
    if (!nfields) return 0;
    w = fields[0];
    memset(&m, 0, sizeof m);
    memset(blk, 0, sizeof blk);
    memset(hdr, 0, sizeof hdr);
    memset(data, 0, sizeof data);
    if (!strcmp(w, "nac")) {
        flush_tsdu();
        nac = (int)strtol(nfields > 1 ? fields[1] : "0", NULL, 0);
    } else if (!strcmp(w, "tsdu")) {
        flush_tsdu();
    } else if (!strcmp(w, "bad")) {
        bad_next = 1;
    } else if (!strcmp(w, "net")) {
        m.kind = P25_TSBK_NET;
        m.u.net.lra = (uint8_t)field("lra");
        m.u.net.wacn = (uint32_t)field("wacn");
        m.u.net.sysid = (uint16_t)field("sysid");
        m.u.net.ch = (uint16_t)field("ch");
        m.u.net.ssc = (uint8_t)field("ssc");
        add_tsbk(&m);
    } else if (!strcmp(w, "rfss") || !strcmp(w, "adj")) {
        m.kind = !strcmp(w, "rfss") ? P25_TSBK_RFSS : P25_TSBK_ADJ;
        m.u.site.lra = (uint8_t)field("lra");
        m.u.site.flags = (uint8_t)field("flags");
        m.u.site.sysid = (uint16_t)field("sysid");
        m.u.site.rfss = (uint8_t)field("rfss");
        m.u.site.site = (uint8_t)field("site");
        m.u.site.ch = (uint16_t)field("ch");
        m.u.site.ssc = (uint8_t)field("ssc");
        add_tsbk(&m);
    } else if (!strcmp(w, "sccb")) {
        m.kind = P25_TSBK_SCCB;
        m.u.sccb.rfss = (uint8_t)field("rfss");
        m.u.sccb.site = (uint8_t)field("site");
        m.u.sccb.ch[0] = (uint16_t)field("ch");
        m.u.sccb.ssc[0] = (uint8_t)field("ssc");
        m.u.sccb.ch[1] = (uint16_t)field("ch2");
        m.u.sccb.ssc[1] = (uint8_t)field("ssc2");
        add_tsbk(&m);
    } else if (!strcmp(w, "sccb_exp")) {
        // RadioFoundry's encoder has only the other form: RFSS, site, channel (T), channel (R), service class
        blk[0] = P25_OP_SCCB_EXP;
        arg_put(blk, 0, 8, (uint32_t)field("rfss"));
        arg_put(blk, 8, 8, (uint32_t)field("site"));
        arg_put(blk, 16, 16, (uint32_t)field("ch"));
        arg_put(blk, 32, 16, (uint32_t)field("ch2"));
        arg_put(blk, 48, 8, (uint32_t)field("ssc"));
        add_block(blk);
    } else if (!strcmp(w, "iden") || !strcmp(w, "iden_vu") || !strcmp(w, "iden_tdma")) {
        long spacing = field("spacing");
        m.kind = P25_TSBK_IDEN;
        m.u.iden.form = !strcmp(w, "iden") ? P25_OP_IDEN_UP : !strcmp(w, "iden_vu") ? P25_OP_IDEN_UP_VU : P25_OP_IDEN_UP_TDMA;
        m.u.iden.id = (uint8_t)field("id");
        m.u.iden.base = in_units(field("base"), 5, "base");
        m.u.iden.spacing = (uint16_t)in_units(spacing, 125, "spacing");
        m.u.iden.bw_khz8 = (uint16_t)in_units(field("bw"), 125, "bw");
        m.u.iden.type = (uint8_t)field("type");
        {
            long off = field("offset"), unit = m.u.iden.form == P25_OP_IDEN_UP ? 250000 : spacing;
            uint32_t mag = in_units(off < 0 ? -off : off, unit, "offset");
            m.u.iden.tx_off = (int16_t)(off < 0 ? -(int32_t)mag : (int32_t)mag);
        }
        add_tsbk(&m);
    } else if (!strcmp(w, "grant") || !strcmp(w, "update") || !strcmp(w, "update_exp") || !strcmp(w, "uu_grant") ||
               !strcmp(w, "uu_update")) {
        m.kind = P25_TSBK_GRANT;
        m.opcode = !strcmp(w, "grant") ? P25_OP_GRP_V_GRANT : !strcmp(w, "update") ? P25_OP_GRP_V_UPDT
                 : !strcmp(w, "update_exp") ? P25_OP_GRP_V_UPDT_EXP : !strcmp(w, "uu_grant") ? P25_OP_UU_V_GRANT
                 : P25_OP_UU_V_UPDT;
        m.u.grant.svc = (uint8_t)field("svc");
        m.u.grant.ch = (uint16_t)field("ch");
        m.u.grant.ch2 = (uint16_t)field("ch2");
        m.u.grant.tg = (uint16_t)field("tg");
        m.u.grant.tg2 = (uint16_t)field("tg2");
        m.u.grant.src = (uint32_t)field("src");
        m.u.grant.dst = (uint32_t)field("dst");
        add_tsbk(&m);
    } else if (!strcmp(w, "sys_srv")) {
        m.kind = P25_TSBK_SYS_SRV;
        m.u.svc.avail = (uint32_t)field("avail");
        m.u.svc.supp = (uint32_t)field("supp");
        m.u.svc.prio = (uint8_t)field("prio");
        add_tsbk(&m);
    } else if (!strcmp(w, "raw")) {
        const char *o = field_text("o");
        if (strlen(o) != 20) return fprintf(stderr, "p25gen: raw wants o= with 20 hex digits\n"), -1;
        for (int i = 0; i < 10; i++) {
            char two[3] = { o[2 * i], o[2 * i + 1], 0 };
            blk[i] = (uint8_t)strtoul(two, NULL, 16);
        }
        add_block(blk);
    } else if (!strcmp(w, "mbt_grant")) {
        // Header: the source in octets 3-5, service options in octet 8. Data: 2 reserved, channel (T), channel (R),
        // group
        long src = field("src");
        hdr[3] = (uint8_t)(src >> 16);
        hdr[4] = (uint8_t)(src >> 8);
        hdr[5] = (uint8_t)src;
        hdr[8] = (uint8_t)field("svc");
        put16(data + 2, field("ch"));
        put16(data + 4, field("ch2"));
        put16(data + 6, field("tg"));
        add_packet(P25_OP_GRP_V_GRANT, hdr, data, 1);
    } else if (!strcmp(w, "mbt_net") || !strcmp(w, "mbt_rfss") || !strcmp(w, "mbt_adj")) {
        // Header: LRA in octet 3, the system in the low 12 bits of octets 4-5
        long sysid = field("sysid");
        hdr[3] = (uint8_t)field("lra");
        hdr[4] = (uint8_t)((sysid >> 8) & 0x0F);
        hdr[5] = (uint8_t)sysid;
        if (!strcmp(w, "mbt_net")) {
            // Data: WACN 20 bits, 4 reserved, channel (T), channel (R), service class
            long wacn = field("wacn");
            data[0] = (uint8_t)(wacn >> 12);
            data[1] = (uint8_t)(wacn >> 4);
            data[2] = (uint8_t)((wacn & 0x0F) << 4);
            put16(data + 3, field("ch"));
            put16(data + 5, field("ch2"));
            data[7] = (uint8_t)field("ssc");
            add_packet(P25_OP_NET_STS_BCST, hdr, data, 1);
        } else if (!strcmp(w, "mbt_rfss")) {
            // Data: RFSS, site, channel (T), channel (R)
            data[0] = (uint8_t)field("rfss");
            data[1] = (uint8_t)field("site");
            put16(data + 2, field("ch"));
            put16(data + 4, field("ch2"));
            add_packet(P25_OP_RFSS_STS_BCST, hdr, data, 1);
        } else {
            // Header: RFSS in octet 8, site in octet 9. Data: channel (T), channel (R)
            hdr[8] = (uint8_t)field("rfss");
            hdr[9] = (uint8_t)field("site");
            put16(data, field("ch"));
            put16(data + 2, field("ch2"));
            add_packet(P25_OP_ADJ_STS_BCST, hdr, data, 1);
        }
    } else {
        return fprintf(stderr, "p25gen: %s?\n", w), -1;
    }
    return 0;
}

// The raised cosine pulse (roll-off a) at t symbols from its centre
static double rc(double t, double a)
{
    double sinc = fabs(t) < 1e-9 ? 1 : sin(M_PI * t) / (M_PI * t);
    double d = 1 - (2 * a * t) * (2 * a * t);
    return fabs(d) < 1e-9 ? sinc * M_PI / 4 : sinc * cos(M_PI * a * t) / d;
}

// The CQPSK signal at t seconds: each symbol a point on the circle a phase step on from the last, through the pulse
static void cqpsk_at(const double *ph, double t, double *re, double *im)
{
    double at = t * 4800;
    long k0 = (long)floor(at) - SPAN, k1 = (long)ceil(at) + SPAN;
    *re = *im = 0;
    for (long k = k0 < 0 ? 0 : k0; k <= k1 && (size_t)k < nlevels; k++) {
        double h;
        if (levels[k] == 0) continue;
        h = rc(at - (double)k, 0.2) * IQ_AMP;
        *re += h * cos(ph[k]);
        *im += h * sin(ph[k]);
    }
}

// The CQPSK signal, 48 kHz complex, and its echo
static void write_iq(FILE *f, int iq16, double echo_us, double echo_db, double echo_hz)
{
    size_t n = nlevels * IQ_SPS;
    double *ph = calloc(nlevels, sizeof *ph), p = 0, g = echo_us > 0 || echo_db != 0 || echo_hz != 0 ? pow(10, echo_db / 20) : 0;
    for (size_t k = 0; k < nlevels; k++) {
        p += levels[k] * M_PI / 4;
        ph[k] = p;
    }
    for (size_t i = 0; i < n; i++) {
        double t = (double)i / IQ_RATE, re, im, er = 0, ei = 0;
        cqpsk_at(ph, t, &re, &im);
        if (g > 0) {
            double w = 2 * M_PI * echo_hz * t;
            cqpsk_at(ph, t - echo_us * 1e-6, &er, &ei);
            re += g * (er * cos(w) - ei * sin(w));
            im += g * (er * sin(w) + ei * cos(w));
        }
        if (iq16) {
            int16_t s[2] = { (int16_t)lrint(re * 16384), (int16_t)lrint(im * 16384) };
            fwrite(s, sizeof s, 1, f);
        } else {
            float s[2] = { (float)re, (float)im };
            fwrite(s, sizeof s, 1, f);
        }
    }
    free(ph);
}

int main(int argc, char **argv)
{
    int repeat = 1, cqpsk = 0, iq16 = 0;
    double echo_us = 0, echo_db = 0, echo_hz = 0;
    char buf[512];
    FILE *f;

    if (argc < 3) return fprintf(stderr, "p25gen SCRIPT OUT.raw [--repeat N] [--cqpsk [--iq16] [--echo US DB HZ]]\n"), 2;
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--repeat") && i + 1 < argc) repeat = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--cqpsk")) cqpsk = 1;
        else if (!strcmp(argv[i], "--iq16")) iq16 = 1;
        else if (!strcmp(argv[i], "--echo") && i + 3 < argc) {
            echo_us = atof(argv[++i]);
            echo_db = atof(argv[++i]);
            echo_hz = atof(argv[++i]);
        } else return fprintf(stderr, "p25gen: %s?\n", argv[i]), 2;
    }
    if ((iq16 || echo_us != 0 || echo_db != 0 || echo_hz != 0) && !cqpsk)
        return fprintf(stderr, "p25gen: --iq16 and --echo are for --cqpsk\n"), 2;

    put_off(100);
    for (int r = 0; r < repeat; r++) {
        if (!(f = fopen(argv[1], "r"))) return perror(argv[1]), 1;
        while (fgets(buf, sizeof buf, f))
            if (line(buf) < 0) return 2;
        fclose(f);
        flush_tsdu();
    }
    put_off(100);

    // C4FM: an impulse of the symbol's level at each symbol, through a raised cosine (roll-off 0.2), so the
    // discriminator reads each symbol's own level at its centre
    {
        enum { TAPS = 2 * SPAN * SPS + 1 };
        double h[TAPS], a = 0.2;
        size_t n = nlevels * SPS;
        int16_t *out = malloc(n * sizeof *out);
        for (int i = 0; i < TAPS; i++) {
            double t = (double)(i - SPAN * SPS) / SPS;
            double sinc = fabs(t) < 1e-9 ? 1 : sin(M_PI * t) / (M_PI * t);
            double d = 1 - (2 * a * t) * (2 * a * t);
            h[i] = fabs(d) < 1e-9 ? sinc * M_PI / 4 : sinc * cos(M_PI * a * t) / d;
        }
        for (size_t i = 0; i < n; i++) {
            double v = 0;
            for (int k = 0; k < TAPS; k++) {
                long j = (long)i + SPAN * SPS - k;
                if (j >= 0 && j % SPS == 0 && (size_t)(j / SPS) < nlevels) v += levels[j / SPS] * h[k];
            }
            out[i] = (int16_t)lrint(v * INNER_HZ * COUNTS_PER_HZ);
        }
        if (!(f = fopen(argv[2], "wb"))) return perror(argv[2]), 1;
        if (cqpsk) write_iq(f, iq16, echo_us, echo_db, echo_hz);
        else fwrite(out, sizeof *out, n, f);
        fclose(f);
        free(out);
        fprintf(stderr, "%s: %d units (%d TSDU blocks, %d packets), %zu symbols, %.2f s\n", argv[2], nunits, nblocks,
                npackets, nlevels, n / 24000.0);
    }
    return 0;
}
