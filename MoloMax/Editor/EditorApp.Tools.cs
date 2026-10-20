using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tools;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Molo Tools page: Mimi's Blender GTA tools rebuilt for MoloMax, sorted by task —
/// doors and openings, mouldings, decals (fake AO, edge dirt, edge wear, smooth edge, contact shadow, sign glow),
/// vertex colours, collision and textures (DDS, maps from a diffuse).
/// </summary>
public sealed partial class EditorApp
{
    // door / window
    float toolDoorW = 1.2f, toolDoorH = 2.3f, toolDoorOffset, toolWinSill = 0.9f, toolWinW = 1.2f, toolWinH = 1.2f;
    bool toolDoorLeaf = true, toolDoorCasing = true, toolDoorFacing = true;
    // mouldings
    readonly TrimSettings toolTrim = new();
    // strips
    readonly StripSettings toolStrip = new();
    float toolAoCenter = 0.7f, toolAoOuter = 0f, toolWearStrength = 4f, toolWearDamage = 0.7f;
    int toolSeed = 1;
    // shadow, glow
    readonly ShadowSettings toolShadow = new();
    readonly GlowSettings toolGlow = new();
    int toolGlowFacing;
    // vertex colours
    float toolVcSplit = 0.55f, toolSunRadius = 2.5f;
    // collision
    bool toolColOriented = true, toolColPerPart = true;

    static string GeneratedFolder
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Polymorph", "generated");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Saves a generated texture as DDS (DXT1 / DXT5 with mips) and makes the viewport find it.</summary>
    string SaveGenerated(string name, RgbaImage img, bool normal = false)
    {
        var path = Path.Combine(GeneratedFolder, TextureBank.SafeName(name) + ".dds");
        File.WriteAllBytes(path, DdsEncoder.Encode(img, normal ? DdsFormat.Dxt5 : DdsEncoder.AutoFormat(img)));
        if (!renderer.Textures.SearchFolders.Contains(GeneratedFolder)) renderer.Textures.SearchFolders.Add(GeneratedFolder);
        renderer.Textures.ClearResolveCache();
        return path;
    }

    /// <summary>Finds a material by name or makes it.</summary>
    GtaMaterial ToolMaterial(string name, string shader, Vector3 colour)
    {
        var m = Scene.Materials.FirstOrDefault(x => x.Name == name);
        if (m == null) { m = Scene.AddMaterial(name, colour); }
        m.Shader = shader;
        return m;
    }

    string NextName(string prefix)
    {
        for (int i = 1; ; i++) { var n = $"{prefix}_{i:000}"; if (!Scene.Objects.Any(o => o.Name == n)) return n; }
    }

    SceneObject AddToolObject(string name, PolyMesh mesh, SceneObject like, GtaMaterial mat, bool decal)
    {
        mesh.SetAllMaterial(mat.Id);
        var o = Scene.Add(name, mesh, like?.Position ?? Vector3.Zero);
        o.Name = name;
        if (like != null) { o.Rotation = like.Rotation; o.Scale = like.Scale; }
        o.Gta.NoAutoCollision = decal;
        // decals follow their source: part of the shell for rooms, else exported with it
        if (like != null) o.Gta.Role = like.GtaType == GtaObjectType.MloRoom ? GtaRole.Shell : like.Gta.Role;
        o.WireColour = decal ? new Vector3(0.9f, 0.75f, 0.3f) : o.WireColour;
        return o;
    }

    HashSet<EdgeKey> SelectedEdgesForTool(SceneObject o)
    {
        var s = SubSel(o);
        if (Level is SubObject.Edge or SubObject.Border) return new HashSet<EdgeKey>(s.Edges);
        return EdgesInScope(o, s);
    }

    // ---------------------------------------------------------------- page

    void HubTools()
    {
        var cur = Current;
        bool edit = cur != null && Level != SubObject.Object;
        Ui.TextDisabled(cur == null ? "Select an object." : edit ? $"{cur.Name}  -  {Level} mode" : $"{Selected.Count} object(s) selected");

        if (Ui.Header("Doors & windows", ImGuiTreeNodeFlags.DefaultOpen)) ToolsDoors(cur);
        if (Ui.Header("Room finishing (skirting, dirt, wear)"))
        {
            RoomMaterialOptions();
            RoomFinishOptions();
            if (ImGui.Button("Finish the selected rooms", new Vector2(-1, 28))) FinishSelectedRooms();
        }
        if (Ui.Header("Mouldings (baseboard, cornice, casings)")) ToolsTrims(cur);
        if (Ui.Header("Decals: fake AO, dirt, wear, smooth edge")) ToolsStrips(cur);
        if (Ui.Header("Contact shadow & sign glow")) ToolsShadowGlow();
        if (Ui.Header("Vertex colours")) ToolsVertexColours(cur);
        if (Ui.Header("Collision boxes")) ToolsCollision();
        if (Ui.Header("Textures: DDS & maps")) ToolsTextures(cur);
        if (Ui.Header("Procedural"))
        {
            Ui.TextWrapped("Stair cage, trash scatter, procedural rooms and buildings are next. Animations (YCD) get their own page.");
        }
    }

