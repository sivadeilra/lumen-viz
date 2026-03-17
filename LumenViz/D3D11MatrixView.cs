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
/// Fully GPU-accelerated adjacency matrix viewer. Inherits data management,
/// ordering, mouse interaction, and selection from <see cref="MatrixView"/>.
///
/// GPU pipeline (Option B):
///   1. Edge list + inverse ordering uploaded as StructuredBuffers
///   2. Compute shader bins edges into a density UAV (InterlockedAdd)
///      and tracks max density (InterlockedMax)
///   3. Pixel shader maps density → gamma/floor/log → LUT color
///   4. Zero-copy swap-chain presentation
///
/// The CPU is only used for ordering computation and mouse/selection logic.
/// All density binning and color mapping runs on the GPU.
/// </summary>
public class D3D11MatrixView : MatrixView
{
    // ── D3D11 core resources ────────────────────────────────────────────
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;

    // ── Shader pipeline ─────────────────────────────────────────────────
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11ComputeShader? _computeShader;

    // ── GPU edge data (StructuredBuffer<uint2>) ─────────────────────────
    private ID3D11Buffer? _edgeBuf;
    private ID3D11ShaderResourceView? _edgeSrv;
    private int _gpuEdgeCount;

    // ── GPU inverse ordering (StructuredBuffer<uint>) ───────────────────
    private ID3D11Buffer? _orderBuf;
    private ID3D11ShaderResourceView? _orderSrv;
    private int _gpuOrderCount;

    // ── GPU density texture (R32_UInt, UAV+SRV) ─────────────────────────
    private ID3D11Texture2D? _densityTex;
    private ID3D11ShaderResourceView? _densitySrv;
    private ID3D11UnorderedAccessView? _densityUav;
    private int _gpuDensityW, _gpuDensityH;

    // ── GPU max density buffer (1 × uint, UAV+SRV) ──────────────────────
    private ID3D11Buffer? _maxDensityBuf;
    private ID3D11ShaderResourceView? _maxDensitySrv;
    private ID3D11UnorderedAccessView? _maxDensityUav;

    // ── GPU LUT texture ─────────────────────────────────────────────────
    private ID3D11Texture2D? _lutTex;
    private ID3D11ShaderResourceView? _lutSrv;
    private bool _lutDirty = true;

    // ── Constant buffers ────────────────────────────────────────────────
    private ID3D11Buffer? _cbCompute;
    private ID3D11Buffer? _cbRender;

    [StructLayout(LayoutKind.Sequential)]
    private struct BinParams
    {
        public float ViewX, ViewY, ViewW, ViewH;
        public uint TexW, TexH;
        public uint IsDirected;
        public uint EdgeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderParams
    {
        public float Gamma;
        public float DensityFloor;
        public uint UseLogScale;
        public uint Pad0;
    }

    // ── State tracking ──────────────────────────────────────────────────
    private bool _d3dInitialized;
    private bool _d3dFailed;
    private bool _gpuEdgesDirty = true;
    private string? _lastError;

    // ═══════════════════════════════════════════════════════════════════
    // HLSL Shaders
    // ═══════════════════════════════════════════════════════════════════

