using System.Numerics;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Anim;

/// <summary>How the value goes from a key to the next one (Blender: Bezier auto-clamped / Linear / Constant).</summary>
public enum KeyInterp { Smooth, Linear, Constant }

public sealed class AnimKey
{
    public float Frame;
    public float Value;
    public KeyInterp Interp = KeyInterp.Smooth;
    public AnimKey Clone() => (AnimKey)MemberwiseClone();
}

/// <summary>One animated value (an F-curve): keys sorted by frame.</summary>
public sealed class AnimCurve
{
    public string Channel;
    public List<AnimKey> Keys = new();

    public AnimCurve Clone() => new() { Channel = Channel, Keys = Keys.Select(k => k.Clone()).ToList() };

    public AnimKey KeyAt(float frame) => Keys.FirstOrDefault(k => MathF.Abs(k.Frame - frame) < 0.01f);

    public AnimKey SetKey(float frame, float value, KeyInterp interp = KeyInterp.Smooth)
    {
        var k = KeyAt(frame);
        if (k != null) { k.Value = value; return k; }
        k = new AnimKey { Frame = frame, Value = value, Interp = interp };
        int i = Keys.FindIndex(x => x.Frame > frame);
        if (i < 0) Keys.Add(k); else Keys.Insert(i, k);
        return k;
    }

    public void Sort() => Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));

    /// <summary>Auto-clamped tangent (value per frame) at key i: no overshoot, flat on peaks and valleys and at both ends.</summary>
    float Slope(int i)
    {
        if (i <= 0 || i >= Keys.Count - 1) return 0;
        var p = Keys[i - 1]; var k = Keys[i]; var n = Keys[i + 1];
        if ((k.Value - p.Value) * (n.Value - k.Value) <= 0) return 0; // a peak or a valley: flat
        return (n.Value - p.Value) / MathF.Max(1e-4f, n.Frame - p.Frame);
    }

    public float Evaluate(float frame)
    {
        if (Keys.Count == 0) return 0;
        if (frame <= Keys[0].Frame) return Keys[0].Value;
        if (frame >= Keys[^1].Frame) return Keys[^1].Value;
        int i = 0;
        while (i < Keys.Count - 2 && Keys[i + 1].Frame <= frame) i++;
        var a = Keys[i]; var b = Keys[i + 1];
        float len = MathF.Max(1e-4f, b.Frame - a.Frame), t = (frame - a.Frame) / len;
        switch (a.Interp)
        {
            case KeyInterp.Constant: return a.Value;
            case KeyInterp.Linear: return a.Value + (b.Value - a.Value) * t;
            default:
                // cubic Hermite with auto-clamped tangents
                float m0 = Slope(i) * len, m1 = Slope(i + 1) * len;
                float t2 = t * t, t3 = t2 * t;
                return (2 * t3 - 3 * t2 + 1) * a.Value + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.Value + (t3 - t2) * m1;
        }
    }
}

/// <summary>The curves of one object (or of the camera: <see cref="AnimTrack.CameraId"/>).</summary>
public sealed class AnimTrack
{
    public const int CameraId = -1;
    public int ObjectId;
    public List<AnimCurve> Curves = new();

    public AnimCurve Curve(string channel, bool create = false)
    {
        var c = Curves.FirstOrDefault(x => x.Channel == channel);
        if (c == null && create) { c = new AnimCurve { Channel = channel }; Curves.Add(c); }
        return c;
    }
    public bool Has(string channel) => Curves.Any(c => c.Channel == channel && c.Keys.Count > 0);
    public AnimTrack Clone() => new() { ObjectId = ObjectId, Curves = Curves.Select(c => c.Clone()).ToList() };
}

