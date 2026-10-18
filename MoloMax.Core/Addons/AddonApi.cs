using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Addons;

/// <summary>
/// A MoloMax add-on: a .dll in %APPDATA%\MoloMax\addons that references MoloMax.Core and has a public class
/// implementing this interface (like a Blender add-on's register()). Enable / disable it in Preferences > Add-ons.
/// </summary>
public interface IMoloAddon
{
    string Name { get; }
    string Version => "1.0";
    string Author => "";
    string Description => "";
    /// <summary>Called once when the add-on is enabled: register commands and panels here.</summary>
    void Register(IAddonHost host);
    /// <summary>Called when the add-on is disabled or MoloMax closes.</summary>
    void Unregister() { }
}

/// <summary>What MoloMax gives to add-ons.</summary>
public interface IAddonHost
{
    Scene Scene { get; }
    /// <summary>Selected objects, active first.</summary>
    IReadOnlyList<SceneObject> Selection { get; }
    SceneObject Active { get; }

    /// <summary>A command: shows in the search (F3 / Tab), in the Add-ons menu, and can be run by other add-ons.</summary>
    void AddCommand(string name, string category, Action run, string description = null);
    /// <summary>A panel drawn with ImGui.NET in the Properties editor (Add-ons tab).</summary>
    void AddPanel(string title, Action draw);

    /// <summary>Runs a change with undo (Ctrl+Z restores the scene as it was before).</summary>
    void Undoable(string name, Action change);
    SceneObject AddObject(string name, PolyMesh mesh, System.Numerics.Vector3 position);
    void Status(string message);
    /// <summary>Folder for the add-on's own files (settings, cache).</summary>
    string DataFolder(string addonName);
}
