using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VrcVa.Windows.OpenVr;

internal sealed partial class OpenVrD3D11Device : IDisposable
{
    private static readonly Guid DxgiFactory1Id =
        new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiFactoryEnumAdapters1Slot = 12;
    private const int DxgiAdapterGetDesc1Slot = 10;
    private const int D3d11DeviceCreateTexture2DSlot = 5;
    private const int D3d11ContextMapSlot = 14;
    private const int D3d11ContextUnmapSlot = 15;
    private const int D3d11ContextCopyResourceSlot = 47;
    private const int D3d11ViewGetResourceSlot = 7;
    private const int D3d11ShaderResourceViewGetDescSlot = 8;
    private const int D3d11Texture2DGetDescSlot = 10;
    private const int D3dDriverTypeUnknown = 0;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11CpuAccessRead = 0x20000;

    private IntPtr _device;
    private IntPtr _context;

    private OpenVrD3D11Device(IntPtr device, IntPtr context, ulong adapterLuid)
    {
        _device = device;
        _context = context;
        AdapterLuid = adapterLuid;
    }

    public ulong AdapterLuid { get; }

    public IntPtr DevicePointer => _device != IntPtr.Zero
        ? _device
        : throw new ObjectDisposedException(nameof(OpenVrD3D11Device));

