using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Paint;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Node material editor for a fill layer (ArmorPaint style): nodes in a canvas, drag from an output to an input to link,
/// right click to add, Delete to remove, middle or right drag to pan. Every change repaints the layer.
/// </summary>
public sealed partial class EditorApp
{
    bool nodeOpen; int nodeLayerId = -1; int nodeSel = -1;
    (int Node, int Out)? nodeDragFrom; Vector2 nodeAddAt; double nodeLastRefresh; bool nodePending;
    const float NodeW = 178, NodeRow = 20, NodeHead = 22;

    void OpenNodeEditor(PaintLayer l)
    {
        l.Graph ??= NodeGraph.Starter();
        l.Graph.Changed();
        nodeLayerId = l.Id; nodeOpen = true; nodeSel = -1;
        Current?.Paint?.Cur?.Refresh();
    }

    void DrawNodeEditor()
    {
        if (!nodeOpen) return;
        var ts = Current?.Paint?.Cur;
        var layer = ts?.Layers.FirstOrDefault(x => x.Id == nodeLayerId);
        if (layer?.Graph == null) { nodeOpen = false; return; }
        var g = layer.Graph;
        ImGui.SetNextWindowSize(new Vector2(980, 560), ImGuiCond.FirstUseEver);
        var ds = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(MathF.Max(10, ds.X * 0.5f - 560), MathF.Max(60, ds.Y - 600)), ImGuiCond.FirstUseEver); // low on the screen: the model stays visible above
        if (!ImGui.Begin($"Node material - {layer.Name}###nodeeditor", ref nodeOpen, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) { ImGui.End(); return; }
        bool changed = false;
        // toolbar
        if (ImGui.Button("+ Add node")) { nodeAddAt = ImGui.GetMousePos() - ImGui.GetWindowPos() - g.Pan + new Vector2(60, 80); ImGui.OpenPopup("##nodeadd"); }
        ImGui.SameLine(); Ui.TextDisabled("Drag an output (right) onto an input (left) · right click: add · Delete: remove · middle / right drag: move the view");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 120 + ImGui.GetCursorPosX());
        if (ImGui.Button("Remove graph", new Vector2(120, 0))) { layer.Graph = null; nodeOpen = false; ts.Refresh(); ImGui.End(); return; }
        Theme.Tip("The layer goes back to its plain fill settings.");

