using SharpDX.Direct3D11;

namespace MoloMax.Rendering;

/// <summary>
/// Time the graphics card spends on a part of the frame (timestamp queries, read back a few frames later without
/// waiting). Tells whether a slow frame waits on the card or on the program.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    readonly Query[] disjoint = new Query[4], t0 = new Query[4], t1 = new Query[4];
    readonly bool[] pending = new bool[4];
    int frame;
    /// <summary>Last measured milliseconds (smoothed).</summary>
    public double Ms;

    public GpuTimer(Device device)
    {
        for (int i = 0; i < 4; i++)
        {
            disjoint[i] = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
            t0[i] = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
            t1[i] = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
        }
    }

    public void Begin(DeviceContext ctx)
    {
        int i = frame % 4;
        if (pending[i]) Read(ctx, i); // the oldest one, three frames old: normally ready
        ctx.Begin(disjoint[i]);
        ctx.End(t0[i]);
    }

    public void End(DeviceContext ctx)
    {
        int i = frame % 4;
        ctx.End(t1[i]);
        ctx.End(disjoint[i]);
        pending[i] = true;
        frame++;
        // collect whatever is ready already
        for (int k = 1; k < 4; k++) { int j = (frame + k) % 4; if (pending[j]) Read(ctx, j); }
    }

    void Read(DeviceContext ctx, int i)
    {
        if (!ctx.GetData(disjoint[i], AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint dj)) return;
        if (!ctx.GetData(t0[i], AsynchronousFlags.DoNotFlush, out long a) || !ctx.GetData(t1[i], AsynchronousFlags.DoNotFlush, out long b)) return;
        pending[i] = false;
        if (dj.Disjoint || dj.Frequency == 0) return;
        double ms = (b - a) * 1000.0 / dj.Frequency;
        Ms = Ms <= 0 ? ms : Ms * 0.8 + ms * 0.2;
    }

    public void Dispose() { foreach (var q in disjoint.Concat(t0).Concat(t1)) q?.Dispose(); }
}