    private const string ComputeShaderSource = @"
struct Edge { uint src; uint tgt; };

StructuredBuffer<Edge>   edges        : register(t0);
StructuredBuffer<uint>   inverseOrder : register(t1);
RWTexture2D<uint>        density      : register(u0);
RWStructuredBuffer<uint> maxDensity   : register(u1);

cbuffer BinParams : register(b0) {
    float viewX, viewY, viewW, viewH;
    uint  texW, texH;
    uint  isDirected;
    uint  edgeCount;
};

[numthreads(256, 1, 1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    uint idx = dtid.x;
    if (idx >= edgeCount) return;

    Edge e = edges[idx];
    uint srcVis = inverseOrder[e.src];
    uint tgtVis = inverseOrder[e.tgt];

    float scaleX = (float)texW / viewW;
    float scaleY = (float)texH / viewH;

    int px = (int)(((float)tgtVis - viewX) * scaleX);
    int py = (int)(((float)srcVis - viewY) * scaleY);

    if (px >= 0 && px < (int)texW && py >= 0 && py < (int)texH) {
        uint prev;
        InterlockedAdd(density[int2(px, py)], 1u, prev);
        InterlockedMax(maxDensity[0], prev + 1u);
    }

    // Symmetric entry for undirected graphs
    if (isDirected == 0u) {
        int px2 = (int)(((float)srcVis - viewX) * scaleX);
        int py2 = (int)(((float)tgtVis - viewY) * scaleY);
        if (px2 >= 0 && px2 < (int)texW && py2 >= 0 && py2 < (int)texH) {
            uint prev2;
            InterlockedAdd(density[int2(px2, py2)], 1u, prev2);
            InterlockedMax(maxDensity[0], prev2 + 1u);
        }
    }
}
";

    private const string VertexShaderSource = @"
struct VS_OUT {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VS_OUT main(uint id : SV_VertexID) {
    VS_OUT o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
";

    private const string PixelShaderSource = @"
Texture2D<uint>          densityTex    : register(t0);
Texture2D<float4>        lutTex        : register(t1);
StructuredBuffer<uint>   maxDensityBuf : register(t2);

cbuffer Params : register(b0) {
    float gamma;
    float densityFloor;
    uint  useLogScale;
    uint  pad0;
};

float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    uint d = densityTex.Load(int3(pos.xy, 0));
    if (d == 0u) return float4(0, 0, 0, 1);

    float maxD = (float)maxDensityBuf[0];
    if (maxD <= 0.0f) return float4(0, 0, 0, 1);

    float t;
    if (useLogScale != 0u)
        t = log(1.0f + (float)d) / log(1.0f + maxD);
    else
        t = (float)d / maxD;

    float mapped = densityFloor + (1.0f - densityFloor) * pow(saturate(t), gamma);
    int lutIdx = (int)(saturate(mapped) * 255.0f);
    return float4(lutTex.Load(int3(lutIdx, 0, 0)).rgb, 1.0f);
}
";

    // ═══════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════

    public D3D11MatrixView()
    {
        DoubleBuffered = false;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Graph data change hooks — set GPU dirty flags
    // ═══════════════════════════════════════════════════════════════════

    protected override void OnGraphDataChanged()
    {
        _gpuEdgesDirty = true;
    }

    // LUT dirty tracking
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
    // D3D11 initialization
    // ═══════════════════════════════════════════════════════════════════

    private void InitD3D11()
    {
        if (_d3dInitialized || _d3dFailed || !IsHandleCreated) return;

        try
        {
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

            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

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
            factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter);

            CreateRenderTarget();
            CompileShaders();
            CreateConstantBuffers();
            CreateMaxDensityBuffer();
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
        var vsBytecode = Compiler.Compile(VertexShaderSource, "main", "vs_hlsl", "vs_5_0");
        _vertexShader = _d3dDevice!.CreateVertexShader(vsBytecode.Span);

        var psBytecode = Compiler.Compile(PixelShaderSource, "main", "ps_hlsl", "ps_5_0");
        _pixelShader = _d3dDevice!.CreatePixelShader(psBytecode.Span);

        var csBytecode = Compiler.Compile(ComputeShaderSource, "main", "cs_hlsl", "cs_5_0");
        _computeShader = _d3dDevice!.CreateComputeShader(csBytecode.Span);
    }

    private void CreateConstantBuffers()
    {
        var cbComputeDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<BinParams>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _cbCompute = _d3dDevice!.CreateBuffer(cbComputeDesc);

        var cbRenderDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<RenderParams>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _cbRender = _d3dDevice!.CreateBuffer(cbRenderDesc);
    }

    private void CreateMaxDensityBuffer()
    {
        var desc = new BufferDescription
        {
            ByteWidth = 4,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 4,
        };
        _maxDensityBuf = _d3dDevice!.CreateBuffer(desc);
        _maxDensitySrv = _d3dDevice.CreateShaderResourceView(_maxDensityBuf);
        _maxDensityUav = _d3dDevice.CreateUnorderedAccessView(_maxDensityBuf);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GPU buffer management
    // ═══════════════════════════════════════════════════════════════════

    private unsafe void UploadEdges()
    {
        var graph = GetGraph();
        if (graph == null || graph.EdgeCount == 0) return;

        _edgeSrv?.Dispose();
        _edgeBuf?.Dispose();

        int m = graph.EdgeCount;
        var edges = new uint[m * 2];
        for (int e = 0; e < m; e++)
        {
            edges[e * 2] = (uint)graph.EdgeSource[e];
            edges[e * 2 + 1] = (uint)graph.EdgeTarget[e];
        }

        fixed (uint* ptr = edges)
        {
            var desc = new BufferDescription
            {
                ByteWidth = (uint)(m * 8),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 8,
            };
            var initData = new SubresourceData((IntPtr)ptr);
            _edgeBuf = _d3dDevice!.CreateBuffer(desc, initData);
        }

        var srvDesc = new ShaderResourceViewDescription
        {
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)m },
        };
        _edgeSrv = _d3dDevice!.CreateShaderResourceView(_edgeBuf, srvDesc);
        _gpuEdgeCount = m;
    }

    private unsafe void UploadInverseOrder()
    {
        var graph = GetGraph();
        if (graph == null) return;

        _orderSrv?.Dispose();
        _orderBuf?.Dispose();

        int n = graph.NodeCount;
        var order = new uint[n];
        for (int i = 0; i < n; i++)
            order[i] = (uint)_inverseOrder[i];

        fixed (uint* ptr = order)
        {
            var desc = new BufferDescription
            {
                ByteWidth = (uint)(n * 4),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 4,
            };
            var initData = new SubresourceData((IntPtr)ptr);
            _orderBuf = _d3dDevice!.CreateBuffer(desc, initData);
        }

        var srvDesc = new ShaderResourceViewDescription
        {
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)n },
        };
        _orderSrv = _d3dDevice!.CreateShaderResourceView(_orderBuf, srvDesc);
        _gpuOrderCount = n;
    }

