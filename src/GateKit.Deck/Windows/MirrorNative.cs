using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Deck.Windows;

/// <summary>
/// The Direct3D the mirror backing needs, and nothing else: open a texture another process shares,
/// copy it into a swap chain of our own, show it. No user-mode graphics library is pulled in for
/// it - about a dozen interface methods are called through their vtables, in the same spirit as
/// the rest of the Win32 here. The index beside each one is its place in that interface's vtable,
/// counted from QueryInterface; getting one wrong is the only way this can go quietly wrong, so
/// they are written down.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Mirror
{
    // --- what the shared frame is ---

    public const int FormatUnknown = 0;
    public const int FormatR8G8B8A8Unorm = 28, FormatR8G8B8A8UnormSrgb = 29;
    public const int FormatB8G8R8A8Unorm = 87, FormatB8G8R8A8UnormSrgb = 91;
    public const int FormatR10G10B10A2Unorm = 24;
    public const int FormatR16G16B16A16Float = 10;

    /// <summary>The format a flip-model swap chain may have for a frame stored in
    /// <paramref name="format"/>: an sRGB texture is copied into its plain twin (the same typeless
    /// family, which is what CopyResource asks for), and anything else is refused.</summary>
    public static int SwapChainFormat(int format) => format switch
    {
        FormatR8G8B8A8Unorm or FormatR8G8B8A8UnormSrgb => FormatR8G8B8A8Unorm,
        FormatB8G8R8A8Unorm or FormatB8G8R8A8UnormSrgb => FormatB8G8R8A8Unorm,
        FormatR10G10B10A2Unorm => FormatR10G10B10A2Unorm,
        FormatR16G16B16A16Float => FormatR16G16B16A16Float,
        _ => FormatUnknown,
    };

    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D11Device1 = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid IID_IDXGIKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");
    public static readonly Guid IID_IDXGISwapChain3 = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleDescription { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SwapChainDescription
    {
        public uint Width, Height;
        public int Format;
        public int Stereo;
        public SampleDescription Sample;
        public uint BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AdapterDescription1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    public const uint UsageRenderTargetOutput = 0x20;
    // Two values worth an hour between them, because getting either wrong gives
    // CreateSwapChainForHwnd a bare DXGI_ERROR_INVALID_CALL (0x887A0001) and nothing else to go
    // on: DXGI_SCALING_STRETCH is 0 (1 is DXGI_SCALING_NONE), and DXGI_ALPHA_MODE_IGNORE is 3.
    public const int ScalingStretch = 0;
    public const int SwapEffectDiscard = 0, SwapEffectFlipSequential = 3, SwapEffectFlipDiscard = 4;
    public const int AlphaModeIgnore = 3;
    public const int DriverTypeUnknown = 0;
    public const uint CreateDeviceBgraSupport = 0x20;
    public const uint SdkVersion = 7;
    public const uint WindowAssociationNoWindowChanges = 1, WindowAssociationNoAltEnter = 2;
    public const uint SharedResourceRead = 0x80000000, SharedResourceWrite = 1;
    public const int ColourSpaceRgbFullG2084NoneP2020 = 12;

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        int* featureLevels, uint featureLevelCount, uint sdkVersion, nint* device, int* featureLevel, nint* context);

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(Guid* riid, nint* factory);

    // --- vtable plumbing ---

    static void* Method(nint self, int index) => (void*)*(nint*)(*(nint*)self + index * sizeof(nint));

    public static int QueryInterface(nint self, in Guid iid, out nint result)
    {
        nint found = 0;
        int hr;
        fixed (Guid* id = &iid) hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(self, 0))(self, id, &found);
        result = found;
        return hr;
    }

    public static uint Release(nint self) => self == 0 ? 0 : ((delegate* unmanaged[Stdcall]<nint, uint>)Method(self, 2))(self);

    /// <summary>Releases a pointer and sets it to zero, so a half-built thing is never let go twice.</summary>
    public static void Let(ref nint self)
    {
        if (self != 0) Release(self);
        self = 0;
    }

    // --- IDXGIFactory1 / IDXGIFactory2 ---

    /// <summary>IDXGIFactory1::EnumAdapters1, vtable 12.</summary>
    public static int EnumAdapters(nint factory, uint index, out nint adapter)
    {
        nint found = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(factory, 12))(factory, index, &found);
        adapter = found;
        return hr;
    }

    /// <summary>IDXGIFactory::MakeWindowAssociation, vtable 8. DXGI must not touch our windows: the
    /// deck decides what a window does, and a message hook of DXGI's would be a second opinion.</summary>
    public static int MakeWindowAssociation(nint factory, nint window, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Method(factory, 8))(factory, window, flags);

    /// <summary>IDXGIFactory2::CreateSwapChainForHwnd, vtable 15.</summary>
    public static int CreateSwapChainForHwnd(nint factory, nint device, nint window, in SwapChainDescription description, out nint swapChain)
    {
        nint made = 0;
        int hr;
        fixed (SwapChainDescription* desc = &description)
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, SwapChainDescription*, nint, nint, nint*, int>)Method(factory, 15))(
                factory, device, window, desc, 0, 0, &made);
        swapChain = made;
        return hr;
    }

    // --- IDXGIAdapter1 ---

    /// <summary>IDXGIAdapter1::GetDesc1, vtable 10.</summary>
    public static int GetAdapterDescription(nint adapter, out AdapterDescription1 description)
    {
        AdapterDescription1 desc = default;
        var hr = ((delegate* unmanaged[Stdcall]<nint, AdapterDescription1*, int>)Method(adapter, 10))(adapter, &desc);
        description = desc;
        return hr;
    }

    // --- ID3D11Device1 ---

    /// <summary>ID3D11Device1::OpenSharedResourceByName, vtable 49.</summary>
    public static int OpenSharedResourceByName(nint device1, string name, uint access, in Guid iid, out nint resource)
    {
        nint found = 0;
        int hr;
        fixed (char* text = name)
        fixed (Guid* id = &iid)
            hr = ((delegate* unmanaged[Stdcall]<nint, char*, uint, Guid*, nint*, int>)Method(device1, 49))(device1, text, access, id, &found);
        resource = found;
        return hr;
    }

    // --- ID3D11DeviceContext ---

    /// <summary>ID3D11DeviceContext::CopyResource, vtable 47. The whole of the drawing.</summary>
    public static void CopyResource(nint context, nint destination, nint source) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(context, 47))(context, destination, source);

    // --- IDXGISwapChain / IDXGISwapChain3 ---

    /// <summary>IDXGISwapChain::Present, vtable 8.</summary>
    public static int Present(nint swapChain, uint syncInterval, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Method(swapChain, 8))(swapChain, syncInterval, flags);

    /// <summary>IDXGISwapChain::GetBuffer, vtable 9.</summary>
    public static int GetBuffer(nint swapChain, uint index, in Guid iid, out nint surface)
    {
        nint found = 0;
        int hr;
        fixed (Guid* id = &iid)
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Method(swapChain, 9))(swapChain, index, id, &found);
        surface = found;
        return hr;
    }

    /// <summary>IDXGISwapChain::ResizeBuffers, vtable 13.</summary>
    public static int ResizeBuffers(nint swapChain, uint count, uint width, uint height, int format, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)Method(swapChain, 13))(swapChain, count, width, height, format, flags);

    /// <summary>IDXGISwapChain3::CheckColorSpaceSupport, vtable 37.</summary>
    public static int CheckColourSpaceSupport(nint swapChain3, int colourSpace, out uint support)
    {
        uint found = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, uint*, int>)Method(swapChain3, 37))(swapChain3, colourSpace, &found);
        support = found;
        return hr;
    }

    /// <summary>IDXGISwapChain3::SetColorSpace1, vtable 38.</summary>
    public static int SetColourSpace(nint swapChain3, int colourSpace) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Method(swapChain3, 38))(swapChain3, colourSpace);

    // --- IDXGIKeyedMutex ---

    /// <summary>IDXGIKeyedMutex::AcquireSync, vtable 8.</summary>
    public static int AcquireSync(nint mutex, ulong key, uint milliseconds) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)Method(mutex, 8))(mutex, key, milliseconds);

    /// <summary>IDXGIKeyedMutex::ReleaseSync, vtable 9.</summary>
    public static int ReleaseSync(nint mutex, ulong key) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, int>)Method(mutex, 9))(mutex, key);
}
