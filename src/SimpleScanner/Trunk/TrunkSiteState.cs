// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Digital;

namespace SimpleScanner.Trunk;

/// <summary>
/// A channel identifier as a site broadcast it: channel n of it is at <see cref="BaseHz"/> + n x
/// <see cref="SpacingHz"/>, or with more than one slot to a carrier, carrier n / <see cref="Slots"/>, slot
/// n mod <see cref="Slots"/>. <see cref="Form"/> is the opcode it came in (0x3D, 0x34 VHF/UHF, 0x33 TDMA).
/// </summary>
public sealed record TrunkIden(int Id, int Form, long BaseHz, int SpacingHz, long TxOffsetHz, int BandwidthHz, int Slots, int ChannelType);

/// <summary>A neighbouring site the control channel names. <see cref="Hz"/> is 0 while its channel's identifier is not known.</summary>
public sealed record TrunkAdjacentSite(int SystemId, int Rfss, int Site, int Channel, double Hz, int Flags);

/// <summary>
/// What a site's control channel has said, as numbers: its identity, its channel identifiers, its control
/// channels and neighbours, and the calls it grants, made "talkgroup on frequency and slot". Pure: it is fed
/// the decoded messages and the time, and touches nothing else. Use from one thread.
/// </summary>
public sealed class TrunkSiteState
{
    /// <summary>How long a grant waits for its channel's identifier before it is dropped.</summary>
    public const double HoldSeconds = 2;
    /// <summary>A grant of the same talkgroup on the same channel within this of the last is the same call.</summary>
    public const double RepeatSeconds = 2;

    private sealed class Held
    {
        public int Talkgroup, Source, Channel;
        public bool Unit, Encrypted, Emergency, Delivered;
        public double First, Last;
    }

    private readonly Dictionary<int, TrunkIden> _idens = [];
    // By channel number as sent (it holds the slot too): the call last granted there.
    private readonly Dictionary<int, Held> _held = [];
    private readonly List<int> _secondary = [];
    private readonly Dictionary<(int SysId, int Rfss, int Site), (int Channel, int Flags)> _adjacent = [];
    private int _controlChannel = -1;

    /// <summary>The site this state was seeded with; null before <see cref="Seed"/>.</summary>
    public TrunkSiteDef? Def { get; private set; }
    // As heard on the air; null until then.
    public int? Nac { get; private set; }
    public int? Wacn { get; private set; }
    public int? SystemId { get; private set; }
    public int? Rfss { get; private set; }
    public int? Site { get; private set; }
    /// <summary>Messages taken in (those whose CRC held).</summary>
    public long Messages { get; private set; }
    /// <summary>Grants dropped because their channel's identifier did not come in time.</summary>
    public int Dropped { get; private set; }

    public IReadOnlyDictionary<int, TrunkIden> Idens => _idens;

    /// <summary>
    /// False when what the air says (NAC, system, WACN, RFSS and site) disagrees with the site seeded; true when
    /// something agrees and nothing disagrees; null before anything to compare has been heard.
    /// </summary>
    public bool? MatchesSite
    {
        get
        {
            if (Def is null) return null;
            bool any = false;
            if (!Same(Def.Nac, Nac, ref any) || !Same(Def.SysId, SystemId, ref any) || !Same(Def.Wacn, Wacn, ref any)) return false;
            if (Def.Site != 0 && !Same(Def.Site, Site, ref any)) return false;
            if (Def.Rfss != 0 && !Same(Def.Rfss, Rfss, ref any)) return false;
            return any ? true : null;
        }
    }

    private static bool Same(int? want, int? heard, ref bool any)
    {
        if (want is null || heard is null) return true;
        any = true;
        return want == heard;
    }

    /// <summary>
    /// The control channels in hertz: those listed for the site first, then those the air names (its own
    /// control channel and the secondary ones) whose identifiers are known.
    /// </summary>
    public IReadOnlyList<double> ControlHz
    {
        get
        {
            var list = new List<double>(Def?.ControlHz ?? []);
            void Add(int ch)
            {
                if (TryResolve(ch, out double hz, out _, out _) && !list.Any(f => Math.Abs(f - hz) < 1)) list.Add(hz);
            }
            if (_controlChannel >= 0) Add(_controlChannel);
            foreach (int ch in _secondary) Add(ch);
            return list;
        }
    }

    /// <summary>Neighbouring sites, as last heard.</summary>
    public IReadOnlyList<TrunkAdjacentSite> Adjacent =>
        _adjacent.Select(a => new TrunkAdjacentSite(a.Key.SysId, a.Key.Rfss, a.Key.Site, a.Value.Channel,
            TryResolve(a.Value.Channel, out double hz, out _, out _) ? hz : 0, a.Value.Flags)).ToList();

    /// <summary>
    /// The first <paramref name="most"/> neighbouring sites by RFSS and site number, made again only when what was heard of
    /// them (or of the channel identifiers) has changed: it is asked for with every status.
    /// </summary>
    public TrunkAdjacentSite[] Neighbours(int most)
    {
        if (_neighbours is null || _neighboursMost != most)
        {
            _neighboursMost = most;
            _neighbours = [.. Adjacent.OrderBy(a => a.Rfss).ThenBy(a => a.Site).ThenBy(a => a.SystemId).Take(most)];
        }
        return _neighbours;
    }
    private TrunkAdjacentSite[]? _neighbours;
    private int _neighboursMost;

