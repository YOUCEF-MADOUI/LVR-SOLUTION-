using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Livereon.Modules.PrintBooth.Rendering;

namespace Livereon.Modules.PrintBooth;

/// <summary>
/// Scène 3D du Print Booth : bureau en bois, montants chromés, barre
/// d'impression, chariot mobile, feuille de papier et rouleau.
/// Tout est rasterisé en perspective avec ombres portées et éclairage par pixel.
/// </summary>
public sealed class PrintBoothScene : IDisposable
{
    public const int PaperTextureWidth = 640;
    public const int PaperTextureHeight = 1192;

    /// <summary>Nombre de pixels éclairés visé par image (qualité / fluidité).</summary>
    public static float PixelBudget = 340000f;

    // Repères du monde (1 unité ≈ 30 cm).
    private const float SheetWidth = .66f;
    private const float SheetTop = .60f;
    private const float SheetHeight = 1.32f;
    private const float SheetBottom = SheetTop - SheetHeight;
    private const float BeamY = .638f;
    private const float BeamZ = .055f;
    private const float RailX = .375f;
    private const float HeadSpan = .252f;
    private const float DeskY = -.78f;

    private readonly Scene3D _scene = new();
    private readonly Renderer3D _shadowRenderer = new();
    private readonly Renderer3D[] _bandRenderers = [new(), new(), new(), new()];
    private Bitmap? _frame;
    private readonly Camera3D _camera = new();

    private readonly Texture3D _paper;
    private readonly Texture3D _label;
    private readonly Texture3D _glowTexture;
    private readonly Texture3D _wood;
    private readonly Bitmap _paperBitmap;
    private readonly Bitmap _labelBitmap;

    private readonly SceneItem _carriage;
    private readonly SceneItem _nozzle;
    private readonly SceneItem _tip;
    private readonly SceneItem _glow;
    private readonly SceneItem _roll;
    private readonly SceneItem _labelItem;
    private readonly SceneItem[] _wheels = new SceneItem[2];
    private readonly Light3D _printLight;

    private string _woodTone = "Miel";
    private string _labelKey = string.Empty;
    private float _headFraction = 1f;
    private bool _printing;
    private bool _disposed;

