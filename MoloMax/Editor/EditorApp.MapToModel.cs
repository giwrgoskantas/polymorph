using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>MAP: the selected game models go to MODEL mode to be edited, laid out as on the map.</summary>
public sealed partial class EditorApp
{
    /// <summary>Selected map entities that have a game model.</summary>
    List<SceneObject> SelectedMapModels() => MapMode ? SelObjects().Where(o => !string.IsNullOrEmpty(o.Gta.MapArchetype)).ToList() : new();

    void SendToModelMenuItem()
    {
        var sel = SelectedMapModels();
        if (!MapMode) return;
        if (ImGui.MenuItem(sel.Count > 1 ? $"Send {sel.Count} models to MODEL" : "Send to MODEL (edit this model)", null, false, sel.Count > 0 && game != null)) SendSelectedToModel();
    }

    /// <summary>Imports the game models of the selected map entities into the MODEL scene, keeping their layout
    /// (the first one at the origin; on an empty MODEL scene the GTA origin is set to its map position, so the export puts it back there).</summary>
    public void SendSelectedToModel()
    {
        var sel = SelectedMapModels();
        if (sel.Count == 0 || game == null) { SetStatus("Select one or more models of the map first."); return; }
        var items = new List<(GameAsset Asset, Vector3 Pos, Quaternion Rot, Vector3 Scale)>();
        var missing = new List<string>();
        foreach (var o in sel)
        {
            var asset = game.FindModel(MapDocument.Hash(o.Gta.MapArchetype)) ?? game.Search(o.Gta.MapArchetype, true, true, false).FirstOrDefault(a => a.Name == o.Gta.MapArchetype);
            if (asset == null) { missing.Add(o.Gta.MapArchetype); continue; }
            Matrix4x4.Decompose(o.World, out var sc, out var rot, out var pos);
            items.Add((asset, pos, rot, sc));
        }
        if (items.Count == 0) { SetStatus("Their models were not found in the game files: " + string.Join(", ", missing)); return; }
        var p0 = items[0].Pos;
        SetMapMode(false);
        bool empty = Scene.Objects.Count == 0;
        if (empty) Do("Set GTA origin", () => Scene.GtaOrigin = p0);
        var origin = empty ? p0 : p0; // the first model lands at the origin either way
        foreach (var (asset, pos, rot, sc) in items)
        {
            var dir = Path.Combine(GameExtractDir, asset.Name);
            var g = game; var a = asset;
            ImportFilesAsync(new[] { Path.Combine(dir, a.Name + a.Ext) }, pos - origin, rot, sc, _ => g.Extract(a, dir));
        }
        SetStatus($"{items.Count} model(s) from the map coming into MODEL" + (missing.Count > 0 ? $" ({missing.Count} not found: {string.Join(", ", missing.Take(3))})" : "") + (empty ? "; GTA origin set to their map position." : "."));
    }
}
