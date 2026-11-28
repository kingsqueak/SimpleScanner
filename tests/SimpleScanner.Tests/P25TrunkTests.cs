// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Trunk;

namespace SimpleScanner.Tests;

/// <summary>
/// A P25 control channel's messages reaching C# as numbers, against the generated test control channel
/// (Data/p25_trunk_control_24k.raw, from the script Data/p25_trunk_control.txt; see Data/README.md).
/// </summary>
public class P25TrunkTests
{
    private const double CountsPerKhz = 6553.6;

    /// <summary>A test file as discriminator samples in units of 2.5 kHz deviation, at 24 kHz.</summary>
    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / CountsPerKhz * 1000 / 2500);
        return x;
    }

    private static List<P25Tsbk> Decode(float[] disc, out long units)
    {
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        p25.Feed24k(disc);
        p25.ReadEvents();
        units = p25.Units;
        return p25.ReadTsbks();
    }

    private static List<P25Tsbk> Fixture() => Decode(Disc("p25_trunk_control_24k.raw"), out _);

    private static P25Tsbk One(List<P25Tsbk> all, Func<P25Tsbk, bool> which) => Assert.Single(all, m => which(m));

    [Fact]
    public void TheLibraryAndThisSideAgreeOnTheEventSize()
    {
        Assert.Equal(P25Decoder.EventSize, P25Decoder.NativeEventSize);
        Assert.Equal(220, P25Decoder.EventSize);
    }

    [Fact]
    public void EveryMessageOfTheScriptArrivesInOrder()
    {
        var all = Decode(Disc("p25_trunk_control_24k.raw"), out long units);
        Assert.Equal(11, units);                   // seven TSDUs and four packets
        Assert.All(all, m => Assert.Equal(0x8A1, m.Nac));
        Assert.Equal(
            new[] { 0x3A, 0x3B, 0x3D, 0x34, 0x33, 0x38, 0x00, 0x02, 0x03, 0x00, 0x00, 0x04, 0x3C, 0x39, 0x29, 0x06, 0x00, 0x00, 0x00,
             0x00, 0x3B, 0x3A, 0x3C },
            all.Select(m => m.Opcode).ToArray());
        Assert.Equal(19, all.Count(m => !m.MultiBlock));
        Assert.Equal(4, all.Count(m => m.MultiBlock));
        // The last block flag, as each TSDU ended (the sixth ends with the block whose CRC fails, which says nothing)
        Assert.Equal(new[] { 2, 5, 8, 11, 14, 18 }, all.Select((m, i) => (m, i)).Where(x => !x.m.MultiBlock && x.m.Last).Select(x => x.i).ToArray());
    }

    [Fact]
    public void StatusBroadcastsGiveTheSite()
    {
        var all = Fixture();
        var rfss = One(all, m => m.Kind == P25TsbkKind.Rfss && !m.MultiBlock);
        Assert.Equal((0x8A6, 1, 1, 0x154D, 0x70, 1, 1), (rfss.SystemId, rfss.Rfss, rfss.Site, rfss.Channel, rfss.ServiceClass, rfss.Lra, rfss.SiteFlags));
        var net = One(all, m => m.Kind == P25TsbkKind.Network && !m.MultiBlock);
        Assert.Equal((0xBEE00, 0x8A6, 0x154D, 0x70, 1), (net.Wacn, net.SystemId, net.Channel, net.ServiceClass, net.Lra));
        var adj = One(all, m => m.Kind == P25TsbkKind.Adjacent && !m.MultiBlock);
        Assert.Equal((0x8A6, 1, 2, 0x1481, 0x70, 1, 3), (adj.SystemId, adj.Rfss, adj.Site, adj.Channel, adj.ServiceClass, adj.Lra, adj.SiteFlags));
        var srv = One(all, m => m.Kind == P25TsbkKind.SystemService);
        Assert.Equal((0x2A0000, 0x3F0000, 3), (srv.ServicesAvailable, srv.ServicesSupported, srv.RequestPriority));
    }

    [Fact]
    public void SecondaryControlChannelsInBothForms()
    {
        var all = Fixture();
        var sccb = One(all, m => m.Opcode == 0x39);
        Assert.Equal(P25TsbkKind.Secondary, sccb.Kind);
        Assert.Equal((1, 1, 0x14AD, 0x70, 0x1481, 0x71), (sccb.Rfss, sccb.Site, sccb.Channel, sccb.ServiceClass, sccb.Channel2, sccb.ServiceClass2));
        var exp = One(all, m => m.Opcode == 0x29);
        Assert.Equal(P25TsbkKind.Secondary, exp.Kind);
        Assert.Equal((1, 1, 0x1455, 0x72, 0), (exp.Rfss, exp.Site, exp.Channel, exp.ServiceClass, exp.Channel2));
    }

    [Fact]
    public void ChannelIdentifiersInAllThreeForms()
    {
        var all = Fixture();
        var iden = One(all, m => m.Opcode == 0x3D);
        Assert.Equal(P25TsbkKind.Identifier, iden.Kind);
        Assert.Equal((1, 0x3D, 851_006_250L, 6250, -45_000_000L, 12500, 1, false, 0),
            (iden.IdenId, iden.IdenForm, iden.BaseHz, iden.SpacingHz, iden.TxOffsetHz, iden.BandwidthHz, iden.Slots, iden.Tdma, iden.ChannelType));
        var vu = One(all, m => m.Opcode == 0x34);
        Assert.Equal((2, 0x34, 152_007_500L, 6250, 600_000L, 12500, 1, false),
            (vu.IdenId, vu.IdenForm, vu.BaseHz, vu.SpacingHz, vu.TxOffsetHz, vu.BandwidthHz, vu.Slots, vu.Tdma));
        var tdma = One(all, m => m.Opcode == 0x33);
        Assert.Equal((3, 0x33, 851_006_250L, 6250, -45_000_000L, 0, 2, true, 3),
            (tdma.IdenId, tdma.IdenForm, tdma.BaseHz, tdma.SpacingHz, tdma.TxOffsetHz, tdma.BandwidthHz, tdma.Slots, tdma.Tdma, tdma.ChannelType));
    }

    [Fact]
    public void GrantsAndUpdatesInEveryForm()
    {
        var all = Fixture();
        var g = One(all, m => m.Opcode == 0x00 && m.Talkgroup == 1001);
        Assert.Equal((P25TsbkKind.Grant, 0x11E9, 1234567, true, false, false, false), (g.Kind, g.Channel, g.Source, g.HasServiceOptions, g.Encrypted, g.Emergency, g.UnitToUnit));
        var u = One(all, m => m.Opcode == 0x02);
        Assert.Equal((0x11E9, 1001, 0x140D, 2002, false, 0), (u.Channel, u.Talkgroup, u.Channel2, u.Talkgroup2, u.HasServiceOptions, u.Source));
        var x = One(all, m => m.Opcode == 0x03);
        Assert.Equal((0x12A5, 3003, true, true, false), (x.Channel, x.Talkgroup, x.HasServiceOptions, x.Emergency, x.Encrypted));
        var t = One(all, m => m.Opcode == 0x00 && m.Talkgroup == 4004);
        Assert.Equal((0x381B, 7654321), (t.Channel, t.Source));
        var e = One(all, m => m.Opcode == 0x00 && m.Talkgroup == 5005);
        Assert.Equal((0x140D, 2345678, true, false), (e.Channel, e.Source, e.Encrypted, e.Emergency));
        var vhf = One(all, m => m.Opcode == 0x00 && m.Talkgroup == 6006);
        Assert.Equal((0x2064, 42), (vhf.Channel, vhf.Source));
        foreach (int op in new[] { 0x04, 0x06 })
        {
            var uu = One(all, m => m.Opcode == op);
            Assert.Equal((P25TsbkKind.Grant, true, 0x12A5, 1111111, 2222222, 0), (uu.Kind, uu.UnitToUnit, uu.Channel, uu.Target, uu.Source, uu.Talkgroup));
        }
    }

    [Fact]
    public void ABlockWhoseCrcFailsComesWithItsOctetsOnly()
    {
        var all = Fixture();
        var bad = Assert.Single(all, m => !m.CrcOk);
        Assert.Equal((P25TsbkKind.Other, 0, 0, 0), (bad.Kind, bad.Talkgroup, bad.Channel, bad.Opcode));
        // The octets as sent: last block, opcode 0, MFID 0, then the grant's arguments (talkgroup 7007 = 0x1B5F)
        Assert.Equal(12, bad.Raw.Length);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00, 0x11, 0xE9, 0x1B, 0x5F, 0x00, 0x00, 0x01 }, bad.Raw[..10]);
        Assert.DoesNotContain(all, m => m.Talkgroup == 7007);
        Assert.Equal(22, all.Count(m => m.CrcOk));
    }

    [Fact]
    public void AManufacturersBlockIsPassedOnAsOctets()
    {
        var mfr = One(Fixture(), m => m.Mfid == 0x90);
        Assert.Equal((P25TsbkKind.Manufacturer, true, true), (mfr.Kind, mfr.CrcOk, mfr.Last));
        Assert.Equal(new byte[] { 0x80, 0x90, 0x0F, 0xA1, 0x03, 0xE9, 0x07, 0xD2, 0x00, 0x00 }, mfr.Raw[..10]);
        Assert.Empty(mfr.RawData);
    }

    [Fact]
    public void MultiBlockMessages()
    {
        var all = Fixture();
        var g = One(all, m => m.MultiBlock && m.Kind == P25TsbkKind.Grant);
        Assert.Equal((0x12A5, 8008, 3456789, true, true, false, 0, 0), (g.Channel, g.Talkgroup, g.Source, g.HasServiceOptions, g.Emergency, g.Encrypted, g.Channel2, g.Talkgroup2));
        // The header and the first data block as sent: alternate format (0x17), outbound, trunking SAP 61, one block
        Assert.Equal(0x37, g.Raw[0]);
        Assert.Equal(61, g.Raw[1] & 0x3F);
        Assert.Equal(0x81, g.Raw[6]);
        Assert.Equal(12, g.RawData.Length);
        Assert.Equal(new byte[] { 0, 0, 0x12, 0xA5, 0x12, 0xA5, 0x1F, 0x48 }, g.RawData[..8]);
        var net = One(all, m => m.MultiBlock && m.Kind == P25TsbkKind.Network);
        Assert.Equal((0xBEE00, 0x8A6, 0x154D, 0x70, 1), (net.Wacn, net.SystemId, net.Channel, net.ServiceClass, net.Lra));
        var rfss = One(all, m => m.MultiBlock && m.Kind == P25TsbkKind.Rfss);
        Assert.Equal((0x8A6, 1, 1, 0x154D, 1), (rfss.SystemId, rfss.Rfss, rfss.Site, rfss.Channel, rfss.Lra));
        var adj = One(all, m => m.MultiBlock && m.Kind == P25TsbkKind.Adjacent);
        Assert.Equal((0x8A6, 1, 3, 0x1455, 1), (adj.SystemId, adj.Rfss, adj.Site, adj.Channel, adj.Lra));
        Assert.All(all.Where(m => m.MultiBlock), m => Assert.True(m.CrcOk && m.Last));
    }

    [Fact]
    public void TheMessagesSurviveNoiseAndAFrequencyOffset()
    {
        var disc = Disc("p25_trunk_control_24k.raw");
        var rng = new Random(11);
        for (int i = 0; i < disc.Length; i++)
        {
            // 300 Hz off frequency, and noise of about 250 Hz rms
            double g = rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5;
            disc[i] += (float)(300 / 2500.0 + g * 0.2);
        }
        var all = Decode(disc, out long units);
        Assert.Equal(11, units);
        Assert.Equal(22, all.Count(m => m.CrcOk));
    }

    [Fact]
    public void TheOldControlChannelStillGivesItsMessages()
    {
        var all = Decode(Disc("p25_control_24k.raw"), out long units);
        Assert.Equal(40, units);
        Assert.All(all, m => Assert.True(m.CrcOk));
        Assert.Contains(all, m => m.Kind == P25TsbkKind.Network && m.Wacn == 0xBEE00 && m.SystemId == 0x123);
    }

    [Fact]
    public void TheSiteStateTurnsTheControlChannelIntoCalls()
    {
        var state = new TrunkSiteState();
        state.Seed(new TrunkSiteDef(1, 1, "County", "Site 1", 0x8A1, 0x8A6, 0xBEE00, 1, 1, [859_487_500], []));
        var all = Fixture();
        for (int i = 0; i < all.Count; i++) state.OnTsbk(all[i], i * 0.01);
        Assert.True(state.MatchesSite);
        var grants = state.TakeGrants(0.3);
        // Taken all at once, each channel gives the call last granted there: 0x140D went to 2002, then 5005;
        // 0x12A5 to 3003, then a unit to unit call, then 8008.
        Assert.Equal(
            new[]
            {
                new TrunkGrant(1001, 1234567, 854_062_500, 0, false, false, false, false, 0x11E9),
                new TrunkGrant(4004, 7654321, 857_487_500, 1, true, false, false, false, 0x381B),
                new TrunkGrant(5005, 2345678, 857_487_500, 0, false, true, false, false, 0x140D),
                new TrunkGrant(6006, 42, 152_632_500, 0, false, false, false, false, 0x2064),
                new TrunkGrant(8008, 3456789, 855_237_500, 0, false, false, true, false, 0x12A5),
            },
            grants);
        Assert.Equal(new double[] { 859_487_500, 858_487_500, 858_212_500, 857_937_500 }, state.ControlHz);
        Assert.Contains(new TrunkAdjacentSite(0x8A6, 1, 2, 0x1481, 858_212_500, 3), state.Adjacent);
        Assert.Contains(new TrunkAdjacentSite(0x8A6, 1, 3, 0x1455, 857_937_500, 0), state.Adjacent);
    }
}