    public PrintBoothScene()
    {
        _paperBitmap = new Bitmap(PaperTextureWidth, PaperTextureHeight, PixelFormat.Format32bppArgb);
        _labelBitmap = new Bitmap(1024, 72, PixelFormat.Format32bppArgb);
        _paper = new Texture3D(PaperTextureWidth, PaperTextureHeight);
        _label = new Texture3D(1024, 72);
        _glowTexture = CreateGlow();
        _wood = CreateWood("Miel");

        SetWoodTone("Miel");
        BuildLights();

        // ------------------------------------------------------------- bureau
        Mesh3D desk = ScaleUv(Primitives.Plane(3.2f, 2.8f, 2), 3.2f, 3.2f);
        SceneItem deskItem = new(desk, new Material3D { Roughness = .62f, Specular = .55f, CastShadow = false })
        {
            Texture = _wood
        };
        deskItem.SetTransform(Mat4.Translation(new Vec3(0f, DeskY, -.55f)) * Mat4.RotationX(-MathF.PI * .5f));

        // Fond de studio : évite de voir l'horizon du bureau.
        SceneItem backdrop = new(Primitives.Plane(4.4f, 2.4f, 1), new Material3D { Roughness = .92f, Specular = .1f, CastShadow = false })
        {
            Texture = CreateBackdrop()
        };
        backdrop.SetTransform(Mat4.Translation(new Vec3(0f, .12f, -1.55f)));

        // -------------------------------------------------------------- papier
        Mesh3D sheet = Primitives.Grid(14, 24, SheetPoint, true);
        SceneItem paperItem = new(sheet, new Material3D
        {
            Roughness = .78f,
            Specular = .45f,
            CastShadow = false,
            Tint = new Vec3(.98f, .97f, .94f)
        })
        {
            Texture = _paper
        };
        _scene.Items.Add(paperItem);

        // -------------------------------------------------------------- rouleau
        Mesh3D rollMesh = Primitives.Cylinder(.052f, .052f, .80f, 24, false);
        _roll = new SceneItem(rollMesh, new Material3D { Roughness = .58f, Specular = .5f })
        {
            Texture = CreateRoll()
        };
        _scene.Items.Add(_roll);
        UpdateRoll(0f);

        // ---------------------------------------------------- montants chromés
        Material3D chrome = Material3D.Metallic(new Vec3(.92f, .93f, .95f), .13f);
        Material3D brushed = Material3D.Metallic(new Vec3(.72f, .74f, .78f), .28f);
        Mesh3D railMesh = Primitives.Cylinder(.0125f, .0125f, 1.352f, 18);

        foreach (float side in new[] { -1f, 1f })
        {
            SceneItem rail = new(railMesh, Material3D.Metallic(new Vec3(.95f, .96f, .98f), .10f));
            rail.SetTransform(Mat4.Translation(new Vec3(side * RailX, -.052f, .05f)));
            _scene.Items.Add(rail);

            // Embouts, haut et bas.
            Mesh3D capMesh = Primitives.RoundedBox(.048f, .05f, .048f, .018f, 10, 6);
            foreach (float y in new[] { .622f, -.726f })
            {
                SceneItem cap = new(capMesh, chrome);
                cap.SetTransform(Mat4.Translation(new Vec3(side * RailX, y, .05f)));
                _scene.Items.Add(cap);
            }

            // Pinces qui relient les montants à la barre et au bureau.
            Mesh3D clampMesh = Primitives.RoundedBox(.078f, .092f, .072f, .012f, 12, 6);
            SceneItem topClamp = new(clampMesh, brushed);
            topClamp.SetTransform(Mat4.Translation(new Vec3(side * RailX, .582f, .055f)));
            _scene.Items.Add(topClamp);

            SceneItem bottomClamp = new(clampMesh, brushed);
            bottomClamp.SetTransform(Mat4.Translation(new Vec3(side * RailX, -.702f, .055f)));
            _scene.Items.Add(bottomClamp);

            // Deux vis sur chaque pince avant.
            Mesh3D screwMesh = Primitives.Cylinder(.0072f, .0072f, .006f, 8);
            foreach (float offset in new[] { -.019f, .019f })
            {
                SceneItem screw = new(screwMesh, brushed);
                screw.SetTransform(Mat4.Translation(new Vec3((side * RailX) + offset, .566f, .092f)) * Mat4.RotationX(MathF.PI * .5f));
                _scene.Items.Add(screw);
            }
        }

        // --------------------------------------------------------- barre avant
        Mesh3D beamMesh = Primitives.RoundedBox(.92f, .076f, .092f, .02f, 20, 10);
        SceneItem beam = new(beamMesh, new Material3D
        {
            Tint = new Vec3(.028f, .029f, .032f),
            Roughness = .26f,
            Specular = 1.35f
        });
        beam.SetTransform(Mat4.Translation(new Vec3(0f, BeamY, BeamZ)));
        _scene.Items.Add(beam);

        // Tranches métalliques de la barre.
        Mesh3D endMesh = Primitives.Cylinder(.034f, .034f, .016f, 14);
        foreach (float side in new[] { -1f, 1f })
        {
            SceneItem end = new(endMesh, chrome);
            end.SetTransform(Mat4.Translation(new Vec3(side * .462f, BeamY, BeamZ)) * Mat4.RotationZ(MathF.PI * .5f));
            _scene.Items.Add(end);
        }

        // Grilles d'aération (petites fentes encastrées).
        Mesh3D ventMesh = Primitives.Box(.0055f, .022f, .002f);
        Material3D ventMaterial = new() { Tint = new Vec3(.006f, .006f, .007f), Roughness = .5f, CastShadow = false };
        for (int i = 0; i < 4; i++)
        {
            SceneItem vent = new(ventMesh, ventMaterial);
            vent.SetTransform(Mat4.Translation(new Vec3(.255f + (i * .0125f), BeamY + .004f, BeamZ + .0465f)));
            _scene.Items.Add(vent);
        }

        // -------------------------------------------------------------- chariot
        Mesh3D carriageMesh = Primitives.RoundedBox(.088f, .104f, .086f, .016f, 16, 8);
        _carriage = new SceneItem(carriageMesh, new Material3D
        {
            Tint = new Vec3(.045f, .047f, .052f),
            Roughness = .3f,
            Specular = 1.25f
        });
        _scene.Items.Add(_carriage);

        // Galets sur le rail de la barre.
        Mesh3D wheelMesh = Primitives.Cylinder(.016f, .016f, .012f, 12);
        Material3D wheelMaterial = Material3D.Metallic(new Vec3(.85f, .86f, .89f), .2f);
        for (int i = 0; i < _wheels.Length; i++)
        {
            _wheels[i] = new SceneItem(wheelMesh, wheelMaterial);
            _scene.Items.Add(_wheels[i]);
        }

        // Buse et pointe lumineuse.
        Mesh3D nozzleMesh = Primitives.Cylinder(.0095f, .017f, .034f, 12);
        _nozzle = new SceneItem(nozzleMesh, Material3D.Metallic(new Vec3(.8f, .82f, .86f), .22f));
        _scene.Items.Add(_nozzle);

        _tip = new SceneItem(Primitives.RoundedBox(.012f, .012f, .012f, .006f, 10, 6), new Material3D
        {
            Tint = new Vec3(1f, .86f, .62f),
            Emissive = 3.2f,
            Roughness = .4f,
            CastShadow = false
        });
        _scene.Items.Add(_tip);

        // Halos du faisceau d'impression.
        _glow = new SceneItem(Primitives.Plane(.085f, .16f, 2), new Material3D
        {
            Tint = new Vec3(.55f, .42f, .24f),
            Emissive = 2.2f,
            Additive = true,
            AlphaBlend = false,
            TwoSided = true,
            CastShadow = false,
            Specular = 0f,
            Roughness = 1f
        })
        {
            Texture = _glowTexture
        };
        _scene.Items.Add(_glow);

        // ---------------------------------------------------------- étiquette
        _labelItem = new SceneItem(Primitives.Plane(.88f, .062f, 2), new Material3D
        {
            AlphaBlend = true,
            Roughness = .35f,
            Specular = .6f,
            CastShadow = false,
            TwoSided = true
        })
        {
            Texture = _label
        };
        _labelItem.SetTransform(Mat4.Translation(new Vec3(0f, BeamY, BeamZ + .0466f)));
        _scene.Items.Add(_labelItem);

        _printLight = new Light3D
        {
            Point = true,
            Color = new Vec3(1f, .82f, .55f),
            Intensity = 0f,
            Range = .24f,
            Position = new Vec3(0f, .52f, .07f)
        };
        _scene.Lights.Add(_printLight);

        // Bureau et fond ajoutés en dernier : le z-buffer élimine alors tout
        // ce qu'ils cachent, au lieu de les éclairer pixel par pixel.
        _scene.Items.Add(deskItem);
        _scene.Items.Add(backdrop);

        SetHead(1f, false);
    }

