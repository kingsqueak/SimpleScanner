using System.Globalization;
using System.Text;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>What an import did.</summary>
public sealed record ImportResult(int Added, int AlreadyStored, int Skipped, int Trunked)
{
    public override string ToString()
    {
        var s = new StringBuilder($"{Added} channel{(Added == 1 ? "" : "s")} added");
        if (AlreadyStored > 0) s.Append($", {AlreadyStored} already stored");
        if (Skipped > 0) s.Append($", {Skipped} left out (outside 0.1–2000 MHz, or in a blocked band)");
        if (Trunked > 0) s.Append($". {Trunked} trunked-system frequencies are stored but not ticked for scanning");
        return s.ToString();
    }
}

/// <summary>
/// Reads the frequency downloads RadioReference offers as CSV (a county's or an agency's
/// conventional frequencies): one row per channel, with the columns named in the first row.
/// </summary>
public static class RadioReferenceCsv
{
    /// <summary>
    /// The channels in a download. Throws <see cref="FormatException"/> when the text is not a
    /// frequency list (a talkgroup download, say, has no frequency column).
    /// </summary>
    public static List<MemoryChannel> Parse(string text)
    {
        var rows = ReadRows(text);
        if (rows.Count == 0) throw new FormatException("The file is empty");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => names.Select(n => header.IndexOf(n)).Where(i => i >= 0).DefaultIfEmpty(-1).First();
        int cOut = Col("frequency output", "frequency"), cIn = Col("frequency input"), cCall = Col("fcc callsign", "callsign"),
            cAgency = Col("agency/category", "agency", "category"), cDesc = Col("description"), cAlpha = Col("alpha tag"),
            cTone = Col("pl output tone", "tone", "pl tone"), cMode = Col("mode"), cClass = Col("class station code"), cTag = Col("tag");
        if (cOut < 0)
            throw new FormatException("This is not a RadioReference frequency download: it has no \"Frequency Output\" column");

        var list = new List<MemoryChannel>();
        foreach (var row in rows.Skip(1))
        {
            string Cell(int c) => c >= 0 && c < row.Count ? row[c].Trim() : "";
            string? Opt(int c) => Cell(c) is { Length: > 0 } v ? v : null;
            if (!TryMHz(Cell(cOut), out double hz) || hz <= 0) continue;
            string agency = Cell(cAgency), desc = Cell(cDesc), alpha = Cell(cAlpha), format = Cell(cMode);
            var (mode, width) = Receive(format, hz);
            list.Add(new MemoryChannel
            {
                // A trunked site's rows have no alpha tag: the system and the site say what it is.
                Name = alpha.Length > 0 ? alpha : desc.Length > 0 ? desc : agency,
                FrequencyHz = hz,
                Mode = mode,
                BandwidthHz = width,
                Agency = agency.Length > 0 ? agency : null,
                Description = desc.Length > 0 ? desc : null,
                Tag = Opt(cTag),
                Format = format.Length > 0 ? format : null,
                Tone = Opt(cTone)?.Replace("|", " "),
                Callsign = Opt(cCall),
                InputHz = TryMHz(Cell(cIn), out double input) && input > 0 ? input : null,
                StationClass = Opt(cClass),
                ImportKey = string.Join('|', (hz / 1e6).ToString("0.#####", CultureInfo.InvariantCulture), agency, desc, alpha),
            });
        }
        return list;
    }

    /// <summary>True for a frequency of a trunked system's site, which only a trunk-following receiver can make sense of.</summary>
    public static bool IsTrunked(MemoryChannel c) => string.Equals(c.Tag, "TRS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Add these channels to the store, in this group. A channel already there from an earlier
    /// import of the same row is kept as it is (the user may have edited it) and joins the group.
    /// </summary>
    public static ImportResult Import(MemoryStore store, IEnumerable<MemoryChannel> channels, string group)
    {
        group = group.Trim();
        int added = 0, had = 0, skipped = 0, trunked = 0;
        var known = store.Channels.Where(c => c.ImportKey is not null)
            .GroupBy(c => c.ImportKey!).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in channels)
        {
            if (c.FrequencyHz is < 100_000 or > 2_000_000_000 || BandPlan.IsBlocked(c.FrequencyHz, c.BandwidthHz ?? 0))
            {
                skipped++;
                continue;
            }
            if (known.TryGetValue(c.ImportKey ?? "", out var existing))
            {
                had++;
                if (group.Length > 0 && !existing.Groups.Contains(group)) existing.Groups.Add(group);
                continue;
            }
            if (group.Length > 0) c.Groups.Add(group);
            if (IsTrunked(c))
            {
                c.Scan = false;
                trunked++;
            }
            store.Channels.Add(c);
            if (c.ImportKey is not null) known[c.ImportKey] = c;
            added++;
        }
        return new ImportResult(added, had, skipped, trunked);
    }

    /// <summary>
    /// How to receive what RadioReference calls this mode. Null leaves it to the band plan. The
    /// digital voice modes are all sent in a 12.5 kHz channel and are found by the decoders that
    /// listen to every narrow-FM channel.
    /// </summary>
    private static (Mode?, double?) Receive(string format, double hz) => format.Trim().ToUpperInvariant() switch
    {
        "AM" => (Mode.Am, hz is >= 108e6 and < 137e6 ? BandPlan.AmAir : BandPlan.AmWide),
        "FM" => (Mode.Nfm, BandPlan.NfmWide),
        "FMN" or "P25" or "P25E" or "PROJECT 25" or "DMR" or "DMRE" or "NXDN" or "NXDNE" or "DSTAR" or "D-STAR"
            or "TRBO" or "LTR" or "MOTOROLA" => (Mode.Nfm, BandPlan.NfmNarrow),
        "FMW" or "WFM" => (Mode.Wfm, BandPlan.Wfm),
        _ => (null, null),
    };

    private static bool TryMHz(string text, out double hz)
    {
        bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        hz = Math.Round(v * 1e6 / 10) * 10;
        return ok;
    }

    /// <summary>CSV as RFC 4180 has it: quoted cells may hold commas, line breaks and doubled quotes.</summary>
    internal static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        void EndRow()
        {
            row.Add(cell.ToString());
            cell.Clear();
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
            row = [];
            any = false;
        }
        for (int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch != '"') cell.Append(ch);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else quoted = false;
            }
            else if (ch == '"') quoted = any = true;
            else if (ch == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
                any = true;
            }
            else if (ch == '\n') EndRow();
            else if (ch != '\r')
            {
                cell.Append(ch);
                any = true;
            }
        }
        if (any || cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
}
