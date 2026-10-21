using MoloMax.Core.Mesh;

namespace MoloMax.Editor;

/// <summary>
/// Ctrl+Z also brings back the previous selection (objects, level, faces / edges / points), like 3ds Max.
/// The selection is not copied into the scene undo (a click on a 500k-face mesh must stay instant): a light history of
/// selection states is kept beside it, each stamped from the undo clock, so one Ctrl+Z undoes whichever came last.
/// </summary>
public sealed partial class EditorApp
{
    sealed class SelState
    {
        public HashSet<int> Sel; public int Active; public SubObject Level; public PolySelection Sub; public long Stamp;
    }

    readonly List<SelState> selUndo = new(), selRedo = new();
    SelState selNow; long selSigNow, selSceneStamp;

    long SelSig()
    {
        long h = Selected.Count * 31 + ActiveId * 7919L + (int)Level * 104729L;
        foreach (var id in Selected) h ^= id * 0x9E3779B1L;
        if (Level != SubObject.Object && Current is { } c && subSel.TryGetValue(c.Id, out var s))
            h = h * 1000003 ^ SetSig(s.Faces) * 3 ^ SetSig(s.Verts) * 5 ^ SetSig(s.Edges) * 7;
        return h;
    }

    SelState CaptureSel()
    {
        PolySelection sub = null;
        if (Level != SubObject.Object && Current is { } c && subSel.TryGetValue(c.Id, out var s)) sub = s.Clone();
        return new SelState { Sel = new HashSet<int>(Selected), Active = ActiveId, Level = Level, Sub = sub };
    }

    void ApplySel(SelState st)
    {
        Selected.Clear(); foreach (var id in st.Sel) if (Scene.Find(id) != null) Selected.Add(id);
        ActiveId = st.Active;
        if (st.Sub != null && Scene.Find(st.Active) is { Mesh: not null } o)
        {
            var copy = st.Sub.Clone(); copy.Sanitise(o.Mesh); subSel[o.Id] = copy;
        }
        Level = st.Level;
        SelectionChanged();
        selNow = CaptureSel(); selSigNow = SelSig(); selStepFrame = selFrame; // what the editor adjusts right after belongs to this step
    }

    long selSeenStamp; int selFrame, selStepFrame;
    object selOwner;

    /// <summary>A new undo stack (new file, map mode) restarts its clock: the old selection steps mean nothing there.</summary>
    void SelOwnerCheck()
    {
        if (ReferenceEquals(selOwner, Undo)) return;
        selOwner = Undo; selUndo.Clear(); selRedo.Clear(); selNow = null;
    }

    /// <summary>Once a frame: a selection that changed since the last frame becomes one step of the selection history.</summary>
    void TrackSelection()
    {
        if (transforming || ModalActive || painting) return;
        SelOwnerCheck();
        selFrame++;
        // a scene step was just made (extrude, clone, add light...): the selection it picks, and what the editor adjusts after it
        // (the level falls back to Object on a light...) a few frames later, belong to that step
        if (Undo.LastStamp != selSeenStamp) { selSeenStamp = Undo.LastStamp; selStepFrame = selFrame; }
        long sig = SelSig();
        if (selNow == null) { selNow = CaptureSel(); selSigNow = sig; selSceneStamp = Undo.LastStamp; return; }
        if (sig == selSigNow) return;
        if (selStepFrame > 0 && selFrame - selStepFrame <= 10)
        { selNow = CaptureSel(); selSigNow = sig; selSceneStamp = Undo.LastStamp; return; }
        selNow.Stamp = Undo.Tick();
        selUndo.Add(selNow);
        if (selUndo.Count > 200) selUndo.RemoveAt(0);
        selRedo.Clear();
        selNow = CaptureSel(); selSigNow = sig; selSceneStamp = Undo.LastStamp;
    }

    /// <summary>The last thing done was a selection change (newer than the last scene step): undo that.</summary>
    bool UndoSelection()
    {
        SelOwnerCheck();
        TrackSelection();
        if (selUndo.Count == 0 || selUndo[^1].Stamp <= Undo.LastStamp) return false;
        var prev = selUndo[^1]; selUndo.RemoveAt(selUndo.Count - 1);
        var cur = CaptureSel(); cur.Stamp = prev.Stamp; selRedo.Add(cur);
        ApplySel(prev); selSceneStamp = Undo.LastStamp;
        SetStatus("Undo selection");
        return true;
    }

    bool RedoSelection()
    {
        SelOwnerCheck();
        if (selRedo.Count == 0) return false;
        var next = selRedo[^1]; selRedo.RemoveAt(selRedo.Count - 1);
        var cur = CaptureSel(); cur.Stamp = next.Stamp; selUndo.Add(cur);
        ApplySel(next); selSceneStamp = Undo.LastStamp;
        SetStatus("Redo selection");
        return true;
    }

    /// <summary>After a scene undo / redo / open: the selection it leaves is the new starting point (not a step).</summary>
    void ResetSelectionBaseline() { selNow = CaptureSel(); selSigNow = SelSig(); selSceneStamp = selSeenStamp = Undo.LastStamp; }
}
