using System.Runtime.InteropServices;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class OpenVrD3D11TextureUploadTests
{
    [Fact]
    public void Overlay027_UsesPublishedSetOverlayTextureSlot()
    {
        Assert.Equal(58, OpenVrInterop.SetOverlayTextureFunctionSlot);
        OpenVrInterop.ValidateAbi();
    }

    [Fact]
    public void CopyRgbaToMappedTexture_CopiesRowsAndPreservesPitchPadding()
    {
        byte[] rgba =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16,
        ];
        IntPtr destination = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)0xee, 24).ToArray(), 0, destination, 24);

            OpenVrD3D11Device.CopyRgbaToMappedTexture(
                rgba,
                width: 2,
                height: 2,
                destination,
                destinationRowPitch: 12);

            byte[] actual = new byte[24];
            Marshal.Copy(destination, actual, 0, actual.Length);
            Assert.Equal(rgba[..8], actual[..8]);
            Assert.Equal([0xee, 0xee, 0xee, 0xee], actual[8..12]);
            Assert.Equal(rgba[8..], actual[12..20]);
            Assert.Equal([0xee, 0xee, 0xee, 0xee], actual[20..24]);
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void CopyRgbaToMappedTexture_RejectsMismatchedBufferAndShortPitch()
    {
        IntPtr destination = Marshal.AllocHGlobal(16);
        try
        {
            Assert.Throws<ArgumentException>(() =>
                OpenVrD3D11Device.CopyRgbaToMappedTexture(
                    new byte[15],
                    width: 2,
                    height: 2,
                    destination,
                    destinationRowPitch: 8));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                OpenVrD3D11Device.CopyRgbaToMappedTexture(
                    new byte[16],
                    width: 2,
                    height: 2,
                    destination,
                    destinationRowPitch: 7));
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void OverlayTexture_UpdateRepeatsAndDisposeAreSafeOnWarpD3D11()
    {
        const int d3dDriverTypeWarp = 5;
        const uint d3d11SdkVersion = 7;
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr uploadTexture = IntPtr.Zero;
        IntPtr sharedTexture = IntPtr.Zero;
        OpenVrD3D11Device.OpenVrD3D11Texture? overlayTexture = null;
        try
        {
            int createDeviceResult = D3D11CreateDevice(
                IntPtr.Zero,
                d3dDriverTypeWarp,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                d3d11SdkVersion,
                out device,
                out _,
                out context);
            Assert.True(
                createDeviceResult >= 0,
                $"D3D11CreateDevice(WARP) failed with HRESULT 0x{createDeviceResult:X8}.");

            OpenVrD3D11Device.D3d11Texture2DDesc description =
                OpenVrD3D11Device.CreateUploadTextureDescription(2, 2);
            Assert.Equal(OpenVrD3D11Device.D3d11Usage.Dynamic, description.Usage);
            Assert.Equal(0x8u, description.BindFlags);
            Assert.Equal(0x10000u, description.CpuAccessFlags);
            Assert.Equal(0u, description.MiscFlags);

            uploadTexture = CreateTexture2D(device, ref description);
            OpenVrD3D11Device.D3d11Texture2DDesc sharedDescription =
                OpenVrD3D11Device.CreateSharedTextureDescription(2, 2);
            sharedTexture = CreateTexture2D(device, ref sharedDescription);
            IntPtr sharedTextureForReadback = sharedTexture;
            overlayTexture = new OpenVrD3D11Device.OpenVrD3D11Texture(
                context,
                uploadTexture,
                sharedTexture,
                IntPtr.Zero,
                2,
                2);
            uploadTexture = IntPtr.Zero;
            sharedTexture = IntPtr.Zero;

            byte[] first = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            byte[] second = Enumerable.Range(101, 16).Select(value => (byte)value).ToArray();
            overlayTexture.Update(first);
            Assert.Equal(first, ReadTexturePixels(device, context, sharedTextureForReadback, 2, 2));
            overlayTexture.Update(second);
            Assert.Equal(second, ReadTexturePixels(device, context, sharedTextureForReadback, 2, 2));

            overlayTexture.Dispose();
            overlayTexture.Dispose();
            Assert.Throws<ObjectDisposedException>(() => overlayTexture.Update(second));
        }
        finally
        {
            overlayTexture?.Dispose();
            ReleaseIfPresent(sharedTexture);
            ReleaseIfPresent(uploadTexture);
            ReleaseIfPresent(context);
            ReleaseIfPresent(device);
        }
    }

    private static IntPtr CreateTexture2D(
        IntPtr device,
        ref OpenVrD3D11Device.D3d11Texture2DDesc description)
    {
        const int createTexture2DSlot = 5;
        CreateTexture2DDelegate createTexture = GetComFunction<CreateTexture2DDelegate>(
            device,
            createTexture2DSlot);
        int result = createTexture(device, ref description, IntPtr.Zero, out IntPtr texture);
        Marshal.ThrowExceptionForHR(result);
        return texture;
    }

    private static byte[] ReadTexturePixels(
        IntPtr device,
        IntPtr context,
        IntPtr source,
        uint width,
        uint height)
    {
        const int contextMapSlot = 14;
        const int contextUnmapSlot = 15;
        const int contextCopyResourceSlot = 47;
        OpenVrD3D11Device.D3d11Texture2DDesc stagingDescription =
            OpenVrD3D11Device.CreateSharedTextureDescription(width, height);
        stagingDescription.Usage = OpenVrD3D11Device.D3d11Usage.Staging;
        stagingDescription.BindFlags = 0;
        stagingDescription.CpuAccessFlags = 0x20000;
        stagingDescription.MiscFlags = 0;
        IntPtr stagingTexture = CreateTexture2D(device, ref stagingDescription);
        try
        {
            CopyResourceDelegate copyResource = GetComFunction<CopyResourceDelegate>(
                context,
                contextCopyResourceSlot);
            copyResource(context, stagingTexture, source);
            MapDelegate map = GetComFunction<MapDelegate>(context, contextMapSlot);
            Marshal.ThrowExceptionForHR(map(
                context,
                stagingTexture,
                0,
                1,
                0,
                out D3d11MappedSubresource mapped));
            try
            {
                int rowBytes = checked((int)(width * 4));
                byte[] pixels = new byte[checked(rowBytes * (int)height)];
                for (int row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.Data, checked(row * (int)mapped.RowPitch)),
                        pixels,
                        row * rowBytes,
                        rowBytes);
                }

                return pixels;
            }
            finally
            {
                UnmapDelegate unmap = GetComFunction<UnmapDelegate>(context, contextUnmapSlot);
                unmap(context, stagingTexture, 0);
            }
        }
        finally
        {
            ReleaseIfPresent(stagingTexture);
        }
    }

    private static T GetComFunction<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void ReleaseIfPresent(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Marshal.Release(value);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(
        IntPtr device,
        ref OpenVrD3D11Device.D3d11Texture2DDesc description,
        IntPtr initialData,
        out IntPtr texture);

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
        int mapType,
        uint mapFlags,
        out D3d11MappedSubresource mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(
        IntPtr context,
        IntPtr resource,
        uint subresource);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
