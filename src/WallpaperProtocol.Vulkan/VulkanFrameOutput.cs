using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace WallpaperProtocol.Vulkan;

/// <summary>
/// Vulkan producer for a <see cref="Transport.Gpu"/> session. Imports the host's D3D11 shared
/// textures as <see cref="VkImage"/>s (VK_KHR_external_memory_win32) and its shared fences as
/// timeline semaphores (VK_KHR_external_semaphore_win32). Per frame:
/// <code>
/// if (output.BeginFrame(out var image))      // false: the host still uses that slot, skip this tick
/// {
///     /* record into cmd: transition image, render/clear/copy, transition to GENERAL and
///        release it to VK_QUEUE_FAMILY_EXTERNAL */
///     output.SubmitAndPublish(queue, cmd, fence);   // signals the produced semaphore, publishes
/// }
/// </code>
/// Device requirements: Vulkan 1.2 with timelineSemaphore enabled, the extensions in
/// <see cref="RequiredDeviceExtensions"/>, and the physical device whose LUID matches
/// (<see cref="MatchesAdapter"/>). Images are VK_FORMAT_B8G8R8A8_UNORM.
/// </summary>
public sealed unsafe class VulkanFrameOutput : IDisposable
{
    public static readonly string[] RequiredDeviceExtensions = { "VK_KHR_external_memory_win32", "VK_KHR_external_semaphore_win32" };

    private readonly WallpaperSession _session;
    private readonly VkDeviceApi _api;
    private readonly VkDeviceMemory[] _memory;
    private int _slot = -1;

    public VkImage[] Images { get; }
    public VkSemaphore ProducedSemaphore { get; }
    public VkSemaphore ConsumedSemaphore { get; }
    public uint Width => (uint)_session.Descriptor.Width;
    public uint Height => (uint)_session.Descriptor.Height;
    public const VkFormat Format = VkFormat.B8G8R8A8Unorm;

    [DllImport("kernel32")] private static extern bool CloseHandle(nint h);

    public VulkanFrameOutput(WallpaperSession session, VkInstanceApi instanceApi, VkPhysicalDevice physicalDevice, VkDeviceApi deviceApi)
    {
        var gpu = session.Gpu ?? throw new ArgumentException("Session doesn't use the GPU transport");
        _session = session;
        _api = deviceApi;
        instanceApi.vkGetPhysicalDeviceMemoryProperties(physicalDevice, out var memProps);

        Images = new VkImage[gpu.Textures.Length];
        _memory = new VkDeviceMemory[gpu.Textures.Length];
        for (var i = 0; i < gpu.Textures.Length; i++)
        {
            var handle = (nint)gpu.Textures[i];
            try { (Images[i], _memory[i]) = ImportImage(handle, memProps); }
            finally { CloseHandle(handle); } // importing NT handles doesn't take ownership
        }
        ProducedSemaphore = ImportTimeline(gpu.ProducedFence);
        ConsumedSemaphore = ImportTimeline(gpu.ConsumedFence);
    }

    private (VkImage, VkDeviceMemory) ImportImage(nint handle, VkPhysicalDeviceMemoryProperties memProps)
    {
        var external = new VkExternalMemoryImageCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.D3D11Texture };
        var ci = new VkImageCreateInfo
        {
            pNext = &external,
            imageType = VkImageType.Image2D,
            format = Format,
            extent = new VkExtent3D(Width, Height, 1),
            mipLevels = 1, arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        VkImage image;
        Check(_api.vkCreateImage(&ci, null, &image), "vkCreateImage");

        _api.vkGetImageMemoryRequirements(image, out var req);
        var handleProps = new VkMemoryWin32HandlePropertiesKHR();
        Check(_api.vkGetMemoryWin32HandlePropertiesKHR(VkExternalMemoryHandleTypeFlags.D3D11Texture, handle, &handleProps), "vkGetMemoryWin32HandlePropertiesKHR");
        var typeIndex = PickMemoryType(req.memoryTypeBits & handleProps.memoryTypeBits, memProps);

        var dedicated = new VkMemoryDedicatedAllocateInfo { image = image };
        var import = new VkImportMemoryWin32HandleInfoKHR { pNext = &dedicated, handleType = VkExternalMemoryHandleTypeFlags.D3D11Texture, handle = handle };
        var alloc = new VkMemoryAllocateInfo { pNext = &import, allocationSize = req.size, memoryTypeIndex = typeIndex };
        VkDeviceMemory memory;
        Check(_api.vkAllocateMemory(&alloc, null, &memory), "vkAllocateMemory (import D3D11 texture)");
        Check(_api.vkBindImageMemory(image, memory, 0), "vkBindImageMemory");
        return (image, memory);
    }

