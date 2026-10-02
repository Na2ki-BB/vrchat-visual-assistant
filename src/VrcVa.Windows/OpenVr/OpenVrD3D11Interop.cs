using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VrcVa.Windows.OpenVr;

internal sealed partial class OpenVrD3D11Device : IDisposable
{
    private static readonly Guid DxgiFactory1Id =
        new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid DxgiResourceId =
        new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiFactoryEnumAdapters1Slot = 12;
    private const int DxgiAdapterGetDesc1Slot = 10;
    private const int D3d11DeviceCreateTexture2DSlot = 5;
    private const int D3d11ContextMapSlot = 14;
    private const int D3d11ContextUnmapSlot = 15;
    private const int D3d11ContextCopyResourceSlot = 47;
    private const int D3d11ContextFlushSlot = 111;
    private const int DxgiResourceGetSharedHandleSlot = 8;
    private const int D3d11ViewGetResourceSlot = 7;
    private const int D3d11ShaderResourceViewGetDescSlot = 8;
    private const int D3d11Texture2DGetDescSlot = 10;
    private const int D3dDriverTypeUnknown = 0;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11CpuAccessRead = 0x20000;
    private const uint D3d11CpuAccessWrite = 0x10000;
    private const uint D3d11BindShaderResource = 0x8;
    private const uint D3d11ResourceMiscShared = 0x2;

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

