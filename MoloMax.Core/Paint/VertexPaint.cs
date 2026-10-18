using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Paint;

/// <summary>
/// Vertex colour painting (the GTA vertex colour 0 of each face corner): brush strokes in object space and fills.
/// Rules from the game: green on everything that is not emissive, white on emissive parts, red on exterior props.
/// </summary>
public static class VertexPaint
{
    public static readonly (string Name, Vector4 Colour, string Tip)[] Presets =
    {
        ("Green", new(0, 1, 0, 1), "Everything that is not emissive (interiors)"),
        ("Dark green", new(0, 0.45f, 0, 1), "Darker ambient, still correct for GTA"),
        ("White", new(1, 1, 1, 1), "Emissive parts: signs, screens, adverts"),
        ("Red", new(1, 0, 0, 1), "Exterior props (trees...), otherwise black in game"),
    };

    /// <summary>One print: every corner within the radius moves towards the colour. Returns the number of corners changed.</summary>
    public static int Stamp(PolyMesh m, Vector3 center, Vector3 normal, float radius, Vector4 colour, float strength, float hardness, bool backfaceCull = true)
    {
        int changed = 0; float inner = radius * Math.Clamp(hardness, 0, 0.99f);
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            if (backfaceCull && Vector3.Dot(m.FaceNormal(f), normal) < 0.05f) continue;
            for (int i = 0; i < f.V.Length; i++)
            {
                float d = Vector3.Distance(m.Verts[f.V[i]], center);
                if (d > radius) continue;
                float a = (d <= inner ? 1 : 1 - Smooth((d - inner) / MathF.Max(radius - inner, 1e-6f))) * strength;
                if (a <= 0) continue;
                f.Col[i] = Vector4.Lerp(f.Col[i], colour, a);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Fills faces (all corners) with a colour.</summary>
    public static void Fill(PolyMesh m, IEnumerable<int> faces, Vector4 colour)
    {
        foreach (var fi in faces) { var f = m.Faces[fi]; for (int i = 0; i < f.Col.Length; i++) f.Col[i] = colour; }
    }

    /// <summary>Faces of the connected element of a face.</summary>
    public static IEnumerable<int> ElementOf(PolyMesh m, int face)
    {
        var el = TexelMap.MeshElements(m);
        if (face < 0 || face >= el.Length) yield break;
        for (int i = 0; i < el.Length; i++) if (el[i] == el[face]) yield return i;
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
}
