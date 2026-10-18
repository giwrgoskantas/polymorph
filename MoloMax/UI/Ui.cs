using System.Numerics;
using ImGuiNET;

namespace MoloMax.UI;

/// <summary>
/// Text helpers. ImGui.NET passes Text/TextColored/TextDisabled/TextWrapped/SetTooltip strings to
/// ImGui as printf formats, so a '%' in a name, a path or "100%" would be read as a format
/// specifier (and can crash the C runtime). Every dynamic string goes through here.
/// </summary>
public static class Ui
{
    static string E(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("%", "%%");

    // brand fonts (set by the ImGui backend)
    public static ImFontPtr LabelFont, BrandFont;
    public static bool HasLabelFont;
    /// <summary>A collapsing section header in the label font (IBM Plex Mono).</summary>
    public static bool Header(string label, ImGuiTreeNodeFlags flags = 0)
    {
        if (HasLabelFont) ImGui.PushFont(LabelFont);
        bool r = ImGui.CollapsingHeader(label, flags);
        if (HasLabelFont) ImGui.PopFont();
        return r;
    }
    public static bool Header(string label, ref bool visible, ImGuiTreeNodeFlags flags = 0)
    {
        if (HasLabelFont) ImGui.PushFont(LabelFont);
        bool r = ImGui.CollapsingHeader(label, ref visible, flags);
        if (HasLabelFont) ImGui.PopFont();
        return r;
    }
    /// <summary>The Polymorph monogram (low-poly P) drawn as vectors: teal bar, orange facets, white vertex dots.</summary>
    public static void DrawLogo(ImDrawListPtr dl, System.Numerics.Vector2 at, float size, bool dots = true)
    {
        float s = size / 1024f;
        System.Numerics.Vector2 P(float x, float y) => new(at.X + x * s, at.Y + y * s);
        uint C(uint rgb) => 0xFF000000u | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        var A = P(425, 102); var B = P(660, 102); var Cc = P(598, 368); var D = P(823, 266); var E = P(823, 470); var F = P(660, 634); var G = P(425, 634); var BL = P(200, 921);
        dl.AddTriangleFilled(P(200, 102), A, BL, C(0x1FB5A3));
        dl.AddTriangleFilled(A, P(425, 921), BL, C(0x1B8579));
        dl.AddTriangleFilled(A, B, Cc, C(0xF26B1D)); dl.AddTriangleFilled(B, D, Cc, C(0xBA551C)); dl.AddTriangleFilled(D, E, Cc, C(0xF26B1D));
        dl.AddTriangleFilled(E, F, Cc, C(0xBA551C)); dl.AddTriangleFilled(F, G, Cc, C(0xF26B1D)); dl.AddTriangleFilled(G, A, Cc, C(0x8D441B));
        if (!dots) return;
        uint seam = C(0x121418); float w = MathF.Max(1f, 14 * s);
        foreach (var (a, b) in new[] { (A, BL), (A, Cc), (B, Cc), (D, Cc), (E, Cc), (F, Cc), (G, Cc) }) dl.AddLine(a, b, seam, w);
        float r = MathF.Max(1.2f, 24 * s);
        foreach (var q in new[] { A, BL, D, F }) dl.AddCircleFilled(q, r, C(0xEEF0F2));
    }

    /// <summary>Small label text in IBM Plex Mono.</summary>
    public static void Label(string s, System.Numerics.Vector4? colour = null)
    {
        if (HasLabelFont) ImGui.PushFont(LabelFont);
        if (colour is { } c) ImGui.TextColored(c, E(s)); else ImGui.TextUnformatted(s ?? "");
        if (HasLabelFont) ImGui.PopFont();
    }

    public static void Text(string s) => ImGui.Text(E(s));
    public static void TextColored(Vector4 c, string s) => ImGui.TextColored(c, E(s));
    public static void TextDisabled(string s) => ImGui.TextDisabled(E(s));
    public static void TextWrapped(string s) => ImGui.TextWrapped(E(s));
    public static void SetTooltip(string s) => ImGui.SetTooltip(E(s));
}