    // ---------------------------------------------------------------- doors & windows

    void ToolsDoors(SceneObject cur)
    {
        Ui.TextWrapped("Select a wall face (Polygon mode) of a room or wall, then cut. Between two rooms, the facing room is cut too and the door goes in the gap: the MLO portal is found by itself.");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##dw", ref toolDoorW, 0.01f, 0.4f, 6f, "W %.2f m"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.DragFloat("##dh", ref toolDoorH, 0.01f, 0.5f, 6f, "H %.2f m");
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##doff", ref toolDoorOffset, 0.01f, -20f, 20f, "Offset %.2f m"); ImGui.SameLine();
        if (ImGui.Button("GTA door 1.20 x 2.30", new Vector2(-1, 0))) { toolDoorW = 1.2f; toolDoorH = 2.3f; }
        ImGui.Checkbox("Door leaf", ref toolDoorLeaf); ImGui.SameLine();
        ImGui.Checkbox("Casing", ref toolDoorCasing); ImGui.SameLine();
        ImGui.Checkbox("Facing room", ref toolDoorFacing);
        if (ImGui.Button("CUT DOORWAY", new Vector2(-1, 28))) CutOpening(cur, toolDoorW, toolDoorH, toolDoorOffset, 0, true);
        ImGui.Separator();
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##ww", ref toolWinW, 0.01f, 0.2f, 6f, "W %.2f m"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.DragFloat("##wh", ref toolWinH, 0.01f, 0.2f, 6f, "H %.2f m");
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##ws", ref toolWinSill, 0.01f, 0f, 5f, "Sill %.2f m"); ImGui.SameLine();
        if (ImGui.Button("CUT WINDOW", new Vector2(-1, 0))) CutOpening(cur, toolWinW, toolWinH, toolDoorOffset, toolWinSill, false);
    }