/// <summary>Channel names (Blender-like data paths).</summary>
public static class Ch
{
    public const string LocX = "loc.x", LocY = "loc.y", LocZ = "loc.z";
    public const string RotX = "rot.x", RotY = "rot.y", RotZ = "rot.z"; // Euler XYZ, degrees (several turns allowed)
    public const string ScaleX = "scale.x", ScaleY = "scale.y", ScaleZ = "scale.z";
    /// <summary>UV animation (GTA globalAnimUV0/1): offset, scale, rotation (degrees) of the texture.</summary>
    public const string UvU = "uv.u", UvV = "uv.v", UvSU = "uv.su", UvSV = "uv.sv", UvRot = "uv.rot";
    /// <summary>Camera: target, yaw / pitch (degrees), distance.</summary>
    public const string CamX = "cam.x", CamY = "cam.y", CamZ = "cam.z", CamYaw = "cam.yaw", CamPitch = "cam.pitch", CamDist = "cam.dist";
    public static readonly string[] Loc = { LocX, LocY, LocZ }, Rot = { RotX, RotY, RotZ }, Scale = { ScaleX, ScaleY, ScaleZ };
    public static readonly string[] Uv = { UvU, UvV, UvSU, UvSV, UvRot };
    public static readonly string[] Cam = { CamX, CamY, CamZ, CamYaw, CamPitch, CamDist };
    public static string Label(string c) => c switch
    {
        LocX => "X Location", LocY => "Y Location", LocZ => "Z Location", RotX => "X Rotation", RotY => "Y Rotation", RotZ => "Z Rotation",
        ScaleX => "X Scale", ScaleY => "Y Scale", ScaleZ => "Z Scale", UvU => "UV offset U", UvV => "UV offset V", UvSU => "UV scale U", UvSV => "UV scale V", UvRot => "UV rotation",
        CamX => "Camera X", CamY => "Camera Y", CamZ => "Camera Z", CamYaw => "Camera yaw", CamPitch => "Camera pitch", CamDist => "Camera distance", _ => c,
    };
}

/// <summary>The animation of a scene: frame range, rate and every track (saved with the scene).</summary>
public sealed class SceneAnim
{
    public int Start = 0, End = 120;
    public float Fps = 30;
    public List<AnimTrack> Tracks = new();

    public AnimTrack Track(int objectId, bool create = false)
    {
        var t = Tracks.FirstOrDefault(x => x.ObjectId == objectId);
        if (t == null && create) { t = new AnimTrack { ObjectId = objectId }; Tracks.Add(t); }
        return t;
    }
    public bool IsEmpty => Tracks.All(t => t.Curves.All(c => c.Keys.Count == 0));
    public SceneAnim Clone() => new() { Start = Start, End = End, Fps = Fps, Tracks = Tracks.Select(t => t.Clone()).ToList() };

    /// <summary>Every keyed frame of these objects (or all), sorted.</summary>
    public List<float> KeyFrames(IEnumerable<int> ids = null)
    {
        var set = ids?.ToHashSet();
        return Tracks.Where(t => set == null || set.Contains(t.ObjectId)).SelectMany(t => t.Curves).SelectMany(c => c.Keys).Select(k => k.Frame)
            .Distinct().OrderBy(f => f).ToList();
    }

    // ---------------------------------------------------------------- objects

