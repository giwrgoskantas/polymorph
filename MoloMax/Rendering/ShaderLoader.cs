using System.Reflection;
using SharpDX.D3DCompiler;

namespace MoloMax.Rendering;

/// <summary>Compiles the embedded HLSL at start-up. A Shaders folder beside the exe overrides the embedded copy (handy while tuning).</summary>
public static class ShaderLoader
{
    public static string Source(string name)
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", name);
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException("Embedded shader missing: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static ShaderBytecode Compile(string name, string entry, string profile)
    {
        var src = Source(name);
        var res = ShaderBytecode.Compile(src, entry, profile, ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor, EffectFlags.None, name);
        if (res.Bytecode == null || res.HasErrors)
            throw new InvalidOperationException($"Shader {name}:{entry} failed to compile:\n{res.Message}");
        return res.Bytecode;
    }
}
