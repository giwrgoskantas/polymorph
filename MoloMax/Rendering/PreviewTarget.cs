using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace MoloMax.Rendering;

/// <summary>An off-screen picture (colour + depth) the 3D renderer draws into and the UI shows as an image (model previews).</summary>
public sealed class PreviewTarget : IDisposable
{
    public readonly int Width, Height;
    readonly Texture2D color, depth;
    public readonly RenderTargetView Rtv;
    public readonly DepthStencilView Dsv;
    public readonly ShaderResourceView Srv;

    public PreviewTarget(Device device, int w, int h)
    {
        Width = w; Height = h;
        color = new Texture2D(device, new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        depth = new Texture2D(device, new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.D24_UNorm_S8_UInt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
        });
        Rtv = new RenderTargetView(device, color);
        Dsv = new DepthStencilView(device, depth);
        Srv = new ShaderResourceView(device, color);
    }

    public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Dsv.Dispose(); color.Dispose(); depth.Dispose(); }
}
