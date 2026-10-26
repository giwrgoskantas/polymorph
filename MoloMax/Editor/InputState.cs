using System.Numerics;
using System.Windows.Forms;

namespace MoloMax.Editor;

/// <summary>Mouse and keyboard state gathered from WinForms events, read once per frame.</summary>
public sealed class InputState
{
    public Vector2 Mouse;
    public Vector2 MouseDelta;
    Vector2 lastMouse;
    public float Wheel;
    float wheelAcc;

    readonly HashSet<MouseButtons> down = new();
    readonly HashSet<MouseButtons> pressed = new(), released = new();
    readonly HashSet<MouseButtons> pressedAcc = new(), releasedAcc = new();
    readonly HashSet<Keys> keysDown = new();
    readonly HashSet<Keys> keysPressed = new(), keysPressedAcc = new();
    public int DoubleClickFrames;
    long lastLeftDownTicks;
    bool doubleAcc;
    public bool DoubleClick;
    readonly List<char> charsAcc = new();
    /// <summary>Printable characters typed this frame (for numeric input in modal tools).</summary>
    public readonly List<char> Chars = new();
    public void KeyChar(char c) { if (!char.IsControl(c)) charsAcc.Add(c); }

    // self test: the posted keys only (the real keyboard of a person working on the same PC must not leak in)
    public bool Ctrl => keysDown.Contains(Keys.ControlKey) || !UI.ImGuiBackend.SyntheticInput && (Control.ModifierKeys & Keys.Control) != 0;
    public bool Shift => keysDown.Contains(Keys.ShiftKey) || !UI.ImGuiBackend.SyntheticInput && (Control.ModifierKeys & Keys.Shift) != 0;
    public bool Alt => UI.ImGuiBackend.SyntheticInput ? keysDown.Contains(Keys.Menu) : (Control.ModifierKeys & Keys.Alt) != 0;

    public void MouseDown(MouseEventArgs e)
    {
        Mouse = new Vector2(e.X, e.Y); // the press carries its position (no move may come before it)
        down.Add(e.Button); pressedAcc.Add(e.Button);
        if (e.Button == MouseButtons.Left)
        {
            long now = Environment.TickCount64;
            if (now - lastLeftDownTicks < SystemInformation.DoubleClickTime) doubleAcc = true;
            lastLeftDownTicks = now;
        }
    }
    public void MouseUp(MouseEventArgs e) { Mouse = new Vector2(e.X, e.Y); down.Remove(e.Button); releasedAcc.Add(e.Button); }
    public void MouseMove(MouseEventArgs e) { Mouse = new Vector2(e.X, e.Y); }
    public void MouseWheel(MouseEventArgs e) { wheelAcc += e.Delta / 120f; }
    public void KeyDown(KeyEventArgs e) { if (keysDown.Add(e.KeyCode)) keysPressedAcc.Add(e.KeyCode); }
    public void KeyUp(KeyEventArgs e) { keysDown.Remove(e.KeyCode); }

    public void BeginFrame()
    {
        MouseDelta = Mouse - lastMouse; lastMouse = Mouse;
        Wheel = wheelAcc; wheelAcc = 0;
        pressed.Clear(); pressed.UnionWith(pressedAcc); pressedAcc.Clear();
        released.Clear(); released.UnionWith(releasedAcc); releasedAcc.Clear();
        keysPressed.Clear(); keysPressed.UnionWith(keysPressedAcc); keysPressedAcc.Clear();
        DoubleClick = doubleAcc; doubleAcc = false;
        Chars.Clear(); Chars.AddRange(charsAcc); charsAcc.Clear();
        // keys released while the window lost focus (the self test sends button messages, the physical state is not used then)
        if (UI.ImGuiBackend.SyntheticInput) return;
        if ((Control.MouseButtons & MouseButtons.Left) == 0) down.Remove(MouseButtons.Left);
        if ((Control.MouseButtons & MouseButtons.Middle) == 0) down.Remove(MouseButtons.Middle);
        if ((Control.MouseButtons & MouseButtons.Right) == 0) down.Remove(MouseButtons.Right);
    }

    public bool Down(MouseButtons b) => down.Contains(b);
    public bool Pressed(MouseButtons b) => pressed.Contains(b);
    public bool Released(MouseButtons b) => released.Contains(b);
    public bool KeyPressed(Keys k) => keysPressed.Contains(k);
    public bool KeyDownNow(Keys k) => keysDown.Contains(k);
    public void ReleaseAllKeys() { keysDown.Clear(); }
}
