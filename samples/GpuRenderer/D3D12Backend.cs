using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;
using WallpaperProtocol;
using WallpaperProtocol.D3D12;

namespace GpuRenderer;

/// <summary>Direct3D 12: COMMON -> RENDER_TARGET, clear background + bar rect, back to COMMON.</summary>
internal sealed class D3D12Backend : IBackend
{
    private readonly Look _look;
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _list;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly uint _rtvStride;
    private readonly ID3D12Fence _cpuFence;   // our own: lets us reuse the allocator safely
    private readonly AutoResetEvent _cpuEvent = new(false);
    private ulong _cpuValue;
    private readonly D3D12FrameOutput _output;
    private readonly int _w, _h;

    public D3D12Backend(WallpaperSession session, Look look)
    {
        _look = look;
        (_w, _h) = (session.Descriptor.Width, session.Descriptor.Height);
        using var adapter = D3D12FrameOutput.FindAdapter(session.Gpu!);
        _device = Vortice.Direct3D12.D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
        _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        _allocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _list = _device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, null);
        _list.Close();
        _cpuFence = _device.CreateFence(0);

        _output = new D3D12FrameOutput(session, _device, _queue);
        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, (uint)_output.Textures.Length));
        _rtvStride = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        for (var i = 0; i < _output.Textures.Length; i++)
            _device.CreateRenderTargetView(_output.Textures[i], null, Rtv(i));
    }

    private CpuDescriptorHandle Rtv(int i) => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)(i * _rtvStride);

    public bool RenderFrame(double t)
    {
        if (_output.BeginFrame() is not { } texture) return false;
        var slot = Array.IndexOf(_output.Textures, texture);

        // Wait for our previous frame before resetting the allocator (a sample can afford this).
        if (_cpuFence.CompletedValue < _cpuValue) { _cpuFence.SetEventOnCompletion(_cpuValue, _cpuEvent); _cpuEvent.WaitOne(); }
        _allocator.Reset();
        _list.Reset(_allocator, null);
        _list.ResourceBarrierTransition(texture, ResourceStates.Common, ResourceStates.RenderTarget);
        var bg = _look.Background;
        _list.ClearRenderTargetView(Rtv(slot), new Color4(bg.R, bg.G, bg.B, bg.A));
        var bar = _look.Bar;
        var x = _look.BarX(_w, t);
        _list.ClearRenderTargetView(Rtv(slot), new Color4(bar.R, bar.G, bar.B, bar.A), new[] { new Vortice.RawRect(x, 0, x + _look.BarWidth(_w), _h) });
        _list.ResourceBarrierTransition(texture, ResourceStates.RenderTarget, ResourceStates.Common); // host reads it as D3D11
        _list.Close();
        _queue.ExecuteCommandList(_list);
        _output.EndFrame();                       // queue.Signal(produced, seq + 1) + publish
        _queue.Signal(_cpuFence, ++_cpuValue);
        return true;
    }

    public void Dispose()
    {
        _queue.Signal(_cpuFence, ++_cpuValue);
        _cpuFence.SetEventOnCompletion(_cpuValue, _cpuEvent); _cpuEvent.WaitOne(2000);
        _output.Dispose(); _rtvHeap.Dispose(); _list.Dispose(); _allocator.Dispose(); _cpuFence.Dispose(); _queue.Dispose(); _device.Dispose();
    }
}