    public static Quaternion FromEulerDeg(Vector3 d)
    {
        const float r = MathF.PI / 180;
        // Blender XYZ Euler: X first, then Y, then Z (world axes)
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, d.Z * r) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, d.Y * r) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, d.X * r));
    }

    /// <summary>Euler XYZ (degrees) of a rotation, the one closest to <paramref name="near"/> (keeps a spin going past 180°).</summary>
    public static Vector3 ToEulerDeg(Quaternion q, Vector3? near = null)
    {
        q = Quaternion.Normalize(q);
        var m = Matrix4x4.CreateFromQuaternion(q);
        // R = Rz * Ry * Rx (column vectors) ; System.Numerics is row-vector: m = Rx * Ry * Rz transposed form
        float sy = -m.M13;
        float y = MathF.Asin(Math.Clamp(sy, -1, 1));
        float x, z;
        if (MathF.Abs(sy) < 0.9999f) { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); }
        else { x = MathF.Atan2(-m.M32, m.M22); z = 0; }
        var e = new Vector3(x, y, z) * (180 / MathF.PI);
        if (near is { } n)
        {
            float Wrap(float a, float t) { while (a - t > 180) a -= 360; while (a - t < -180) a += 360; return a; }
            e = new Vector3(Wrap(e.X, n.X), Wrap(e.Y, n.Y), Wrap(e.Z, n.Z));
        }
        return e;
    }

    /// <summary>The UV rows (GTA globalAnimUV0 / 1: u' = r0.x u + r0.y v + r0.z) for offset, scale and rotation about the texture centre.</summary>
    public static (Vector3 R0, Vector3 R1) UvRows(float u, float v, float su, float sv, float rotDeg)
    {
        float a = rotDeg * MathF.PI / 180, c = MathF.Cos(a), s = MathF.Sin(a);
        // about (0.5, 0.5): p' = R S (p - 0.5) + 0.5 + offset
        float r00 = c * su, r01 = -s * sv, r10 = s * su, r11 = c * sv;
        float tx = 0.5f - (r00 * 0.5f + r01 * 0.5f) + u, ty = 0.5f - (r10 * 0.5f + r11 * 0.5f) + v;
        return (new Vector3(r00, r01, tx), new Vector3(r10, r11, ty));
    }

    /// <summary>Puts every animated object of the scene where its curves say at this frame. Returns the objects moved.</summary>
    public int Apply(Scene s, float frame)
    {
        int n = 0;
        foreach (var t in Tracks)
        {
            if (t.ObjectId == AnimTrack.CameraId) continue;
            var o = s.Find(t.ObjectId); if (o == null) continue;
            bool moved = false;
            float V(string ch, float cur) { var c = t.Curve(ch); return c != null && c.Keys.Count > 0 ? c.Evaluate(frame) : cur; }
            if (Ch.Loc.Any(t.Has)) { o.Position = new Vector3(V(Ch.LocX, o.Position.X), V(Ch.LocY, o.Position.Y), V(Ch.LocZ, o.Position.Z)); moved = true; }
            if (Ch.Rot.Any(t.Has))
            {
                var cur = ToEulerDeg(o.Rotation);
                o.Rotation = FromEulerDeg(new Vector3(V(Ch.RotX, cur.X), V(Ch.RotY, cur.Y), V(Ch.RotZ, cur.Z))); moved = true;
            }
            if (Ch.Scale.Any(t.Has)) { o.Scale = new Vector3(V(Ch.ScaleX, o.Scale.X), V(Ch.ScaleY, o.Scale.Y), V(Ch.ScaleZ, o.Scale.Z)); moved = true; }
            if (Ch.Uv.Any(t.Has))
            {
                var (r0, r1) = UvRows(V(Ch.UvU, 0), V(Ch.UvV, 0), V(Ch.UvSU, 1), V(Ch.UvSV, 1), V(Ch.UvRot, 0));
                o.UvRow0 = new Vector4(r0, 1); o.UvRow1 = new Vector4(r1, 1);
            }
            if (moved) n++;
        }
        return n;
    }

    /// <summary>Keys the current state of an object on these channels at a frame.</summary>
    public void KeyObject(SceneObject o, float frame, IEnumerable<string> channels)
    {
        var t = Track(o.Id, true);
        // rotation keyed next to the previous key, so a spin past 180° is kept
        var prevRot = new Vector3(t.Curve(Ch.RotX)?.Evaluate(frame) ?? 0, t.Curve(Ch.RotY)?.Evaluate(frame) ?? 0, t.Curve(Ch.RotZ)?.Evaluate(frame) ?? 0);
        var e = ToEulerDeg(o.Rotation, prevRot);
        foreach (var ch in channels)
        {
            float v = ch switch
            {
                Ch.LocX => o.Position.X, Ch.LocY => o.Position.Y, Ch.LocZ => o.Position.Z,
                Ch.RotX => e.X, Ch.RotY => e.Y, Ch.RotZ => e.Z,
                Ch.ScaleX => o.Scale.X, Ch.ScaleY => o.Scale.Y, Ch.ScaleZ => o.Scale.Z,
                _ => t.Curve(ch)?.Evaluate(frame) ?? (ch is Ch.UvSU or Ch.UvSV ? 1 : 0),
            };
            t.Curve(ch, true).SetKey(frame, v);
        }
    }

    public int DeleteKeys(int objectId, float frame)
    {
        var t = Track(objectId); if (t == null) return 0;
        int n = 0;
        foreach (var c in t.Curves) n += c.Keys.RemoveAll(k => MathF.Abs(k.Frame - frame) < 0.01f);
        t.Curves.RemoveAll(c => c.Keys.Count == 0);
        if (t.Curves.Count == 0) Tracks.Remove(t);
        return n;
    }
}
