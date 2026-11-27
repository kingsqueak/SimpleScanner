// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.RadioReference;

// What the RadioReference web service says, in the shapes its definition (the WSDL) gives them.
// Only the fields the browser uses are kept.

public sealed record RrCountry(int Id, string Name, string Code);
public sealed record RrState(int Id, string Name, string Code);
public sealed record RrCounty(int Id, string Name);
/// <summary>An agency with frequencies of its own: a state police force, a federal department, a railway.</summary>
public sealed record RrAgency(int Id, string Name);
/// <summary>A trunked radio system. Listed only: the receiver does not follow trunked systems yet.</summary>
public sealed record RrSystem(int Id, string Name, string City);

/// <summary>The smallest unit that can be loaded: one list of frequencies ("Police", "Fire", "Public Works").</summary>
public sealed record RrSubcat(int Id, string Name, double? Lat, double? Lon, double? Range);
/// <summary>A heading over subcategories: a town, a kind of user.</summary>
public sealed record RrCat(int Id, string Name, List<RrSubcat> Subcats);

public sealed record RrCountryInfo(int Id, string Name, List<RrState> States, List<RrAgency> Agencies);
public sealed record RrStateInfo(int Id, string Name, List<RrCounty> Counties, List<RrAgency> Agencies, List<RrSystem> Systems);
public sealed record RrCountyInfo(int Id, string Name, string Header, int StateId, double? Lat, double? Lon,
    List<RrCat> Cats, List<RrAgency> Agencies, List<RrSystem> Systems);
public sealed record RrAgencyInfo(int Id, string Name, List<RrCat> Cats);

public sealed record RrTag(int Id, string Descr);

/// <summary>One conventional frequency. <see cref="Mode"/> may be a name or the number of one (see getMode).</summary>
public sealed record RrFreq(int Id, double OutMHz, double InMHz, string Callsign, string Descr, string Alpha, string Tone,
    string ColorCode, string Tg, string Slot, string Mode, int Enc, string Class, List<RrTag> Tags, int Scid);

public sealed record RrZip(int Zip, double? Lat, double? Lon, string City, int StateId, int CountyId);
/// <summary>The signed-in account: its name and when its Premium subscription runs out, in the service's words.</summary>
public sealed record RrUser(string Username, string Expires);

/// <summary>The service refused a call, or could not be reached; the message is fit to show.</summary>
public sealed class RrException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What every call but the list of countries must carry: the user's own account and the program's key.</summary>
public sealed record RrAuth(string Username, string Password, string AppKey);
