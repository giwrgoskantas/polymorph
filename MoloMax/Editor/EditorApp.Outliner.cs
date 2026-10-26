using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Scene Explorer like 3ds Max + GIMS: folders (layers), eye / freeze columns, and a parent / child hierarchy:
/// the parent is the model, its children are the meshes joined into it and its collision.
/// </summary>
public sealed partial class EditorApp
{
    int outlinerMode; // 0 folders, 1 GTA types
    string folderRename; string folderRenameBuf = ""; bool folderRenameFocus;
    const string ObjDragType = "MOLO_OBJ";
    int dragObjId = -1;
    readonly List<int> outlinerOrder = new(); int outlinerAnchor = -1;
    readonly Dictionary<int, List<SceneObject>> kidsOf = new();
    static readonly List<SceneObject> NoKids = new();

    static readonly Vector4 ColFolder = new(0.93f, 0.76f, 0.32f, 1), ColDrawable = new(0.36f, 0.63f, 1f, 1), ColFragment = new(1f, 0.6f, 0.24f, 1),
        ColBound = new(0.36f, 0.83f, 0.43f, 1), ColRoom = new(0.69f, 0.49f, 1f, 1), ColPortal = new(0.24f, 0.85f, 0.88f, 1), ColGeom = new(0.7f, 0.7f, 0.7f, 1),
        ColShader = new(0.95f, 0.5f, 0.75f, 1);

    static string SollumzType(SceneObject o) => o.GtaType switch
    {
        GtaObjectType.Collision => "Bound Composite",
        GtaObjectType.MloRoom => "MLO Room",
        GtaObjectType.Portal => "MLO Portal",
        _ => o.Gta.Fragment ? "Fragment" : "Drawable",
    };
    static Vector4 TypeColour(SceneObject o) => o.GtaType switch
    {
        GtaObjectType.Collision => ColBound, GtaObjectType.MloRoom => ColRoom, GtaObjectType.Portal => ColPortal,
        _ => o.Gta.Fragment ? ColFragment : ColDrawable,
    };