    public OpenVrD3D11Texture CreateOverlayTexture(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_device == IntPtr.Zero, this);
        if (width == 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
        if (height == 0) { throw new ArgumentOutOfRangeException(nameof(height)); }

        D3d11Texture2DDesc sharedDescription = CreateSharedTextureDescription(width, height);
        D3d11Texture2DDesc uploadDescription = CreateUploadTextureDescription(width, height);
        CreateTexture2DDelegate createTexture = GetComFunction<CreateTexture2DDelegate>(
            _device,
            D3d11DeviceCreateTexture2DSlot);
        IntPtr sharedTexture = IntPtr.Zero;
        IntPtr uploadTexture = IntPtr.Zero;
        IntPtr dxgiResource = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(createTexture(
                _device,
                ref sharedDescription,
                IntPtr.Zero,
                out sharedTexture));
            Marshal.ThrowExceptionForHR(createTexture(
                _device,
                ref uploadDescription,
                IntPtr.Zero,
                out uploadTexture));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                sharedTexture,
                in DxgiResourceId,
                out dxgiResource));
            GetSharedHandleDelegate getSharedHandle = GetComFunction<GetSharedHandleDelegate>(
                dxgiResource,
                DxgiResourceGetSharedHandleSlot);
            Marshal.ThrowExceptionForHR(getSharedHandle(dxgiResource, out IntPtr sharedHandle));
            if (sharedHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("D3D11 returned an empty shared texture handle.");
            }

            OpenVrD3D11Texture created = new(
                _context,
                uploadTexture,
                sharedTexture,
                sharedHandle,
                width,
                height);
            uploadTexture = IntPtr.Zero;
            sharedTexture = IntPtr.Zero;
            return created;
        }
        finally
        {
            ReleaseIfPresent(dxgiResource);
            ReleaseIfPresent(uploadTexture);
            ReleaseIfPresent(sharedTexture);
        }
    }

    internal static D3d11Texture2DDesc CreateSharedTextureDescription(uint width, uint height) =>
        new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.R8G8B8A8Unorm,
            SampleDescription = new DxgiSampleDescription { Count = 1 },
            Usage = D3d11Usage.Default,
            BindFlags = D3d11BindShaderResource,
            MiscFlags = D3d11ResourceMiscShared,
        };

    internal static D3d11Texture2DDesc CreateUploadTextureDescription(uint width, uint height)
    {
        D3d11Texture2DDesc description = CreateSharedTextureDescription(width, height);
        description.Usage = D3d11Usage.Dynamic;
        description.CpuAccessFlags = D3d11CpuAccessWrite;
        description.MiscFlags = 0;
        return description;
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
    private delegate void FlushDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSharedHandleDelegate(IntPtr resource, out IntPtr sharedHandle);

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
    internal struct D3d11Texture2DDesc
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
    internal struct DxgiSampleDescription
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

    internal enum D3d11Usage
    {
        Default = 0,
        Dynamic = 2,
        Staging = 3,
    }

    private enum D3d11Map
    {
        Read = 1,
        WriteDiscard = 4,
    }

    internal sealed class OpenVrD3D11Texture : IDisposable
    {
        private readonly IntPtr _context;
        private IntPtr _uploadTexture;
        private IntPtr _sharedTexture;

        internal OpenVrD3D11Texture(
            IntPtr context,
            IntPtr uploadTexture,
            IntPtr sharedTexture,
            IntPtr sharedHandle,
            uint width,
            uint height)
        {
            _context = context;
            _uploadTexture = uploadTexture;
            _sharedTexture = sharedTexture;
            SharedHandle = sharedHandle;
            Width = width;
            Height = height;
        }

        public uint Width { get; }

        public uint Height { get; }

        public IntPtr SharedHandle { get; }

        private IntPtr UploadTexture => _uploadTexture != IntPtr.Zero
            ? _uploadTexture
            : throw new ObjectDisposedException(nameof(OpenVrD3D11Texture));

        public void Update(ReadOnlySpan<byte> rgbaPixels)
        {
            IntPtr uploadTexture = UploadTexture;
            MapDelegate map = GetComFunction<MapDelegate>(_context, D3d11ContextMapSlot);
            Marshal.ThrowExceptionForHR(map(
                _context,
                uploadTexture,
                0,
                D3d11Map.WriteDiscard,
                0,
                out D3d11MappedSubresource mapped));
            try
            {
                CopyRgbaToMappedTexture(rgbaPixels, Width, Height, mapped.Data, mapped.RowPitch);
            }
            finally
            {
                UnmapDelegate unmap = GetComFunction<UnmapDelegate>(
                    _context,
                    D3d11ContextUnmapSlot);
                unmap(_context, uploadTexture, 0);
            }

            IntPtr sharedTexture = _sharedTexture != IntPtr.Zero
                ? _sharedTexture
                : throw new ObjectDisposedException(nameof(OpenVrD3D11Texture));
            // OpenVR samples DXGI shared-handle textures directly. Its public ABI
            // requires writers to replace their contents with an atomic GPU copy.
            CopyResourceDelegate copyResource = GetComFunction<CopyResourceDelegate>(
                _context,
                D3d11ContextCopyResourceSlot);
            copyResource(_context, sharedTexture, uploadTexture);
            FlushDelegate flush = GetComFunction<FlushDelegate>(_context, D3d11ContextFlushSlot);
            flush(_context);
        }

        public void Dispose()
        {
            IntPtr uploadTexture = Interlocked.Exchange(ref _uploadTexture, IntPtr.Zero);
            IntPtr sharedTexture = Interlocked.Exchange(ref _sharedTexture, IntPtr.Zero);
            ReleaseIfPresent(uploadTexture);
            ReleaseIfPresent(sharedTexture);
        }
    }

    internal static unsafe void CopyRgbaToMappedTexture(
        ReadOnlySpan<byte> rgbaPixels,
        uint width,
        uint height,
        IntPtr destination,
        uint destinationRowPitch)
    {
        int rowBytes = checked((int)(width * 4));
        int expectedBytes = checked(rowBytes * (int)height);
        if (rgbaPixels.Length != expectedBytes)
        {
            throw new ArgumentException(
                "The RGBA buffer size does not match its dimensions.",
                nameof(rgbaPixels));
        }
        if (destination == IntPtr.Zero)
        {
            throw new ArgumentException("The mapped D3D11 destination is null.", nameof(destination));
        }
        if (destinationRowPitch < rowBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationRowPitch),
                "The mapped D3D11 row pitch is smaller than one RGBA row.");
        }

        fixed (byte* sourceStart = rgbaPixels)
        {
            byte* destinationStart = (byte*)destination;
            for (uint row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(
                    sourceStart + checked((int)(row * (uint)rowBytes)),
                    destinationStart + checked((int)(row * destinationRowPitch)),
                    destinationRowPitch,
                    rowBytes);
            }
        }
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
