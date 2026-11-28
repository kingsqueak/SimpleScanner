// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// The message length table and the walk through a PDU's messages are ported from op25 (boatbod/op25,
// gr-op25_repeater/lib/p25p2_tdma.cc: mac_msg_len, p25p2_tdma::decode_mac_msg), GPL-3; see THIRD-PARTY-NOTICES.txt.

namespace SimpleScanner.Digital;

/// <summary>A Phase 2 MAC PDU's type: the top three bits of its first octet.</summary>
public enum P25MacType
{
    Signal = 0,
    PushToTalk = 1,
    EndPushToTalk = 2,
    Idle = 3,
    Active = 4,
    Hangtime = 6,
}

/// <summary>
/// One message inside a MAC PDU of type SIGNAL, IDLE, ACTIVE or HANGTIME: its opcode (the first octet, the two
/// "B1 B2" bits with it), the manufacturer for a manufacturer's own message (else 0), and its octets from the opcode on.
/// A group voice channel user message (abbreviated 0x01, extended 0x21) says whose call the channel is carrying:
/// <see cref="Talkgroup"/>, <see cref="Source"/> and <see cref="ServiceOptions"/> (0x80 emergency, 0x40 encrypted); in
/// any other message they are 0.
/// </summary>
public sealed record P25MacMessage(int Opcode, int Mfid, byte[] Octets)
{
    /// <summary>Group voice channel user, abbreviated and extended (sdrtrunk MacOpcode TDMA_01, TDMA_21; op25 tk_p25.py decode_tdma_msg).</summary>
    public const int GroupVoiceUser = 0x01, GroupVoiceUserExtended = 0x21;

    public bool IsGroupVoiceUser => Opcode is GroupVoiceUser or GroupVoiceUserExtended && Octets.Length >= 7;
    // The fields: opcode, service options, group (2 octets), source (3); the extended form then the source's WACN (20 bits),
    // system (12) and radio (24) (sdrtrunk MacStructureVoiceService SERVICE_OPTIONS octet 2, MacStructureGroupVoiceService
    // GROUP_ADDRESS octet 3, GroupVoiceChannelUserAbbreviated SOURCE_ADDRESS octet 5, GroupVoiceChannelUserExtended bits
    // 32-111; op25 tk_p25.py decode_tdma_msg: opts msg[1], ga msg[2:4], sa msg[4:7], suid msg[7:14])
    public int ServiceOptions => IsGroupVoiceUser ? Octets[1] : 0;
    public int Talkgroup => IsGroupVoiceUser ? Octets[2] << 8 | Octets[3] : 0;
    public int Source => IsGroupVoiceUser ? Octets[4] << 16 | Octets[5] << 8 | Octets[6] : 0;
}

/// <summary>
/// A Phase 2 MAC PDU from a FACCH (18 octets) or SACCH (21 octets), its CRC already checked (p25p2rx.c), read into
/// numbers. PUSH-TO-TALK starts a call and says whose and whether it is encrypted; END-PUSH-TO-TALK ends it; SIGNAL,
/// IDLE, ACTIVE and HANGTIME carry messages (<see cref="Messages"/>), among them, during and after a call, the group
/// voice channel user message that tells a receiver joining late whose call it is.
///
/// Fields that do not apply to the type are 0. Where each one sits was read in op25 (p25p2_tdma.cc handle_mac_ptt,
/// handle_mac_end_ptt, process_mac_pdu; tk_p25.py decode_tdma_ptt, decode_tdma_endptt) and sdrtrunk
/// (phase2/message/mac/MacMessage, MacPduType, structure/PushToTalk, EndPushToTalk), which agree.
/// </summary>
public sealed record P25MacPdu
{
    /// <summary>The algorithm id of a clear call (sdrtrunk reference/Encryption UNENCRYPTED 0x80; op25 p25p2_tdma ess_algid 0x80).</summary>
    public const int Clear = 0x80;

    /// <summary>The type, 0 to 7 (<see cref="P25MacType"/>; 5 and 7 are reserved).</summary>
    public int Type { get; init; }
    /// <summary>The three bits after the type: PTT and ACTIVE give the bursts to the next 4V (not used here).</summary>
    public int Offset { get; init; }

    // PUSH-TO-TALK: the message indicator (octets 1-9), algorithm (10), key id (11-12), source (13-15), group (16-17)
    // (op25 handle_mac_ptt ess_mi byte_buf[1..9], ess_algid byte_buf[10], ess_keyid byte_buf[11..12], srcaddr [13..15],
    // grpaddr [16..17]; sdrtrunk PushToTalk MESSAGE_INDICATOR bits 8-79, ALGORITHM_ID octet 11, KEY_ID octet 12,
    // SOURCE_ADDRESS octet 14, GROUP_ADDRESS octet 17, its octets counted from 1). END-PUSH-TO-TALK: the color code
    // (the NAC, 12 bits from bit 12), source and group in the same places (op25 handle_mac_end_ptt colorcd; sdrtrunk
    // EndPushToTalk COLOR_CODE)
    public byte[] Mi { get; init; } = [];
    public int AlgId { get; init; } = Clear;
    public int KeyId { get; init; }
    public int Source { get; init; }
    public int Talkgroup { get; init; }
    public int ColorCode { get; init; }

    /// <summary>SIGNAL, IDLE, ACTIVE, HANGTIME: the messages, null information left out.</summary>
    public IReadOnlyList<P25MacMessage> Messages { get; init; } = [];

