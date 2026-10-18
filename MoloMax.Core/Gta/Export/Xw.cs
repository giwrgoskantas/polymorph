using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;

namespace MoloMax.Core.Gta.Export;

/// <summary>Tiny indented XML writer producing CodeWalker XML (invariant culture, CodeWalker tag style).</summary>
public sealed class Xw
{
    readonly StringBuilder sb = new();
    int depth;
    static readonly CultureInfo ci = CultureInfo.InvariantCulture;

    public Xw() { sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"); }

    public static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
        return v.ToString("R", ci);
    }

    void Indent() => sb.Append(' ', depth);

    public Xw Open(string tag, string attrs = null) { Indent(); sb.Append('<').Append(tag); if (attrs != null) sb.Append(' ').Append(attrs); sb.Append(">\n"); depth++; return this; }
    public Xw Close(string tag) { depth--; Indent(); sb.Append("</").Append(tag).Append(">\n"); return this; }
    public Xw Empty(string tag, string attrs = null) { Indent(); sb.Append('<').Append(tag); if (attrs != null) sb.Append(' ').Append(attrs); sb.Append(" />\n"); return this; }
    public Xw Text(string tag, string text)
    {
        if (string.IsNullOrEmpty(text)) return Empty(tag);
        Indent(); sb.Append('<').Append(tag).Append('>').Append(SecurityElement.Escape(text)).Append("</").Append(tag).Append(">\n"); return this;
    }
    public Xw Value(string tag, float v) => Empty(tag, $"value=\"{F(v)}\"");
    public Xw Value(string tag, int v) => Empty(tag, $"value=\"{v.ToString(ci)}\"");
    public Xw Value(string tag, uint v) => Empty(tag, $"value=\"{v.ToString(ci)}\"");
    public Xw Vec(string tag, Vector3 v) => Empty(tag, $"x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\"");
    public Xw Vec(string tag, Vector4 v) => Empty(tag, $"x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\" w=\"{F(v.W)}\"");
    public Xw Quat(string tag, Quaternion q) => Empty(tag, $"x=\"{F(q.X)}\" y=\"{F(q.Y)}\" z=\"{F(q.Z)}\" w=\"{F(q.W)}\"");

    /// <summary>Raw multi-line content (vertex data, index lists).</summary>
    public Xw Lines(string tag, IEnumerable<string> lines)
    {
        Open(tag);
        foreach (var l in lines) { Indent(); sb.Append(l).Append('\n'); }
        return Close(tag);
    }

    /// <summary>Pre-formatted XML lines (already indented), appended as is.</summary>
    public Xw Raw(string xml) { sb.Append(xml); if (!xml.EndsWith("\n")) sb.Append('\n'); return this; }
    public override string ToString() => sb.ToString();
}
