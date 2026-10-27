using System.Numerics;
using System.Text.Json.Serialization;

namespace MoloMax.Core.Paint;

/// <summary>
/// Node material of a fill layer (ArmorPaint / Substance Designer style, CPU): nodes joined by links, evaluated per texel.
/// The Output node gives the layer its colour, roughness, relief and metal. Every socket carries a colour; a single value
/// is the grey colour (x = y = z).
/// </summary>
public sealed class NodeGraph
{
    public List<GNode> Nodes = new();
    public List<GLink> Links = new();
    public int NextId = 1;
    public Vector2 Pan;

    public GNode Add(string type, Vector2 pos)
    {
        var d = NodeTypes.Get(type);
        var n = new GNode { Id = NextId++, Type = type, Pos = pos, C1 = d.C1, C2 = d.C2, A = d.A, B = d.B, S = d.S, Mode = d.Mode };
        Nodes.Add(n);
        return n;
    }

    public GNode Find(int id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>Joins an output to an input (an input takes one link: the old one goes). Refuses a loop.</summary>
    public bool Link(int fromNode, int fromOut, int toNode, int toIn)
    {
        if (fromNode == toNode || DependsOn(fromNode, toNode)) return false;
        Links.RemoveAll(l => l.ToNode == toNode && l.ToIn == toIn);
        Links.Add(new GLink { FromNode = fromNode, FromOut = fromOut, ToNode = toNode, ToIn = toIn });
        return true;
    }

    /// <summary>True when <paramref name="node"/> reads (directly or not) from <paramref name="target"/>.</summary>
    bool DependsOn(int node, int target)
    {
        var seen = new HashSet<int>(); var stack = new Stack<int>(); stack.Push(node);
        while (stack.Count > 0)
        {
            int n = stack.Pop(); if (n == target) return true;
            if (!seen.Add(n)) continue;
            foreach (var l in Links) if (l.ToNode == n) stack.Push(l.FromNode);
        }
        return false;
    }

    public void Remove(GNode n)
    {
        if (n.Type == "Output") return;
        Nodes.Remove(n); Links.RemoveAll(l => l.FromNode == n.Id || l.ToNode == n.Id);
    }

    public NodeGraph Copy()
    {
        var g = new NodeGraph { NextId = NextId, Pan = Pan };
        foreach (var n in Nodes) g.Nodes.Add(n.Copy());
        foreach (var l in Links) g.Links.Add(new GLink { FromNode = l.FromNode, FromOut = l.FromOut, ToNode = l.ToNode, ToIn = l.ToIn });
        return g;
    }

    /// <summary>A starting graph: noise into a colour ramp into the output.</summary>
    public static NodeGraph Starter()
    {
        var g = new NodeGraph();
        var noise = g.Add("Noise", new Vector2(20, 40));
        var ramp = g.Add("Color ramp", new Vector2(250, 40));
        var outp = g.Add("Output", new Vector2(480, 40));
        g.Link(noise.Id, 0, ramp.Id, 0);
        g.Link(ramp.Id, 0, outp.Id, 0);
        return g;
    }

    // ------------------------------------------------------------------ evaluation

    [JsonIgnore] Dictionary<int, GNode> byId;
    [JsonIgnore] Dictionary<(int Node, int In), GLink> linkTo;
    [JsonIgnore] public int Version;

    /// <summary>Call after any change (the lookups are rebuilt on the next evaluation).</summary>
    public void Changed() { byId = null; Version++; }

    void Index()
    {
        byId = Nodes.ToDictionary(n => n.Id);
        linkTo = new();
        foreach (var l in Links) if (byId.ContainsKey(l.FromNode) && byId.ContainsKey(l.ToNode)) linkTo[(l.ToNode, l.ToIn)] = l;
    }

    /// <summary>What the Output node gets at a texel: colour, roughness, relief (0.5 = flat), metal. Null without an Output.</summary>
    public (Vector3 Color, float Rough, float Height, float Metal)? Evaluate(TextureSet ts, int t)
    {
        if (byId == null) Index();
        var outp = Nodes.FirstOrDefault(n => n.Type == "Output");
        if (outp == null) return null;
        var memo = new Dictionary<(int, int), Vector3>();
        Vector3 In(GNode n, int i, Vector3 dflt) => linkTo.TryGetValue((n.Id, i), out var l) ? Out(byId[l.FromNode], l.FromOut) : dflt;
        Vector3 Out(GNode n, int o)
        {
            if (memo.TryGetValue((n.Id, o), out var v)) return v;
            memo[(n.Id, o)] = Vector3.Zero; // a loop reads 0
            v = Calc(ts, t, n, o, (i, d) => In(n, i, d));
            memo[(n.Id, o)] = v;
            return v;
        }
        var c = In(outp, 0, new Vector3(0.5f));
        var r = In(outp, 1, new Vector3(outp.A)).X;
        var h = In(outp, 2, new Vector3(0.5f)).X;
        var m = In(outp, 3, new Vector3(outp.B)).X;
        return (Vector3.Clamp(c, Vector3.Zero, Vector3.One), Math.Clamp(r, 0, 1), Math.Clamp(h, 0, 1), Math.Clamp(m, 0, 1));
    }

    static Vector3 G(float v) => new(v);
    static float Luma(Vector3 c) => c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f;

    static Vector3 Calc(TextureSet ts, int t, GNode n, int o, Func<int, Vector3, Vector3> inp)
    {
        var map = ts.Map; var p = map.Pos[t]; var nr = map.Nrm[t];
        switch (n.Type)
        {
            case "Value": return G(n.A);
            case "Color": return n.C1;
            case "Mesh":
                return o switch
                {
                    0 => G(1 - map.Ao[t]),                                           // dirt (ambient occlusion)
                    1 => G(MathF.Max(0, map.Curv[t])),                               // edges
                    2 => G(MathF.Max(0, -map.Curv[t])),                              // cavities
                    3 => G(nr.Z * 0.5f + 0.5f),                                      // facing up
                    _ => G(Math.Clamp((p.Z - map.Min.Z) / MathF.Max(map.Max.Z - map.Min.Z, 1e-5f), 0, 1)), // height
                };
            case "Noise": return G(ProcTex.Perlin(p, n.S, n.A, n.B, n.C1.X, n.Mode));
            case "Voronoi":
                {
                    var (f1, edge, cell) = ProcTex.Voronoi(p, n.S, 1, n.Mode);
                    return o switch { 0 => G(cell), 1 => G(1 - MathF.Min(1, edge / MathF.Max(n.A, 1e-3f))), _ => G(f1) };
                }
            case "Pattern": return G(ProcTex.Pattern((FillPattern)Math.Clamp(n.Mode, 1, 8), p, nr, n.S, 0));
            case "Bricks":
                {
                    var (mortar, tint) = ProcTex.Brick(ProcTex.PlaneUv(p, nr), n.S, n.A);
                    return o == 0 ? G(mortar) : G(tint);
                }
            case "Color ramp":
                {
                    float f = Luma(inp(0, G(0.5f)));
                    float lo = n.A, hi = MathF.Max(n.B, lo + 1e-3f);
                    float k = Math.Clamp((f - lo) / (hi - lo), 0, 1);
                    return Vector3.Lerp(n.C1, n.C2, k);
                }
            case "Mix":
                {
                    var a = inp(0, n.C1); var b = inp(1, n.C2); float f = Math.Clamp(Luma(inp(2, G(n.A))), 0, 1);
                    return TextureSet.BlendPublic(a, b, f, (PaintBlend)Math.Clamp(n.Mode, 0, Enum.GetValues<PaintBlend>().Length - 1));
                }
            case "Math":
                {
                    var a = inp(0, G(n.A)); var b = inp(1, G(n.B));
                    return n.Mode switch
                    {
                        0 => a + b, 1 => a - b, 2 => a * b, 3 => Vector3.Max(a, b), 4 => Vector3.Min(a, b),
                        5 => new Vector3(MathF.Pow(MathF.Max(a.X, 0), b.X), MathF.Pow(MathF.Max(a.Y, 0), b.X), MathF.Pow(MathF.Max(a.Z, 0), b.X)),
                        6 => Vector3.One - a,
                        _ => Vector3.Clamp((a - new Vector3(0.5f)) * MathF.Max(b.X, 0) + new Vector3(0.5f), Vector3.Zero, Vector3.One), // contrast
                    };
                }
            case "Levels":
                {
                    var a = inp(0, G(0.5f)); float lo = n.A, hi = MathF.Max(n.B, lo + 1e-3f), gm = MathF.Max(n.S, 0.01f);
                    float L(float x) => MathF.Pow(Math.Clamp((x - lo) / (hi - lo), 0, 1), 1 / gm);
                    return new Vector3(L(a.X), L(a.Y), L(a.Z));
                }
            case "Warp":
                {
                    // the position under the noise moved by the amount: pattern / noise / voronoi read it through this node
                    var q = ProcTex.Warp(p, n.A, n.S, n.Mode);
                    return o == 0 ? G(ProcTex.Perlin(q, n.B, 4, 0.5f, 0, n.Mode)) : G(ProcTex.Voronoi(q, n.B, 1, n.Mode).Cell);
                }
            default: return Vector3.Zero;
        }
    }
}

public sealed class GNode
{
    public int Id; public string Type; public Vector2 Pos;
    /// <summary>Settings (meaning depends on the type, see NodeTypes): two colours, two values, a scale, a mode / seed.</summary>
    public Vector3 C1, C2; public float A, B, S = 1; public int Mode;
    public GNode Copy() => (GNode)MemberwiseClone();
}

public sealed class GLink { public int FromNode, FromOut, ToNode, ToIn; }

/// <summary>The node types: sockets and what the settings mean.</summary>
public static class NodeTypes
{
    public sealed record Def(string Type, string Group, string[] Inputs, string[] Outputs, Vector3 C1, Vector3 C2, float A, float B, float S, int Mode, string Tip);

