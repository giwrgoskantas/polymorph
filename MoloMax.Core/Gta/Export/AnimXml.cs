using System.Globalization;
using System.Numerics;
using System.Text;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Gta.Export;

/// <summary>One UV animation clip: a model, a shader index in its drawable, and the animation settings.</summary>
public sealed record UvClip(string Model, int ShaderIndex, GtaAnim Anim);

/// <summary>
/// UV animations as a CodeWalker clip dictionary (.ycd.xml), 30 fps like every Molo animation.
/// Clip hash = joaat(model) + shaderIndex + 1 (how the game finds a UV clip for an archetype),
/// animation "model_uv_N", tracks 17 / 18 (globalAnimUV0 / globalAnimUV1) on bone 0.
/// </summary>
public static class AnimXml
{
    public const int Fps = 30;
    static string F(float v) => Xw.F(v);

    /// <summary>Frames of a clip (last frame equals the first so it loops) and its duration.</summary>
    public static (int Frames, float Duration) Timing(GtaAnim a)
    {
        if (a.Kind == UvAnimKind.Spritesheet)
        {
            int cells = Math.Max(1, a.Cols) * Math.Max(1, a.Rows), n = cells * Math.Max(1, a.HoldFrames);
            return (n + 1, n / (float)Fps);
        }
        int frames = Math.Max(2, (int)MathF.Round(MathF.Max(0.1f, a.Duration) * Fps)) + 1;
        return (frames, (frames - 1) / (float)Fps);
    }

    /// <summary>The two UV rows at a frame: u' = a.x u + a.y v + a.z, v' = b.x u + b.y v + b.z (DirectX texcoords).</summary>
    public static (Vector3 Uv0, Vector3 Uv1) Evaluate(GtaAnim a, int frame, int frames)
    {
        if (a.Kind == UvAnimKind.Spritesheet)
        {
            int c = Math.Max(1, a.Cols), r = Math.Max(1, a.Rows), cells = c * r;
            int cell = frame / Math.Max(1, a.HoldFrames);
            if (frame == frames - 1) cell = 0; // loop
            cell %= cells;
            int col = cell % c, row = cell / c;
            return (new Vector3(1f / c, 0, (float)col / c), new Vector3(0, 1f / r, (float)row / r));
        }
        float t = frames <= 1 ? 0 : (float)frame / (frames - 1);
        // keep offsets increasing over the loop (1.0 wraps to 0 with a repeating sampler)
        return (new Vector3(1, 0, a.ScrollU * t), new Vector3(0, 1, a.ScrollV * t));
    }

    public static uint ClipHash(string model, int shaderIndex) => MetaXml.Jenkins(model) + (uint)(shaderIndex + 1);

    public static string ClipDictionary(IEnumerable<UvClip> clips)
    {
        var list = clips.ToList();
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ClipDictionary>\n <Clips>\n");
        foreach (var c in list)
        {
            var (frames, dur) = Timing(c.Anim);
            string anim = $"{c.Model}_uv_{c.ShaderIndex}";
            sb.Append("  <Item>\n");
            sb.Append($"   <Hash>hash_{ClipHash(c.Model, c.ShaderIndex):X8}</Hash>\n");
            sb.Append($"   <Name>pack:/{anim}.clip</Name>\n");
            sb.Append("   <Type value=\"Animation\" />\n   <Unknown30 value=\"0\" />\n   <Tags />\n   <Properties />\n");
            sb.Append($"   <AnimationHash>{anim}</AnimationHash>\n");
            sb.Append($"   <StartTime value=\"0\" />\n   <EndTime value=\"{F(dur)}\" />\n   <Rate value=\"1\" />\n");
            sb.Append("  </Item>\n");
        }
        sb.Append(" </Clips>\n <Animations>\n");
        foreach (var c in list)
        {
            var (frames, dur) = Timing(c.Anim);
            string anim = $"{c.Model}_uv_{c.ShaderIndex}";
            var rows = Enumerable.Range(0, frames).Select(f => Evaluate(c.Anim, f, frames)).ToList();
            sb.Append("  <Item>\n");
            sb.Append($"   <Hash>{anim}</Hash>\n");
            sb.Append("   <Unknown10 value=\"1\" />\n");
            sb.Append($"   <FrameCount value=\"{frames}\" />\n");
            sb.Append($"   <SequenceFrameLimit value=\"{frames + 2}\" />\n");
            sb.Append($"   <Duration value=\"{F(dur)}\" />\n");
            sb.Append("   <Unknown1C>hash_6B002400</Unknown1C>\n");
            sb.Append("   <BoneIds>\n");
            sb.Append("    <Item><BoneId value=\"0\" /><Track value=\"17\" /><Unk0 value=\"0\" /></Item>\n");
            sb.Append("    <Item><BoneId value=\"0\" /><Track value=\"18\" /><Unk0 value=\"0\" /></Item>\n");
            sb.Append("   </BoneIds>\n");
            sb.Append($"   <Sequences>\n    <Item>\n     <Hash>hash_00000000</Hash>\n     <FrameCount value=\"{frames}\" />\n     <SequenceData>\n");
            for (int row = 0; row < 2; row++)
            {
                sb.Append("      <Item>\n       <Channels>\n");
                for (int comp = 0; comp < 3; comp++)
                {
                    var vals = rows.Select(r => { var v = row == 0 ? r.Uv0 : r.Uv1; return comp == 0 ? v.X : comp == 1 ? v.Y : v.Z; }).ToList();
                    bool constant = vals.All(v => MathF.Abs(v - vals[0]) < 1e-7f);
                    if (constant) sb.Append($"        <Item><Type value=\"StaticFloat\" /><Value value=\"{F(vals[0])}\" /></Item>\n");
                    else sb.Append($"        <Item><Type value=\"RawFloat\" /><Values>{string.Join(" ", vals.Select(F))}</Values></Item>\n");
                }
                sb.Append("       </Channels>\n      </Item>\n");
            }
            sb.Append("     </SequenceData>\n    </Item>\n   </Sequences>\n  </Item>\n");
        }
        sb.Append(" </Animations>\n</ClipDictionary>\n");
        return sb.ToString();
    }
}