    private void EnsureDensityTexture(int w, int h)
    {
        if (_gpuDensityW == w && _gpuDensityH == h && _densityTex != null) return;

        _densityUav?.Dispose();
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
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        };

        _densityTex = _d3dDevice!.CreateTexture2D(desc);
        _densitySrv = _d3dDevice.CreateShaderResourceView(_densityTex);
        _densityUav = _d3dDevice.CreateUnorderedAccessView(_densityTex);
        _gpuDensityW = w;
        _gpuDensityH = h;
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

    // ═══════════════════════════════════════════════════════════════════
    // Constant buffer updates
    // ═══════════════════════════════════════════════════════════════════

    private unsafe void UpdateComputeCB(int texW, int texH)
    {
        var graph = GetGraph();
        if (graph == null) return;

        var mapped = _d3dContext!.Map(_cbCompute!, 0, MapMode.WriteDiscard);
        var p = new BinParams
        {
            ViewX = (float)_viewX,
            ViewY = (float)_viewY,
            ViewW = (float)_viewW,
            ViewH = (float)_viewH,
            TexW = (uint)texW,
            TexH = (uint)texH,
            IsDirected = graph.IsDirected ? 1u : 0u,
            EdgeCount = (uint)graph.EdgeCount,
        };
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        _d3dContext.Unmap(_cbCompute!, 0);
    }

    private unsafe void UpdateRenderCB()
    {
        var mapped = _d3dContext!.Map(_cbRender!, 0, MapMode.WriteDiscard);
        var p = new RenderParams
        {
            Gamma = (float)Gamma,
            DensityFloor = (float)DensityFloor,
            UseLogScale = LogScale ? 1u : 0u,
        };
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        _d3dContext.Unmap(_cbRender!, 0);
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

    protected override void OnPaintBackground(PaintEventArgs e) { }

    // ═══════════════════════════════════════════════════════════════════
    // Rendering
    // ═══════════════════════════════════════════════════════════════════

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            OnPaintCore(e);
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            Console.Error.WriteLine($"D3D11MatrixView.OnPaint EXCEPTION: {ex}");
            _frameCount++;
        }
    }

