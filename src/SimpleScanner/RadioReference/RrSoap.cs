// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SimpleScanner.RadioReference;

/// <summary>
/// The wire format of the RadioReference service: SOAP 1.1 in the "rpc/encoded" style. A call is
/// an element named for it holding one element per argument; an answer is a <c>return</c> element
/// whose lists are rows of <c>item</c> elements. Little enough that it is written and read by hand.
/// </summary>
internal static class RrSoap
{
    public const string Namespace = "http://api.radioreference.com/soap2";

    /// <summary>The request for a call. Arguments are whole numbers or text; <paramref name="auth"/> goes last, as the service has it.</summary>
    public static string Request(string call, IEnumerable<(string Name, object Value)> args, RrAuth? auth)
    {
        var s = new StringBuilder();
        s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .Append("<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://schemas.xmlsoap.org/soap/envelope/\"")
            .Append(" xmlns:ns1=\"").Append(Namespace).Append('"')
            .Append(" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"")
            .Append(" SOAP-ENV:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><SOAP-ENV:Body>")
            .Append("<ns1:").Append(call).Append('>');
        foreach (var (name, value) in args) Field(s, name, value);
        if (auth is not null)
        {
            s.Append("<authInfo xsi:type=\"ns1:authInfo\">");
            Field(s, "username", auth.Username);
            Field(s, "password", auth.Password);
            Field(s, "appKey", auth.AppKey);
            Field(s, "version", "latest");
            Field(s, "style", "rpc");
            s.Append("</authInfo>");
        }
        s.Append("</ns1:").Append(call).Append("></SOAP-ENV:Body></SOAP-ENV:Envelope>");
        return s.ToString();
    }

    private static void Field(StringBuilder s, string name, object value)
    {
        string type = value is int or long ? "xsd:int" : "xsd:string";
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        s.Append('<').Append(name).Append(" xsi:type=\"").Append(type).Append("\">")
            .Append(SecurityElement.Escape(text)).Append("</").Append(name).Append('>');
    }

    /// <summary>
    /// The <c>return</c> element of an answer. Throws <see cref="RrException"/> with the service's own
    /// words when the answer is a refusal (a SOAP fault), or when it is not an answer at all.
    /// </summary>
    public static XElement Return(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException e) { throw new RrException("RadioReference sent something that is not an answer: " + Short(xml), e); }

        var body = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
                   ?? throw new RrException("RadioReference sent an answer with nothing in it");
        if (body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
        {
            string why = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim() ?? "";
            throw new RrException(why.Length > 0 ? "RadioReference says: " + why : "RadioReference refused the request");
        }
        ResolveReferences(body);
        return body.Elements().FirstOrDefault()?.Elements().FirstOrDefault(e => e.Name.LocalName == "return")
               ?? throw new RrException("RadioReference sent an answer with nothing in it");
    }

    // The encoding lets a value that is used twice be sent once, with an id, and pointed to
    // from elsewhere with href="#id". Each pointer is filled in with what it points to.
    private static void ResolveReferences(XElement body)
    {
        var byId = body.Descendants().Where(e => e.Attribute("id") is not null)
            .GroupBy(e => e.Attribute("id")!.Value).ToDictionary(g => g.Key, g => g.First());
        if (byId.Count == 0) return;
        foreach (var pointer in body.Descendants().Where(e => e.Attribute("href") is not null).ToList())
        {
            string id = pointer.Attribute("href")!.Value.TrimStart('#');
            if (!byId.TryGetValue(id, out var target) || target == pointer || target.Ancestors().Contains(pointer)) continue;
            pointer.ReplaceNodes(target.Nodes());
        }
    }

    private static string Short(string text) => text.Length <= 120 ? text : text[..120] + "…";

    // ---- reading the fields of an answer ----------------------------------------------------

    public static XElement? Child(this XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    public static string Str(this XElement e, string name) => e.Child(name)?.Value.Trim() ?? "";

    public static int Int(this XElement e, string name) =>
        int.TryParse(e.Str(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    public static double Dbl(this XElement e, string name) =>
        double.TryParse(e.Str(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>A coordinate or a distance; the service sends 0 where it has none.</summary>
    public static double? Geo(this XElement e, string name) => e.Dbl(name) is var v && v != 0 ? v : null;

    /// <summary>The rows of a list: of the element itself, or of the list in its field of this name.</summary>
    public static IEnumerable<XElement> Rows(this XElement e, string? name = null) =>
        (name is null ? e : e.Child(name))?.Elements() ?? [];
}