    /// <summary>Vue du dessus de la feuille : l'image se compose directement ici.</summary>
    public Bitmap PaperBitmap => _paperBitmap;

    public void CommitPaper() => _paper.Update(_paperBitmap);

    // --------------------------------------------------------------- lumières

    private void BuildLights()
    {
        _scene.Lights.Add(new Light3D
        {
            Direction = new Vec3(-.42f, .84f, .36f),
            Color = new Vec3(1f, .955f, .9f),
            Intensity = 1.42f,
            CastShadow = true
        });

        _scene.Lights.Add(new Light3D
        {
            Direction = new Vec3(.72f, .32f, .48f),
            Color = new Vec3(.55f, .66f, .88f),
            Intensity = .55f
        });

        _scene.AmbientSky = new Vec3(.26f, .30f, .40f);
        _scene.AmbientGround = new Vec3(.11f, .078f, .05f);
        _scene.Exposure = .92f;
    }

    // ------------------------------------------------------------ animation

    public void SetHead(float fraction, bool printing)
    {
        _headFraction = Math.Clamp(fraction, -.08f, 1.08f);
        _printing = printing;
        UpdateHead();
    }

    private void UpdateHead()
    {
        float x = ((_headFraction * 2f) - 1f) * HeadSpan;
        float dip = _printing ? -.0035f : 0f;

        _carriage.SetTransform(Mat4.Translation(new Vec3(x, BeamY - .062f, BeamZ + .012f)));
        for (int i = 0; i < _wheels.Length; i++)
        {
            float offset = i == 0 ? -.031f : .031f;
            _wheels[i].SetTransform(Mat4.Translation(new Vec3(x + offset, BeamY - .022f, BeamZ + .05f)) * Mat4.RotationZ(MathF.PI * .5f));
        }
        _nozzle.SetTransform(Mat4.Translation(new Vec3(x, BeamY - .124f + dip, BeamZ + .012f)));
        _tip.SetTransform(Mat4.Translation(new Vec3(x, BeamY - .1435f + dip, BeamZ + .012f)));
        _glow.SetTransform(Mat4.Translation(new Vec3(x, BeamY - .205f + dip, BeamZ + .004f)));
        _glow.Enabled = _printing;

        _printLight.Position = new Vec3(x, BeamY - .19f + dip, .055f);
        _printLight.Intensity = _printing ? .42f : 0f;
    }