    /// <summary>Start again for a site: everything heard before is forgotten.</summary>
    public void Seed(TrunkSiteDef def)
    {
        Def = def;
        _idens.Clear();
        _held.Clear();
        _secondary.Clear();
        _adjacent.Clear();
        _neighbours = null;
        _controlChannel = -1;
        Nac = Wacn = SystemId = Rfss = Site = null;
        Messages = 0;
        Dropped = 0;
    }

    /// <summary>A message from the control channel, received at <paramref name="now"/> (seconds, any origin).</summary>
    public void OnTsbk(P25Tsbk t, double now)
    {
        if (!t.CrcOk) return;
        Messages++;
        Nac = t.Nac;
        switch (t.Kind)
        {
            case P25TsbkKind.Identifier:
                if (t.BaseHz > 0 && t.SpacingHz > 0)
                {
                    var iden = new TrunkIden(t.IdenId, t.IdenForm, t.BaseHz, t.SpacingHz, t.TxOffsetHz, t.BandwidthHz, Math.Max(1, t.Slots),
                        t.ChannelType);
                    if (!_idens.TryGetValue(t.IdenId, out var had) || had != iden) _neighbours = null;
                    _idens[t.IdenId] = iden;
                }
                break;
            case P25TsbkKind.Network:
                Wacn = t.Wacn;
                SystemId = t.SystemId;
                _controlChannel = t.Channel;
                break;
            case P25TsbkKind.Rfss:
                SystemId = t.SystemId;
                Rfss = t.Rfss;
                Site = t.Site;
                _controlChannel = t.Channel;
                break;
            case P25TsbkKind.Secondary:
                // The explicit form (0x29) names one; the other form two, the second 0 when there is none.
                if (!_secondary.Contains(t.Channel)) _secondary.Add(t.Channel);
                if (t.Channel2 != 0 && !_secondary.Contains(t.Channel2)) _secondary.Add(t.Channel2);
                break;
            case P25TsbkKind.Adjacent:
                var key = (t.SystemId, t.Rfss, t.Site);
                if (!_adjacent.TryGetValue(key, out var was) || was != (t.Channel, t.SiteFlags)) _neighbours = null;
                _adjacent[key] = (t.Channel, t.SiteFlags);
                break;
            case P25TsbkKind.Grant:
                if (t.UnitToUnit)
                {
                    Offer(t.Channel, t.Target, t.Source, true, t, now);
                    break;
                }
                Offer(t.Channel, t.Talkgroup, t.Source, false, t, now);
                // The update form names two calls; often the same one twice.
                if (t.Opcode == 0x02 && (t.Channel2 != 0 || t.Talkgroup2 != 0) && (t.Channel2 != t.Channel || t.Talkgroup2 != t.Talkgroup))
                    Offer(t.Channel2, t.Talkgroup2, 0, false, t, now);
                break;
        }
    }

    private void Offer(int channel, int talkgroup, int source, bool unit, P25Tsbk t, double now)
    {
        if (_held.TryGetValue(channel, out var h) && h.Talkgroup == talkgroup && h.Unit == unit && now - h.Last <= RepeatSeconds)
        {
            // The same call again: a repeat or an update. It may name what the first one did not.
            h.Last = now;
            if (h.Source == 0) h.Source = source;
            if (t.HasServiceOptions)
            {
                h.Encrypted |= t.Encrypted;
                h.Emergency |= t.Emergency;
            }
            return;
        }
        _held[channel] = new Held
        {
            Channel = channel, Talkgroup = talkgroup, Source = source, Unit = unit, First = now, Last = now,
            Encrypted = t.HasServiceOptions && t.Encrypted, Emergency = t.HasServiceOptions && t.Emergency,
        };
    }

    /// <summary>
    /// The calls granted since the last call, each once, oldest first. A grant whose channel's identifier is
    /// not known yet is held back until it is, for up to <see cref="HoldSeconds"/>.
    /// </summary>
    public List<TrunkGrant> TakeGrants(double now)
    {
        var grants = new List<TrunkGrant>();
        var gone = new List<int>();
        foreach (var h in _held.Values.OrderBy(h => h.First))
        {
            if (h.Delivered)
            {
                if (now - h.Last > RepeatSeconds) gone.Add(h.Channel);
                continue;
            }
            if (TryResolve(h.Channel, out double hz, out int slot, out bool tdma))
            {
                h.Delivered = true;
                grants.Add(new TrunkGrant(h.Talkgroup, h.Source, hz, slot, tdma, h.Encrypted, h.Emergency, h.Unit, h.Channel));
            }
            else if (now - h.First > HoldSeconds)
            {
                gone.Add(h.Channel);
                Dropped++;
            }
        }
        foreach (int ch in gone) _held.Remove(ch);
        return grants;
    }

    /// <summary>
    /// A channel number (identifier in the top four bits, channel in the low twelve) to the frequency to listen
    /// on, the slot, and whether the identifier is a TDMA one (more than one slot to a carrier). False while its
    /// identifier is not known. The same arithmetic as RadioFoundry's p25_chan_hz.
    /// </summary>
    public bool TryResolve(int channel, out double hz, out int slot, out bool tdma)
    {
        hz = 0;
        slot = 0;
        tdma = false;
        if (!_idens.TryGetValue((channel >> 12) & 0xF, out var iden)) return false;
        int n = channel & 0xFFF;
        if (iden.Slots > 1)
        {
            tdma = true;
            slot = n % iden.Slots;
            n /= iden.Slots;
        }
        hz = iden.BaseHz + (long)n * iden.SpacingHz;
        return true;
    }
}
