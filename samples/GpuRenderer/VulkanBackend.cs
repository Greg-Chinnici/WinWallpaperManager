using System.Runtime.InteropServices;
using Vortice.Vulkan;
using WallpaperProtocol;
using WallpaperProtocol.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace GpuRenderer;

/// <summary>
/// Vulkan 1.2: imports the host's textures/fences, clears the image, copies a pre-filled bar image
/// into it, then releases it to VK_QUEUE_FAMILY_EXTERNAL in GENERAL layout for the host.
/// </summary>
internal sealed unsafe class VulkanBackend : IBackend
{
    private readonly Look _look;
    private readonly VkInstance _instance;
    private readonly VkInstanceApi _vki;
    private readonly VkDevice _device;
    private readonly VkDeviceApi _vkd;
    private readonly VkQueue _queue;
    private readonly uint _family;
    private readonly VkCommandPool _pool;
    private readonly VkCommandBuffer _cmd;
    private readonly VkFence _fence;
    private readonly VkImage _bar;
    private readonly VkDeviceMemory _barMemory;
    private readonly VulkanFrameOutput _output;
    private readonly uint _w, _h, _barW;

    public VulkanBackend(WallpaperSession session, Look look)
    {
        _look = look;
        (_w, _h) = ((uint)session.Descriptor.Width, (uint)session.Descriptor.Height);
        _barW = (uint)look.BarWidth((int)_w);
        Check(vkInitialize(), "vkInitialize (is a Vulkan driver installed?)");

        // Instance (Vulkan 1.2 for timeline semaphores).
        var appName = Utf8("WinWallpaper GPU sample");
        var app = new VkApplicationInfo { pApplicationName = appName, apiVersion = VkVersion.Version_1_2 };
        var ici = new VkInstanceCreateInfo { pApplicationInfo = &app };
        VkInstance instance;
        Check(vkCreateInstance(&ici, null, &instance), "vkCreateInstance");
        _instance = instance;
        _vki = GetApi(instance);

        // The physical device the host renders on (matched by LUID).
        uint count = 0;
        _vki.vkEnumeratePhysicalDevices(&count, null);
        var devices = stackalloc VkPhysicalDevice[(int)count];
        _vki.vkEnumeratePhysicalDevices(&count, devices);
        VkPhysicalDevice phys = default;
        for (var i = 0; i < count; i++) if (VulkanFrameOutput.MatchesAdapter(_vki, devices[i], session.Gpu!)) phys = devices[i];
        if (phys.Handle == 0) throw new InvalidOperationException("No Vulkan device matches the host's GPU");

        uint qCount = 0;
        _vki.vkGetPhysicalDeviceQueueFamilyProperties(phys, &qCount, null);
        var families = stackalloc VkQueueFamilyProperties[(int)qCount];
        _vki.vkGetPhysicalDeviceQueueFamilyProperties(phys, &qCount, families);
        _family = uint.MaxValue;
        for (uint i = 0; i < qCount; i++) if ((families[i].queueFlags & VkQueueFlags.Graphics) != 0) { _family = i; break; }

        // Device: external memory/semaphore extensions + timeline semaphores.
        var priority = 1f;
        var qci = new VkDeviceQueueCreateInfo { queueFamilyIndex = _family, queueCount = 1, pQueuePriorities = &priority };
        var features12 = new VkPhysicalDeviceVulkan12Features { timelineSemaphore = true };
        var exts = new byte*[VulkanFrameOutput.RequiredDeviceExtensions.Length];
        for (var i = 0; i < exts.Length; i++) exts[i] = Utf8(VulkanFrameOutput.RequiredDeviceExtensions[i]);
        fixed (byte** pExts = exts)
        {
            var dci = new VkDeviceCreateInfo
            {
                pNext = &features12, queueCreateInfoCount = 1, pQueueCreateInfos = &qci,
                enabledExtensionCount = (uint)exts.Length, ppEnabledExtensionNames = pExts,
            };
            VkDevice device;
            Check(_vki.vkCreateDevice(phys, &dci, null, &device), "vkCreateDevice");
            _device = device;
        }
        _vkd = GetApi(instance, _device);
        _vkd.vkGetDeviceQueue(_family, 0, out _queue);

        var pci = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = _family };
        VkCommandPool pool;
        Check(_vkd.vkCreateCommandPool(&pci, null, &pool), "vkCreateCommandPool");
        _pool = pool;
        var cai = new VkCommandBufferAllocateInfo { commandPool = _pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cmd;
        Check(_vkd.vkAllocateCommandBuffers(&cai, &cmd), "vkAllocateCommandBuffers");
        _cmd = cmd;
        var fci = new VkFenceCreateInfo { flags = VkFenceCreateFlags.Signaled };
        VkFence fence;
        Check(_vkd.vkCreateFence(&fci, null, &fence), "vkCreateFence");
        _fence = fence;

        _output = new VulkanFrameOutput(session, _vki, phys, _vkd);
        (_bar, _barMemory) = CreateBarImage(phys);
    }

