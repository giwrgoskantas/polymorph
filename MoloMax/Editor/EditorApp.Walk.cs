using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;

namespace MoloMax.Editor;

/// <summary>
/// Game-style camera ("fly"): hold the right mouse button in a viewport and move the mouse to look around, then
/// Z/W forward, S back, Q/A left, D right, E / Space up, C down (AZERTY and QWERTY), Shift faster, the wheel sets the speed.
/// Without the right button the arrows do the same (Up / Down forward / back, Left / Right side step, Page Up / Down rise / sink).
/// A right click without moving still opens the context menu. Moves are smoothed (speeds up and slows down like a game).
/// </summary>
public sealed partial class EditorApp
{
    public float WalkSpeed = 4f; // m/s in MODEL; MAP is a city: faster
    /// <summary>Speed factor set with the wheel while flying (x0.1 .. x20).</summary>
    public float FlySpeedFactor = 1f;
    Vector3 flyVel; double flySpeedShownAt = -10;
    /// <summary>The right button is down in a viewport (fly look): letters move the camera instead of running shortcuts.</summary>
    bool FlyKeysActive => nav == NavMode.Fly || rmbTracking;
    float BaseFlySpeed => (MapMode ? 30f : WalkSpeed) * FlySpeedFactor;

    void WalkCamera(float dt)
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput || ModalActive || transforming || dt <= 0) { flyVel = Vector3.Zero; return; }
        bool fly = FlyKeysActive;
        bool K(ImGuiKey k) => ImGui.IsKeyDown(k);
        // edit mode with a sub-object selection: Up / Down grow / shrink it instead of moving the camera
        bool arrowsSelect = Level != SubObject.Object && Current != null && !fly;
        bool fwd = K(ImGuiKey.UpArrow) && !arrowsSelect || fly && (K(ImGuiKey.Z) || K(ImGuiKey.W));
        bool back = K(ImGuiKey.DownArrow) && !arrowsSelect || fly && K(ImGuiKey.S);
        bool left = K(ImGuiKey.LeftArrow) || fly && (K(ImGuiKey.Q) || K(ImGuiKey.A));
        bool right = K(ImGuiKey.RightArrow) || fly && K(ImGuiKey.D);
        bool rise = K(ImGuiKey.PageUp) || fly && (K(ImGuiKey.E) || K(ImGuiKey.Space));
        bool sink = K(ImGuiKey.PageDown) || fly && K(ImGuiKey.C);
        bool any = fwd || back || left || right || rise || sink;
        if (any && !fly)
        {
            // Industry keymap: Up / Down grow / shrink the selection in edit mode; lists under the mouse use the arrows
            if (Level != SubObject.Object && Settings.Keymap == Keymap.Industry && (fwd || back)) any = false;
            if (io.WantCaptureMouse && ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)) any = false;
        }
        if (!any && flyVel.LengthSquared() < 1e-4f) { flyVel = Vector3.Zero; return; }
        WalkMove(any && fwd, any && back, any && left, any && right, any && rise, any && sink, false, io.KeyShift, dt, fly);
    }

    /// <summary>One step of the game camera (keys already read). <paramref name="fly"/>: forward follows the view (up / down
    /// included), else it stays on the ground plane (walking).</summary>
    public void WalkMove(bool up, bool down, bool left, bool right, bool rise, bool sink, bool ctrl, bool run, float dt, bool fly = false)
    {
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera;
        float speed = BaseFlySpeed * (run ? 4f : 1f);
        if (cam.Ortho)
        {
            // flat views: the keys pan
            float px = cam.OrthoHeight * dt * (run ? 1.5f : 0.5f);
            if (left) cam.Target -= cam.Right * px; if (right) cam.Target += cam.Right * px;
            if (up) cam.Target += cam.CamUp * px; if (down) cam.Target -= cam.CamUp * px;
            return;
        }
        if (cam.Kind != ViewKind.Perspective) cam.Kind = ViewKind.User;
        var eye = cam.Eye;
        var f3 = cam.Forward; var flat = new Vector3(f3.X, f3.Y, 0);
        flat = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitY;
        var fwd = fly ? f3 : flat;
        var side = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitZ));
        var wish = Vector3.Zero;
        if (up) wish += fwd; if (down) wish -= fwd;
        if (right) wish += side; if (left) wish -= side;
        if (rise) wish += Vector3.UnitZ; if (sink) wish -= Vector3.UnitZ;
        var target = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) * speed : Vector3.Zero;
        // smooth start / stop (about 0.1 s), like a game camera
        flyVel = Vector3.Lerp(flyVel, target, 1 - MathF.Exp(-12f * dt));
        eye += flyVel * dt;
        // short orbit distance while moving: a later orbit turns around what is just ahead, the map streams around the eye
        cam.Distance = Math.Clamp(cam.Distance, 0.5f, MapMode ? 12f : 6f);
        cam.Target = eye + cam.Forward * cam.Distance;
    }

    /// <summary>Mouse look while the right button is held (turns around the eye, the camera stays where it is).</summary>
    void FlyLook(Camera cam, Vector2 d)
    {
        var eye = cam.Eye;
        if (cam.Ortho) { cam.Ortho = false; }
        cam.Kind = ViewKind.Perspective;
        cam.Yaw -= d.X * 0.0035f; // mouse right = turn right (yaw grows counter-clockwise, seen from above)
        cam.Pitch = Math.Clamp(cam.Pitch + d.Y * 0.0035f, -1.55f, 1.55f);
        cam.Distance = Math.Clamp(cam.Distance, 0.5f, MapMode ? 12f : 6f);
        cam.Target = eye + cam.Forward * cam.Distance;
    }

    /// <summary>Speed shown for a moment after the wheel changed it while flying.</summary>
    void FlyOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (vp != Viewports[ActiveViewport]) return;
        bool show = nav == NavMode.Fly || ImGui.GetTime() - flySpeedShownAt < 1.5;
        if (!show) return;
        string txt = nav == NavMode.Fly ? $"Fly  ·  ZQSD / WASD move, E / C up / down, Shift faster, wheel speed  ·  {BaseFlySpeed:0.#} m/s" : $"Speed {BaseFlySpeed:0.#} m/s";
        var sz = ImGui.CalcTextSize(txt);
        var p = new Vector2(vp.X + (vp.W - sz.X) / 2, vp.Y + vp.H - sz.Y - 14);
        dl.AddRectFilled(p - new Vector2(8, 4), p + sz + new Vector2(8, 4), 0xB0101010, 4);
        dl.AddText(p, 0xFFE8E8E8, txt);
    }
}
