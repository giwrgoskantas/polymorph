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

    /// <summary>Copies the picture back from the graphics card and writes it as a PNG (asset browser thumbnails).</summary>
    public void SavePng(DeviceContext ctx, string path)
    {
        var desc = color.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CpuAccessFlags = CpuAccessFlags.Read; desc.OptionFlags = ResourceOptionFlags.None;
        using var staging = new Texture2D(color.Device, desc);
        ctx.CopyResource(color, staging);
        var box = ctx.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            using var bmp = new System.Drawing.Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, Width, Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(box.DataPointer + y * box.RowPitch, row, 0, row.Length);
                for (int i = 0; i < row.Length; i += 4) { (row[i], row[i + 2]) = (row[i + 2], row[i]); row[i + 3] = 255; } // RGBA -> BGRA, opaque
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
            }
            bmp.UnlockBits(bd);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { ctx.UnmapSubresource(staging, 0); }
    }

    public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Dsv.Dispose(); color.Dispose(); depth.Dispose(); }
}
