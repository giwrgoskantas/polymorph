using System.Numerics;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>One of the four 3ds Max viewports.</summary>
public sealed class Viewport
{
    public Camera Camera = new();
    public ShadingMode Shading = ShadingMode.Realistic;
    public bool EdgedFaces;
    public bool WireframeOnly;
    public float X, Y, W, H;

    public Viewport(ViewKind kind)
    {
        Camera.SetView(kind);
        if (kind != ViewKind.Perspective) Shading = ShadingMode.Shaded;
    }

    public bool Contains(Vector2 p) => p.X >= X && p.Y >= Y && p.X < X + W && p.Y < Y + H;
    public Vector2 Local(Vector2 screen) => new(screen.X - X, screen.Y - Y);
    public Vector2 ToScreen(Vector2 local) => new(local.X + X, local.Y + Y);
    public float Aspect => W / MathF.Max(H, 1);

    public string ShadingLabel => WireframeOnly ? "Wireframe" : EdgedFaces ? ShadingName + " + Edged Faces" : ShadingName;

    string ShadingName => Shading switch
    {
        ShadingMode.Realistic => "Realistic",
        ShadingMode.Shaded => "Shaded",
        ShadingMode.VertexColour => "Vertex Color",
        ShadingMode.Normals => "Normals",
        ShadingMode.UVChecker => "UV Checker",
        ShadingMode.Unlit => "Flat Color",
        _ => "",
    };
}
