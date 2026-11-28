// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Trunk;

// A trunked system as it is stored: loaded from RadioReference, followed by the receiver.

/// <summary>
/// A trunked system. <see cref="Id"/> is its row here; <see cref="RrSid"/> its number at RadioReference,
/// by which loading it again finds it. <see cref="SysId"/> and <see cref="Wacn"/> are null when not listed.
/// </summary>
public sealed record TrunkSystem(long Id, int RrSid, string Name, string Type, string Flavor, string Voice, int? SysId, int? Wacn,
    string Location, DateTime Imported);

/// <summary>What a site's frequency is used for.</summary>
public enum TrunkFreqUse { Voice, Control, Alternate }

/// <summary>One frequency of a site, in hertz, with its channel number there (LCN) when known.</summary>
public sealed record TrunkSiteFreq(double Hz, int? Lcn, TrunkFreqUse Use);

/// <summary>
/// A site (a transmitter, or a simulcast of several) of a system. <see cref="Nac"/> is null when not listed;
/// <see cref="TdmaControl"/> marks a site whose control channel is Phase 2.
/// </summary>
public sealed record TrunkSite(long Id, long SystemId, int RrSiteId, int Number, int Rfss, string Name, string County, int? Nac,
    string Modulation, bool TdmaControl, double? Lat, double? Lon, IReadOnlyList<TrunkSiteFreq> Freqs);

/// <summary>
/// A talkgroup of a system. <see cref="Mode"/> is "D" (Phase 1) or "T" (Phase 2), followed by "E" when all of
/// its traffic is encrypted or "e" when some is; <see cref="Enc"/> is RadioReference's number (0 none, 1 some, 2 all).
/// <see cref="Listen"/> is the user's tick, which loading again leaves alone.
/// </summary>
public sealed record TrunkTalkgroup(long SystemId, int Dec, string Alpha, string Descr, string Mode, int Enc, string Tag, string Category,
    bool Listen);