    public P25MacType Kind => (P25MacType)Type;
    /// <summary>A PUSH-TO-TALK whose algorithm is not the clear one.</summary>
    public bool Encrypted => Type == (int)P25MacType.PushToTalk && AlgId != Clear;
    /// <summary>The first group voice channel user message, if any.</summary>
    public P25MacMessage? GroupVoiceUser => Messages.FirstOrDefault(m => m.IsGroupVoiceUser);

    /// <summary>A FACCH's or SACCH's octets (18 or 21, the CRC left off); null when too short.</summary>
    public static P25MacPdu? Parse(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 18) return null;
        int type = pdu[0] >> 5, offset = (pdu[0] >> 2) & 7;
        switch ((P25MacType)type)
        {
            case P25MacType.PushToTalk:
                return new P25MacPdu
                {
                    Type = type, Offset = offset, Mi = pdu[1..10].ToArray(), AlgId = pdu[10], KeyId = pdu[11] << 8 | pdu[12],
                    Source = pdu[13] << 16 | pdu[14] << 8 | pdu[15], Talkgroup = pdu[16] << 8 | pdu[17],
                };
            case P25MacType.EndPushToTalk:
                return new P25MacPdu
                {
                    Type = type, Offset = offset, ColorCode = (pdu[1] & 0x0F) << 8 | pdu[2],
                    Source = pdu[13] << 16 | pdu[14] << 8 | pdu[15], Talkgroup = pdu[16] << 8 | pdu[17],
                };
            case P25MacType.Signal:
                // op25 handle_mac_signal reads the messages of the first 18 octets only
                return new P25MacPdu { Type = type, Offset = offset, Messages = Walk(pdu[..18]) };
            case P25MacType.Idle:
            case P25MacType.Active:
            case P25MacType.Hangtime:
                return new P25MacPdu { Type = type, Offset = offset, Messages = Walk(pdu) };
            default:
                return new P25MacPdu { Type = type, Offset = offset };
        }
    }

    // The messages after the PDU's first octet, each as long as its opcode says (op25 p25p2_tdma::decode_mac_msg). A message of
    // unknown length ends the walk, and so does null information (opcode 0), which fills the rest.
    private static List<P25MacMessage> Walk(ReadOnlySpan<byte> pdu)
    {
        var list = new List<P25MacMessage>();
        int len = pdu.Length;
        for (int at = 1; at < len;)
        {
            int remaining = len - at, op = pdu[at], b1b2 = op >> 6, mfid = 0, n;
            switch (op)
            {
                case 0x00: n = remaining; break;                                            // null information
                case 0x08: n = at + 1 < len ? pdu[at + 1] & 0x3F : 0; break;                // null avoid zero bias
                case 0x11: n = at + 1 < len ? ((pdu[at + 1] & 3) + 1) * 2 + 2 : 0; break;   // indirect group paging without priority
                case 0x12: n = at + 1 < len ? ((pdu[at + 1] & 3) + 1) * 3 + 2 : 0; break;   // individual paging with priority
                default:
                    n = 0;
                    // A manufacturer's message has its manufacturer and length after the opcode, but not always the length
                    if (b1b2 == 2 && at + 2 < len)
                    {
                        mfid = pdu[at + 1];
                        n = pdu[at + 2] & 0x3F;
                    }
                    if (n == 0) n = MessageLength[op];
                    break;
            }
            if (op != 0x00 && op != 0x08 && n != 0)
                list.Add(new P25MacMessage(op, mfid, pdu.Slice(at, Math.Min(n, remaining)).ToArray()));
            if (n == 0) break;
            at += n;
        }
        return list;
    }

    // Octets of each message by its opcode, 0 not known (op25 p25p2_tdma.cc mac_msg_len). sdrtrunk's MacOpcode lengths agree for
    // those looked at: 0x01 7, 0x02 8, 0x03 7, 0x05 16, 0x21 14, 0x22 15, 0x25 15, 0x30 5, 0x31 7, 0x45 8, 0x4A 9, 0xC5 12.
    private static readonly byte[] MessageLength =
    [
        0, 7, 8, 7, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 14, 15, 0, 0, 15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        9, 7, 9, 0, 9, 8, 9, 0, 10, 10, 9, 0, 10, 0, 0, 0,
        0, 0, 0, 0, 9, 7, 0, 0, 10, 0, 7, 0, 10, 8, 14, 7,
        9, 9, 0, 0, 9, 0, 0, 9, 10, 0, 7, 10, 10, 7, 0, 9,
        9, 29, 9, 9, 9, 9, 10, 13, 9, 9, 9, 11, 9, 9, 0, 0,
        8, 0, 0, 7, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        16, 0, 0, 11, 13, 11, 11, 11, 10, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        11, 0, 0, 8, 15, 12, 15, 32, 12, 12, 0, 27, 14, 29, 29, 32,
        0, 0, 0, 0, 0, 0, 9, 0, 14, 29, 11, 27, 14, 0, 40, 11,
        28, 0, 0, 14, 17, 14, 0, 0, 16, 8, 11, 0, 13, 19, 0, 0,
        0, 0, 16, 14, 0, 0, 12, 0, 22, 0, 11, 13, 11, 0, 15, 0,
    ];

    /// <summary>An ESS (p25p2rx.c's 12 octets): algorithm, key id, message indicator (op25 handle_4V2V_ess; sdrtrunk EncryptionSynchronizationSequence).</summary>
    public static (int AlgId, int KeyId) Ess(ReadOnlySpan<byte> ess) => ess.Length < 3 ? (Clear, 0) : (ess[0], ess[1] << 8 | ess[2]);
}