    private void OnPaintCore(PaintEventArgs e)
    {
        var graph = GetGraph();
        if (graph == null || Width <= 0 || Height <= 0)
        {
            e.Graphics.Clear(System.Drawing.Color.Black);
            _frameCount++;
            return;
        }

        if (!_d3dInitialized && !_d3dFailed)
            InitD3D11();

        if (_d3dFailed)
        {
            base.OnPaint(e);
            return;
        }

        _sw.Restart();

        int dw = DataWidth;
        int dh = DataHeight;

        // ── 1. Upload edge + ordering data if graph changed ─────────
        if (_gpuEdgesDirty)
        {
            UploadEdges();
            UploadInverseOrder();
            _gpuEdgesDirty = false;
            _densityDirty = true; // force recompute
        }

        // ── 2. Recompute density via compute shader if needed ───────
        bool needCompute = _densityDirty || _gpuDensityW != dw || _gpuDensityH != dh;
        if (needCompute && graph.EdgeCount > 0 && _edgeBuf != null && _orderBuf != null)
        {
            // Re-upload ordering on density dirty (ordering may have changed)
            if (_densityDirty)
                UploadInverseOrder();

            EnsureDensityTexture(dw, dh);

            // Clear density UAV and maxDensity to 0
            _d3dContext!.ClearUnorderedAccessView(_densityUav!, new Vortice.Mathematics.Int4(0, 0, 0, 0));
            _d3dContext.ClearUnorderedAccessView(_maxDensityUav!, new Vortice.Mathematics.Int4(0, 0, 0, 0));

            // Update compute constant buffer
            UpdateComputeCB(dw, dh);

            // Bind compute resources
            _d3dContext.CSSetShader(_computeShader);
            _d3dContext.CSSetShaderResource(0, _edgeSrv);
            _d3dContext.CSSetShaderResource(1, _orderSrv);
            _d3dContext.CSSetUnorderedAccessViews(0, new[] { _densityUav!, _maxDensityUav! });
            _d3dContext.CSSetConstantBuffer(0, _cbCompute);

            // Dispatch: one thread per edge
            uint groups = (uint)((graph.EdgeCount + 255) / 256);
            _d3dContext.Dispatch(groups, 1, 1);

            // Unbind UAVs (required before binding as SRVs for pixel shader)
            _d3dContext.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[2]);
            _d3dContext.CSSetShader(null);

            _densityDirty = false;
        }

        // ── 3. Update LUT if color ramp changed ────────────────────
        if (_lutDirty)
            UploadLutData();

        // ── 4. Update render constant buffer ────────────────────────
        UpdateRenderCB();

        // ── 5. Draw full-screen heatmap ─────────────────────────────
        _d3dContext!.OMSetRenderTargets(_rtv!);
        var vp = new Vortice.Mathematics.Viewport(0, 0, Width, Height);
        _d3dContext.RSSetViewport(vp);

        _d3dContext.VSSetShader(_vertexShader);
        _d3dContext.PSSetShader(_pixelShader);

        _d3dContext.PSSetShaderResource(0, _densitySrv!);
        _d3dContext.PSSetShaderResource(1, _lutSrv!);
        _d3dContext.PSSetShaderResource(2, _maxDensitySrv!);
        _d3dContext.PSSetConstantBuffer(0, _cbRender);

        _d3dContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _d3dContext.Draw(3, 0);

        _swapChain!.Present(1, PresentFlags.None);

        double renderMs = _sw.Elapsed.TotalMilliseconds;
        _frameCount++;
        UpdateEma(ref _avgRenderMs, renderMs);
        _avgBlitMs = 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Render stats
    // ═══════════════════════════════════════════════════════════════════

    public override Dictionary<string, object> GetRenderStats()
    {
        var stats = base.GetRenderStats();
        stats["renderer"] = _d3dFailed ? "cpu_fallback" : "d3d11_compute";
        stats["d3d_initialized"] = _d3dInitialized;
        stats["d3d_failed"] = _d3dFailed;
        stats["gpu_density_w"] = _gpuDensityW;
        stats["gpu_density_h"] = _gpuDensityH;
        stats["gpu_edge_count"] = _gpuEdgeCount;
        if (_lastError != null) stats["last_error"] = _lastError;
        return stats;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════════

    private void CleanupD3D11()
    {
        _cbRender?.Dispose(); _cbRender = null;
        _cbCompute?.Dispose(); _cbCompute = null;
        _maxDensityUav?.Dispose(); _maxDensityUav = null;
        _maxDensitySrv?.Dispose(); _maxDensitySrv = null;
        _maxDensityBuf?.Dispose(); _maxDensityBuf = null;
        _orderSrv?.Dispose(); _orderSrv = null;
        _orderBuf?.Dispose(); _orderBuf = null;
        _edgeSrv?.Dispose(); _edgeSrv = null;
        _edgeBuf?.Dispose(); _edgeBuf = null;
        _densityUav?.Dispose(); _densityUav = null;
        _densitySrv?.Dispose(); _densitySrv = null;
        _densityTex?.Dispose(); _densityTex = null;
        _lutSrv?.Dispose(); _lutSrv = null;
        _lutTex?.Dispose(); _lutTex = null;
        _computeShader?.Dispose(); _computeShader = null;
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
