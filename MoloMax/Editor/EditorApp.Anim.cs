using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>Molo GTA hub, Animation page: UV scroll and spritesheet clips exported to &lt;resource&gt;_anims.ycd at 30 fps.</summary>
public sealed partial class EditorApp
{
    void HubAnim()
    {
        Ui.TextWrapped("UV animations play on the GPU through the drawable's shader (globalAnimUV0/1): neons, screens, water, scrolling signs. " +
                       "Polymorph writes one clip per animated shader into <resource>_anims.ycd at 30 fps and sets flag 1024 + clipDictionary on the archetype.");
        ImGui.Spacing();

        var objs = SelObjects().Where(o => o.GtaType == GtaObjectType.Drawable).ToList();
        if (objs.Count == 0)
        {
            Ui.TextDisabled("Select a prop to animate.");
        }
        else
        {
            var o = Current != null && objs.Contains(Current) ? Current : objs[0];
            o.Gta.Anim ??= new GtaAnim();
            var a = o.Gta.Anim;
            Ui.Text(objs.Count == 1 ? o.Name : $"{objs.Count} props (showing {o.Name})");
            float lw = 130;
            void Label(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }
            void Apply(Action<GtaAnim> f) { foreach (var x in objs) { x.Gta.Anim ??= new GtaAnim(); f(x.Gta.Anim); } Dirty = true; }

            Label("Animation");
            int k = (int)a.Kind;
            if (ImGui.Combo("##animkind", ref k, new[] { "None", "UV scroll", "Spritesheet" }, 3)) Apply(x => x.Kind = (UvAnimKind)k);

            if (a.Kind == UvAnimKind.Scroll)
            {
                Label("Scroll U / loop");
                float su = a.ScrollU;
                if (ImGui.DragFloat("##su", ref su, 0.05f, -16, 16, "%.2f")) Apply(x => x.ScrollU = su);
                Label("Scroll V / loop");
                float sv = a.ScrollV;
                if (ImGui.DragFloat("##sv", ref sv, 0.05f, -16, 16, "%.2f")) Apply(x => x.ScrollV = sv);
                Label("Loop length");
                float d = a.Duration;
                if (ImGui.DragFloat("##dur", ref d, 0.05f, 0.1f, 120, "%.2f s")) Apply(x => x.Duration = d);
                Theme.Tip("Use whole numbers of repeats per loop (1, 2, -1...) so the end matches the start without a jump.");
            }
            else if (a.Kind == UvAnimKind.Spritesheet)
            {
                Label("Columns");
                int c = a.Cols;
                if (ImGui.InputInt("##cols", ref c)) Apply(x => x.Cols = Math.Clamp(c, 1, 64));
                Label("Rows");
                int r = a.Rows;
                if (ImGui.InputInt("##rows", ref r)) Apply(x => x.Rows = Math.Clamp(r, 1, 64));
                Label("Frames per cell");
                int h = a.HoldFrames;
                if (ImGui.InputInt("##hold", ref h)) Apply(x => x.HoldFrames = Math.Clamp(h, 1, 300));
                Theme.Tip("Cells play left to right, top to bottom. 30 frames per cell = one cell per second.");
            }

            if (a.Kind != UvAnimKind.None)
            {
                // material filter
                var mats = UsedMaterials(o).Select(id => Scene.Material(id)).DistinctBy(m => m.Id).ToList();
                Label("Material");
                var names = new[] { "All materials that can animate" }.Concat(mats.Select(m => $"{m.Name}  ({m.Shader})")).ToArray();
                int sel = a.MaterialId < 0 ? 0 : Math.Max(0, mats.FindIndex(m => m.Id == a.MaterialId) + 1);
                if (ImGui.Combo("##animmat", ref sel, names, names.Length)) { int id = sel == 0 ? -1 : mats[sel - 1].Id; Apply(x => x.MaterialId = id); }

                var (frames, dur) = AnimXml.Timing(a);
                Ui.TextDisabled($"{frames} frames at {AnimXml.Fps} fps  =  {dur:0.00} s loop");

                foreach (var m in mats)
                {
                    if (a.MaterialId >= 0 && m.Id != a.MaterialId) continue;
                    bool ok = ShaderDb.FromPreset(m.Shader)?.Def?.Params.Any(p => p.Name == "globalAnimUV0") == true;
                    if (!ok) Ui.TextColored(new Vector4(1, 0.6f, 0.2f, 1), $"'{m.Name}' uses {m.Shader}: no UV animation in that shader (use default, spec, normal, emissive or a decal).");
                }
                if (o.Gta.Role is GtaRole.Shell or GtaRole.Exterior)
                    Ui.TextColored(new Vector4(1, 0.6f, 0.2f, 1), "Joined shell / exterior models are merged: set Role to Prop to animate this object on its own.");
                else if (o.Gta.Role == GtaRole.Auto)
                    Ui.TextDisabled("Tip: set Role to Prop so the object keeps its own model and clip.");
            }
        }

        ImGui.Spacing(); ImGui.Separator();
        var animated = Scene.Objects.Where(x => x.Gta.Anim != null && x.Gta.Anim.Kind != UvAnimKind.None).ToList();
        Ui.Text($"Animated in this scene: {animated.Count}");
        foreach (var x in animated)
        {
            var (f, d) = AnimXml.Timing(x.Gta.Anim);
            string kind = x.Gta.Anim.Kind == UvAnimKind.Scroll ? "scroll" : $"sheet {x.Gta.Anim.Cols}x{x.Gta.Anim.Rows}";
            if (ImGui.Selectable($"{x.Name}   {kind}, {d:0.00} s##an{x.GetHashCode()}", Selected.Contains(x.Id))) { Selected.Clear(); Selected.Add(x.Id); ActiveId = x.Id; SelectionChanged(); }
        }
        ImGui.Spacing();
        Ui.TextDisabled("Coming next: object rotation / bobbing clips and auto rig.");
    }
}