    public static OpenVrD3D11Device Create(ulong requiredLuid)
    {
        IntPtr factory = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in DxgiFactory1Id, out factory));
            EnumAdapters1Delegate enumAdapters = GetComFunction<EnumAdapters1Delegate>(
                factory,
                DxgiFactoryEnumAdapters1Slot);

            for (uint index = 0; ; index++)
            {
                IntPtr candidate = IntPtr.Zero;
                int result = enumAdapters(factory, index, out candidate);
                if (result == DxgiErrorNotFound)
                {
                    break;
                }

                Marshal.ThrowExceptionForHR(result);
                try
                {
                    GetAdapterDesc1Delegate getDescription =
                        GetComFunction<GetAdapterDesc1Delegate>(
                            candidate,
                            DxgiAdapterGetDesc1Slot);
                    getDescription(candidate, out DxgiAdapterDesc1 description);
                    if (description.AdapterLuid.ToUInt64() == requiredLuid)
                    {
                        adapter = candidate;
                        candidate = IntPtr.Zero;
                        break;
                    }
                }
                finally
                {
                    ReleaseIfPresent(candidate);
                }
            }

            if (adapter == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"OpenVR compositor adapter LUID 0x{requiredLuid:X16} was not found.");
            }

            int createResult = D3D11CreateDevice(
                adapter,
                D3dDriverTypeUnknown,
                IntPtr.Zero,
                D3d11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3d11SdkVersion,
                out device,
                out _,
                out context);
            Marshal.ThrowExceptionForHR(createResult);

            OpenVrD3D11Device created = new(device, context, requiredLuid);
            device = IntPtr.Zero;
            context = IntPtr.Zero;
            return created;
        }
        finally
        {
            ReleaseIfPresent(context);
            ReleaseIfPresent(device);
            ReleaseIfPresent(adapter);
            ReleaseIfPresent(factory);
        }
    }

    public OpenVrEyeMirrorFrame ReadMirrorView(
        IntPtr shaderResourceView,
        OpenVrEye eye)
    {
        ObjectDisposedException.ThrowIf(_device == IntPtr.Zero, this);
        if (shaderResourceView == IntPtr.Zero)
        {
            throw new ArgumentException("The mirror texture view is null.", nameof(shaderResourceView));
        }

        IntPtr sourceTexture = IntPtr.Zero;
        IntPtr stagingTexture = IntPtr.Zero;
        bool mapped = false;
        uint mappedSubresource = 0;
        try
        {
            GetResourceDelegate getResource = GetComFunction<GetResourceDelegate>(
                shaderResourceView,
                D3d11ViewGetResourceSlot);
            getResource(shaderResourceView, out sourceTexture);
            if (sourceTexture == IntPtr.Zero)
            {
                throw new InvalidOperationException("The OpenVR mirror view has no D3D11 resource.");
            }

            GetTexture2DDescDelegate getDescription =
                GetComFunction<GetTexture2DDescDelegate>(
                    sourceTexture,
                    D3d11Texture2DGetDescSlot);
            getDescription(sourceTexture, out D3d11Texture2DDesc description);
            GetShaderResourceViewDescDelegate getViewDescription =
                GetComFunction<GetShaderResourceViewDescDelegate>(
                    shaderResourceView,
                    D3d11ShaderResourceViewGetDescSlot);
            getViewDescription(shaderResourceView, out D3d11ShaderResourceViewDesc viewDescription);
            ValidateDescription(description, viewDescription);

            uint selectedMip = viewDescription.MostDetailedMip;
            uint selectedArraySlice = viewDescription.ViewDimension == D3d11SrvDimension.Texture2DArray
                ? viewDescription.FirstArraySlice
                : 0;
            uint selectedSubresource = checked(
                selectedMip + (selectedArraySlice * description.MipLevels));
            mappedSubresource = selectedSubresource;
            uint selectedWidth = Math.Max(1, description.Width >> checked((int)selectedMip));
            uint selectedHeight = Math.Max(1, description.Height >> checked((int)selectedMip));

            D3d11Texture2DDesc stagingDescription = description;
            stagingDescription.Usage = D3d11Usage.Staging;
            stagingDescription.BindFlags = 0;
            stagingDescription.CpuAccessFlags = D3d11CpuAccessRead;
            stagingDescription.MiscFlags = 0;

            CreateTexture2DDelegate createTexture =
                GetComFunction<CreateTexture2DDelegate>(
                    _device,
                    D3d11DeviceCreateTexture2DSlot);
            Marshal.ThrowExceptionForHR(createTexture(
                _device,
                ref stagingDescription,
                IntPtr.Zero,
                out stagingTexture));

            CopyResourceDelegate copyResource = GetComFunction<CopyResourceDelegate>(
                _context,
                D3d11ContextCopyResourceSlot);
            copyResource(_context, stagingTexture, sourceTexture);

            MapDelegate map = GetComFunction<MapDelegate>(_context, D3d11ContextMapSlot);
            Marshal.ThrowExceptionForHR(map(
                _context,
                stagingTexture,
                selectedSubresource,
                D3d11Map.Read,
                0,
                out D3d11MappedSubresource mappedResource));
            mapped = true;

            byte[] bgraPixels = CopyAsBgra(
                selectedWidth,
                selectedHeight,
                viewDescription.Format,
                mappedResource);
            return new OpenVrEyeMirrorFrame(
                eye,
                checked((int)selectedWidth),
                checked((int)selectedHeight),
                viewDescription.Format,
                viewDescription.ViewDimension,
                selectedArraySlice,
                bgraPixels);
        }
        finally
        {
            if (mapped && stagingTexture != IntPtr.Zero && _context != IntPtr.Zero)
            {
                UnmapDelegate unmap = GetComFunction<UnmapDelegate>(
                    _context,
                    D3d11ContextUnmapSlot);
                unmap(_context, stagingTexture, mappedSubresource);
            }

            ReleaseIfPresent(stagingTexture);
            ReleaseIfPresent(sourceTexture);
        }
    }

    public void Dispose()
    {
        IntPtr context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        IntPtr device = Interlocked.Exchange(ref _device, IntPtr.Zero);
        ReleaseIfPresent(context);
        ReleaseIfPresent(device);
    }

    private static void ValidateDescription(
        D3d11Texture2DDesc description,
        D3d11ShaderResourceViewDesc viewDescription)
    {
        if (description.Width == 0 || description.Height == 0)
        {
            throw new InvalidOperationException("The OpenVR mirror texture has invalid dimensions.");
        }

        if (description.SampleDescription.Count != 1)
        {
            throw new NotSupportedException(
                $"Multisampled OpenVR mirror textures are not supported (count={description.SampleDescription.Count}).");
        }

        if (viewDescription.ViewDimension is not (
            D3d11SrvDimension.Texture2D or D3d11SrvDimension.Texture2DArray))
        {
            throw new NotSupportedException(
                $"OpenVR mirror SRV dimension {viewDescription.ViewDimension} is not supported by the spike.");
        }

        if (viewDescription.MostDetailedMip >= description.MipLevels)
        {
            throw new InvalidOperationException("The OpenVR mirror SRV references an invalid mip level.");
        }

        if (viewDescription.ViewDimension == D3d11SrvDimension.Texture2DArray
            && viewDescription.FirstArraySlice >= description.ArraySize)
        {
            throw new InvalidOperationException("The OpenVR mirror SRV references an invalid array slice.");
        }

        if (viewDescription.Format is not (
            DxgiFormat.R10G10B10A2Unorm
            or DxgiFormat.R8G8B8A8Typeless
            or DxgiFormat.R8G8B8A8Unorm
            or DxgiFormat.R8G8B8A8UnormSrgb
            or DxgiFormat.B8G8R8A8Unorm
            or DxgiFormat.B8G8R8A8UnormSrgb))
        {
            throw new NotSupportedException(
                $"OpenVR mirror DXGI format {(int)viewDescription.Format} is not supported by the spike.");
        }
    }

    private static byte[] CopyAsBgra(
        uint sourceWidth,
        uint sourceHeight,
        DxgiFormat sourceFormat,
        D3d11MappedSubresource mapped)
    {
        int width = checked((int)sourceWidth);
        int height = checked((int)sourceHeight);
        int packedStride = checked(width * 4);
        if (mapped.RowPitch < packedStride)
        {
            throw new InvalidOperationException("The mapped OpenVR mirror row pitch is too small.");
        }

        byte[] sourceRow = new byte[packedStride];
        byte[] pixels = new byte[checked(packedStride * height)];
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(mapped.Data, checked((int)(y * mapped.RowPitch))),
                    sourceRow,
                    0,
                    packedStride);
                int targetOffset = y * packedStride;
                ConvertRowToBgra(
                    sourceRow,
                    pixels.AsSpan(targetOffset, packedStride),
                    sourceFormat);
            }

            return pixels;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(pixels);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourceRow);
        }
    }

    private static void ConvertRowToBgra(
        ReadOnlySpan<byte> source,
        Span<byte> target,
        DxgiFormat format)
    {
        if (format is DxgiFormat.B8G8R8A8Unorm or DxgiFormat.B8G8R8A8UnormSrgb)
        {
            source.CopyTo(target);
            return;
        }

        if (format is DxgiFormat.R8G8B8A8Typeless
            or DxgiFormat.R8G8B8A8Unorm
            or DxgiFormat.R8G8B8A8UnormSrgb)
        {
            for (int offset = 0; offset < source.Length; offset += 4)
            {
                target[offset] = source[offset + 2];
                target[offset + 1] = source[offset + 1];
                target[offset + 2] = source[offset];
                target[offset + 3] = source[offset + 3];
            }

            return;
        }

        for (int offset = 0; offset < source.Length; offset += 4)
        {
            uint packed = MemoryMarshal.Read<uint>(source[offset..]);
            target[offset] = ToByte((packed >> 20) & 0x3ff, 1023);
            target[offset + 1] = ToByte((packed >> 10) & 0x3ff, 1023);
            target[offset + 2] = ToByte(packed & 0x3ff, 1023);
            target[offset + 3] = ToByte((packed >> 30) & 0x3, 3);
        }
    }

    private static byte ToByte(uint value, uint maximum) =>
        checked((byte)((value * 255u + (maximum / 2u)) / maximum));

    private static T GetComFunction<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        if (function == IntPtr.Zero)
        {
            throw new InvalidOperationException($"COM vtable slot {slot} is unavailable.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static void ReleaseIfPresent(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Marshal.Release(value);
        }
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid interfaceId, out IntPtr factory);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint selectedFeatureLevel,
        out IntPtr immediateContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(
        IntPtr factory,
        uint adapterIndex,
        out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetAdapterDesc1Delegate(
        IntPtr adapter,
        out DxgiAdapterDesc1 description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(
        IntPtr device,
        ref D3d11Texture2DDesc description,
        IntPtr initialData,
        out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetResourceDelegate(IntPtr view, out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTexture2DDescDelegate(
        IntPtr texture,
        out D3d11Texture2DDesc description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetShaderResourceViewDescDelegate(
        IntPtr shaderResourceView,
        out D3d11ShaderResourceViewDesc description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(
        IntPtr context,
        IntPtr destination,
        IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(
        IntPtr context,
        IntPtr resource,
        uint subresource,
        D3d11Map mapType,
        uint mapFlags,
        out D3d11MappedSubresource mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(
        IntPtr context,
        IntPtr resource,
        uint subresource);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public readonly ulong ToUInt64() =>
            LowPart | ((ulong)unchecked((uint)HighPart) << 32);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public DxgiFormat Format;
        public DxgiSampleDescription SampleDescription;
        public D3d11Usage Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSampleDescription
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11ShaderResourceViewDesc
    {
        public DxgiFormat Format;
        public D3d11SrvDimension ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    private enum D3d11Usage
    {
        Staging = 3,
    }

    private enum D3d11Map
    {
        Read = 1,
    }
}

internal enum D3d11SrvDimension
{
    Texture2D = 4,
    Texture2DArray = 5,
}

internal enum DxgiFormat
{
    R10G10B10A2Unorm = 24,
    R8G8B8A8Typeless = 27,
    R8G8B8A8Unorm = 28,
    R8G8B8A8UnormSrgb = 29,
    B8G8R8A8Unorm = 87,
    B8G8R8A8UnormSrgb = 91,
}

internal sealed class OpenVrEyeMirrorFrame : IDisposable
{
    private byte[]? _bgraPixels;

    public OpenVrEyeMirrorFrame(
        OpenVrEye eye,
        int width,
        int height,
        DxgiFormat format,
        D3d11SrvDimension viewDimension,
        uint arraySlice,
        byte[] bgraPixels)
    {
        Eye = eye;
        Width = width;
        Height = height;
        Format = format;
        ViewDimension = viewDimension;
        ArraySlice = arraySlice;
        _bgraPixels = bgraPixels;
    }

    public OpenVrEye Eye { get; }

    public int Width { get; }

    public int Height { get; }

    public DxgiFormat Format { get; }

    public D3d11SrvDimension ViewDimension { get; }

    public uint ArraySlice { get; }

    public TimeSpan Elapsed { get; private set; }

    public ReadOnlyMemory<byte> BgraPixels =>
        _bgraPixels ?? throw new ObjectDisposedException(nameof(OpenVrEyeMirrorFrame));

    internal void SetElapsed(TimeSpan elapsed) => Elapsed = elapsed;

    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _bgraPixels, null);
        if (pixels is not null)
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }
}