    /// <summary>A small device-local image pre-filled with the bar colour (source for vkCmdCopyImage).</summary>
    private (VkImage, VkDeviceMemory) CreateBarImage(VkPhysicalDevice phys)
    {
        var ci = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D, format = VulkanFrameOutput.Format, extent = new VkExtent3D(_barW, _h, 1),
            mipLevels = 1, arrayLayers = 1, samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst, initialLayout = VkImageLayout.Undefined,
        };
        VkImage image;
        Check(_vkd.vkCreateImage(&ci, null, &image), "vkCreateImage (bar)");
        _vkd.vkGetImageMemoryRequirements(image, out var req);
        _vki.vkGetPhysicalDeviceMemoryProperties(phys, out var props);
        uint type = 0;
        for (uint i = 0; i < props.memoryTypeCount; i++)
            if ((req.memoryTypeBits & (1u << (int)i)) != 0 && (props.memoryTypes[(int)i].propertyFlags & VkMemoryPropertyFlags.DeviceLocal) != 0) { type = i; break; }
        var ai = new VkMemoryAllocateInfo { allocationSize = req.size, memoryTypeIndex = type };
        VkDeviceMemory memory;
        Check(_vkd.vkAllocateMemory(&ai, null, &memory), "vkAllocateMemory (bar)");
        Check(_vkd.vkBindImageMemory(image, memory, 0), "vkBindImageMemory (bar)");

        BeginRecord();
        Barrier(image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED);
        Clear(image, _look.Bar);
        Barrier(image, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED);
        EndRecord();
        var cmd = _cmd;
        var f = _fence;
        var submit = new VkSubmitInfo { commandBufferCount = 1, pCommandBuffers = &cmd };
        Check(_vkd.vkResetFences(1, &f), "vkResetFences");
        Check(_vkd.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit (bar)");
        WaitFence();
        return (image, memory);
    }

    public bool RenderFrame(double t)
    {
        if (!_output.BeginFrame(out var image)) return false;
        WaitFence(); // our previous submission (one command buffer, reused)
        var f = _fence;
        Check(_vkd.vkResetFences(1, &f), "vkResetFences");
        var x = _look.BarX((int)_w, t);
        BeginRecord();
        // Acquire from the host (external) and discard old contents; we overwrite every pixel.
        Barrier(image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VK_QUEUE_FAMILY_EXTERNAL, _family);
        Clear(image, _look.Background);
        var region = new VkImageCopy
        {
            srcSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            dstSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            dstOffset = new VkOffset3D(x, 0, 0),
            extent = new VkExtent3D(_barW, _h, 1),
        };
        _vkd.vkCmdCopyImage(_cmd, _bar, VkImageLayout.TransferSrcOptimal, image, VkImageLayout.TransferDstOptimal, 1, &region);
        // Release back to the host (Direct3D 11) in GENERAL layout.
        Barrier(image, VkImageLayout.TransferDstOptimal, VkImageLayout.General, _family, VK_QUEUE_FAMILY_EXTERNAL);
        EndRecord();
        _output.SubmitAndPublish(_queue, _cmd, _fence); // signals the produced timeline semaphore
        return true;
    }

    private void BeginRecord()
    {
        Check(_vkd.vkResetCommandBuffer(_cmd, 0), "vkResetCommandBuffer");
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_vkd.vkBeginCommandBuffer(_cmd, &bi), "vkBeginCommandBuffer");
    }

    private void EndRecord() => Check(_vkd.vkEndCommandBuffer(_cmd), "vkEndCommandBuffer");

    private void Clear(VkImage image, (float R, float G, float B, float A) c)
    {
        var color = new VkClearColorValue(c.R, c.G, c.B, c.A);
        var range = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);
        _vkd.vkCmdClearColorImage(_cmd, image, VkImageLayout.TransferDstOptimal, &color, 1, &range);
    }

    private void Barrier(VkImage image, VkImageLayout from, VkImageLayout to, uint srcFamily, uint dstFamily)
    {
        var b = new VkImageMemoryBarrier
        {
            srcAccessMask = VkAccessFlags.MemoryWrite, dstAccessMask = VkAccessFlags.MemoryRead | VkAccessFlags.MemoryWrite,
            oldLayout = from, newLayout = to, srcQueueFamilyIndex = srcFamily, dstQueueFamilyIndex = dstFamily,
            image = image, subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        _vkd.vkCmdPipelineBarrier(_cmd, VkPipelineStageFlags.AllCommands, VkPipelineStageFlags.AllCommands, 0, 0, null, 0, null, 1, &b);
    }

    private void WaitFence()
    {
        var f = _fence;
        _vkd.vkWaitForFences(1, &f, true, ulong.MaxValue);
    }

    private static byte* Utf8(string s) => (byte*)Marshal.StringToHGlobalAnsi(s); // freed with the process; fine for a sample

    private static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new InvalidOperationException($"{what} failed: {r}");
    }

    public void Dispose()
    {
        _vkd.vkDeviceWaitIdle();
        _output.Dispose();
        _vkd.vkDestroyImage(_bar, null); _vkd.vkFreeMemory(_barMemory, null);
        _vkd.vkDestroyFence(_fence, null); _vkd.vkDestroyCommandPool(_pool, null);
        _vkd.vkDestroyDevice(null);
        _vki.vkDestroyInstance(null);
    }
}
