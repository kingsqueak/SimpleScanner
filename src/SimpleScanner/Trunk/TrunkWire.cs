// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Trunk;

// What the window hands the trunk engine, and what the engine hands back. Plain values only: they cross to the
// radio's helper process, which never opens the database.

/// <summary>
/// A site as the trunk engine is given it: which system and site it should be (null where RadioReference does
/// not say), and its listed control and voice frequencies in hertz. The names are for showing only.
/// </summary>
public sealed record TrunkSiteDef(long SystemId, long SiteId, string SystemName, string SiteName, int? Nac, int? SysId, int? Wacn,
    int Rfss, int Site, IReadOnlyList<double> ControlHz, IReadOnlyList<double> VoiceHz)
{
    /// <summary>A stored site of a stored system. Control and alternate control frequencies are both control channels here.</summary>
    public static TrunkSiteDef From(TrunkSystem system, TrunkSite site) => new(
        system.Id, site.Id, system.Name, site.Name, site.Nac, system.SysId, system.Wacn, site.Rfss, site.Number,
        site.Freqs.Where(f => f.Use != TrunkFreqUse.Voice).Select(f => f.Hz).ToList(),
        site.Freqs.Where(f => f.Use == TrunkFreqUse.Voice).Select(f => f.Hz).ToList());
}

/// <summary>
/// A call the control channel has granted, with its channel made a frequency: <see cref="Slot"/> is the TDMA slot
/// (0 or 1) when <see cref="Tdma"/>, else 0. A unit to unit call (<see cref="Unit"/>) has the called radio in
/// <see cref="Talkgroup"/>. <see cref="Source"/> is 0 when the grant's form does not name the caller;
/// <see cref="Encrypted"/> and <see cref="Emergency"/> are false when it carries no service options.
/// <see cref="Channel"/> is the channel number as sent.
/// </summary>
public sealed record TrunkGrant(int Talkgroup, int Source, double Hz, int Slot, bool Tdma, bool Encrypted, bool Emergency, bool Unit,
    int Channel);

/// <summary>
/// Which calls are followed: talkgroups in <see cref="Listen"/> (ticked), not those in <see cref="Off"/> (in the database, not
/// ticked), and those in neither (not in the database) when <see cref="ListenUnknown"/>. With <see cref="SkipEncrypted"/> an
/// encrypted call is logged and never followed; without it, it is followed and shown, and stays silent.
/// </summary>
public sealed record TrunkFilter(int[] Listen, int[] Off, bool ListenUnknown = true, bool SkipEncrypted = true)
{
    /// <summary>Every talkgroup.</summary>
    public static TrunkFilter All { get; } = new([], []);

    public bool Wants(int talkgroup) =>
        Array.IndexOf(Listen, talkgroup) >= 0 || (ListenUnknown && Array.IndexOf(Off, talkgroup) < 0);
}

/// <summary>What the trunk engine is doing.</summary>
public enum TrunkState
{
    Off,
    /// <summary>Looking for the site's control channel, one control frequency after another.</summary>
    Hunt,
    /// <summary>On the control channel, waiting for a call to follow.</summary>
    Control,
    /// <summary>Following a call on its voice channel.</summary>
    Call,
    /// <summary>The call has ended; still on its voice channel for a moment, for the reply.</summary>
    Hang,
    /// <summary>The control channel went silent: looking for it again.</summary>
    Lost,
}

/// <summary>What became of a call the control channel granted.</summary>
public enum TrunkOutcome
{
    /// <summary>Being followed now.</summary>
    Following,
    /// <summary>Followed, and speech was heard.</summary>
    Heard,
    /// <summary>Followed, and nothing was heard on the voice channel.</summary>
    Lost,
    /// <summary>Encrypted: not followed, or left when the voice channel said so.</summary>
    Encrypted,
    /// <summary>Its talkgroup is not ticked (or it was a call to one radio, which is not followed).</summary>
    NotTicked,
    /// <summary>Another call was being followed.</summary>
    Busy,
    /// <summary>A TDMA (Phase 2) call followed and shown, not heard: this copy has no AMBE+2 speech decoder.</summary>
    Phase2,
}

[Flags]
public enum TrunkCallFlags
{
    None = 0,
    Emergency = 1,
    Encrypted = 2,
    /// <summary>A call to one radio: <see cref="TrunkCall.Talkgroup"/> is the radio called.</summary>
    Unit = 4,
    /// <summary>Its voice channel was outside the radio's span: the radio was retuned to it and back.</summary>
    Hop = 8,
    /// <summary>Decoded speech was played.</summary>
    Voice = 16,
    /// <summary>Left by Skip.</summary>
    Skipped = 32,
}

/// <summary>
/// One call, as the trunk engine logged it. <see cref="Seq"/> numbers the finished calls in the order they were finished (1, 2,
/// ...); the call being followed has 0. <see cref="Phase"/> is 1 (FDMA) or 2 (TDMA, with <see cref="Slot"/> 0 or 1).
/// <see cref="Seconds"/> is how long it was heard (0 for one not followed).
/// </summary>
public sealed record TrunkCall(long Seq, int Talkgroup, int Source, double Hz, int Slot, int Phase, TrunkCallFlags Flags, DateTime Start,
    double Seconds, TrunkOutcome Outcome);

/// <summary>
/// The trunk engine as it stands, small enough to be sent twenty times a second: its state, the site it was given
/// (<see cref="SystemKey"/> and <see cref="SiteKey"/>, the window's ids) and what the air says of it (null until heard;
/// <see cref="MatchesSite"/> as <see cref="TrunkSiteState.MatchesSite"/>), the control frequency watched or tried and its
/// level, trunking messages a second and in all, the retunes made to reach voice channels outside the span, the call
/// being followed, the last <see cref="MostCalls"/> finished calls, oldest first, and the neighbouring sites the control channel
/// names (at most <see cref="MostNeighbours"/>; null or empty before any).
/// </summary>
public sealed record TrunkStatus(TrunkState State, long SystemKey, long SiteKey, double ControlHz, double ControlLevelDb, int? Nac,
    int? SysId, int? Wacn, int? Rfss, int? Site, bool? MatchesSite, double BlocksPerSecond, long Messages, int Hops, TrunkCall? Active,
    TrunkCall[] Calls, TrunkAdjacentSite[]? Neighbours = null)
{
    public const int MostCalls = 24;
    public const int MostNeighbours = 8;

    /// <summary>Just started on a site: nothing heard yet.</summary>
    public static TrunkStatus Starting(TrunkSiteDef site) => new(TrunkState.Hunt, site.SystemId, site.SiteId,
        site.ControlHz.Count > 0 ? site.ControlHz[0] : 0, -140, null, null, null, null, null, null, 0, 0, 0, null, []);
}
