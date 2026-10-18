using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tools;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>Human scale: a GTA ped to model next to (never exported) and Auto scale of props from their name.</summary>
public sealed partial class EditorApp
{
    int scaleKindPick = -1; // -1: from the name

    /// <summary>A ped of the game's size beside the selection (or on the 3D cursor / the ground of the map). Not exported.</summary>
    public SceneObject AddPedReference()
    {
        Vector3 at = MapMode ? MapGround() : Cursor3D;
        var sel = SelObjects().Where(o => o.Mesh.Faces.Count > 0).ToList();
        if (sel.Count > 0)
        {
            var mn = sel.Select(o => o.WorldBounds().Min).Aggregate(Vector3.Min); var mx = sel.Select(o => o.WorldBounds().Max).Aggregate(Vector3.Max);
            at = new Vector3(mx.X + 0.6f, (mn.Y + mx.Y) * 0.5f, mn.Z);
        }
        SceneObject ped = null;
        Do("Add ped (scale)", () =>
        {
            ped = Scene.Add("ped_scale", GtaScale.Ped(), at);
            ped.Name = Scene.UniqueName("ped_scale");
            ped.Gta.Role = GtaRole.Ignore; ped.Gta.NoAutoCollision = true;
            ped.WireColour = new Vector3(0.95f, 0.55f, 0.2f);
            MapAdopt(ped);
            Selected.Clear(); Selected.Add(ped.Id); ActiveId = ped.Id;
        });
        SetStatus($"Ped for scale: {GtaScale.PedHeight:0.000} m, never exported.");
        return ped;
    }

    /// <summary>Brings each selected object to the height of its kind (from its name, or <paramref name="force"/>), scaling around its base.</summary>
    public int AutoScale(GtaScale.Kind force = null)
    {
        var objs = SelObjects().Where(o => o.Mesh.Faces.Count > 0 && o.Gta.Role != GtaRole.Ignore).ToList();
        var done = new List<string>(); var unknown = new List<string>();
        Do("Auto scale", () =>
        {
            foreach (var o in objs)
            {
                var k = force ?? GtaScale.Guess(o.Name);
                if (k == null) { unknown.Add(o.Name); continue; }
                var (mn, mx) = o.WorldBounds();
                float f = GtaScale.FactorFor(k, mx.Z - mn.Z);
                if (MathF.Abs(f - 1) < 1e-4f) { done.Add($"{o.Name}: already {k.Label.ToLowerInvariant()} size"); continue; }
                var baseC = new Vector3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, mn.Z);
                o.Scale *= f;
                o.Position = baseC + (o.Position - baseC) * f;
                o.Touch();
                done.Add($"{o.Name}: {k.Label.ToLowerInvariant()} {k.Height:0.###} m (x{f:0.###})");
            }
        });
        SetStatus(done.Count == 0 ? (unknown.Count > 0 ? "Auto scale: no known kind in the names (" + string.Join(", ", unknown.Take(3)) + "); pick the kind by hand." : "Select the props to scale.")
                                  : "Auto scale: " + string.Join("; ", done.Take(4)) + (unknown.Count > 0 ? $"  ({unknown.Count} unknown)" : ""));
        return done.Count;
    }

    /// <summary>Tool tab section: the ped and Auto scale.</summary>
    void ScalePanel()
    {
        if (!Ui.Header("GTA scale", Open)) return;
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        if (ImGui.Button("Ped (scale)", new Vector2(w2, 0))) AddPedReference();
        Theme.Tip($"A GTA ped ({GtaScale.PedHeight:0.000} m) beside the selection, to model at the right size. Never exported.");
        ImGui.SameLine(0, 4);
        var kinds = new[] { "Kind from the name" }.Concat(GtaScale.Kinds.Select(k => $"{k.Label}  {k.Height:0.###} m")).ToArray();
        int pick = scaleKindPick + 1;
        if (ImGui.Button("Auto scale", new Vector2(w2, 0))) AutoScale(scaleKindPick >= 0 ? GtaScale.Kinds[scaleKindPick] : null);
        Theme.Tip("Brings the selected props to real GTA size: the kind comes from the name (chair, stool, door, counter, desk...) or from the list below.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##scalekind", ref pick, kinds, kinds.Length)) scaleKindPick = pick - 1;
        var cur = Current;
        if (cur != null && scaleKindPick < 0)
        {
            var k = GtaScale.Guess(cur.Name);
            var (mn, mx) = cur.WorldBounds();
            Ui.TextDisabled(k != null ? $"{cur.Name}: {k.Label.ToLowerInvariant()}, {mx.Z - mn.Z:0.00} m -> {k.Height:0.###} m" : $"{cur.Name}: {mx.Z - mn.Z:0.00} m tall, kind unknown");
        }
    }
}