    public static readonly Def[] All =
    {
        new("Output", "Output", new[] { "Colour", "Roughness", "Relief", "Metal" }, Array.Empty<string>(), default, default, 0.6f, 0, 1, 0, "What the layer shows. Unplugged roughness / metal use the values below."),
        new("Color", "Input", Array.Empty<string>(), new[] { "Colour" }, new(0.6f, 0.3f, 0.2f), default, 0, 0, 1, 0, "A colour."),
        new("Value", "Input", Array.Empty<string>(), new[] { "Value" }, default, default, 0.5f, 0, 1, 0, "A number from 0 to 1."),
        new("Mesh", "Input", Array.Empty<string>(), new[] { "Dirt (AO)", "Edges", "Cavities", "Facing up", "Height" }, default, default, 0, 0, 1, 0, "What the model's shape gives: dirt in corners, worn edges, cavities, top faces, height."),
        new("Noise", "Texture", Array.Empty<string>(), new[] { "Fac" }, new(0, 0, 0), default, 4, 0.5f, 4, 0, "Perlin noise (scale, detail, roughness, distortion in the colour's red)."),
        new("Voronoi", "Texture", Array.Empty<string>(), new[] { "Cells", "Cracks", "Distance" }, default, default, 0.08f, 0, 5, 0, "Cells: a random value per cell; cracks: the borders (width A)."),
        new("Bricks", "Texture", Array.Empty<string>(), new[] { "Mortar", "Tint" }, default, default, 0.02f, 0, 12, 0, "Bricks (rows per metre), mortar width A."),
        new("Pattern", "Texture", Array.Empty<string>(), new[] { "Fac" }, default, default, 0, 0, 4, 4, "Checker, waves, wood rings, magic..."),
        new("Warp", "Texture", Array.Empty<string>(), new[] { "Noise", "Cells" }, default, default, 0.05f, 4, 2, 0, "Noise / cells read through a wobbly position (amount A metres, size B)."),
        new("Color ramp", "Colour", new[] { "Fac" }, new[] { "Colour" }, new(0.1f, 0.07f, 0.05f), new(0.85f, 0.75f, 0.6f), 0, 1, 1, 0, "Dark values become the first colour, bright ones the second (between A and B)."),
        new("Mix", "Colour", new[] { "A", "B", "Fac" }, new[] { "Result" }, new(0.5f, 0.5f, 0.5f), new(0.2f, 0.2f, 0.2f), 0.5f, 0, 1, 0, "Blends B over A by Fac, with a blend mode."),
        new("Math", "Converter", new[] { "A", "B" }, new[] { "Result" }, default, default, 0.5f, 0.5f, 1, 2, "Add, subtract, multiply, max, min, power, invert, contrast."),
        new("Levels", "Converter", new[] { "In" }, new[] { "Out" }, default, default, 0, 1, 1, 0, "Black point A, white point B, gamma."),
    };

    public static Def Get(string type) => All.FirstOrDefault(d => d.Type == type) ?? All[1];
    public static readonly string[] MathOps = { "Add", "Subtract", "Multiply", "Max", "Min", "Power", "Invert", "Contrast" };
    public static readonly string[] PatternNames = { "-", "Cells", "Cracks", "Bricks", "Checker", "Waves", "Wood rings", "Magic", "Noise" };
}
