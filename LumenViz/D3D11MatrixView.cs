using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using LumenGraph;

namespace LumenViz;

/// <summary>
/// GPU-accelerated adjacency matrix viewer. Inherits all data management,
/// ordering, density computation, mouse interaction, and selection from
/// <see cref="MatrixView"/>. Overrides rendering to use a Direct3D 11
/// pixel shader for the heatmap color mapping (gamma, floor, log scale,
/// color ramp LUT), with zero-copy swap-chain presentation.
///
/// The density grid is still computed on the CPU (edge binning), but the
/// per-pixel color mapping and presentation are fully GPU-accelerated,
/// making gamma/floor/ramp changes instant.
/// </summary>
public class D3D11MatrixView : MatrixView, IGraphViewer
{
    // Override viewer type via explicit interface re-implementation
    string IGraphViewer.ViewerType => "matrix_gpu";

    // ── D3D11 core resources ────────────────────────────────────────────
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;

    // ── Shader pipeline ─────────────────────────────────────────────────
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;

    // ── GPU textures ────────────────────────────────────────────────────
    private ID3D11Texture2D? _densityTex;
    private ID3D11ShaderResourceView? _densitySrv;
    private int _gpuDensityW, _gpuDensityH;

    private ID3D11Texture2D? _lutTex;
    private ID3D11ShaderResourceView? _lutSrv;
    private bool _lutDirty = true;

    // ── Constant buffer ─────────────────────────────────────────────────
    private ID3D11Buffer? _cbParams;

    [StructLayout(LayoutKind.Sequential)]
    private struct HeatmapParams
    {
        public float MaxDensity;
        public float Gamma;
        public float DensityFloor;
        public uint UseLogScale;
    }

    // ── State tracking ──────────────────────────────────────────────────
    private bool _d3dInitialized;
    private bool _d3dFailed; // fallback to CPU if GPU init fails

    // ═══════════════════════════════════════════════════════════════════
    // HLSL Shaders (embedded)
    // ═══════════════════════════════════════════════════════════════════

    private const string VertexShaderSource = @"
struct VS_OUT {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VS_OUT main(uint id : SV_VertexID) {
    VS_OUT o;
    // Full-screen triangle: 3 vertices cover the entire viewport
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
";

    private const string PixelShaderSource = @"
Texture2D<uint>   densityTex : register(t0);
Texture2D<float4> lutTex     : register(t1);

cbuffer Params : register(b0) {
    float maxDensity;
    float gamma;
    float densityFloor;
    uint  useLogScale;
};

float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    int2 coord = int2(pos.xy);
    uint density = densityTex.Load(int3(coord, 0));

    if (density == 0u || maxDensity <= 0.0f)
        return float4(0, 0, 0, 1);

    float t;
    if (useLogScale != 0u)
        t = log(1.0f + (float)density) / log(1.0f + maxDensity);
    else
        t = (float)density / maxDensity;

    float mapped = densityFloor + (1.0f - densityFloor) * pow(saturate(t), gamma);
    mapped = saturate(mapped);

    int lutIndex = (int)(mapped * 255.0f);
    float4 color = lutTex.Load(int3(lutIndex, 0, 0));
    return float4(color.rgb, 1.0f);
}
";

    // ═══════════════════════════════════════════════════════════════════
    // D3D11 initialization
    // ═══════════════════════════════════════════════════════════════════

    private void InitD3D11()
    {
        if (_d3dInitialized || _d3dFailed || !IsHandleCreated) return;

        try
        {
            // Create device
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                out _d3dDevice,
                out _,
                out _d3dContext);

            if (_d3dDevice == null || _d3dContext == null)
            {
                _d3dFailed = true;
                return;
            }

            // Get DXGI factory
            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            // Create swap chain
            var swapDesc = new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, Width),
                Height = (uint)Math.Max(1, Height),
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
            };

            _swapChain = factory.CreateSwapChainForHwnd(_d3dDevice, Handle, swapDesc);

