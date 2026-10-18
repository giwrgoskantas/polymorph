using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Addons;
using MoloMax.Core.Mesh;

namespace MoloExampleAddon;

/// <summary>A small add-on: a command that scatters crates around the 3D cursor, and a panel with its settings.</summary>
public sealed class ExampleAddon : IMoloAddon
{
    public string Name => "Molo Example";
    public string Version => "1.0";
    public string Author => "Molo Modding";
    public string Description => "Scatter crates (example of a command and a panel).";

    IAddonHost host;
    int count = 8;
    float radius = 3f, size = 0.6f;

    public void Register(IAddonHost h)
    {
        host = h;
        h.AddCommand("Scatter Crates", "Add-ons", Scatter, "Adds crates in a circle");
        h.AddPanel("Scatter Crates", () =>
        {
            ImGui.SliderInt("Count", ref count, 1, 100);
            ImGui.DragFloat("Radius", ref radius, 0.05f, 0.1f, 100f, "%.2f m");
            ImGui.DragFloat("Crate size", ref size, 0.01f, 0.05f, 10f, "%.2f m");
            if (ImGui.Button("Scatter", new Vector2(-1, 0))) Scatter();
        });
    }

    void Scatter()
    {
        var rnd = new Random();
        host.Undoable("Scatter Crates", () =>
        {
            for (int i = 0; i < count; i++)
            {
                float a = (float)(rnd.NextDouble() * Math.PI * 2), r = (float)Math.Sqrt(rnd.NextDouble()) * radius;
                var o = host.Scene.Add("crate", Primitives.Box(size, size, size), new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0));
                o.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)rnd.NextDouble() * 6.28f);
            }
        });
        host.Status($"{count} crates added.");
    }
}
