// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Digital;
using SimpleScanner.Trunk;

namespace SimpleScanner.Tests;

/// <summary>The site model alone: messages in, frequencies and calls out.</summary>
public class TrunkSiteStateTests
{
    // An 800 MHz site as two local systems broadcast it: 851.00625 MHz + n x 6.25 kHz
    private static P25Tsbk Iden800 => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Identifier, Opcode = 0x3D, IdenId = 1, IdenForm = 0x3D, BaseHz = 851_006_250,
        SpacingHz = 6250, TxOffsetHz = -45_000_000, BandwidthHz = 12500, Slots = 1,
    };
    private static P25Tsbk IdenVhf => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Identifier, Opcode = 0x34, IdenId = 2, IdenForm = 0x34, BaseHz = 152_007_500,
        SpacingHz = 6250, TxOffsetHz = 600_000, BandwidthHz = 12500, Slots = 1,
    };
    private static P25Tsbk IdenTdma => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Identifier, Opcode = 0x33, IdenId = 3, IdenForm = 0x33, BaseHz = 851_006_250,
        SpacingHz = 6250, TxOffsetHz = -45_000_000, Slots = 2, Tdma = true, ChannelType = 3,
    };

    private static P25Tsbk Grant(int ch, int tg, int src = 0, int svc = 0) => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Grant, Opcode = 0x00, Channel = ch, Talkgroup = tg, Source = src,
        ServiceOptions = svc, HasServiceOptions = true,
    };

    private static P25Tsbk Update(int ch, int tg, int ch2, int tg2) => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Grant, Opcode = 0x02, Channel = ch, Talkgroup = tg, Channel2 = ch2, Talkgroup2 = tg2,
    };

    private static TrunkSiteState Site(params P25Tsbk[] first)
    {
        var s = new TrunkSiteState();
        s.Seed(new TrunkSiteDef(1, 1, "Sys", "Site", 0x8A1, 0x8A6, 0xBEE00, 1, 1, [859_487_500], [854_062_500]));
        foreach (var m in first) s.OnTsbk(m, 0);
        return s;
    }

    [Theory]
    [InlineData(0x154D, 859_487_500)]                    // the control channels of the local systems
    [InlineData(0x14AD, 858_487_500)]
    [InlineData(0x1481, 858_212_500)]
    [InlineData(0x1455, 857_937_500)]
    [InlineData(0x11E9, 854_062_500)]                    // voice channels
    [InlineData(0x140D, 857_487_500)]
    [InlineData(0x1000, 851_006_250)]
    public void ChannelsOf800MHz(int ch, double hz)
    {
        var s = Site(Iden800);
        Assert.True(s.TryResolve(ch, out double got, out int slot, out bool tdma));
        Assert.Equal(hz, got);
        Assert.Equal(0, slot);
        Assert.False(tdma);
    }

    [Fact]
    public void ChannelsOfVhf()
    {
        var s = Site(IdenVhf);
        Assert.True(s.TryResolve(0x2064, out double hz, out int slot, out bool tdma));
        Assert.Equal((152_632_500d, 0, false), (hz, slot, tdma));
        Assert.True(s.TryResolve(0x2000, out hz, out _, out _));
        Assert.Equal(152_007_500d, hz);
        var iden = s.Idens[2];
        Assert.Equal((0x34, 600_000L, 12500), (iden.Form, iden.TxOffsetHz, iden.BandwidthHz));
    }

    [Fact]
    public void TdmaChannelsShareACarrierBetweenTwoSlots()
    {
        var s = Site(IdenTdma);
        Assert.True(s.TryResolve(0x381A, out double hz0, out int slot0, out bool tdma0));
        Assert.True(s.TryResolve(0x381B, out double hz1, out int slot1, out bool tdma1));
        Assert.Equal((857_487_500d, 0, true), (hz0, slot0, tdma0));
        Assert.Equal((857_487_500d, 1, true), (hz1, slot1, tdma1));
        Assert.True(s.TryResolve(0x3000, out double hz, out int slot, out _));
        Assert.Equal((851_006_250d, 0), (hz, slot));
    }

    [Fact]
    public void AnUnknownIdentifierResolvesNothing()
    {
        var s = Site(Iden800);
        Assert.False(s.TryResolve(0x2064, out _, out _, out _));
        Assert.False(s.TryResolve(0xF001, out _, out _, out _));
    }

    [Fact]
    public void AGrantWaitsForItsIdentifier()
    {
        var s = Site();
        s.OnTsbk(Grant(0x11E9, 1001, 1234567), 10.0);
        Assert.Empty(s.TakeGrants(10.0));
        Assert.Empty(s.TakeGrants(11.0));
        s.OnTsbk(Iden800, 11.5);
        var g = Assert.Single(s.TakeGrants(11.5));
        Assert.Equal(new TrunkGrant(1001, 1234567, 854_062_500, 0, false, false, false, false, 0x11E9), g);
        Assert.Equal(0, s.Dropped);
    }

    [Fact]
    public void AGrantWhoseIdentifierNeverComesIsDroppedAfterTwoSeconds()
    {
        var s = Site(Iden800);
        s.OnTsbk(Grant(0x2064, 6006), 0);
        Assert.Empty(s.TakeGrants(1.9));
        Assert.Equal(0, s.Dropped);
        Assert.Empty(s.TakeGrants(2.1));
        Assert.Equal(1, s.Dropped);
        // Its identifier after that: the dropped grant does not come back on its own
        s.OnTsbk(IdenVhf, 2.2);
        Assert.Empty(s.TakeGrants(2.2));
    }

    [Fact]
    public void RepeatsAndUpdatesOfACallGiveOneGrant()
    {
        var s = Site(Iden800);
        var grants = new List<TrunkGrant>();
        // A call kept up for ten seconds: its grant, then an update every half second
        s.OnTsbk(Grant(0x11E9, 1001, 1234567), 0);
        grants.AddRange(s.TakeGrants(0));
        for (double t = 0.5; t <= 10; t += 0.5)
        {
            s.OnTsbk(t % 2 == 0 ? Grant(0x11E9, 1001, 1234567) : Update(0x11E9, 1001, 0x11E9, 1001), t);
            grants.AddRange(s.TakeGrants(t));
        }
        Assert.Single(grants);
    }

    [Fact]
    public void AnUpdateFirstThenTheGrantFillsInNothingNew()
    {
        var s = Site(Iden800);
        s.OnTsbk(Update(0x11E9, 1001, 0x11E9, 1001), 0);
        s.OnTsbk(Grant(0x11E9, 1001, 1234567, 0x80), 0.1);
        // Taken together: one call, with what the grant added
        var g = Assert.Single(s.TakeGrants(0.1));
        Assert.Equal((1001, 1234567, true), (g.Talkgroup, g.Source, g.Emergency));
    }

    [Fact]
    public void AnotherTalkgroupOnTheChannelOrTheSameOneLaterIsANewCall()
    {
        var s = Site(Iden800);
        s.OnTsbk(Grant(0x11E9, 1001), 0);
        Assert.Single(s.TakeGrants(0));
        s.OnTsbk(Grant(0x11E9, 2002), 1);
        Assert.Equal(2002, Assert.Single(s.TakeGrants(1)).Talkgroup);
        // 2002 again after a silence longer than the repeat window: a new call
        s.OnTsbk(Grant(0x11E9, 2002), 3.5);
        Assert.Equal(2002, Assert.Single(s.TakeGrants(3.5)).Talkgroup);
        // The same talkgroup on two slots of a TDMA carrier: two calls
        s.OnTsbk(IdenTdma, 4);
        s.OnTsbk(Grant(0x381A, 3003), 4);
        s.OnTsbk(Grant(0x381B, 3003), 4);
        Assert.Equal(new[] { 0, 1 }, s.TakeGrants(4).Select(g => g.Slot).ToArray());
    }

    [Fact]
    public void AnUpdateNamingTwoCallsGivesBoth()
    {
        var s = Site(Iden800);
        s.OnTsbk(Update(0x11E9, 1001, 0x140D, 2002), 0);
        var grants = s.TakeGrants(0);
        Assert.Equal(new[] { (1001, 854_062_500d), (2002, 857_487_500d) }, grants.Select(g => (g.Talkgroup, g.Hz)).ToArray());
        Assert.All(grants, g => Assert.Equal(0, g.Source));
    }

    [Fact]
    public void EncryptedEmergencyAndUnitToUnitGrants()
    {
        var s = Site(Iden800);
        s.OnTsbk(Grant(0x11E9, 5005, 2345678, 0x40), 0);
        s.OnTsbk(Grant(0x140D, 3003, 0, 0xC0), 0);
        s.OnTsbk(new P25Tsbk
        {
            CrcOk = true, Kind = P25TsbkKind.Grant, Opcode = 0x04, UnitToUnit = true, Channel = 0x12A5, Target = 1111111, Source = 2222222,
        }, 0);
        var g = s.TakeGrants(0);
        Assert.Equal((true, false, false), (g[0].Encrypted, g[0].Emergency, g[0].Unit));
        Assert.Equal((true, true), (g[1].Encrypted, g[1].Emergency));
        Assert.Equal((1111111, 2222222, true, 855_237_500d), (g[2].Talkgroup, g[2].Source, g[2].Unit, g[2].Hz));
    }

    [Fact]
    public void AMessageWhoseCrcFailedIsIgnored()
    {
        var s = Site();
        s.OnTsbk(Iden800 with { CrcOk = false }, 0);
        Assert.Empty(s.Idens);
        Assert.Equal(0, s.Messages);
    }

    [Fact]
    public void TheSiteIdentityIsComparedWithTheSiteSeeded()
    {
        var s = Site();
        Assert.Null(s.MatchesSite);
        s.OnTsbk(new P25Tsbk { CrcOk = true, Nac = 0x8A1, Kind = P25TsbkKind.Rfss, SystemId = 0x8A6, Rfss = 1, Site = 1, Channel = 0x154D }, 0);
        Assert.True(s.MatchesSite);
        s.OnTsbk(new P25Tsbk { CrcOk = true, Nac = 0x8A1, Kind = P25TsbkKind.Network, Wacn = 0xBEE01, SystemId = 0x8A6 }, 0);
        Assert.False(s.MatchesSite);
        // Seeding again forgets what was heard
        s.Seed(s.Def!);
        Assert.Null(s.MatchesSite);
        Assert.Null(s.Wacn);
    }

    [Fact]
    public void ControlChannelsFromTheListAndTheAir()
    {
        var s = Site();
        s.OnTsbk(new P25Tsbk { CrcOk = true, Kind = P25TsbkKind.Rfss, SystemId = 0x8A6, Rfss = 1, Site = 1, Channel = 0x154D }, 0);
        s.OnTsbk(new P25Tsbk { CrcOk = true, Kind = P25TsbkKind.Secondary, Opcode = 0x39, Channel = 0x14AD, Channel2 = 0x1481 }, 0);
        s.OnTsbk(new P25Tsbk { CrcOk = true, Kind = P25TsbkKind.Secondary, Opcode = 0x29, Channel = 0x1455 }, 0);
        s.OnTsbk(new P25Tsbk { CrcOk = true, Kind = P25TsbkKind.Adjacent, SystemId = 0x8A6, Rfss = 1, Site = 2, Channel = 0x1481, SiteFlags = 2 }, 0);
        // Without the identifier only the listed one is known; the adjacent site has no frequency yet
        Assert.Equal(new double[] { 859_487_500 }, s.ControlHz);
        Assert.Equal(0, Assert.Single(s.Adjacent).Hz);
        s.OnTsbk(Iden800, 0);
        Assert.Equal(new double[] { 859_487_500, 858_487_500, 858_212_500, 857_937_500 }, s.ControlHz);
        Assert.Equal(new TrunkAdjacentSite(0x8A6, 1, 2, 0x1481, 858_212_500, 2), Assert.Single(s.Adjacent));
    }

    [Fact]
    public void ASiteDefFromTheStore()
    {
        var system = new TrunkSystem(7, 1234, "County", "Project 25 Phase II", "", "", 0x8A6, 0xBEE00, "", DateTime.UnixEpoch);
        var site = new TrunkSite(9, 7, 55, 1, 1, "Site 1", "", 0x8A1, "", false, null, null,
        [
            new TrunkSiteFreq(859_487_500, null, TrunkFreqUse.Control), new TrunkSiteFreq(858_487_500, null, TrunkFreqUse.Alternate),
            new TrunkSiteFreq(854_062_500, null, TrunkFreqUse.Voice),
        ]);
        var def = TrunkSiteDef.From(system, site);
        Assert.Equal((7L, 9L, "County", "Site 1", 0x8A1, 0x8A6, 0xBEE00, 1, 1),
            (def.SystemId, def.SiteId, def.SystemName, def.SiteName, def.Nac, def.SysId, def.Wacn, def.Rfss, def.Site));
        Assert.Equal(new double[] { 859_487_500, 858_487_500 }, def.ControlHz);
        Assert.Equal(new double[] { 854_062_500 }, def.VoiceHz);
    }
}
