// The vulkan native extension's lookup (NX-10), written by hand and compiled with the generated stubs. Vulkan finds
// functions through vkGetInstanceProcAddr and vkGetDeviceProcAddr, which take an instance or a device, so the
// generator's one-argument lookup does not fit. These answer as the host's do, with the extension's stubs: a pointer
// exactly when the host provides the function. A stub whose function the library does not export (an extension
// function, or every function when SwiftShader is used without a loader) binds through vkGetInstanceProcAddr with the
// instance the program created or asked about last, and with no instance for the global functions. The loader's
// functions dispatch through the dispatchable handle they are given, so one binding serves every instance and device.
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet.Ext.Vulkan;

internal static unsafe class VulkanLookup
{
    private static nint instance;
    private static nint getInstanceProcAddr;
    private static nint getDeviceProcAddr;

    private static nint Query(nint function, nint handle, string name)
    {
        byte[] text = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* pointer = text) return ((delegate* unmanaged<nint, nint, nint>)function)(handle, (nint)pointer);
    }

    private static nint InstanceQuery(nint handle, string name)
    {
        nint function = getInstanceProcAddr;
        if (function == 0 && (function = getInstanceProcAddr = VulkanExtension.LibraryVulkan.Export("vkGetInstanceProcAddr")) == 0) return 0;
        return Query(function, handle, name);
    }

    /// <summary>The address a stub binds to when the library does not export its function, or 0.</summary>
    internal static nint Bind(string name)
    {
        nint address = instance != 0 ? InstanceQuery(instance, name) : 0;
        return address != 0 ? address : InstanceQuery(0, name);
    }

    /// <summary>vkGetInstanceProcAddr: the stub of a function the host provides for the instance, else 0.</summary>
    internal static nint InstanceProcAddress(nint handle, nint name)
    {
        if (name == 0) return 0;
        if (handle != 0) instance = handle;
        string text = Marshal.PtrToStringUTF8(name)!;
        return InstanceQuery(handle, text) != 0 && Commands.Table.TryGetValue(text, out Commands.Entry entry) ? entry.Stub : 0;
    }

    /// <summary>vkGetDeviceProcAddr: the stub of a function the host provides for the device, else 0.</summary>
    internal static nint DeviceProcAddress(nint device, nint name)
    {
        if (name == 0) return 0;
        nint function = getDeviceProcAddr;
        if (function == 0 && (function = getDeviceProcAddr = VulkanExtension.LibraryVulkan.Export("vkGetDeviceProcAddr")) == 0 &&
            (function = getDeviceProcAddr = Bind("vkGetDeviceProcAddr")) == 0)
            return 0;
        string text = Marshal.PtrToStringUTF8(name)!;
        return Query(function, device, text) != 0 && Commands.Table.TryGetValue(text, out Commands.Entry entry) ? entry.Stub : 0;
    }

    /// <summary>After vkCreateInstance: remembers the new instance for binding.</summary>
    internal static void InstanceCreated(int result, nint createInfo, nint allocator, nint created)
    {
        if (result == 0 && created != 0) instance = *(nint*)created;
    }

    /// <summary>After vkDestroyInstance: forgets the instance.</summary>
    internal static void InstanceDestroyed(nint destroyed, nint allocator)
    {
        if (destroyed == instance) instance = 0;
    }
}