            // Disable Alt+Enter fullscreen
            factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter);

            // Create RTV
            CreateRenderTarget();

            // Compile shaders
            CompileShaders();

            // Create constant buffer
            var cbDesc = new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<HeatmapParams>(),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            };
            _cbParams = _d3dDevice.CreateBuffer(cbDesc);

            // Create LUT texture
            BuildLutTexture();

            _d3dInitialized = true;
        }
        catch
        {
            _d3dFailed = true;
            CleanupD3D11();
        }
    }

    private void CreateRenderTarget()
    {
        _rtv?.Dispose();
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _d3dDevice!.CreateRenderTargetView(backBuffer);
    }

    private void CompileShaders()
    {
        // Vertex shader — Compile throws on HLSL errors
        var vsBytecode = Compiler.Compile(VertexShaderSource, "main", "vs_hlsl", "vs_5_0");
        _vertexShader = _d3dDevice!.CreateVertexShader(vsBytecode.Span);

        // Pixel shader
        var psBytecode = Compiler.Compile(PixelShaderSource, "main", "ps_hlsl", "ps_5_0");
        _pixelShader = _d3dDevice!.CreatePixelShader(psBytecode.Span);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GPU texture management
    // ═══════════════════════════════════════════════════════════════════

    private void EnsureDensityTexture(int w, int h)
    {
        if (_gpuDensityW == w && _gpuDensityH == h && _densityTex != null) return;

        _densitySrv?.Dispose();
        _densityTex?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };

        _densityTex = _d3dDevice!.CreateTexture2D(desc);
        _densitySrv = _d3dDevice.CreateShaderResourceView(_densityTex);
        _gpuDensityW = w;
        _gpuDensityH = h;
    }

    private unsafe void UploadDensity(int w, int h)
    {
        EnsureDensityTexture(w, h);

        var mapped = _d3dContext!.Map(_densityTex!, 0, MapMode.WriteDiscard);
        try
        {
            fixed (int* src = _density)
            {
                byte* dst = (byte*)mapped.DataPointer;
                int srcRowBytes = w * sizeof(int);
                for (int row = 0; row < h; row++)
                {
                    Buffer.MemoryCopy(
                        src + row * w,
                        dst + row * (int)mapped.RowPitch,
                        srcRowBytes, srcRowBytes);
                }
            }
        }
        finally
        {
            _d3dContext.Unmap(_densityTex!, 0);
        }
    }

    private unsafe void BuildLutTexture()
    {
        _lutSrv?.Dispose();
        _lutTex?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = 256,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };

        _lutTex = _d3dDevice!.CreateTexture2D(desc);
        _lutSrv = _d3dDevice.CreateShaderResourceView(_lutTex);
        UploadLutData();
    }

    private unsafe void UploadLutData()
    {
        if (_lutTex == null) return;

        var mapped = _d3dContext!.Map(_lutTex, 0, MapMode.WriteDiscard);
        try
        {
            fixed (uint* src = _colorLut)
            {
                Buffer.MemoryCopy(src, (void*)mapped.DataPointer,
                    256 * sizeof(uint), 256 * sizeof(uint));
            }
        }
        finally
        {
            _d3dContext.Unmap(_lutTex, 0);
        }
        _lutDirty = false;
    }

    private unsafe void UpdateLutTexture()
    {
        UploadLutData();
    }

    private unsafe void UpdateConstantBuffer()
    {
        var mapped = _d3dContext!.Map(_cbParams!, 0, MapMode.WriteDiscard);
        var p = new HeatmapParams
        {
            MaxDensity = _maxDensity,
            Gamma = (float)Gamma,
            DensityFloor = (float)DensityFloor,
            UseLogScale = LogScale ? 1u : 0u,
        };
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        _d3dContext.Unmap(_cbParams!, 0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Resize handling
    // ═══════════════════════════════════════════════════════════════════

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeSwapChain();
    }

    private void ResizeSwapChain()
    {
        if (_swapChain == null || Width <= 0 || Height <= 0) return;

        _rtv?.Dispose();
        _rtv = null;

        _swapChain.ResizeBuffers(
            0,
            (uint)Math.Max(1, Width),
            (uint)Math.Max(1, Height),
            Format.Unknown,
            SwapChainFlags.None);

        CreateRenderTarget();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Rendering (overrides MatrixView.OnPaint)
    // ═══════════════════════════════════════════════════════════════════

    protected override void OnPaint(PaintEventArgs e)
    {
        var graph = GetGraph();
        if (graph == null || Width <= 0 || Height <= 0)
        {
            e.Graphics.Clear(System.Drawing.Color.Black);
            return;
        }

        // Lazy D3D11 init
        if (!_d3dInitialized && !_d3dFailed)
            InitD3D11();

        // Fallback to CPU rendering if D3D11 failed
        if (_d3dFailed)
        {
            base.OnPaint(e);
            return;
        }

        _sw.Restart();

        int dw = DataWidth;
        int dh = DataHeight;

        // Recompute density if viewport changed
        if (_densityDirty || _densityW != dw || _densityH != dh)
            RecomputeDensity();

        // Upload density to GPU
        UploadDensity(dw, dh);

        // Update LUT if color ramp changed
        if (_lutDirty)
            UpdateLutTexture();

        // Update constant buffer
        UpdateConstantBuffer();

        // ── Draw ────────────────────────────────────────────────────────
        _d3dContext!.OMSetRenderTargets(_rtv!);

        var vp = new Vortice.Mathematics.Viewport(0, 0, Width, Height);
        _d3dContext.RSSetViewport(vp);

        // Bind shaders
        _d3dContext.VSSetShader(_vertexShader);
        _d3dContext.PSSetShader(_pixelShader);

        // Bind textures
        _d3dContext.PSSetShaderResource(0, _densitySrv);
        _d3dContext.PSSetShaderResource(1, _lutSrv);

        // Bind constant buffer
        _d3dContext.PSSetConstantBuffer(0, _cbParams);

        // Draw full-screen triangle (3 vertices, no vertex buffer)
        _d3dContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _d3dContext.Draw(3, 0);

        // Present
        _swapChain!.Present(1, PresentFlags.None);

        double renderMs = _sw.Elapsed.TotalMilliseconds;
        _frameCount++;
        UpdateEma(ref _avgRenderMs, renderMs);
        _avgBlitMs = 0; // no blit in D3D11 path
    }

    // ═══════════════════════════════════════════════════════════════════
    // LUT dirty tracking (hook into base class property changes)
    // ═══════════════════════════════════════════════════════════════════

    // Override the ColorRamp property to mark LUT dirty
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new string ColorRamp
    {
        get => base.ColorRamp;
        set
        {
            base.ColorRamp = value;
            _lutDirty = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Render stats
    // ═══════════════════════════════════════════════════════════════════

    public new Dictionary<string, object> GetRenderStats()
    {
        var stats = base.GetRenderStats();
        stats["viewer_type"] = "matrix_gpu";
        stats["renderer"] = _d3dFailed ? "cpu_fallback" : "d3d11";
        return stats;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════════

    private void CleanupD3D11()
    {
        _cbParams?.Dispose(); _cbParams = null;
        _lutSrv?.Dispose(); _lutSrv = null;
        _lutTex?.Dispose(); _lutTex = null;
        _densitySrv?.Dispose(); _densitySrv = null;
        _densityTex?.Dispose(); _densityTex = null;
        _pixelShader?.Dispose(); _pixelShader = null;
        _vertexShader?.Dispose(); _vertexShader = null;
        _rtv?.Dispose(); _rtv = null;
        _swapChain?.Dispose(); _swapChain = null;
        _d3dContext?.Dispose(); _d3dContext = null;
        _d3dDevice?.Dispose(); _d3dDevice = null;
        _d3dInitialized = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CleanupD3D11();
        base.Dispose(disposing);
    }
}