    void CutOpening(SceneObject cur, float w, float h, float offset, float sill, bool door)
    {
        if (cur == null || Level is not (SubObject.Polygon or SubObject.Element) || SubSel(cur).Faces.Count != 1) { SetStatus("Select one wall face in Polygon mode."); return; }
        int face = SubSel(cur).Faces.First();
        string msg = null;
        Do(door ? "Cut Doorway" : "Cut Window", () =>
        {
            var r = Doorway.Cut(cur.Mesh, face, w, h, offset, sill);
            msg = r.Message;
            if (!r.Ok) return;
            SubSel(cur).Clear();
            cur.Touch();
            var world = cur.World;
            var bottomW = Vector3.Transform(r.BottomCenter, world);
            var nW = Vector3.Normalize(Vector3.TransformNormal(r.Normal, world));
            var rightW = Vector3.Normalize(Vector3.TransformNormal(r.Right, world));
            var cuts = new List<(SceneObject Obj, Doorway.Result R)> { (cur, r) };

            // the room on the other side of the gap gets the same opening
            if (toolDoorFacing)
            {
                foreach (var other in Scene.Objects.Where(o => o != cur && o.GtaType == GtaObjectType.MloRoom && !o.Hidden))
                {
                    var ow = other.World;
                    for (int fi = 0; fi < other.Mesh.Faces.Count; fi++)
                    {
                        var on = Vector3.Normalize(Vector3.TransformNormal(other.Mesh.FaceNormal(fi), ow));
                        if (Vector3.Dot(on, nW) > -0.99f) continue; // must face the other way
                        var oc = Vector3.Transform(other.Mesh.FaceCenter(fi), ow);
                        float dist = Vector3.Dot(oc - bottomW, -nW);
                        if (dist < 0 || dist > Scene.Gta.PortalMaxGap) continue;
                        // centre the cut on the same spot along the wall
                        var inv = Matrix4x4.Invert(ow, out var iw) ? iw : Matrix4x4.Identity;
                        var localBottom = Vector3.Transform(bottomW - nW * dist, inv);
                        var fn = other.Mesh.FaceNormal(fi);
                        var upO = Vector3.UnitZ - fn * Vector3.Dot(Vector3.UnitZ, fn);
                        if (upO.LengthSquared() < 0.25f) continue;
                        var rightO = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(upO), fn));
                        float off2 = Vector3.Dot(localBottom - other.Mesh.FaceCenter(fi), rightO);
                        var r2 = Doorway.Cut(other.Mesh, fi, w, h, off2, sill);
                        if (r2.Ok) { other.Touch(); cuts.Add((other, r2)); msg += " Facing room cut too."; }
                        goto doneFacing;
                    }
                }
            }
        doneFacing:
            // door leaf in the middle of the gap (or in the wall plane)
            var mid = cuts.Select(c => Vector3.Transform(c.R.BottomCenter, c.Obj.World)).Aggregate(Vector3.Zero, (a, b) => a + b) / cuts.Count;
            var rot = Quaternion.CreateFromRotationMatrix(new Matrix4x4(rightW.X, rightW.Y, rightW.Z, 0, nW.X, nW.Y, nW.Z, 0, 0, 0, 1, 0, 0, 0, 0, 1));
            if (door && toolDoorLeaf)
            {
                var leafMat = ToolMaterial("door", "normal_spec", new Vector3(0.55f, 0.42f, 0.3f));
                var leaf = Scene.Add("door", Doorway.Leaf(w, h), mid);
                leaf.Name = NextName("door");
                leaf.Rotation = Quaternion.Normalize(rot);
                leaf.Mesh.SetAllMaterial(leafMat.Id);
                leaf.Gta.Role = GtaRole.Door;
                leaf.Gta.PropCollision = GtaPropCollision.Box;
            }
            // casing around each opening, on the room side
            if (toolDoorCasing)
            {
                var casingMat = ToolMaterial("molo_casing", "normal_spec", new Vector3(0.85f, 0.83f, 0.8f));
                foreach (var (obj, cr) in cuts)
                {
                    var loop = MloLayout.BorderLoops(obj.Mesh)
                        .OrderBy(l => Vector3.Distance(l.Select(v => obj.Mesh.Verts[v]).Aggregate(Vector3.Zero, (a, b) => a + b) / l.Count, cr.BottomCenter + cr.Up * cr.Height / 2))
                        .FirstOrDefault();
                    if (loop == null) continue;
                    var edges = new HashSet<EdgeKey>();
                    for (int i = 0; i < loop.Count; i++)
                    {
                        int a = loop[i], b = loop[(i + 1) % loop.Count];
                        // no casing across the floor of a doorway
                        if (door && MathF.Abs(Vector3.Dot(obj.Mesh.Verts[a] - cr.BottomCenter, cr.Up)) < 1e-3f && MathF.Abs(Vector3.Dot(obj.Mesh.Verts[b] - cr.BottomCenter, cr.Up)) < 1e-3f) continue;
                        edges.Add(EdgeKey.Make(a, b));
                    }
                    var ts = TrimSettings.Frame(door ? TrimKind.DoorFrame : TrimKind.WindowFrame);
                    var trim = TrimSweep.Build(obj.Mesh, edges, ts, casingMat.Id);
                    if (trim.Faces.Count == 0) continue;
                    var t = AddToolObject(NextName(door ? "doorframe" : "windowframe"), trim, obj, casingMat, false);
                    t.Gta.NoAutoCollision = true;
                }
            }
        });
        hubLayout = null;
        SetStatus(msg ?? "Nothing cut.");
    }

    // ---------------------------------------------------------------- mouldings

    void ToolsTrims(SceneObject cur)
    {
        int kind = (int)toolTrim.Kind;
        if (ImGui.Combo("Type##trim", ref kind, new[] { "Baseboard", "Cornice", "Door casing", "Window casing" }, 4))
        {
            var k = (TrimKind)kind;
            var fresh = k is TrimKind.DoorFrame or TrimKind.WindowFrame ? TrimSettings.Frame(k) : new TrimSettings { Kind = k };
            CopyTrim(fresh, toolTrim);
        }
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##th", ref toolTrim.Height, 0.001f, 0.01f, 1f, "Height %.3f m"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.DragFloat("##td", ref toolTrim.Depth, 0.001f, 0.002f, 0.3f, "Depth %.3f m");
        var edges = new[] { "Square", "Chamfer", "Round", "Cove", "Ogee", "Step" };
        int ie = (int)toolTrim.Inner, oe = (int)toolTrim.Outer;
        ImGui.SetNextItemWidth(w2); if (ImGui.Combo("##tin", ref ie, edges, edges.Length)) toolTrim.Inner = (TrimEdge)ie; ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); if (ImGui.Combo("##tout", ref oe, edges, edges.Length)) toolTrim.Outer = (TrimEdge)oe;
        ImGui.SetNextItemWidth(w2); ImGui.SliderFloat("##tia", ref toolTrim.InnerAmount, 0, 0.98f, "inner %.2f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##toa", ref toolTrim.OuterAmount, 0, 0.98f, "outer %.2f");
        ImGui.Checkbox("Cap ends", ref toolTrim.CapEnds); ImGui.SameLine(); ImGui.Checkbox("Flip side", ref toolTrim.Flip);
        DrawProfilePreview(TrimSweep.Profile(toolTrim));
        if (ImGui.Button("Build on selected edges", new Vector2(-1, 0))) BuildTrim(cur, false);
        if (toolTrim.Kind is TrimKind.Baseboard or TrimKind.Cornice && ImGui.Button(toolTrim.Kind == TrimKind.Baseboard ? "Baseboard around selected rooms" : "Cornice around selected rooms", new Vector2(-1, 0))) BuildTrim(cur, true);
    }

    static void CopyTrim(TrimSettings a, TrimSettings b)
    {
        b.Kind = a.Kind; b.Height = a.Height; b.Depth = a.Depth; b.Inner = a.Inner; b.Outer = a.Outer; b.InnerAmount = a.InnerAmount; b.OuterAmount = a.OuterAmount;
        b.Steps = a.Steps; b.StepDepth = a.StepDepth; b.CapEnds = a.CapEnds; b.Flip = a.Flip;
    }

    void DrawProfilePreview(List<Vector2> prof)
    {
        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 70);
        dl.AddRectFilled(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(Theme.Field));
        float mx = MathF.Max(prof.Max(p => p.X), 1e-3f), my = MathF.Max(prof.Max(p => p.Y), 1e-3f);
        float sc = MathF.Min((size.X - 20) / mx, (size.Y - 10) / my);
        Vector2 S(Vector2 p) => p0 + new Vector2(10 + p.X * sc, size.Y - 5 - p.Y * sc);
        for (int i = 0; i < prof.Count; i++) dl.AddLine(S(prof[i]), S(prof[(i + 1) % prof.Count]), ImGui.ColorConvertFloat4ToU32(Theme.Gold), 1.5f);
        ImGui.Dummy(size);
    }

    void BuildTrim(SceneObject cur, bool roomFloor)
    {
        var mat = ToolMaterial(toolTrim.Kind switch { TrimKind.Cornice => "molo_cornice", TrimKind.Baseboard => "molo_baseboard", _ => "molo_casing" }, "normal_spec", new Vector3(0.85f, 0.83f, 0.8f));
        int made = 0;
        if (roomFloor)
        {
            var rooms = SelObjects().Where(o => o.Mesh.Faces.Count > 0).ToList();
            if (rooms.Count == 0) { SetStatus("Select the room objects."); return; }
            Do("Build " + toolTrim.Kind, () =>
            {
                foreach (var o in rooms)
                {
                    var (mn, mx) = o.Mesh.Bounds();
                    float z = toolTrim.Kind == TrimKind.Cornice ? mx.Z : mn.Z;
                    var edges = o.Mesh.BuildEdgeMap().Keys.Where(e => MathF.Abs(o.Mesh.Verts[e.A].Z - z) < 1e-3f && MathF.Abs(o.Mesh.Verts[e.B].Z - z) < 1e-3f).ToHashSet();
                    var m = TrimSweep.Build(o.Mesh, edges, toolTrim, mat.Id);
                    if (m.Faces.Count == 0) continue;
                    var t = AddToolObject(NextName(toolTrim.Kind.ToString().ToLowerInvariant()), m, o, mat, false);
                    t.Gta.NoAutoCollision = true;
                    made++;
                }
            });
        }
        else
        {
            if (cur == null || Level == SubObject.Object) { SetStatus("Select edges (Edge mode) first."); return; }
            var edges = SelectedEdgesForTool(cur);
            if (edges.Count == 0) { SetStatus("Select edges first."); return; }
            Do("Build " + toolTrim.Kind, () =>
            {
                var m = TrimSweep.Build(cur.Mesh, edges, toolTrim, mat.Id);
                if (m.Faces.Count == 0) return;
                var t = AddToolObject(NextName(toolTrim.Kind.ToString().ToLowerInvariant()), m, cur, mat, false);
                t.Gta.NoAutoCollision = true;
                made++;
            });
        }
        SetStatus(made > 0 ? $"{made} moulding(s) built." : "Nothing built: check the selection.");
    }

    // ---------------------------------------------------------------- decal strips

    void ToolsStrips(SceneObject cur)
    {
        Ui.TextWrapped("Select edges in Edge mode (corners, floor edges, door edges). The strip lies on the faces next to each edge, mitred at corners.");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##sw", ref toolStrip.Width, 0.002f, 0.005f, 1f, "Width %.3f m"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.DragFloat("##so", ref toolStrip.Offset, 0.0002f, 0f, 0.02f, "Lift %.4f m");
        ImGui.SetNextItemWidth(w2); ImGui.SliderFloat("##sac", ref toolAoCenter, 0, 1, "centre %.2f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##sao", ref toolAoOuter, 0, 1, "border %.2f");
        ImGui.Checkbox("One side only", ref toolStrip.OneSided); ImGui.SameLine(); ImGui.Checkbox("Flip", ref toolStrip.Flip);
        float w4 = (ImGui.GetContentRegionAvail().X - 12) / 4;
        if (ImGui.Button("Fake AO", new Vector2(w4, 26))) BuildStrip(cur, "fake_ao"); Theme.Tip("Soft dark corner (decal.sps).");
        ImGui.SameLine(); if (ImGui.Button("Edge dirt", new Vector2(w4, 26))) BuildStrip(cur, "edge_dirt"); Theme.Tip("Grime along edges (decal.sps, noise).");
        ImGui.SameLine(); if (ImGui.Button("Edge wear", new Vector2(w4, 26))) BuildStrip(cur, "edge_wear"); Theme.Tip("Chipped edges (decal_normal_only.sps).");
        ImGui.SameLine(); if (ImGui.Button("Smooth edge", new Vector2(-1, 26))) BuildStrip(cur, "smooth_edge"); Theme.Tip("Hard edge reads rounded (decal_normal_only.sps).");
        ImGui.SetNextItemWidth(w2); ImGui.SliderFloat("##wst", ref toolWearStrength, 0.5f, 8, "wear %.1f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##wdm", ref toolWearDamage, 0, 1, "chips %.2f");
    }

    /// <summary>The generated decal material of a strip kind (fake_ao, edge_dirt, edge_wear, smooth_edge).</summary>
    GtaMaterial StripMaterial(string kind)
    {
        bool normal = kind is "edge_wear" or "smooth_edge";
        string texName = kind switch
        {
            "fake_ao" => $"molo_fake_ao_{(int)(toolAoCenter * 100)}_{(int)(toolAoOuter * 100)}",
            "edge_dirt" => $"molo_edge_dirt_{toolSeed}",
            "edge_wear" => $"molo_edge_wear_{(int)(toolWearStrength * 10)}_{(int)(toolWearDamage * 100)}_{toolSeed}",
            _ => $"molo_smooth_edge_{(int)(toolWearStrength * 10)}",
        };
        // already made (live rebuilds of the room builder): reuse it
        var have = Scene.Materials.FirstOrDefault(x => x.Name == texName && x.DiffuseTexture != null && File.Exists(x.DiffuseTexture));
        if (have != null) return have;
        RgbaImage img = kind switch
        {
            "fake_ao" => ProcTextures.StripGradient(toolAoCenter, toolAoOuter, Vector3.Zero),
            "edge_dirt" => ProcTextures.DirtStrip(toolAoCenter, toolAoOuter, new Vector3(0.16f, 0.13f, 0.1f), toolSeed),
            "edge_wear" => ProcTextures.EdgeNormal(toolWearStrength / 8f, toolWearDamage, 6, toolSeed, toolAoCenter, toolAoOuter),
            _ => ProcTextures.EdgeNormal(toolWearStrength / 8f, 0, 6, toolSeed, toolAoCenter, toolAoOuter),
        };
        var path = SaveGenerated(texName, img, normal);
        var mat = ToolMaterial(texName, normal ? "decal_normal_only" : "decal", new Vector3(0.2f));
        mat.DiffuseTexture = path;
        if (normal) { mat.BumpTexture = path; mat.Bumpiness = 0.5f * toolWearStrength / 4f * 2f; mat.SpecularIntensity = 0.125f * toolWearStrength; }
        return mat;
    }

    void BuildStrip(SceneObject cur, string kind)
    {
        if (cur == null || Level == SubObject.Object) { SetStatus("Select edges (Edge mode) first."); return; }
        var edges = SelectedEdgesForTool(cur);
        if (edges.Count == 0) { SetStatus("Select edges first."); return; }
        var mat = StripMaterial(kind);
        Do("Build " + kind, () =>
        {
            var m = EdgeStrips.Build(cur.Mesh, edges, toolStrip, mat.Id);
            if (m.Faces.Count == 0) return;
            AddToolObject(NextName(kind), m, cur, mat, true);
        });
        toolSeed++;
        SetStatus($"{kind.Replace('_', ' ')} built on {edges.Count} edge(s).");
    }

    // ---------------------------------------------------------------- shadow & glow

    void ToolsShadowGlow()
    {
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        Ui.TextDisabled("Contact shadow (selected objects)");
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##shm", ref toolShadow.Margin, 0.005f, 0, 2, "margin %.2f m"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##sho", ref toolShadow.Opacity, 0, 1, "opacity %.2f");
        ImGui.SetNextItemWidth(w2); ImGui.SliderFloat("##shs", ref toolShadow.Softness, 0.01f, 1, "softness %.2f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##shr", ref toolShadow.Roundness, 0.5f, 8, "roundness %.1f");
        if (ImGui.Button("Add contact shadow", new Vector2(-1, 0)))
        {
            var objs = SelObjects().Where(o => o.Mesh.Faces.Count > 0).ToList();
            if (objs.Count == 0) SetStatus("Select the objects.");
            else
            {
                var key = $"molo_shadow_{(int)(toolShadow.Opacity * 100)}_{(int)(toolShadow.Softness * 100)}_{(int)(toolShadow.Roundness * 10)}";
                var path = SaveGenerated(key, ProcTextures.BlobShadow(toolShadow.Opacity, toolShadow.Softness, toolShadow.Roundness));
                var mat = ToolMaterial(key, "decal", Vector3.Zero); mat.DiffuseTexture = path;
                Do("Contact Shadow", () =>
                {
                    foreach (var o in objs)
                    {
                        var (mn, mx) = o.WorldBounds();
                        var r = DecalTools.FakeShadow(mn, mx, toolShadow);
                        r.Mesh.SetAllMaterial(mat.Id);
                        var s = Scene.Add("shadow", r.Mesh, r.Position);
                        s.Name = NextName("fake_shadow"); s.Gta.NoAutoCollision = true; s.Gta.Role = o.Gta.Role;
                    }
                });
            }
        }
        ImGui.Separator();
        Ui.TextDisabled("Sign glow (selected lettering)");
        var col = toolGlow.Colour; if (ImGui.ColorEdit3("Colour##glow", ref col)) toolGlow.Colour = col;
        ImGui.SetNextItemWidth(w2); ImGui.SliderFloat("##gcs", ref toolGlow.CoreSize, 0.002f, 0.15f, "core %.3f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##gci", ref toolGlow.CoreIntensity, 0, 2, "core int %.2f");
        ImGui.SetNextItemWidth(w2); ImGui.SliderFloat("##ghs", ref toolGlow.HaloSize, 0.01f, 0.4f, "halo %.3f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.SliderFloat("##ghi", ref toolGlow.HaloIntensity, 0, 2, "halo int %.2f");
        ImGui.SetNextItemWidth(w2); ImGui.DragFloat("##gob", ref toolGlow.OffsetBehind, 0.001f, 0.001f, 0.5f, "behind %.3f m"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1); ImGui.Combo("##gface", ref toolGlowFacing, new[] { "Faces auto", "Faces -Y", "Faces +Y", "Faces -X", "Faces +X" }, 5);
        if (ImGui.Button("Add sign glow", new Vector2(-1, 0)))
        {
            var objs = SelObjects().Where(o => o.Mesh.Faces.Count > 0).ToList();
            if (objs.Count == 0) { SetStatus("Select the lettering."); return; }
            toolGlow.Facing = toolGlowFacing switch { 1 => -Vector3.UnitY, 2 => Vector3.UnitY, 3 => -Vector3.UnitX, 4 => Vector3.UnitX, _ => null };
            var r = DecalTools.SignGlow(objs.Select(o => (o.Mesh, o.World)).ToList(), toolGlow);
            if (r.Mesh == null) { SetStatus(r.Message); return; }
            var name = NextName("sign_glow");
            var path = SaveGenerated(name + "_tex", r.Texture);
            var mat = ToolMaterial(name, "emissive_additive_alpha", toolGlow.Colour); mat.DiffuseTexture = path; mat.EmissiveMultiplier = 2f;
            Do("Sign Glow", () =>
            {
                r.Mesh.SetAllMaterial(mat.Id);
                var g = Scene.Add(name, r.Mesh, r.Position);
                g.Name = name; g.Rotation = r.Rotation; g.Gta.NoAutoCollision = true; g.Gta.Role = objs[0].Gta.Role;
            });
            SetStatus(r.Message);
        }
    }

    // ---------------------------------------------------------------- vertex colours

    void ToolsVertexColours(SceneObject cur)
    {
        float w4 = (ImGui.GetContentRegionAvail().X - 12) / 4;
        if (ColourButton("Green", new Vector4(0, 1, 0, 1), w4)) PaintVertexColour(VertexColours.Green); Theme.Tip("Everything that is not emissive.");
        ImGui.SameLine(); if (ColourButton("Dark", new Vector4(0, 0.55f, 0, 1), w4)) PaintVertexColour(VertexColours.DarkGreen);
        ImGui.SameLine(); if (ColourButton("White", Vector4.One, w4)) PaintVertexColour(VertexColours.White); Theme.Tip("Emissive: signs, screens, ads.");
        ImGui.SameLine(); if (ColourButton("Red", new Vector4(1, 0, 0, 1), w4)) PaintVertexColour(VertexColours.Red); Theme.Tip("Exterior props (trees...), otherwise black in game.");
        ImGui.Separator();
        Ui.TextDisabled("Interior gradient (floor green > dark green > blue > ceiling)");
        ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Split", ref toolVcSplit, 0.05f, 0.95f);
        if (ImGui.Button("Paint gradient on selected objects", new Vector2(-1, 0)))
        {
            var objs = SelObjects().ToList();
            Do("Interior Gradient", () => { foreach (var o in objs) { VertexColours.InteriorGradient(o.Mesh, o.World, toolVcSplit); o.Touch(); } });
        }
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Sun radius", ref toolSunRadius, 0.05f, 0.1f, 20f, "%.2f m");
        if (ImGui.Button("Sun glow around selected vertices", new Vector2(-1, 0)))
        {
            if (cur == null || Level == SubObject.Object) { SetStatus("Select the vertices of a window or door opening (Vertex mode)."); return; }
            var centres = SubSel(cur).VerticesOf(cur.Mesh, Level).Select(v => Vector3.Transform(cur.Mesh.Verts[v], cur.World)).ToList();
            var targets = SelObjects().ToList(); if (!targets.Contains(cur)) targets.Add(cur);
            Do("Sun Glow", () => { foreach (var o in targets) { VertexColours.SunGlow(o.Mesh, o.World, centres, toolSunRadius); o.Touch(); } });
        }
    }

    // ---------------------------------------------------------------- collision boxes

    void ToolsCollision()
    {
        ImGui.Checkbox("One box per part", ref toolColPerPart); ImGui.SameLine(); ImGui.Checkbox("Oriented (turns with the prop)", ref toolColOriented);
        if (ImGui.Button("Collision boxes from selection", new Vector2(-1, 0)))
        {
            var objs = SelObjects().Where(o => o.GtaType != GtaObjectType.Collision).ToList();
            if (objs.Count == 0) { SetStatus("Select the objects."); return; }
            int n = 0;
            Do("Auto Collision", () =>
            {
                foreach (var o in objs)
                {
                    int k = 1;
                    foreach (var b in AutoCollision.Boxes(o.Mesh, o.World, toolColPerPart ? AutoCollision.Mode.PerPart : AutoCollision.Mode.Single, toolColOriented))
                    {
                        var box = Primitives.Box(b.Half.X * 2, b.Half.Y * 2, b.Half.Z * 2);
                        box.Transform(Matrix4x4.CreateTranslation(0, 0, -b.Half.Z)); // centre pivot
                        var c = Scene.Add($"{o.Name}_col", box, b.Center, GtaObjectType.Collision);
                        c.Name = $"{o.Name}_col_{k++:00}"; c.Rotation = b.Rotation; c.Gta.CollisionMaterial = o.Gta.CollisionMaterial;
                        c.WireColour = new Vector3(1f, 0.55f, 0.15f);
                        n++;
                    }
                }
            });
            SetStatus($"{n} collision box(es) created.");
        }
        Ui.TextDisabled("They are added to the automatic collision at export.");
    }

    // ---------------------------------------------------------------- textures

    void ToolsTextures(SceneObject cur)
    {
        Ui.TextWrapped("DDS: converts the textures of the selected objects' materials to DDS (1024, mipmaps, DXT1 opaque / DXT5 alpha and normals) named molo_<material>_d/_n/_s, next to the source in a dds folder, and relinks the materials.");
        if (ImGui.Button("Convert to DDS", new Vector2(-1, 0))) ConvertMaterialsToDds();
        ImGui.Separator();
        Ui.TextWrapped("Maps from diffuse: builds a normal map and a spec map from the diffuse of the selected objects' materials and switches them to normal_spec.");
        if (ImGui.Button("Generate normal + spec", new Vector2(-1, 0))) GenerateMapsFromDiffuse();
    }

    IEnumerable<GtaMaterial> SelectedMaterials() =>
        SelObjects().SelectMany(o => UsedMaterials(o)).Distinct().Select(Scene.Material).Distinct();

    void ConvertMaterialsToDds()
    {
        var mats = SelectedMaterials().ToList();
        if (mats.Count == 0) { SetStatus("Select objects with textured materials."); return; }
        int ok = 0, fail = 0;
        var bank = new TextureBank { ImageLoader = LoadImageRgba, MaxSize = 1024 };
        bank.SearchFolders.AddRange(renderer.Textures.SearchFolders);
        Do("Convert to DDS", () =>
        {
            foreach (var m in mats)
            {
                string Conv(string path, TexKind kind, string suffix)
                {
                    if (string.IsNullOrEmpty(path) || path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return path;
                    try
                    {
                        if (!File.Exists(path)) { fail++; return path; }
                        var img = LoadImageRgba(path);
                        var fmt = kind == TexKind.Normal ? DdsFormat.Dxt5 : DdsEncoder.AutoFormat(img);
                        var dir = Path.Combine(Path.GetDirectoryName(path) ?? GeneratedFolder, "dds");
                        Directory.CreateDirectory(dir);
                        var baseName = TextureBank.SafeName(m.Name).Replace("molo_", "");
                        var outPath = Path.Combine(dir, $"molo_{baseName}_{suffix}.dds");
                        File.WriteAllBytes(outPath, DdsEncoder.Encode(img, fmt, 1024));
                        ok++;
                        return outPath;
                    }
                    catch { fail++; return path; }
                }
                m.DiffuseTexture = Conv(m.DiffuseTexture, TexKind.Diffuse, "d");
                m.BumpTexture = Conv(m.BumpTexture, TexKind.Normal, "n");
                m.SpecTexture = Conv(m.SpecTexture, TexKind.Specular, "s");
            }
        });
        renderer.Textures.ClearResolveCache();
        SetStatus($"DDS: {ok} converted, {fail} failed.");
    }

    void GenerateMapsFromDiffuse()
    {
        var mats = SelectedMaterials().Where(m => !string.IsNullOrEmpty(m.DiffuseTexture) && File.Exists(m.DiffuseTexture)).ToList();
        if (mats.Count == 0) { SetStatus("Select objects whose materials have a diffuse texture file."); return; }
        Do("Maps from Diffuse", () =>
        {
            foreach (var m in mats)
            {
                RgbaImage img;
                if (m.DiffuseTexture.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) { SetStatus("Maps need a PNG/JPG diffuse (convert to DDS afterwards)."); continue; }
                img = LoadImageRgba(m.DiffuseTexture);
                if (img.Width > 1024 || img.Height > 1024) img = img.Resize(Math.Min(1024, img.Width), Math.Min(1024, img.Height));
                var h = ProcTextures.HeightFromDiffuse(img);
                var n = ProcTextures.NormalFromHeight(h, img.Width, img.Height);
                var sp = ProcTextures.SpecFromDiffuse(img);
                var baseName = TextureBank.SafeName(m.Name);
                m.BumpTexture = SaveGenerated($"molo_{baseName}_n", n, true);
                m.SpecTexture = SaveGenerated($"molo_{baseName}_s", sp);
                if (m.Shader is "default" or "spec" or "normal") m.Shader = "normal_spec";
            }
        });
        SetStatus($"Normal and spec maps generated for {mats.Count} material(s).");
    }
}