        // canvas
        ImGui.BeginChild("##nodecanvas", new Vector2(-1, -1), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse);
        var dl = ImGui.GetWindowDrawList();
        var cmin = ImGui.GetCursorScreenPos(); var csize = ImGui.GetContentRegionAvail();
        var origin = cmin + g.Pan;
        dl.AddRectFilled(cmin, cmin + csize, 0xFF1C1C1F);
        for (float x = (g.Pan.X % 32 + 32) % 32; x < csize.X; x += 32) dl.AddLine(new Vector2(cmin.X + x, cmin.Y), new Vector2(cmin.X + x, cmin.Y + csize.Y), 0xFF26262A);
        for (float y = (g.Pan.Y % 32 + 32) % 32; y < csize.Y; y += 32) dl.AddLine(new Vector2(cmin.X, cmin.Y + y), new Vector2(cmin.X + csize.X, cmin.Y + y), 0xFF26262A);
        // background button: pan, right click menu, click clears the selection
        ImGui.SetCursorScreenPos(cmin);
        ImGui.InvisibleButton("##nodebg", Vector2.Max(csize, new Vector2(1)), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool bgHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0) || ImGui.IsMouseDragging(ImGuiMouseButton.Right, 3))) g.Pan += ImGui.GetIO().MouseDelta;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) nodeSel = -1;
        if (bgHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.GetMouseDragDelta(ImGuiMouseButton.Right).Length() < 4) { nodeAddAt = ImGui.GetMousePos() - origin; ImGui.OpenPopup("##nodeadd"); }

        Vector2 InPos(GNode n, int i) => origin + n.Pos + new Vector2(0, NodeHead + i * NodeRow + NodeRow / 2);
        Vector2 OutPos(GNode n, int o) => origin + n.Pos + new Vector2(NodeW, NodeHead + o * NodeRow + NodeRow / 2);
        static void Wire(ImDrawListPtr d, Vector2 a, Vector2 b, uint col)
        {
            float dx = MathF.Max(40, MathF.Abs(b.X - a.X) * 0.5f);
            d.AddBezierCubic(a, a + new Vector2(dx, 0), b - new Vector2(dx, 0), b, col, 2.2f);
        }
        // links
        foreach (var lk in g.Links.ToList())
        {
            var a = g.Find(lk.FromNode); var b = g.Find(lk.ToNode);
            if (a == null || b == null) { g.Links.Remove(lk); continue; }
            Wire(dl, OutPos(a, lk.FromOut), InPos(b, lk.ToIn), 0xFFB0C4D0);
        }

        int hoverIn = -1, hoverInNode = -1;
        foreach (var n in g.Nodes.ToList())
        {
            var def = NodeTypes.Get(n.Type);
            int rows = Math.Max(def.Inputs.Length, def.Outputs.Length);
            float paramsH = NodeParamRows(n) * 24;
            var p0 = origin + n.Pos; var p1 = p0 + new Vector2(NodeW, NodeHead + rows * NodeRow + paramsH + 8);
            ImGui.PushID(n.Id);
            uint head = n.Type == "Output" ? 0xFF2F6B3A : def.Group switch { "Texture" => 0xFF6B4A2F, "Input" => 0xFF5A3F6B, "Colour" => 0xFF6B6B2F, _ => 0xFF3F4F6B };
            dl.AddRectFilled(p0, p1, 0xF0303036, 5);
            dl.AddRectFilled(p0, p0 + new Vector2(NodeW, NodeHead), head, 5, ImDrawFlags.RoundCornersTop);
            dl.AddRect(p0, p1, nodeSel == n.Id ? 0xFF30B0FF : 0xFF101012, 5, ImDrawFlags.None, nodeSel == n.Id ? 2 : 1);
            dl.AddText(p0 + new Vector2(8, 4), 0xFFFFFFFF, n.Type);
            // drag by the title
            ImGui.SetCursorScreenPos(p0);
            ImGui.InvisibleButton("##title", new Vector2(NodeW, NodeHead));
            if (ImGui.IsItemClicked()) nodeSel = n.Id;
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0)) n.Pos += ImGui.GetIO().MouseDelta;
            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(def.Tip)) ImGui.SetTooltip(def.Tip);
            // inputs
            for (int i = 0; i < def.Inputs.Length; i++)
            {
                var sp = InPos(n, i);
                bool linked = g.Links.Any(l => l.ToNode == n.Id && l.ToIn == i);
                dl.AddCircleFilled(sp, 5, linked ? 0xFFB0C4D0 : 0xFF707078);
                dl.AddText(sp + new Vector2(9, -7), 0xFFD8D8D8, def.Inputs[i]);
                ImGui.SetCursorScreenPos(sp - new Vector2(8, 8));
                ImGui.InvisibleButton("##in" + i, new Vector2(16, 16)); TutRect($"nin:{n.Id}:{i}");
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)) { hoverIn = i; hoverInNode = n.Id; }
                // pulling a linked input takes its link off (and carries it)
                if (ImGui.IsItemActivated() && g.Links.FirstOrDefault(l => l.ToNode == n.Id && l.ToIn == i) is { } old)
                { g.Links.Remove(old); nodeDragFrom = (old.FromNode, old.FromOut); changed = true; }
            }
            // outputs
            for (int o = 0; o < def.Outputs.Length; o++)
            {
                var sp = OutPos(n, o);
                dl.AddCircleFilled(sp, 5, 0xFFE0B060);
                var tw = ImGui.CalcTextSize(def.Outputs[o]).X;
                dl.AddText(sp - new Vector2(tw + 9, 7), 0xFFD8D8D8, def.Outputs[o]);
                ImGui.SetCursorScreenPos(sp - new Vector2(8, 8));
                ImGui.InvisibleButton("##out" + o, new Vector2(16, 16)); TutRect($"nout:{n.Id}:{o}");
                if (ImGui.IsItemActivated()) nodeDragFrom = (n.Id, o);
            }
            // settings
            ImGui.SetCursorScreenPos(p0 + new Vector2(8, NodeHead + rows * NodeRow + 4));
            ImGui.PushItemWidth(NodeW - 16);
            changed |= NodeParams(n);
            ImGui.PopItemWidth();
            ImGui.PopID();
        }
        // a link being dragged
        if (nodeDragFrom is { } from)
        {
            var a = g.Find(from.Node);
            if (a != null) Wire(dl, OutPos(a, from.Out), ImGui.GetMousePos(), 0xFFE0B060);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                if (hoverInNode >= 0 && g.Link(from.Node, from.Out, hoverInNode, hoverIn)) changed = true;
                nodeDragFrom = null;
            }
        }
        if (nodeSel >= 0 && ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.Delete) && g.Find(nodeSel) is { } del) { g.Remove(del); nodeSel = -1; changed = true; }

        // add menu
        if (ImGui.BeginPopup("##nodeadd"))
        {
            foreach (var grp in NodeTypes.All.Where(d => d.Type != "Output").GroupBy(d => d.Group))
            {
                Ui.TextDisabled(grp.Key);
                foreach (var d in grp)
                {
                    if (ImGui.MenuItem(d.Type)) { var nn = g.Add(d.Type, nodeAddAt); nodeSel = nn.Id; changed = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(d.Tip);
                }
            }
            if (!g.Nodes.Any(x => x.Type == "Output") && ImGui.MenuItem("Output")) { g.Add("Output", nodeAddAt); changed = true; }
            ImGui.EndPopup();
        }
        ImGui.EndChild();
        ImGui.End();

        // repaint: right away for a link, at most ~6 times a second while a slider moves
        if (changed) { g.Changed(); nodePending = true; }
        if (nodePending && (ImGui.GetTime() - nodeLastRefresh > 0.16 || !ImGui.IsMouseDown(ImGuiMouseButton.Left)))
        {
            nodePending = false; nodeLastRefresh = ImGui.GetTime();
            ts.Refresh(); Dirty = true;
        }
    }

    static int NodeParamRows(GNode n) => n.Type switch
    {
        "Output" => 2, "Color" => 1, "Value" => 1, "Mesh" => 0, "Noise" => 4, "Voronoi" => 3, "Bricks" => 2, "Pattern" => 2,
        "Warp" => 4, "Color ramp" => 4, "Mix" => 2, "Math" => 3, "Levels" => 3, _ => 0,
    };

    bool NodeParams(GNode n)
    {
        bool c = false;
        switch (n.Type)
        {
            case "Output": c |= ImGui.SliderFloat("##r", ref n.A, 0, 1, "roughness %.2f"); c |= ImGui.SliderFloat("##m", ref n.B, 0, 1, "metal %.2f"); break;
            case "Color": c |= ImGui.ColorEdit3("##c1", ref n.C1, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel); break;
            case "Value": c |= ImGui.SliderFloat("##v", ref n.A, 0, 1, "%.2f"); break;
            case "Noise":
                c |= ImGui.SliderFloat("##s", ref n.S, 0.2f, 60, "scale %.1f", ImGuiSliderFlags.Logarithmic);
                c |= ImGui.SliderFloat("##d", ref n.A, 0, 8, "detail %.1f");
                c |= ImGui.SliderFloat("##ro", ref n.B, 0, 1, "roughness %.2f");
                c |= ImGui.SliderFloat("##di", ref n.C1.X, 0, 4, "distortion %.2f");
                break;
            case "Voronoi":
                c |= ImGui.SliderFloat("##s185", ref n.S, 0.2f, 60, "scale %.1f", ImGuiSliderFlags.Logarithmic);
                c |= ImGui.SliderFloat("##w", ref n.A, 0.01f, 0.4f, "crack width %.2f");
                c |= ImGui.InputInt("##seed", ref n.Mode); break;
            case "Bricks":
                c |= ImGui.SliderFloat("##s189", ref n.S, 1, 40, "rows / m %.1f");
                c |= ImGui.SliderFloat("##mo", ref n.A, 0.002f, 0.1f, "mortar %.3f"); break;
            case "Pattern":
                { int k = Math.Clamp(n.Mode, 1, 8) - 1; var names = NodeTypes.PatternNames.Skip(1).ToArray(); if (ImGui.Combo("##p", ref k, names, names.Length)) { n.Mode = k + 1; c = true; } }
                c |= ImGui.SliderFloat("##s193", ref n.S, 0.2f, 60, "scale %.1f", ImGuiSliderFlags.Logarithmic); break;
            case "Warp":
                c |= ImGui.SliderFloat("##a", ref n.A, 0, 0.5f, "amount %.2f m");
                c |= ImGui.SliderFloat("##ws", ref n.S, 0.2f, 20, "wobble size %.1f");
                c |= ImGui.SliderFloat("##b", ref n.B, 0.2f, 60, "scale %.1f", ImGuiSliderFlags.Logarithmic);
                c |= ImGui.InputInt("##seed198", ref n.Mode); break;
            case "Color ramp":
                c |= ImGui.ColorEdit3("dark##c1", ref n.C1, ImGuiColorEditFlags.NoInputs); ImGui.SameLine(); c |= ImGui.ColorEdit3("bright##c2", ref n.C2, ImGuiColorEditFlags.NoInputs);
                c |= ImGui.SliderFloat("##lo", ref n.A, 0, 1, "from %.2f");
                c |= ImGui.SliderFloat("##hi", ref n.B, 0, 1, "to %.2f");
                break;
            case "Mix":
                {
                    int m = n.Mode; var names = Enum.GetNames<PaintBlend>();
                    if (ImGui.Combo("##bm", ref m, names, names.Length)) { n.Mode = m; c = true; }
                    c |= ImGui.SliderFloat("##f", ref n.A, 0, 1, "fac %.2f");
                    break;
                }
            case "Math":
                {
                    int m = n.Mode; if (ImGui.Combo("##op", ref m, NodeTypes.MathOps, NodeTypes.MathOps.Length)) { n.Mode = m; c = true; }
                    c |= ImGui.SliderFloat("##a214", ref n.A, 0, 4, "A %.2f");
                    c |= ImGui.SliderFloat("##b215", ref n.B, 0, 4, "B %.2f");
                    break;
                }
            case "Levels":
                c |= ImGui.SliderFloat("##lvb", ref n.A, 0, 1, "black %.2f");
                c |= ImGui.SliderFloat("##lvw", ref n.B, 0, 1, "white %.2f");
                c |= ImGui.SliderFloat("##g", ref n.S, 0.1f, 5, "gamma %.2f");
                break;
        }
        return c;
    }
}