    public void SetRollAngle(float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        UpdateRoll(radians);
    }

    private void UpdateRoll(float radians)
    {
        _roll.SetTransform(Mat4.Translation(new Vec3(0f, SheetBottom + .032f, .045f))
                           * Mat4.RotationZ(MathF.PI * .5f)
                           * Mat4.RotationY(radians));
    }

    /// <summary>Point de la feuille (u = largeur, v = 0 en haut) avec sa courbure.</summary>
    private static Vec3 SheetPoint(float u, float v)
    {
        float x = (u - .5f) * SheetWidth;
        float y = SheetTop - (v * SheetHeight);

        float bow = .0105f * MathF.Sin(MathF.PI * u) * (.35f + (.65f * MathF.Sin(MathF.PI * Math.Clamp(v, 0f, 1f))));
        bow -= .016f * MathF.Pow(1f - v, 5f);
        bow += .024f * MathF.Pow(Math.Clamp((v - .84f) / .16f, 0f, 1f), 2f);

        return new Vec3(x, y, bow);
    }

    // ------------------------------------------------------------- textures

    public void SetWoodTone(string tone)
    {
        if (tone == _woodTone) return;
        _woodTone = tone;

        Texture3D generated = CreateWood(tone);
        _wood.Pixels.AsSpan(0, Math.Min(_wood.Pixels.Length, generated.Pixels.Length)).Clear();
        Array.Copy(generated.Pixels, _wood.Pixels, Math.Min(generated.Pixels.Length, _wood.Pixels.Length));
        _wood.BuildMips();
    }

    private Texture3D CreateWood(string tone)
    {
        const int size = 512;
        Texture3D texture = new(size, size) { Wrap = true };
        (float R, float G, float B) baseColor = tone switch
        {
            "Clair" => (205f, 162f, 116f),
            "Foncé" => (96f, 62f, 38f),
            _ => (172f, 120f, 76f)
        };

        for (int y = 0; y < size; y++)
        {
            float v = y / (float)size;
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;

                float warp = Fbm(u * 3.4f, v * 1.2f, 4) - .5f;
                float rings = MathF.Sin((u * 34f) + (warp * 14f));
                float grain = MathF.Sin((u * 210f) + (warp * 30f) + (Fbm(u * 16f, v * 3.5f, 3) * 6f));
                float fine = (Fbm(u * 120f, v * 40f, 2) - .5f) * .35f;

                float shade = 1f + (rings * .16f) + (grain * .085f) + fine;

                // Lames : joints sombres tous les 1/4 de texture.
                float plank = (u * 4f) % 1f;
                if (plank < .012f) shade *= .42f;
                else if (plank < .03f) shade *= .74f;

                // Deux nœuds discrets.
                shade *= 1f - (.35f * Knot(u, v, .22f, .38f));
                shade *= 1f - (.28f * Knot(u, v, .74f, .72f));

                // Usure du plateau : plus sombre sur les bords.
                shade *= .82f + (.24f * MathF.Sin(v * MathF.PI));

                texture.Pixels[(y * size) + x] = Pack(baseColor.R * shade, baseColor.G * shade, baseColor.B * shade);
            }
        }