    private static uint PickMemoryType(uint bits, VkPhysicalDeviceMemoryProperties props)
    {
        if (bits == 0) throw new InvalidOperationException("No memory type can import the shared texture");
        for (var i = 0; i < props.memoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (props.memoryTypes[i].propertyFlags & VkMemoryPropertyFlags.DeviceLocal) != 0) return (uint)i;
        return (uint)System.Numerics.BitOperations.TrailingZeroCount(bits);
    }

    private VkSemaphore ImportTimeline(long handle)
    {
        try
        {
            var type = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
            var ci = new VkSemaphoreCreateInfo { pNext = &type };
            VkSemaphore sem;
            Check(_api.vkCreateSemaphore(&ci, null, &sem), "vkCreateSemaphore");
            var import = new VkImportSemaphoreWin32HandleInfoKHR
            {
                semaphore = sem, handleType = VkExternalSemaphoreHandleTypeFlags.D3D12Fence, handle = (nint)handle,
            };
            Check(_api.vkImportSemaphoreWin32HandleKHR(&import), "vkImportSemaphoreWin32HandleKHR (D3D fence)");
            return sem;
        }
        finally { CloseHandle((nint)handle); }
    }

    /// <summary>The image to render the next frame into; false if the host hasn't released that slot yet.</summary>
    public bool BeginFrame(out VkImage image)
    {
        ulong consumed;
        Check(_api.vkGetSemaphoreCounterValue(ConsumedSemaphore, &consumed), "vkGetSemaphoreCounterValue");
        _slot = _session.TryBeginGpuFrame(consumed);
        image = _slot < 0 ? VkImage.Null : Images[_slot];
        return _slot >= 0;
    }

    /// <summary>Value to signal on <see cref="ProducedSemaphore"/> if you submit yourself (then call <see cref="Publish"/>).</summary>
    public ulong ProducedValue => _session.ProducedFenceValue;

    /// <summary>Submit <paramref name="cmd"/> signalling the produced semaphore, then publish the frame.</summary>
    public void SubmitAndPublish(VkQueue queue, VkCommandBuffer cmd, VkFence fence)
    {
        if (_slot < 0) throw new InvalidOperationException("SubmitAndPublish without a successful BeginFrame");
        var value = _session.ProducedFenceValue;
        var sem = ProducedSemaphore;
        var timeline = new VkTimelineSemaphoreSubmitInfo { signalSemaphoreValueCount = 1, pSignalSemaphoreValues = &value };
        var submit = new VkSubmitInfo
        {
            pNext = &timeline,
            commandBufferCount = 1, pCommandBuffers = &cmd,
            signalSemaphoreCount = 1, pSignalSemaphores = &sem,
        };
        Check(_api.vkQueueSubmit(queue, 1, &submit, fence), "vkQueueSubmit");
        Publish();
    }

    /// <summary>Publish after you submitted work that signals <see cref="ProducedSemaphore"/> to <see cref="ProducedValue"/>.</summary>
    public void Publish()
    {
        _session.PublishGpuFrame();
        _slot = -1;
    }

    /// <summary>True if <paramref name="physicalDevice"/> is the GPU the host renders on.</summary>
    public static bool MatchesAdapter(VkInstanceApi api, VkPhysicalDevice physicalDevice, GpuResources gpu)
    {
        var id = new VkPhysicalDeviceIDProperties();
        var props = new VkPhysicalDeviceProperties2 { pNext = &id };
        api.vkGetPhysicalDeviceProperties2(physicalDevice, &props);
        if (!id.deviceLUIDValid) return false;
        var luid = *(long*)id.deviceLUID; // LUID = { DWORD LowPart; LONG HighPart; }
        return luid == gpu.AdapterLuid;
    }

    private static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    public void Dispose()
    {
        foreach (var i in Images) _api.vkDestroyImage(i, null);
        foreach (var m in _memory) _api.vkFreeMemory(m, null);
        _api.vkDestroySemaphore(ProducedSemaphore, null);
        _api.vkDestroySemaphore(ConsumedSemaphore, null);
    }
}
