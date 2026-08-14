namespace VrcVa.Windows.OpenVr;

internal static class PointerCursorTexture
{
    public const int PixelSize = 64;

    public static byte[] RenderRgba()
    {
        byte[] pixels = new byte[PixelSize * PixelSize * 4];
        float center = (PixelSize - 1) / 2f;
        for (int y = 0; y < PixelSize; y++)
        {
            for (int x = 0; x < PixelSize; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                if (distance > 27)
                {
                    continue;
                }

                int index = ((y * PixelSize) + x) * 4;
                bool outline = distance >= 20;
                pixels[index] = outline ? (byte)255 : (byte)78;
                pixels[index + 1] = outline ? (byte)255 : (byte)205;
                pixels[index + 2] = 255;
                pixels[index + 3] = distance >= 25
                    ? (byte)Math.Clamp((27 - distance) * 128, 0, 255)
                    : (byte)235;
            }
        }

        return pixels;
    }
}