    void DrawOutlinerContents()
    {
        float fw = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(fw - 60);
        ImGui.InputTextWithHint("##filter", "Find...", ref explorerFilter, 64);
        ImGui.SameLine(0, 4);
        bool typed = outFilter != OutFilter.None;
        if (typed) { ImGui.PushStyleColor(ImGuiCol.Button, Theme.ToggleOn); }
        if (ImGui.Button("\u2261##of", new Vector2(26, 0))) ImGui.OpenPopup("##outfilter");
        if (typed) ImGui.PopStyleColor();
        Theme.Tip(typed ? "Filter on: " + outFilter : "Show only some kinds of objects");
        if (ImGui.BeginPopup("##outfilter"))
        {
            Ui.Label("Show only", Theme.TextDim);
            foreach (OutFilter f in Enum.GetValues<OutFilter>())
            {
                if (f == OutFilter.None) continue;
                bool on = outFilter.HasFlag(f);
                if (ImGui.Checkbox(FilterLabel(f), ref on)) outFilter = on ? outFilter | f : outFilter & ~f;
            }
            ImGui.Separator();
            if (ImGui.Button("Clear", new Vector2(-1, 0))) outFilter = OutFilter.None;
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, 4);
        if (ImGui.Button("+##nf", new Vector2(26, 0))) NewFolder(true);
        Theme.Tip("New folder (selected objects go inside)");
        float b3 = (fw - 8) / 3;
        if (ImGui.Button("Drawable", new Vector2(b3, 0))) CreateDrawable();
        Theme.Tip("GIMS: put the selected game meshes (and their collision) under a new Drawable. One Drawable = one .ydr.");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Col group", new Vector2(b3, 0))) CreateCollisionGroup();
        Theme.Tip("GIMS 'Drawable Col': a folder under the Drawable that holds its collision meshes (drag collisions into it)");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Collision", new Vector2(-1, 0))) CollisionFromSelection();
        Theme.Tip("Copy the selected meshes as Collision, into the Drawable's collision group");
        // what the view and the list show: everything, only the drawables, only the collisions
        float b4 = (fw - 12 - 52) / 3;
        string[] modes = { "All", "Drawables", "Collisions" };
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4);
            if (Theme.Toggle(modes[i] + "##sm", showMode == i, new Vector2(b4, 0))) SetShowMode(i);
        }
        Theme.Tip("Show only the drawables or only the collisions, in the view and in this list. All brings everything back.");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("\u2212##coll", new Vector2(24, 0))) CollapseAll();
        Theme.Tip("Collapse all");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("+##expa", new Vector2(24, 0))) ExpandAll();
        Theme.Tip("Expand all");

        bool filtering = explorerFilter.Length > 0 || outFilter != OutFilter.None;
        // picked in the view: open its parents and folder so its row exists, then scroll to it (below)
        if (ActiveId != outRevealSeen)
        {
            outRevealSeen = ActiveId;
            if (ImGui.GetFrameCount() - outClickFrame > 3 && Scene.Find(ActiveId) is { } act)
            {
                for (var pp = Scene.Parent(act); pp != null; pp = Scene.Parent(pp)) closedObjs.Remove(pp.Id);
                var top = act; while (Scene.Parent(top) is { } up) top = up;
                // MAP: only the picked model's map opens; the maps opened before by a pick close again
                // (hundreds of rows per .ymap: a long list costs frames). Folders you opened yourself stay open.
                if (MapMode)
                    foreach (var f in Scene.Folders)
                        if (f != (top.Folder ?? "") && !outUserOpened.Contains(f)) closedFolders.Add(f);
                closedFolders.Remove(top.Folder ?? "");
                outRevealId = act.Id; outlinerSigTime = -1;
            }
        }
        bool checkNowForced = outlinerSigTime < 0;
        // the row list is rebuilt only when the scene's structure changes (thousands of map entities: no work, no garbage per frame)
        // on a streamed map (tens of thousands of objects) the structure is checked 4 times a second, not every frame
        bool bigScene = Scene.Objects.Count > 3000;
        bool checkNow = !bigScene || ImGui.GetTime() - outlinerSigTime > 0.25 || filtering || checkNowForced;
        long sig = outlinerSig;
        if (checkNow)
        {
        outlinerSigTime = ImGui.GetTime();
        sig = Scene.Objects.Count * 7L + (filtering ? 1 : 0) + closedFolders.Count * 131L + closedObjs.Count * 8191L;
        // the object itself counts, not only its id: an undo / redo or a script gives back new objects with the same ids,
        // and rows kept on the old ones made the eye, freeze and rename act on copies nobody sees (the eye "did nothing")
        foreach (var o in Scene.Objects) sig = sig * 1000003 + o.Id * 31L + o.ParentId * 17L + (o.Folder?.GetHashCode() ?? 0) + (o.InstanceKey == null ? 0 : 5) + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        foreach (var f in Scene.Folders) sig = sig * 31 + f.GetHashCode();
        foreach (var f in closedFolders) sig += f.GetHashCode();
        foreach (var id in closedObjs) sig += id * 92821L;
        sig = sig * 31 + (folderRename?.GetHashCode() ?? 0);
        if (MapMode) sig = sig * 31 + mapPinned.Count * 7919L + (mapActive?.GetHashCode() ?? 0) + interiors.Count * 104729L + (interiorActive?.GetHashCode() ?? 0) + Selected.Count * 13L;
        }
        if (filtering || sig != outlinerSig) { outlinerSig = sig; BuildOutlinerRows(filtering); }

        if (MapMode && outStreamedHidden > 0)
        {
            Ui.TextDisabled($"{outStreamedHidden} map(s) / interior(s) around the camera: view only");
            Theme.Tip("The HD around the camera is shown, not listed. Click a model in the view to edit it: its map comes here.");
        }
        ImGui.BeginChild("##objs", Vector2.Zero, ImGuiChildFlags.Borders);
        outBandRows.Clear();
        if (outRevealId >= 0)
        {
            int ri = orows.FindIndex(r => r.Obj != null && r.Obj.Id == outRevealId);
            if (ri >= 0)
            {
                float y = ri * outRowH, h = ImGui.GetWindowHeight();
                // only when it is not already on screen: then centred
                if (y < ImGui.GetScrollY() || y + outRowH > ImGui.GetScrollY() + h) ImGui.SetScrollY(MathF.Max(0, y - h * 0.4f));
            }
            outRevealId = -1;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2));
        unsafe
        {
            var clip = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clip.Begin(orows.Count);
            bool gotH = false;
            while (clip.Step())
                for (int i = clip.DisplayStart; i < clip.DisplayEnd; i++)
                {
                    var r = orows[i];
                    // a row on an object that was replaced (big scenes check the structure 4 times a second): the live one
                    if (r.Obj != null && !ReferenceEquals(Scene.Find(r.Obj.Id), r.Obj))
                    {
                        outlinerSigTime = -1; // rebuild on the next frame
                        if (Scene.Find(r.Obj.Id) is { } live) r.Obj = live; else continue;
                    }
                    float ind = r.Depth * ImGui.GetStyle().IndentSpacing;
                    if (ind > 0) ImGui.Indent(ind);
                    if (r.Obj == null) FolderRow(r.Folder, r.FolderObjs); else ObjectRow(r.Obj, r.Kids);
                    if (ind > 0) ImGui.Unindent(ind);
                    if (!gotH && clip.ItemsHeight > 1) { outRowH = clip.ItemsHeight; gotH = true; }
                }
            clip.End(); clip.Destroy();
        }
        EndEyeDrag();
        ImGui.PopStyleVar();
        // empty space: click clears, right click offers a new folder
        ImGui.InvisibleButton("##empty", new Vector2(-1, MathF.Max(20, ImGui.GetContentRegionAvail().Y)));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyCtrl) { Selected.Clear(); SelectionChanged(); }
        if (ImGui.IsItemActive() && outBandStart == null && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 4)) BeginOutlinerBand();
        if (ImGui.BeginPopupContextItem("##emptyctx"))
        {
            if (ImGui.MenuItem("New folder")) NewFolder(false);
            ImGui.Separator();
            if (ImGui.MenuItem("Collapse all")) CollapseAll();
            if (ImGui.MenuItem("Expand all")) ExpandAll();
            ImGui.EndPopup();
        }
        UpdateOutlinerBand();
        ImGui.EndChild();
    }

    int showMode; Dictionary<int, bool> showModeHidden;

    static bool IsCollisionObj(SceneObject o) => o.GtaType is GtaObjectType.Collision or GtaObjectType.CollisionGroup;

    /// <summary>All / Drawables / Collisions: hides the other kind (their own hidden state comes back with All).</summary>
    void SetShowMode(int mode)
    {
        if (mode == showMode) return;
        if (showModeHidden != null) foreach (var o in Scene.Objects) if (showModeHidden.TryGetValue(o.Id, out var h)) o.Hidden = h;
        showModeHidden = null; showMode = mode;
        outFilter &= ~(OutFilter.Meshes | OutFilter.Drawables | OutFilter.Collisions);
        if (mode == 0) { SetStatus("Showing everything."); return; }
        showModeHidden = Scene.Objects.ToDictionary(o => o.Id, o => o.Hidden);
        int shown = 0;
        foreach (var o in Scene.Objects)
        {
            bool col = IsCollisionObj(o) || Scene.Parent(o) is { } p && IsCollisionObj(p);
            bool keep = mode == 2 ? col : !col;
            // a Drawable node holding both stays visible so its kids can show
            if (!keep && o.GtaType == GtaObjectType.Model && mode == 2) keep = true;
            o.Hidden = !keep || showModeHidden[o.Id];
            if (keep && o.Mesh.Faces.Count > 0) shown++;
        }
        outFilter |= mode == 2 ? OutFilter.Collisions : OutFilter.Meshes | OutFilter.Drawables;
        SetStatus(mode == 2 ? $"Collisions only ({shown} shown). All shows everything again." : $"Drawables only ({shown} shown). All shows everything again.");
    }

    void CollapseAll()
    {
        foreach (var f in AllFolders().Append("")) closedFolders.Add(f);
        outUserOpened.Clear();
        foreach (var o in Scene.Objects) if (o.ParentId >= 0) closedObjs.Add(o.ParentId);
    }

    void ExpandAll() { closedFolders.Clear(); closedObjs.Clear(); foreach (var f in AllFolders()) outUserOpened.Add(f); }

    long outlinerSig = long.MinValue; double outlinerSigTime = -10;
    int outStreamedHidden;
    void BuildOutlinerRows(bool filtering)
    {
        outlinerOrder.Clear();
        // one pass per frame instead of scanning the scene for every row (big imported maps have thousands of objects)
        kidsOf.Clear(); var ids = new HashSet<int>(Scene.Objects.Select(o => o.Id));
        instCount.Clear();
        foreach (var o in Scene.Objects)
        {
            if (o.ParentId >= 0 && ids.Contains(o.ParentId)) { if (!kidsOf.TryGetValue(o.ParentId, out var l)) kidsOf[o.ParentId] = l = new(); l.Add(o); }
            if (o.InstanceKey != null) instCount[o.InstanceKey] = instCount.TryGetValue(o.InstanceKey, out var n) ? n + 1 : 1;
        }
        // filtering shows matches flat; otherwise folders hold the top of each hierarchy
        var byFolder = new Dictionary<string, List<SceneObject>>();
        var names = new List<string> { "" };
        foreach (var f in Scene.Folders) if (!names.Contains(f)) names.Add(f);
        foreach (var o in Scene.Objects)
        {
            var f = o.Folder ?? "";
            if (f != "" && !names.Contains(f)) names.Add(f);
            bool show = filtering ? (explorerFilter.Length == 0 || o.Name.Contains(explorerFilter, StringComparison.OrdinalIgnoreCase)) && FilterOk(o) : !(o.ParentId >= 0 && ids.Contains(o.ParentId));
            if (!show) continue;
            if (!byFolder.TryGetValue(f, out var l)) byFolder[f] = l = new();
            l.Add(o);
        }
        // flat list of visible rows, drawn with a clipper: only the rows on screen cost anything
        orows.Clear();
        void AddObj(SceneObject o, int depth)
        {
            outlinerOrder.Add(o.Id);
            var kids = filtering || depth > 32 || !kidsOf.TryGetValue(o.Id, out var kl) ? NoKids : kl;
            orows.Add(new ORow { Obj = o, Depth = depth, Kids = kids });
            if (kids.Count > 0 && !closedObjs.Contains(o.Id)) foreach (var c in kids) AddObj(c, depth + 1);
        }
        outStreamedHidden = 0;
        foreach (var f in names)
        {
            // MAP: the maps streamed around the camera stay out of the list until one of their models is picked
            if (MapMode && !filtering && IsViewOnlyMap(f)) { outStreamedHidden++; continue; }
            if (MapMode && !filtering && IsViewOnlyInterior(f)) { outStreamedHidden++; continue; }
            var objs = byFolder.TryGetValue(f, out var l) ? l : NoKids;
            if (f == "" && objs.Count == 0 && names.Count > 1) continue;
            orows.Add(new ORow { Folder = f, FolderObjs = objs });
            if (!closedFolders.Contains(f) || folderRename == f) foreach (var o in objs) AddObj(o, 1);
        }

    }

    [Flags] enum OutFilter { None = 0, Meshes = 1, Drawables = 2, Collisions = 4, Lights = 8, Rooms = 16, Instances = 32, Hidden = 64, Selected = 128 }
    OutFilter outFilter;
    static string FilterLabel(OutFilter f) => f switch
    {
        OutFilter.Meshes => "Game meshes", OutFilter.Drawables => "Drawable nodes", OutFilter.Collisions => "Collisions",
        OutFilter.Lights => "Lights", OutFilter.Rooms => "MLO rooms / portals", OutFilter.Instances => "Instances only",
        OutFilter.Hidden => "Hidden only", OutFilter.Selected => "Selected only", _ => f.ToString(),
    };
    /// <summary>Kinds (OR) narrowed by the state filters (AND): "Collisions + Hidden" = hidden collisions.</summary>
    bool FilterOk(SceneObject o)
    {
        var kinds = outFilter & (OutFilter.Meshes | OutFilter.Drawables | OutFilter.Collisions | OutFilter.Lights | OutFilter.Rooms);
        if (kinds != 0)
        {
            bool k = kinds.HasFlag(OutFilter.Meshes) && o.GtaType == GtaObjectType.Drawable
                  || kinds.HasFlag(OutFilter.Drawables) && o.GtaType == GtaObjectType.Model
                  || kinds.HasFlag(OutFilter.Collisions) && o.GtaType is GtaObjectType.Collision or GtaObjectType.CollisionGroup
                  || kinds.HasFlag(OutFilter.Lights) && o.GtaType == GtaObjectType.Light
                  || kinds.HasFlag(OutFilter.Rooms) && o.GtaType is GtaObjectType.MloRoom or GtaObjectType.Portal;
            if (!k) return false;
        }
        if (outFilter.HasFlag(OutFilter.Instances) && !(o.InstanceKey != null && instCount.TryGetValue(o.InstanceKey, out var n) && n > 1)) return false;
        if (outFilter.HasFlag(OutFilter.Hidden) && !o.Hidden) return false;
        if (outFilter.HasFlag(OutFilter.Selected) && !Selected.Contains(o.Id)) return false;
        return true;
    }

    int eyeDrag = -1; readonly HashSet<int> eyeDragDone = new();
    /// <summary>The mouse is on this row's height, left of the end of its eye (the eyes of children sit a little to the right).</summary>
    bool EyeColumnHovered()
    {
        var mn = ImGui.GetItemRectMin(); var mx = ImGui.GetItemRectMax(); var m = ImGui.GetMousePos();
        return m.Y >= mn.Y && m.Y < mx.Y + ImGui.GetStyle().ItemSpacing.Y && m.X >= ImGui.GetWindowPos().X && m.X <= mx.X + 30;
    }
    /// <summary>Once a frame, after the rows: the eye slide ends with the mouse button.</summary>
    void EndEyeDrag()
    {
        if (eyeDrag < 0 || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        if (eyeDrag == 1) DropHiddenFromSelection();
        eyeDrag = -1; eyeDragDone.Clear();
    }

    /// <summary>Hidden objects stay selected (one more eye click on the selection shows them all again); only the edit mode
    /// of a hidden object is left, so no edit highlights float in the view.</summary>
    void DropHiddenFromSelection()
    {
        if (Level != SubObject.Object && Current is { Hidden: true }) SetLevel(SubObject.Object);
    }

    /// <summary>Objects an eye / freeze click on this row changes: the selection when the row is in it, with their children.</summary>
    IEnumerable<SceneObject> RowTargets(SceneObject o)
    {
        var set = new HashSet<SceneObject>();
        var roots = Selected.Contains(o.Id) && Selected.Count > 1 ? SelObjects().ToList() : new List<SceneObject> { o };
        foreach (var r in roots) { set.Add(r); foreach (var d in Scene.Descendants(r)) set.Add(d); }
        return set;
    }

    struct ORow { public SceneObject Obj; public List<SceneObject> Kids; public string Folder; public List<SceneObject> FolderObjs; public int Depth; }
    readonly List<ORow> orows = new();
    readonly HashSet<string> closedFolders = new();
    /// <summary>Folders opened by hand (a pick in MAP mode leaves them open).</summary>
    readonly HashSet<string> outUserOpened = new();
    readonly HashSet<int> closedObjs = new();
    readonly Dictionary<string, int> instCount = new();

    // ---------------------------------------------------------------- folders

    void FolderRow(string folder, List<SceneObject> objs)
    {
        ImGui.PushID("F:" + folder);
        bool allHidden = objs.Count > 0 && objs.All(o => o.Hidden), allFrozen = objs.Count > 0 && objs.All(o => o.Frozen);
        if (EyeButton(!allHidden)) { foreach (var o in objs) o.Hidden = !allHidden; if (!allHidden) DropHiddenFromSelection(); }
        ImGui.SameLine(0, 2);
        if (FreezeButton(allFrozen)) foreach (var o in objs) o.Frozen = !allFrozen;
        ImGui.SameLine(0, 4);
        string label = folder == "" ? "0 (default)" : folder;
        if (folderRename == folder && folder != "")
        {
            ImGui.SetNextItemWidth(-1);
            if (folderRenameFocus) { ImGui.SetKeyboardFocusHere(); folderRenameFocus = false; }
            if (ImGui.InputText("##frn", ref folderRenameBuf, 63, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll)) RenameFolder(folder, folderRenameBuf.Trim());
            if (ImGui.IsItemDeactivated()) folderRename = null;
            ImGui.PopID();
            return;
        }
        bool isOpen = !closedFolders.Contains(folder);
        ImGui.SetNextItemOpen(isOpen);
        bool open = ImGui.TreeNodeEx("##fold", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.NoTreePushOnOpen, "");
        if (open != isOpen) { if (open) { closedFolders.Remove(folder); outUserOpened.Add(folder); } else { closedFolders.Add(folder); outUserOpened.Remove(folder); } }
        var min = ImGui.GetItemRectMin();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && folder != "") { folderRename = folder; folderRenameBuf = folder; folderRenameFocus = true; }
            else SelectObjects(objs, ImGui.GetIO().KeyCtrl);
        }
        FolderDropTarget(folder);
        if (ImGui.BeginPopupContextItem("##fctx"))
        {
            if (ImGui.MenuItem("Select objects")) SelectObjects(objs, false);
            if (ImGui.MenuItem("Collapse all")) CollapseAll();
            if (ImGui.MenuItem("Expand all")) ExpandAll();
            if (ImGui.MenuItem("Move selection here", null, false, Selected.Count > 0)) MoveToFolder(SelObjects().ToList(), folder, unlink: true);
            ImGui.Separator();
            if (ImGui.MenuItem("Expand all")) { closedFolders.Clear(); closedObjs.Clear(); }
            if (ImGui.MenuItem("Collapse all")) { foreach (var o in Scene.Objects) closedObjs.Add(o.Id); }
            ImGui.Separator();
            if (MapMode && mapDocs.TryGetValue(folder, out var fdoc))
            {
                if (ImGui.MenuItem("Save this .ymap", "Ctrl+S")) SaveMapDoc(fdoc);
                if (ImGui.MenuItem("Save this .ymap as...")) SaveMapDoc(fdoc, saveAs: true);
                ImGui.Separator();
            }
            if (ImGui.MenuItem("New folder")) NewFolder(false);
            if (folder != "")
            {
                if (ImGui.MenuItem("Rename")) { folderRename = folder; folderRenameBuf = folder; folderRenameFocus = true; }
                if (ImGui.MenuItem("Delete folder (keep objects)")) DeleteFolder(folder);
            }
            ImGui.EndPopup();
        }
        var dl = ImGui.GetWindowDrawList();
        float x = min.X + ImGui.GetTreeNodeToLabelSpacing();
        float lh = ImGui.GetTextLineHeight();
        DrawFolderIcon(dl, new Vector2(x, min.Y + 1), lh);
        dl.AddText(new Vector2(x + 18, min.Y), ImGui.GetColorU32(ImGuiCol.Text), label);
        dl.AddText(new Vector2(x + 24 + ImGui.CalcTextSize(label).X, min.Y), ImGui.GetColorU32(ImGuiCol.TextDisabled), $"{objs.Count}");
        ImGui.PopID();
    }

    // ---------------------------------------------------------------- objects (Sollumz tree)

    static readonly Vector4 ColInstance = new(0.55f, 0.85f, 1f, 1);

    void ObjectRow(SceneObject o, List<SceneObject> kids)
    {
        ImGui.PushID(o.Id);
        // the eye / freeze of a selected row act on the whole selection (and on what is under the clicked objects)
        // press on an eye toggles it (the whole selection when the row is selected); keep the button held and slide down
        // the column: every eye passed over takes the same state, like Blender
        EyeButton(!o.Hidden); TutRect("eye:" + o.Id);
        if (ImGui.IsItemActivated())
        {
            bool hide = !o.Hidden;
            eyeDrag = hide ? 1 : 0; eyeDragDone.Clear(); eyeDragDone.Add(o.Id);
            foreach (var t in RowTargets(o)) t.Hidden = hide;
        }
        else if (eyeDrag >= 0 && !eyeDragDone.Contains(o.Id) && EyeColumnHovered())
        {
            eyeDragDone.Add(o.Id);
            o.Hidden = eyeDrag == 1; foreach (var d in Scene.Descendants(o)) d.Hidden = eyeDrag == 1;
        }
        ImGui.SameLine(0, 2);
        if (FreezeButton(o.Frozen))
        {
            bool fr = !o.Frozen;
            foreach (var t in RowTargets(o)) t.Frozen = fr;
        }
        ImGui.SameLine(0, 4);
        if (renameId == o.Id)
        {
            ImGui.SetNextItemWidth(-1);
            if (renameFocus) { ImGui.SetKeyboardFocusHere(); renameFocus = false; }
            if (ImGui.InputText("##rn", ref renameBuf, 63, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
            {
                var newName = renameBuf.Trim();
                if (newName.Length > 0) Do("Rename", () => o.Name = newName);
                renameId = -1;
            }
            if (ImGui.IsItemDeactivated()) renameId = -1;
            ImGui.PopID();
            return;
        }
        bool sel = Selected.Contains(o.Id);
        bool isOpen = kids.Count > 0 && !closedObjs.Contains(o.Id);
        // selected rows: pale red (the active one a little stronger), like the selection in the view
        if (sel)
        {
            var red = o.Id == ActiveId ? new Vector4(0.62f, 0.27f, 0.27f, 0.85f) : new Vector4(0.5f, 0.24f, 0.24f, 0.6f);
            ImGui.PushStyleColor(ImGuiCol.Header, red); ImGui.PushStyleColor(ImGuiCol.HeaderHovered, red * new Vector4(1.15f, 1.15f, 1.15f, 1)); ImGui.PushStyleColor(ImGuiCol.HeaderActive, red);
        }
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.NoTreePushOnOpen | (sel ? ImGuiTreeNodeFlags.Selected : 0)
                  | (kids.Count == 0 ? ImGuiTreeNodeFlags.Leaf : 0);
        if (kids.Count > 0) ImGui.SetNextItemOpen(isOpen);
        bool open = ImGui.TreeNodeEx("##obj", flags, "");
        if (sel) ImGui.PopStyleColor(3);
        if (kids.Count > 0 && open != isOpen) { if (open) closedObjs.Remove(o.Id); else closedObjs.Add(o.Id); }
        var min = ImGui.GetItemRectMin();
        var rmax = ImGui.GetItemRectMax();
        TutRect("row:" + o.Id);
        outBandRows.Add((o.Id, min.Y, rmax.Y));
        // the name is the handle for drag & drop (linking); a drag that starts right of the name selects a box of rows
        float nameEnd = min.X + ImGui.GetTreeNodeToLabelSpacing() + 24 + ImGui.CalcTextSize(o.Name).X;
        bool pressOnName = ImGui.GetIO().MouseClickedPos[0].X <= nameEnd;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { renameId = o.Id; renameBuf = o.Name; renameFocus = true; }
            else OutlinerClick(o);
        }
        if (ImGui.IsItemActive() && !pressOnName && outBandStart == null && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 4)) BeginOutlinerBand();
        if (pressOnName && outBandStart == null && ImGui.BeginDragDropSource())
        {
            dragObjId = o.Id;
            unsafe { int id = o.Id; ImGui.SetDragDropPayload(ObjDragType, (IntPtr)(&id), sizeof(int)); }
            int n = Selected.Contains(o.Id) ? Selected.Count : 1;
            ImGui.Text(n > 1 ? $"{n} objects" : o.Name);
            Ui.TextDisabled("on an object = link as child   |   on a folder = unlink");
            ImGui.EndDragDropSource();
        }
        ObjectDropTarget(o);
        int inst = o.InstanceKey != null && instCount.TryGetValue(o.InstanceKey, out var ic) ? ic : 1;
        if (ImGui.BeginPopupContextItem("##octx"))
        {
            if (!Selected.Contains(o.Id)) { Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id; SelectionChanged(); }
            if (MapMode && SelectedMapDoc() is { } odoc) { if (ImGui.MenuItem($"Save its .ymap ({odoc.Name})")) SaveMapDoc(odoc); ImGui.Separator(); }
            if (ImGui.MenuItem("Rename")) { renameId = o.Id; renameBuf = o.Name; renameFocus = true; }
            if (ImGui.MenuItem("Create Drawable from selection")) CreateDrawable();
            if (ImGui.MenuItem("Collision from selection")) CollisionFromSelection();
            if (ImGui.MenuItem("New collision group here")) CreateCollisionGroup();
            ImGui.Separator();
            if (ImGui.MenuItem("Copy", K("Shift+D", "Ctrl+D"))) CloneSelection(false);
            if (ImGui.MenuItem("Instance", K("Alt+D", "Ctrl+V"))) CloneSelection(true);
            if (inst > 1)
            {
                if (ImGui.MenuItem($"Select instances ({inst})")) SelectInstances(o);
                if (ImGui.MenuItem("Make unique")) MakeUniqueSelection();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Select children", null, false, kids.Count > 0)) { foreach (var d in Scene.Descendants(o)) Selected.Add(d.Id); SelectionChanged(); }
            if (ImGui.MenuItem("Unlink from parent", null, false, Scene.Parent(o) != null))
                Do("Unlink", () => { foreach (var x in SelObjects().ToList()) Scene.SetParent(x, null); });
            if (ImGui.BeginMenu("Move to folder"))
            {
                if (ImGui.MenuItem("0 (default)")) MoveToFolder(SelObjects().ToList(), "", unlink: true);
                foreach (var f in AllFolders()) if (ImGui.MenuItem(f)) MoveToFolder(SelObjects().ToList(), f, unlink: true);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            SendToModelMenuItem();
            HideMenuItems();
            ImGui.Separator();
            if (ImGui.MenuItem("Collapse all")) CollapseAll();
            if (ImGui.MenuItem("Expand all")) ExpandAll();
            ImGui.Separator();
            if (ImGui.MenuItem("Delete", "X")) DeleteSelection();
            ImGui.EndPopup();
        }
        var dl = ImGui.GetWindowDrawList();
        float x = min.X + ImGui.GetTreeNodeToLabelSpacing(), lh = ImGui.GetTextLineHeight();
        DrawTypeIcon(dl, o, kids.Count > 0, new Vector2(x, min.Y + 1), lh);
        // instances: name in light blue (3ds Max shows instances in bold), count at the right
        uint nameCol = o.Hidden ? ImGui.GetColorU32(ImGuiCol.TextDisabled) : inst > 1 ? ImGui.ColorConvertFloat4ToU32(ColInstance) : ImGui.GetColorU32(ImGuiCol.Text);
        dl.AddText(new Vector2(x + 18, min.Y), nameCol, o.Name);
        string tag = o.GtaType == GtaObjectType.Model ? "Drawable" : o.GtaType == GtaObjectType.CollisionGroup ? "Col" : null;
        if (inst > 1) tag = (tag != null ? tag + " " : "") + $"x{inst}";
        if (tag != null)
        {
            var mx = ImGui.GetItemRectMax(); float tw = ImGui.CalcTextSize(tag).X;
            var tc = inst > 1 && o.GtaType is not (GtaObjectType.Model or GtaObjectType.CollisionGroup) ? ColInstance * new Vector4(1, 1, 1, 0.8f) : ColFolder * new Vector4(1, 1, 1, 0.8f);
            if (mx.X - tw - 4 > x + 24 + ImGui.CalcTextSize(o.Name).X) dl.AddText(new Vector2(mx.X - tw - 4, min.Y), ImGui.ColorConvertFloat4ToU32(tc), tag);
        }
        if (ImGui.IsItemHovered() && inst > 1 && !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) ImGui.SetTooltip($"Instance: {inst} objects share this mesh. Edit one, they all change.");
        ImGui.PopID();
    }

    /// <summary>Dropping objects on an object links them under it (meshes of a model, its collision).</summary>
    void ObjectDropTarget(SceneObject parent)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        unsafe
        {
            var p = ImGui.AcceptDragDropPayload(ObjDragType);
            if (p.NativePtr != null)
            {
                int id = *(int*)p.Data;
                var objs = Selected.Contains(id) ? SelObjects().ToList() : Scene.Objects.Where(o => o.Id == id).ToList();
                objs.RemoveAll(o => o == parent);
                int bad = 0;
                Do("Link", () => { foreach (var o in objs) if (!Scene.SetParent(o, parent)) bad++; });
                SetStatus(bad > 0 ? "Can't link an object under its own child." : $"{objs.Count} object(s) linked under {parent.Name}");
            }
        }
        ImGui.EndDragDropTarget();
    }

    // ---------------------------------------------------------------- GIMS: Drawable / collision

    /// <summary>GIMS "Create drawable": a Drawable node at the first mesh's pivot, the selected game meshes (and collisions) under it.</summary>
    public void CreateDrawable()
    {
        var objs = SelObjects().Where(o => o.GtaType is GtaObjectType.Drawable or GtaObjectType.Collision).ToList();
        if (objs.Count == 0) { SetStatus("Select the game meshes to put in the Drawable."); return; }
        var first = objs.FirstOrDefault(o => o.GtaType == GtaObjectType.Drawable) ?? objs[0];
        var name = System.Text.RegularExpressions.Regex.Replace(first.Name, @"(_high(_\d+)?|_mesh|_col)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SceneObject node = null;
        Do("Create Drawable", () =>
        {
            node = Scene.Add(name, new PolyMesh(), first.Position, GtaObjectType.Model);
            node.Name = Scene.Objects.Any(o => o != node && !objs.Contains(o) && o.Name == name) ? node.Name : name;
            node.Rotation = first.Rotation;
            node.Folder = first.Folder;
            node.Gta.Role = first.Gta.Role == GtaRole.Auto ? GtaRole.Auto : first.Gta.Role;
            node.Gta.CollisionMaterial = first.Gta.CollisionMaterial;
            var oldParent = Scene.Parent(first);
            if (oldParent != null && !objs.Contains(oldParent)) Scene.SetParent(node, oldParent);
            int i = 0;
            foreach (var o in objs)
            {
                Scene.SetParent(o, node);
                if (o.GtaType == GtaObjectType.Drawable && o.Name == node.Name) o.Name = $"{name}_high_{i++}";
            }
        });
        Selected.Clear(); Selected.Add(node.Id); ActiveId = node.Id; SelectionChanged();
        SetStatus($"Drawable '{node.Name}' created with {objs.Count} object(s). It exports as one model.");
    }

    /// <summary>The collision group ("Drawable Col") under a Drawable, made when missing.</summary>
    SceneObject CollisionGroupOf(SceneObject node, bool create)
    {
        if (node == null) return null;
        var g = Scene.Children(node).FirstOrDefault(c => c.GtaType == GtaObjectType.CollisionGroup);
        if (g != null || !create) return g;
        g = Scene.Add(node.Name + "_col", new PolyMesh(), node.Position, GtaObjectType.CollisionGroup);
        g.Name = node.Name + "_col"; g.Folder = node.Folder; g.Rotation = node.Rotation;
        Scene.SetParent(g, node);
        return g;
    }

    /// <summary>GIMS "Drawable Col": a folder under the Drawable for its collision meshes (collisions already under it move inside).</summary>
    public void CreateCollisionGroup()
    {
        var o = Current;
        var node = o == null ? null : o.GtaType == GtaObjectType.Model ? o : Scene.Parent(o) is { GtaType: GtaObjectType.Model } p ? p : Scene.Parent(o) is { } pp && Scene.Parent(pp) is { GtaType: GtaObjectType.Model } p2 ? p2 : null;
        if (node == null) { SetStatus("Select a Drawable (or something under it) first."); return; }
        SceneObject g = null;
        Do("Collision group", () =>
        {
            g = CollisionGroupOf(node, true);
            foreach (var c in Scene.Children(node).Where(c => c.GtaType == GtaObjectType.Collision).ToList()) Scene.SetParent(c, g);
            foreach (var c in SelObjects().Where(c => c.GtaType == GtaObjectType.Collision).ToList()) Scene.SetParent(c, g);
        });
        Selected.Clear(); Selected.Add(g.Id); ActiveId = g.Id; SelectionChanged();
        SetStatus($"Collision group '{g.Name}' under {node.Name}. Drag collision meshes into it.");
    }

    /// <summary>GIMS "create collision": copies of the selected meshes as Collision objects, in the Drawable's collision group.</summary>
    public void CollisionFromSelection()
    {
        var objs = SelObjects().Where(o => o.GtaType == GtaObjectType.Drawable && o.Mesh.Faces.Count > 0).ToList();
        if (objs.Count == 0) { SetStatus("Select game meshes to copy as collision."); return; }
        var made = new List<SceneObject>();
        Do("Collision from selection", () =>
        {
            foreach (var o in objs)
            {
                var c = Scene.Add(o.Name + "_col", o.Mesh.Clone(), o.Position, GtaObjectType.Collision);
                c.Rotation = o.Rotation; c.Scale = o.Scale; c.Folder = o.Folder;
                c.Gta.CollisionMaterial = o.Gta.CollisionMaterial;
                var parent = Scene.Parent(o);
                var node = parent != null && parent.GtaType == GtaObjectType.Model ? parent : null;
                Scene.SetParent(c, node != null ? CollisionGroupOf(node, true) : o);
                made.Add(c);
            }
        });
        SetStatus($"{made.Count} collision object(s) added. Simplify them for the game (fewer polygons = better).");
    }

    // ---------------------------------------------------------------- selection and folder actions

    // ---- box selection in the list (free mouse selection): drag from the empty space or right of a name
    readonly List<(int Id, float Y0, float Y1)> outBandRows = new();
    Vector2? outBandStart;
    float outBandScroll;
    HashSet<int> outBandBase;

    void BeginOutlinerBand()
    {
        var io = ImGui.GetIO();
        outBandStart = io.MouseClickedPos[0];
        outBandScroll = ImGui.GetScrollY();
        outBandBase = io.KeyCtrl || io.KeyShift ? new HashSet<int>(Selected) : new HashSet<int>();
    }

    void UpdateOutlinerBand()
    {
        if (outBandStart is not { } st) return;
        var io = ImGui.GetIO();
        // the start point follows the list when it scrolls during the drag
        var a = st - new Vector2(0, ImGui.GetScrollY() - outBandScroll); var b = io.MousePos;
        float y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
        var dl = ImGui.GetWindowDrawList();
        var wp = ImGui.GetWindowPos(); var ws = ImGui.GetWindowSize();
        dl.AddRectFilled(new Vector2(wp.X + 2, y0), new Vector2(wp.X + ws.X - 2, y1), 0x30D0A040);
        dl.AddRect(new Vector2(wp.X + 2, y0), new Vector2(wp.X + ws.X - 2, y1), 0xC0D0A040);
        // auto-scroll at the edges
        if (b.Y < wp.Y + 10) ImGui.SetScrollY(ImGui.GetScrollY() - 8);
        else if (b.Y > wp.Y + ws.Y - 10) ImGui.SetScrollY(ImGui.GetScrollY() + 8);
        Selected.Clear(); Selected.UnionWith(outBandBase);
        int last = -1;
        foreach (var (id, r0, r1) in outBandRows)
            if (r1 >= y0 && r0 <= y1 && Scene.Find(id) is { } o && !o.Frozen) { Selected.Add(id); last = id; }
        if (last >= 0) ActiveId = last;
        SelectionChanged();
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { outBandStart = null; outBandBase = null; }
    }

    int outRevealSeen = -1, outRevealId = -1; int outClickFrame = -100; float outRowH = 21;

    void OutlinerClick(SceneObject o)
    {
        outClickFrame = ImGui.GetFrameCount();
        var io = ImGui.GetIO();
        if (io.KeyShift && outlinerAnchor >= 0)
        {
            int a = outlinerOrder.IndexOf(outlinerAnchor), b = outlinerOrder.IndexOf(o.Id);
            if (a >= 0 && b >= 0)
            {
                if (!io.KeyCtrl) Selected.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) Selected.Add(outlinerOrder[i]);
                ActiveId = o.Id; SelectionChanged();
                return;
            }
        }
        if (!io.KeyCtrl) Selected.Clear();
        if (!Selected.Add(o.Id) && io.KeyCtrl) Selected.Remove(o.Id);
        if (Selected.Contains(o.Id)) ActiveId = o.Id;
        outlinerAnchor = o.Id;
        SelectionChanged();
    }

    void SelectObjects(IEnumerable<SceneObject> objs, bool add)
    {
        if (!add) Selected.Clear();
        foreach (var o in objs) if (!o.Hidden && !o.Frozen) Selected.Add(o.Id);
        SelectionChanged();
    }

    IEnumerable<string> AllFolders() => Scene.Folders.Concat(Scene.Objects.Select(o => o.Folder ?? "")).Where(f => f != "").Distinct();

    void NewFolder(bool withSelection)
    {
        int n = 1; string name;
        do name = "Folder " + n++; while (AllFolders().Contains(name));
        var objs = withSelection ? SelObjects().ToList() : new List<SceneObject>();
        Do("New folder", () => { Scene.Folders.Add(name); foreach (var o in objs) { Scene.SetParent(o, null); o.Folder = name; foreach (var d in Scene.Descendants(o)) d.Folder = name; } });
        folderRename = name; folderRenameBuf = name; folderRenameFocus = true;
    }

    void RenameFolder(string from, string to)
    {
        folderRename = null;
        if (to.Length == 0 || to == from) return;
        Do("Rename folder", () =>
        {
            for (int i = 0; i < Scene.Folders.Count; i++) if (Scene.Folders[i] == from) Scene.Folders[i] = to;
            if (!Scene.Folders.Contains(to)) Scene.Folders.Add(to);
            foreach (var o in Scene.Objects) if (o.Folder == from) o.Folder = to;
        });
    }

    void DeleteFolder(string folder) =>
        Do("Delete folder", () => { Scene.Folders.Remove(folder); foreach (var o in Scene.Objects) if (o.Folder == folder) o.Folder = ""; });

    void MoveToFolder(List<SceneObject> objs, string folder, bool unlink = false)
    {
        if (objs.Count == 0) return;
        Do("Move to folder", () =>
        {
            if (folder != "" && !Scene.Folders.Contains(folder)) Scene.Folders.Add(folder);
            foreach (var o in objs)
            {
                if (unlink) Scene.SetParent(o, null);
                o.Folder = folder;
                foreach (var d in Scene.Descendants(o)) d.Folder = folder;
            }
        });
        SetStatus($"{objs.Count} object(s) moved to {(folder == "" ? "0 (default)" : folder)}");
    }

    void FolderDropTarget(string folder)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        unsafe
        {
            var p = ImGui.AcceptDragDropPayload(ObjDragType);
            if (p.NativePtr != null)
            {
                int id = *(int*)p.Data;
                var objs = Selected.Contains(id) ? SelObjects().ToList() : Scene.Objects.Where(o => o.Id == id).ToList();
                MoveToFolder(objs, folder, unlink: true);
            }
        }
        ImGui.EndDragDropTarget();
    }

    // ---------------------------------------------------------------- little drawn icons (no icon font needed)

    bool EyeButton(bool on)
    {
        var p = ImGui.GetCursorScreenPos(); float h = ImGui.GetTextLineHeight();
        bool click = ImGui.InvisibleButton("##eye", new Vector2(16, h));
        bool hov = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        uint c = ImGui.GetColorU32(on ? (hov ? ImGuiCol.Text : ImGuiCol.Text) : ImGuiCol.TextDisabled, on ? (hov ? 1f : 0.75f) : 0.5f);
        var ctr = p + new Vector2(8, h * 0.5f);
        if (on)
        {
            dl.AddBezierQuadratic(ctr - new Vector2(7, 0), ctr - new Vector2(0, 7), ctr + new Vector2(7, 0), c, 1.3f);
            dl.AddBezierQuadratic(ctr - new Vector2(7, 0), ctr + new Vector2(0, 7), ctr + new Vector2(7, 0), c, 1.3f);
            dl.AddCircleFilled(ctr, 2.2f, c);
        }
        else dl.AddLine(ctr - new Vector2(6, 0), ctr + new Vector2(6, 0), c, 1.3f);
        if (hov) ImGui.SetTooltip(on ? "Hide" : "Unhide");
        return click;
    }

    bool FreezeButton(bool frozen)
    {
        var p = ImGui.GetCursorScreenPos(); float h = ImGui.GetTextLineHeight();
        bool click = ImGui.InvisibleButton("##frz", new Vector2(14, h));
        bool hov = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var ctr = p + new Vector2(7, h * 0.5f);
        uint c = frozen ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.8f, 1f, 1)) : ImGui.GetColorU32(ImGuiCol.TextDisabled, hov ? 0.8f : 0.3f);
        for (int i = 0; i < 3; i++)
        {
            float a = i * MathF.PI / 3;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a)) * 5f;
            dl.AddLine(ctr - d, ctr + d, c, 1.2f);
        }
        if (hov) ImGui.SetTooltip(frozen ? "Unfreeze" : "Freeze (can't be selected)");
        return click;
    }

    static void DrawFolderIcon(ImDrawListPtr dl, Vector2 p, float h, Vector4? colour = null)
    {
        uint c = ImGui.ColorConvertFloat4ToU32(colour ?? ColFolder);
        float y0 = p.Y + h * 0.18f, y1 = p.Y + h * 0.88f;
        dl.AddRectFilled(new Vector2(p.X, y0), new Vector2(p.X + 6, y0 + 3), c, 1);
        dl.AddRectFilled(new Vector2(p.X, y0 + 2), new Vector2(p.X + 14, y1), c, 1.5f);
    }

    /// <summary>3ds Max / GIMS look: a model (parent) gets the GTA triangle, meshes a dot, collisions a wire box.</summary>
    static void DrawTypeIcon(ImDrawListPtr dl, SceneObject o, bool parent, Vector2 p, float h)
    {
        var ctr = p + new Vector2(7, h * 0.52f);
        uint c;
        switch (o.GtaType)
        {
            case GtaObjectType.Collision:
                c = ImGui.ColorConvertFloat4ToU32(ColBound);
                dl.AddRect(ctr - new Vector2(5, 5), ctr + new Vector2(5, 5), c, 0, ImDrawFlags.None, 1.5f);
                dl.AddLine(ctr - new Vector2(5, 5), ctr + new Vector2(5, 5), c, 1f);
                break;
            case GtaObjectType.MloRoom:
                c = ImGui.ColorConvertFloat4ToU32(ColRoom);
                dl.AddTriangleFilled(ctr + new Vector2(-6, -1), ctr + new Vector2(0, -6), ctr + new Vector2(6, -1), c);
                dl.AddRectFilled(ctr + new Vector2(-4.5f, -1), ctr + new Vector2(4.5f, 5.5f), c);
                break;
            case GtaObjectType.Portal:
                c = ImGui.ColorConvertFloat4ToU32(ColPortal);
                dl.AddRect(ctr - new Vector2(4, 6), ctr + new Vector2(4, 6), c, 0, ImDrawFlags.None, 2f);
                break;
            case GtaObjectType.Light:
                c = ImGui.ColorConvertFloat4ToU32(new Vector4(o.Gta.Light?.Colour ?? new Vector3(1, 0.85f, 0.5f), 1));
                dl.AddCircleFilled(ctr, 3.5f, c);
                for (int i = 0; i < 8; i++) { float a = i * MathF.PI / 4; var d = new Vector2(MathF.Cos(a), MathF.Sin(a)); dl.AddLine(ctr + d * 5, ctr + d * 7, c, 1.2f); }
                break;
            case GtaObjectType.CollisionGroup: // GIMS bound composite: a folder of collision boxes
                c = ImGui.ColorConvertFloat4ToU32(ColBound);
                dl.AddRect(ctr + new Vector2(-6, -3), ctr + new Vector2(6, 6), c, 1.5f, ImDrawFlags.None, 1.5f);
                dl.AddRectFilled(ctr + new Vector2(-6, -6), ctr + new Vector2(-1, -3), c, 1);
                dl.AddLine(ctr + new Vector2(-6, -3), ctr + new Vector2(6, 6), c, 1f);
                break;
            case GtaObjectType.Model: // GIMS drawable: right-angle triangle
                c = ImGui.ColorConvertFloat4ToU32(ColFolder);
                dl.AddTriangle(ctr + new Vector2(-6, 6), ctr + new Vector2(-6, -6), ctr + new Vector2(6, 6), c, 1.6f);
                dl.AddLine(ctr + new Vector2(-6, 1), ctr + new Vector2(-1, 6), c, 1f);
                break;
            default:
                c = ImGui.GetColorU32(ImGuiCol.Text, 0.85f);
                if (false && parent)
                {
                    dl.AddTriangle(ctr + new Vector2(-6, 6), ctr + new Vector2(-6, -6), ctr + new Vector2(6, 6), c, 1.4f);
                    dl.AddLine(ctr + new Vector2(-6, 1), ctr + new Vector2(-1, 6), c, 1f);
                }
                else dl.AddCircleFilled(ctr, 4f, c);
                break;
        }
    }
}
