using System.Numerics;
using ImGuiNET;
using MoloMax.Editor;

namespace MoloMax.UI;

/// <summary>
/// The two looks of MoloMax. 3ds Max 2016: #444 panels, bevelled grey buttons, light-blue toggles, square corners.
/// Blender: #303030 regions, rounded widgets, blue selection, orange active object.
/// </summary>
public static class Theme
{
    public static UiMode Mode { get; private set; } = UiMode.Blender;
    public static bool IsBlender => Mode == UiMode.Blender;

    public static Vector4 Panel, PanelDark, Field, Button, ButtonHover, ToggleOn, ToggleOnHover, ToggleOnText, Text, TextDim, Rollout, Gold;
    /// <summary>Selected-object wire colour in the viewport (Max: white, Blender: orange) and the active one.</summary>
    public static Vector4 SelectWire, ActiveWire;

    static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    public static void Apply(UiMode mode)
    {
        Mode = mode;
        if (mode == UiMode.Blender) ApplyBlender(); else ApplyMax();
    }

    static void ApplyMax()
    {
        Panel = Rgb(0x44, 0x44, 0x44); PanelDark = Rgb(0x3a, 0x3a, 0x3a); Field = Rgb(0x2a, 0x2a, 0x2a);
        Button = Rgb(0x5c, 0x5c, 0x5c); ButtonHover = Rgb(0x6b, 0x6b, 0x6b);
        ToggleOn = Rgb(0x8e, 0xb4, 0xe3); ToggleOnHover = Rgb(0xa2, 0xc4, 0xec); ToggleOnText = Rgb(0x10, 0x18, 0x22);
        Text = Rgb(0xdc, 0xdc, 0xdc); TextDim = Rgb(0xa0, 0xa0, 0xa0); Rollout = Rgb(0x52, 0x52, 0x52); Gold = Rgb(0xff, 0xd2, 0x40);
        SelectWire = Vector4.One; ActiveWire = Vector4.One;

        var s = ImGui.GetStyle();
        s.WindowRounding = 0; s.ChildRounding = 0; s.FrameRounding = 2; s.PopupRounding = 0; s.ScrollbarRounding = 2; s.GrabRounding = 2; s.TabRounding = 0;
        s.WindowBorderSize = 1; s.FrameBorderSize = 1; s.PopupBorderSize = 1;
        s.WindowPadding = new Vector2(6, 6); s.FramePadding = new Vector2(6, 3); s.ItemSpacing = new Vector2(4, 4); s.ItemInnerSpacing = new Vector2(4, 4);
        s.ScrollbarSize = 13; s.IndentSpacing = 12; s.WindowTitleAlign = new Vector2(0, 0.5f);
        var c = s.Colors;
        c[(int)ImGuiCol.Text] = Text;
        c[(int)ImGuiCol.TextDisabled] = TextDim;
        c[(int)ImGuiCol.WindowBg] = Panel;
        c[(int)ImGuiCol.ChildBg] = PanelDark;
        c[(int)ImGuiCol.PopupBg] = Rgb(0x3c, 0x3c, 0x3c);
        c[(int)ImGuiCol.Border] = Rgb(0x26, 0x26, 0x26);
        c[(int)ImGuiCol.BorderShadow] = Rgb(0x5a, 0x5a, 0x5a, 0.4f);
        c[(int)ImGuiCol.FrameBg] = Field;
        c[(int)ImGuiCol.FrameBgHovered] = Rgb(0x33, 0x33, 0x33);
        c[(int)ImGuiCol.FrameBgActive] = Rgb(0x22, 0x22, 0x22);
        c[(int)ImGuiCol.TitleBg] = PanelDark;
        c[(int)ImGuiCol.TitleBgActive] = Rgb(0x50, 0x50, 0x50);
        c[(int)ImGuiCol.TitleBgCollapsed] = PanelDark;
        c[(int)ImGuiCol.MenuBarBg] = Rgb(0x3e, 0x3e, 0x3e);
        c[(int)ImGuiCol.ScrollbarBg] = Rgb(0x3a, 0x3a, 0x3a);
        c[(int)ImGuiCol.ScrollbarGrab] = Rgb(0x66, 0x66, 0x66);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0x77, 0x77, 0x77);
        c[(int)ImGuiCol.ScrollbarGrabActive] = Rgb(0x88, 0x88, 0x88);
        c[(int)ImGuiCol.CheckMark] = ToggleOn;
        c[(int)ImGuiCol.SliderGrab] = Rgb(0x8a, 0x8a, 0x8a);
        c[(int)ImGuiCol.SliderGrabActive] = ToggleOn;
        c[(int)ImGuiCol.Button] = Button;
        c[(int)ImGuiCol.ButtonHovered] = ButtonHover;
        c[(int)ImGuiCol.ButtonActive] = Rgb(0x4a, 0x4a, 0x4a);
        c[(int)ImGuiCol.Header] = Rollout;
        c[(int)ImGuiCol.HeaderHovered] = Rgb(0x5e, 0x5e, 0x5e);
        c[(int)ImGuiCol.HeaderActive] = Rgb(0x62, 0x62, 0x62);
        c[(int)ImGuiCol.Separator] = Rgb(0x2c, 0x2c, 0x2c);
        c[(int)ImGuiCol.Tab] = Rgb(0x3a, 0x3a, 0x3a);
        c[(int)ImGuiCol.TabHovered] = Rgb(0x5e, 0x5e, 0x5e);
        c[(int)ImGuiCol.TabSelected] = Panel;
        c[(int)ImGuiCol.TableHeaderBg] = Rollout;
        c[(int)ImGuiCol.TableRowBgAlt] = Rgb(0xff, 0xff, 0xff, 0.03f);
        c[(int)ImGuiCol.TextSelectedBg] = Rgb(0x55, 0x7a, 0xaa, 0.6f);
        c[(int)ImGuiCol.NavCursor] = Gold;
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0, 0, 0, 0.45f);
    }

    /// <summary>Blender 4.x default dark theme.</summary>
    // Polymorph palette: graphite #121418, panels #1B1E24, text #EEF0F2 / #9AA3AE, teal #1FB5A3 / #1B8579, orange #F26B1D
    static void ApplyBlender()
    {
        Panel = Rgb(0x1B, 0x1E, 0x24); PanelDark = Rgb(0x15, 0x17, 0x1C); Field = Rgb(0x12, 0x14, 0x18);
        Button = Rgb(0x2A, 0x2F, 0x38); ButtonHover = Rgb(0x35, 0x3B, 0x46);
        ToggleOn = Rgb(0x1B, 0x85, 0x79); ToggleOnHover = Rgb(0x1F, 0xB5, 0xA3); ToggleOnText = Rgb(0xff, 0xff, 0xff);
        Text = Rgb(0xEE, 0xF0, 0xF2); TextDim = Rgb(0x9A, 0xA3, 0xAE); Rollout = Rgb(0x24, 0x28, 0x30); Gold = Rgb(0xF2, 0x6B, 0x1D);
        SelectWire = Rgb(0xF2, 0x6B, 0x1D); ActiveWire = Rgb(0xFF, 0x9A, 0x52);

        var s = ImGui.GetStyle();
        s.WindowRounding = 0; s.ChildRounding = 4; s.FrameRounding = 4; s.PopupRounding = 6; s.ScrollbarRounding = 6; s.GrabRounding = 4; s.TabRounding = 4;
        s.WindowBorderSize = 1; s.FrameBorderSize = 1; s.PopupBorderSize = 1;
        s.WindowPadding = new Vector2(8, 8); s.FramePadding = new Vector2(8, 3); s.ItemSpacing = new Vector2(4, 4); s.ItemInnerSpacing = new Vector2(4, 4);
        s.ScrollbarSize = 12; s.IndentSpacing = 14; s.WindowTitleAlign = new Vector2(0, 0.5f);
        var c = s.Colors;
        c[(int)ImGuiCol.Text] = Text;
        c[(int)ImGuiCol.TextDisabled] = TextDim;
        c[(int)ImGuiCol.WindowBg] = Panel;
        c[(int)ImGuiCol.ChildBg] = PanelDark;
        c[(int)ImGuiCol.PopupBg] = Rgb(0x15, 0x17, 0x1C, 0.98f);
        c[(int)ImGuiCol.Border] = Rgb(0x0C, 0x0D, 0x10);
        c[(int)ImGuiCol.BorderShadow] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = Rgb(0x12, 0x14, 0x18);
        c[(int)ImGuiCol.FrameBgHovered] = Rgb(0x22, 0x26, 0x2E);
        c[(int)ImGuiCol.FrameBgActive] = Rgb(0x0E, 0x10, 0x13);
        c[(int)ImGuiCol.TitleBg] = Rgb(0x12, 0x14, 0x18);
        c[(int)ImGuiCol.TitleBgActive] = Rgb(0x1B, 0x1E, 0x24);
        c[(int)ImGuiCol.TitleBgCollapsed] = Rgb(0x12, 0x14, 0x18);
        c[(int)ImGuiCol.MenuBarBg] = Rgb(0x12, 0x14, 0x18);
        c[(int)ImGuiCol.ScrollbarBg] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab] = Rgb(0x35, 0x3B, 0x46);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0x45, 0x4C, 0x58);
        c[(int)ImGuiCol.ScrollbarGrabActive] = Rgb(0x1F, 0xB5, 0xA3);
        c[(int)ImGuiCol.CheckMark] = Rgb(0x1F, 0xB5, 0xA3);
        c[(int)ImGuiCol.SliderGrab] = ToggleOnHover;
        c[(int)ImGuiCol.SliderGrabActive] = Gold;
        c[(int)ImGuiCol.Button] = Button;
        c[(int)ImGuiCol.ButtonHovered] = ButtonHover;
        c[(int)ImGuiCol.ButtonActive] = ToggleOn;
        c[(int)ImGuiCol.Header] = Rgb(0x24, 0x28, 0x30);
        c[(int)ImGuiCol.HeaderHovered] = Rgb(0x1F, 0xB5, 0xA3, 0.35f);
        c[(int)ImGuiCol.HeaderActive] = ToggleOn;
        c[(int)ImGuiCol.Separator] = Rgb(0x2A, 0x2F, 0x38);
        c[(int)ImGuiCol.Tab] = Rgb(0x15, 0x17, 0x1C);
        c[(int)ImGuiCol.TabHovered] = Rgb(0x2A, 0x2F, 0x38);
        c[(int)ImGuiCol.TabSelected] = Rgb(0x2A, 0x2F, 0x38);
        c[(int)ImGuiCol.TableHeaderBg] = Rgb(0x24, 0x28, 0x30);
        c[(int)ImGuiCol.TableRowBgAlt] = Rgb(0xff, 0xff, 0xff, 0.03f);
        c[(int)ImGuiCol.TextSelectedBg] = Rgb(0x1F, 0xB5, 0xA3, 0.45f);
        c[(int)ImGuiCol.NavCursor] = Gold;
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0, 0, 0, 0.55f);
    }

    /// <summary>Viewport background: Max gradient (flat grey in ortho), Blender flat #3d3d3d.</summary>
    public static (Vector4 Top, Vector4 Bottom) ViewportBackground(bool ortho)
    {
        if (IsBlender) return (Rgb(0x2E, 0x33, 0x3B), Rgb(0x22, 0x26, 0x2C)); // graphite gradient
        return ortho ? (new(0.47f, 0.47f, 0.47f, 1), new(0.47f, 0.47f, 0.47f, 1)) : (new(0.42f, 0.42f, 0.42f, 1), new(0.17f, 0.17f, 0.17f, 1));
    }

    /// <summary>A toggle button: Max light blue with dark text, Blender blue with white text.</summary>
    public static bool Toggle(string label, bool on, Vector2 size = default)
    {
        if (on)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ToggleOn);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToggleOnHover);
            ImGui.PushStyleColor(ImGuiCol.Text, ToggleOnText);
        }
        bool r = ImGui.Button(label, size);
        if (on) ImGui.PopStyleColor(3);
        return r;
    }

    public static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) Ui.SetTooltip(text);
    }
}
