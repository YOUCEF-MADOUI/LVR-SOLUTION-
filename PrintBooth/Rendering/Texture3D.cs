using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Livereon.Modules.PrintBooth.Rendering;

/// <summary>
/// Texture ARGB avec échantillonnage bilinéaire, répétition et chaîne de mipmaps.
/// Le filtrage se fait en sRGB (perceptuel) puis une seule conversion linéaire
/// est appliquée au résultat : bien plus rapide que de convertir chaque texel.
/// </summary>
public sealed class Texture3D
{
    private readonly List<Texture3D> _levels = [];
    private bool _mipsBuilt;

    public Texture3D(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new int[Width * Height];
        Array.Fill(Pixels, unchecked((int)0xFF000000));
        _levels.Add(this);
    }

    public int Width { get; }

    public int Height { get; }

    public int[] Pixels { get; }

    /// <summary>Répète la texture hors des bornes 0..1 (bois, papier peint).</summary>
    public bool Wrap { get; set; }

    public int LevelCount => _levels.Count;

    public static Texture3D FromBitmap(Bitmap bitmap, bool wrap = false, bool mipmapped = false)
    {
        Texture3D texture = new(bitmap.Width, bitmap.Height) { Wrap = wrap };
        texture.Update(bitmap);
        if (mipmapped) texture.BuildMips();
        return texture;
    }

    /// <summary>Recopie le contenu d'un bitmap 32 bits dans la texture.</summary>
    public void Update(Bitmap bitmap)
    {
        Rectangle area = new(0, 0, Math.Min(Width, bitmap.Width), Math.Min(Height, bitmap.Height));
        BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < area.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), Pixels, y * Width, area.Width);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (_mipsBuilt) BuildMips();
    }

    public void Clear(int argb) => Array.Fill(Pixels, argb);

    public void SetPixel(int x, int y, int argb)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        Pixels[(y * Width) + x] = argb;
    }

    /// <summary>Construit la chaîne de niveaux (moyenne 2x2).</summary>
    public void BuildMips()
    {
        _levels.Clear();
        _levels.Add(this);
        Texture3D current = this;
        while (current.Width > 2 && current.Height > 2)
        {
            int width = Math.Max(1, current.Width / 2);
            int height = Math.Max(1, current.Height / 2);
            Texture3D next = new(width, height) { Wrap = Wrap };
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int oy = y * 2;
                    int ox = x * 2;
                    int c0 = current.Pixels[(oy * current.Width) + ox];
                    int c1 = current.Pixels[(oy * current.Width) + Math.Min(current.Width - 1, ox + 1)];
                    int c2 = current.Pixels[(Math.Min(current.Height - 1, oy + 1) * current.Width) + ox];
                    int c3 = current.Pixels[(Math.Min(current.Height - 1, oy + 1) * current.Width) + Math.Min(current.Width - 1, ox + 1)];
                    next.Pixels[(y * width) + x] = Average4(c0, c1, c2, c3);
                }
            }
            _levels.Add(next);
            current = next;
        }
        _mipsBuilt = true;
    }

    private static int Average4(int c0, int c1, int c2, int c3)
    {
        int a = (((c0 >> 24) & 255) + ((c1 >> 24) & 255) + ((c2 >> 24) & 255) + ((c3 >> 24) & 255)) / 4;
        int r = (((c0 >> 16) & 255) + ((c1 >> 16) & 255) + ((c2 >> 16) & 255) + ((c3 >> 16) & 255)) / 4;
        int g = (((c0 >> 8) & 255) + ((c1 >> 8) & 255) + ((c2 >> 8) & 255) + ((c3 >> 8) & 255)) / 4;
        int b = ((c0 & 255) + (c1 & 255) + (c2 & 255) + (c3 & 255)) / 4;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>Échantillonne la texture ; la couleur renvoyée est linéaire.</summary>
    public void Sample(float u, float v, float lod, out Vec3 color, out float alpha)
    {
        Texture3D source = this;
        if (_levels.Count > 1)
        {
            int level = Math.Clamp((int)(lod + .5f), 0, _levels.Count - 1);
            source = _levels[level];
        }
        source.SampleRaw(u, v, out color, out alpha);
    }

    private void SampleRaw(float u, float v, out Vec3 color, out float alpha)
    {
        float x;
        float y;
        if (Wrap)
        {
            u -= MathF.Floor(u);
            v -= MathF.Floor(v);
            x = (u * Width) - .5f;
            y = (v * Height) - .5f;
        }
        else
        {
            u = Math.Clamp(u, 0f, 1f);
            v = Math.Clamp(v, 0f, 1f);
            x = u * (Width - 1);
            y = v * (Height - 1);
        }

        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;
        if (Wrap)
        {
            x0 = ((x0 % Width) + Width) % Width;
            y0 = ((y0 % Height) + Height) % Height;
            x1 = ((x1 % Width) + Width) % Width;
            y1 = ((y1 % Height) + Height) % Height;
        }
        else
        {
            x0 = Math.Clamp(x0, 0, Width - 1);
            y0 = Math.Clamp(y0, 0, Height - 1);
            x1 = Math.Clamp(x1, 0, Width - 1);
            y1 = Math.Clamp(y1, 0, Height - 1);
        }

        int c00 = Pixels[(y0 * Width) + x0];
        int c10 = Pixels[(y0 * Width) + x1];
        int c01 = Pixels[(y1 * Width) + x0];
        int c11 = Pixels[(y1 * Width) + x1];

        float w00 = (1f - fx) * (1f - fy);
        float w10 = fx * (1f - fy);
        float w01 = (1f - fx) * fy;
        float w11 = fx * fy;

        float red = (((c00 >> 16) & 255) * w00) + (((c10 >> 16) & 255) * w10) + (((c01 >> 16) & 255) * w01) + (((c11 >> 16) & 255) * w11);
        float green = (((c00 >> 8) & 255) * w00) + (((c10 >> 8) & 255) * w10) + (((c01 >> 8) & 255) * w01) + (((c11 >> 8) & 255) * w11);
        float blue = ((c00 & 255) * w00) + ((c10 & 255) * w10) + ((c01 & 255) * w01) + ((c11 & 255) * w11);

        color = new Vec3(
            ColorUtil.SrgbToLinear((int)red),
            ColorUtil.SrgbToLinear((int)green),
            ColorUtil.SrgbToLinear((int)blue));

        alpha = ((((c00 >> 24) & 255) * w00) + (((c10 >> 24) & 255) * w10) + (((c01 >> 24) & 255) * w01) + (((c11 >> 24) & 255) * w11)) / 255f;
    }
}
