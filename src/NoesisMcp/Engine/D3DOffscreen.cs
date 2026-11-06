
using Noesis;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace NoesisMcp.Engine;

/// <summary>
/// Headless D3D11 target: no window and no swap chain. Noesis draws into a texture that we
/// copy to a staging texture and read back as BGRA. Falls back to WARP (software) when no
/// hardware device exists, e.g. in CI or over RDP.
/// </summary>
sealed class D3DOffscreen : IDisposable
{
    readonly D3D11Device _device;
    readonly DeviceContext _ctx;
    readonly RenderDeviceD3D11 _noesisDevice;
    Texture2D? _color, _depth, _staging;
    RenderTargetView? _rtv;
    DepthStencilView? _dsv;
    int _w, _h;

    public string Adapter { get; }

    public D3DOffscreen()
    {
        try
        {
            _device = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using var dxgi = _device.QueryInterface<SharpDX.DXGI.Device>();
            Adapter = dxgi.Adapter.Description.Description.Trim();
        }
        catch (SharpDX.SharpDXException)
        {
            _device = new D3D11Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
            Adapter = "WARP (software)";
        }
        _ctx = _device.ImmediateContext;
        _noesisDevice = new RenderDeviceD3D11(_ctx.NativePointer, false);
    }

    void EnsureTargets(int w, int h)
    {
        if (w == _w && h == _h && _color != null) return;
        ReleaseTargets();
        Texture2DDescription Desc(Format f, BindFlags b, ResourceUsage u = ResourceUsage.Default, CpuAccessFlags c = CpuAccessFlags.None) => new()
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = u, BindFlags = b, CpuAccessFlags = c,
        };
        _color = new Texture2D(_device, Desc(Format.B8G8R8A8_UNorm, BindFlags.RenderTarget));
        _depth = new Texture2D(_device, Desc(Format.D24_UNorm_S8_UInt, BindFlags.DepthStencil)); // Noesis needs stencil for clipping
        _staging = new Texture2D(_device, Desc(Format.B8G8R8A8_UNorm, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _rtv = new RenderTargetView(_device, _color);
        _dsv = new DepthStencilView(_device, _depth);
        _w = w; _h = h;
    }

    /// <summary>Renders the element at animation time <paramref name="time"/> seconds. Returns BGRA rows (w*4 stride).</summary>
    public byte[] Render(FrameworkElement root, int w, int h, double time)
    {
        EnsureTargets(w, h);
        var view = GUI.CreateView(root);
        try
        {
            view.SetSize(w, h);
            view.SetFlags(RenderFlags.PPAA);
            view.Renderer.Init(_noesisDevice);
            view.Activate();

            // Loaded (and so Loaded-triggered storyboards) only fires once a frame has been drawn,
            // so draw real frames while stepping the clock up to `time`. At most 60 frames.
            Frame(view, 0, w, h);
            if (time > 0)
            {
                double step = Math.Max(1.0 / 30.0, time / 60.0);
                for (double t = step; t < time; t += step) Frame(view, t, w, h);
                Frame(view, time, w, h);
            }

            _ctx.CopyResource(_color, _staging);
            var box = _ctx.MapSubresource(_staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            var pixels = new byte[w * h * 4];
            try
            {
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(box.DataPointer + y * box.RowPitch, pixels, y * w * 4, w * 4);
            }
            finally { _ctx.UnmapSubresource(_staging, 0); }
            return pixels;
        }
        finally
        {
            view.Deactivate();
            view.Renderer.Shutdown();

        }
    }

    void Frame(View view, double t, int w, int h)
    {
        view.Update(t);
        view.Renderer.UpdateRenderTree();
        view.Renderer.RenderOffscreen();
        _ctx.OutputMerger.SetRenderTargets(_dsv, _rtv);
        _ctx.Rasterizer.SetViewport(0, 0, w, h);
        _ctx.ClearRenderTargetView(_rtv, new RawColor4(0, 0, 0, 0));
        _ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
        view.Renderer.Render(false, false);
    }

    void ReleaseTargets()
    {
        _rtv?.Dispose(); _dsv?.Dispose(); _color?.Dispose(); _depth?.Dispose(); _staging?.Dispose();
        _rtv = null; _dsv = null; _color = null; _depth = null; _staging = null;
    }

    public void Dispose()
    {
        ReleaseTargets();
        _noesisDevice.Dispose();
        _ctx.Dispose();
        _device.Dispose();
    }
}