        texture.BuildMips();
        return texture;
    }

    private static float Knot(float u, float v, float centerU, float centerV)
    {
        float dx = (u - centerU) * 3.2f;
        float dy = (v - centerV) * 1.6f;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy));
        if (distance > .32f) return 0f;
        float rings = .5f + (.5f * MathF.Cos(distance * 46f));
        return (1f - (distance / .32f)) * rings;
    }

    private Texture3D CreateBackdrop()
    {
        const int width = 320;
        const int height = 320;
        Texture3D texture = new(width, height);
        for (int y = 0; y < height; y++)
        {
            float v = y / (float)height;
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width;
                float glow = MathF.Max(0f, 1f - (MathF.Sqrt(((u - .5f) * (u - .5f) * 2.4f) + ((v - .42f) * (v - .42f))) * 2.1f));
                float value = .12f + (.26f * glow) + (.07f * (1f - v));
                texture.Pixels[(y * width) + x] = Pack(168f * value, 138f * value, 116f * value);
            }
        }
        return texture;
    }

    private Texture3D CreateRoll()
    {
        const int width = 256;
        const int height = 64;
        Texture3D texture = new(width, height) { Wrap = true };
        for (int y = 0; y < height; y++)
        {
            float v = y / (float)height;
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width;
                float noise = (Fbm(u * 26f, v * 8f, 2) - .5f) * .12f;
                float bands = .5f + (.5f * MathF.Sin((u * MathF.PI * 2f * 3f) + (noise * 8f)));
                float shade = .9f + (.06f * bands) + noise;

                // Repères d'enroulement : ils tournent avec le rouleau.
                if (u > .10f && u < .118f) shade *= .70f;
                if (u > .56f && u < .566f) shade *= .80f;

                shade *= .72f + (.4f * MathF.Sin(v * MathF.PI));
                texture.Pixels[(y * width) + x] = Pack(250f * shade, 244f * shade, 226f * shade);
            }
        }
        texture.BuildMips();
        return texture;
    }

    private static Texture3D CreateGlow()
    {
        const int width = 64;
        const int height = 128;
        Texture3D texture = new(width, height);
        for (int y = 0; y < height; y++)
        {
            float v = y / (float)(height - 1);
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)(width - 1);
                float horizontal = 1f - MathF.Abs((u * 2f) - 1f);
                float vertical = MathF.Pow(1f - v, .7f);
                float alpha = Math.Clamp(horizontal * horizontal * vertical, 0f, 1f);
                texture.Pixels[(y * width) + x] = unchecked((int)((uint)(alpha * 255f) << 24)) | 0xFFFFFF;
            }
        }
        return texture;
    }

    private static int Pack(float r, float g, float b) => unchecked((int)0xFF000000)
                                                          | (ClampByte(r) << 16)
                                                          | (ClampByte(g) << 8)
                                                          | ClampByte(b);

    private static int ClampByte(float value) => Math.Clamp((int)MathF.Round(value), 0, 255);

    private static Mesh3D ScaleUv(Mesh3D mesh, float u, float v)
    {
        for (int i = 0; i < mesh.Uvs.Length; i += 2)
        {
            mesh.Uvs[i] *= u;
            mesh.Uvs[i + 1] *= v;
        }
        return mesh;
    }

    // ------------------------------------------------------------ bruitage

    private static float Hash(int x, int y)
    {
        unchecked
        {
            uint hash = (uint)((x * 374761393) + (y * 668265263));
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash >> 8) & 0xFFFFFF) / 16777215f;
        }
    }

    private static float ValueNoise(float x, float y)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        fx = fx * fx * (3f - (2f * fx));
        fy = fy * fy * (3f - (2f * fy));

        float c00 = Hash(x0, y0);
        float c10 = Hash(x0 + 1, y0);
        float c01 = Hash(x0, y0 + 1);
        float c11 = Hash(x0 + 1, y0 + 1);

        float top = c00 + ((c10 - c00) * fx);
        float bottom = c01 + ((c11 - c01) * fx);
        return top + ((bottom - top) * fy);
    }

    private static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f;
        float amplitude = .5f;
        float total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += ValueNoise(x, y) * amplitude;
            total += amplitude;
            x *= 2.03f;
            y *= 2.03f;
            amplitude *= .5f;
        }
        return total > 0f ? sum / total : 0f;
    }

    // ------------------------------------------------------------- étiquette

    public void SetHeader(string title, string subtitle, bool showHeader, bool showLive, string badge, bool live)
    {
        string key = $"{title}|{subtitle}|{showHeader}|{showLive}|{badge}|{live}";
        if (key == _labelKey) return;
        _labelKey = key;

        using Graphics graphics = Graphics.FromImage(_labelBitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        if (showHeader)
        {
            using Font titleFont = new("Segoe UI", 32f, FontStyle.Bold, GraphicsUnit.Pixel);
            using Font subtitleFont = new("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            using SolidBrush ink = new(Color.FromArgb(248, 249, 246, 240));
            using SolidBrush subtle = new(Color.FromArgb(185, 226, 220, 228));
            using StringFormat format = new(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Near };

            string caption = title.ToUpperInvariant();
            SizeF size = graphics.MeasureString(caption, titleFont, int.MaxValue, StringFormat.GenericTypographic);
            graphics.DrawString(caption, titleFont, ink, new PointF(26f, (_labelBitmap.Height - titleFont.Height) * .5f), format);

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                graphics.DrawString(subtitle.ToUpperInvariant(), subtitleFont, subtle,
                    new PointF(26f + size.Width + 16f, (_labelBitmap.Height - subtitleFont.Height) * .5f), format);
            }
        }

        if (showLive)
        {
            using Font badgeFont = new("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            float width = 108f;
            float height = 34f;
            RectangleF pill = new(_labelBitmap.Width - 26f - width, (_labelBitmap.Height - height) * .5f, width, height);

            using GraphicsPath path = new();
            float radius = height * .5f;
            path.AddArc(pill.X, pill.Y, radius * 2f, height, 180, 90);
            path.AddArc(pill.Right - (radius * 2f), pill.Y, radius * 2f, height, 270, 90);
            path.AddArc(pill.Right - (radius * 2f), pill.Bottom - height, radius * 2f, height, 0, 90);
            path.AddArc(pill.X, pill.Bottom - height, radius * 2f, height, 90, 90);
            path.CloseFigure();

            using SolidBrush fill = new(live ? Color.FromArgb(236, 58, 96) : Color.FromArgb(74, 78, 88));
            graphics.FillPath(fill, path);

            using SolidBrush text = new(Color.White);
            using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            graphics.DrawString(badge, badgeFont, text, pill, centered);
        }

        _label.Update(_labelBitmap);
    }

    // ----------------------------------------------------------------- rendu

    public Bitmap Render(int width, int height, float time)
    {
        UpdateCamera(time);

        float pixels = width * (float)height;
        float scale = Math.Clamp(MathF.Sqrt(PixelBudget / MathF.Max(1f, pixels)), .45f, 2f);
        int internalHeight = Math.Max(16, (int)MathF.Round(height * scale));

        int bands = Math.Clamp(Environment.ProcessorCount - 1, 1, _bandRenderers.Length);
        int slice = (internalHeight + bands - 1) / bands;

        // Une seule shadow map, partagée par toutes les bandes.
        _shadowRenderer.BuildShadowMap(_scene);

        if (_frame is null || _frame.Width != width || _frame.Height != height)
        {
            _frame?.Dispose();
            _frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        Parallel.For(0, bands, index =>
        {
            int top = index * slice;
            int rows = Math.Min(slice, internalHeight - top);
            if (rows <= 0) return;

            Renderer3D renderer = _bandRenderers[index];
            renderer.BandTop = top;
            renderer.BandRows = rows;
            renderer.ReuseShadowMap = true;
            renderer.Render(_scene, _camera, width, height, scale);
        });

        BitmapData data = _frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int index = 0; index < bands; index++)
            {
                Renderer3D renderer = _bandRenderers[index];
                int[] source = renderer.BandOutput;
                int sourceRows = renderer.BandOutputRows;
                for (int row = 0; row < sourceRows; row++)
                {
                    int target = renderer.BandOutputTop + row;
                    if (target >= height) break;
                    Marshal.Copy(source, row * width, IntPtr.Add(data.Scan0, target * data.Stride), width);
                }
            }
        }
        finally
        {
            _frame.UnlockBits(data);
        }

        return _frame;
    }

    private void UpdateCamera(float time)
    {
        float driftX = MathF.Sin(time * .23f) * .009f;
        float driftY = MathF.Sin(time * .31f) * .005f;
        float driftZ = MathF.Sin(time * .17f) * .011f;
        float roll = MathF.Sin(time * .11f) * .0035f;

        _camera.Position = new Vec3(.062f + driftX, .058f + driftY, 1.855f + driftZ);
        _camera.Target = new Vec3(.004f, .012f, 0f);
        _camera.Up = new Vec3(roll, 1f, 0f);
        _camera.FovDegrees = 46.5f;
        _camera.Near = .04f;
        _camera.Far = 30f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _paperBitmap.Dispose();
        _labelBitmap.Dispose();
        _frame?.Dispose();
        _shadowRenderer.Dispose();
        foreach (Renderer3D renderer in _bandRenderers) renderer.Dispose();
    }
}
