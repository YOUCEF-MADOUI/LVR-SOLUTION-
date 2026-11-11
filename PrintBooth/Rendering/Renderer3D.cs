using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Livereon.Modules.PrintBooth.Rendering;

/// <summary>Caméra perspective.</summary>
public sealed class Camera3D
{
    public Vec3 Position = new(0f, .08f, 1.9f);
    public Vec3 Target = Vec3.Zero;
    public Vec3 Up = Vec3.Up;
    public float FovDegrees = 46f;
    public float Near = .04f;
    public float Far = 40f;
    public float Aspect = 9f / 16f;

    public Mat4 View => Mat4.LookAt(Position, Target, Up);

    public Mat4 Projection => Mat4.Perspective(FovDegrees * MathF.PI / 180f, Aspect, Near, Far);
}

/// <summary>Paramètres de surface d'un objet de la scène.</summary>
public sealed class Material3D
{
    public Vec3 Tint = Vec3.One;

    /// <summary>0 = diélectrique (papier, plastique), 1 = métal.</summary>
    public float Metal;

    /// <summary>0 = miroir, 1 = totalement mat.</summary>
    public float Roughness = .6f;

    public float Specular = 1f;

    public float Emissive;

    /// <summary>Mélange additif (halos, faisceaux).</summary>
    public bool Additive;

    /// <summary>Mélange alpha (autocollants, étiquettes).</summary>
    public bool AlphaBlend;

    public bool TwoSided;

    public bool CastShadow = true;

    private float[]? _specularLut;

    /// <summary>
    /// Table de x^brillance pour 256 valeurs : évite un MathF.Pow par pixel et
    /// par lumière, qui coûtait à lui seul l'essentiel du temps de rendu.
    /// </summary>
    internal float[] SpecularLut
    {
        get
        {
            if (_specularLut is not null) return _specularLut;
            float roughness = Math.Clamp(Roughness, .04f, 1f);
            float shininess = Math.Clamp((2f / (roughness * roughness * roughness * roughness)) - 2f, 2f, 4000f);
            float normalization = (shininess + 8f) / 25.1f;
            float[] lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = MathF.Pow(i / 255f, shininess) * normalization;
            return _specularLut = lut;
        }
    }

    public static Material3D Plastic(Vec3 tint, float roughness = .35f) => new()
    {
        Tint = tint,
        Roughness = roughness,
        Specular = 1f
    };

    public static Material3D Metallic(Vec3 tint, float roughness = .22f) => new()
    {
        Tint = tint,
        Metal = 1f,
        Roughness = roughness,
        Specular = 1f
    };
}

public sealed class Light3D
{
    /// <summary>Direction vers la source (normalisée à l'usage).</summary>
    public Vec3 Direction = new(-.42f, .82f, .39f);

    public Vec3 Color = new(1f, .95f, .88f);

    public float Intensity = 1.6f;

    public bool CastShadow;

    public bool Point;

    public Vec3 Position;

    public float Range = 1f;
}

/// <summary>Un maillage placé dans la scène, avec son matériau et sa texture.</summary>
public sealed class SceneItem
{
    public SceneItem(Mesh3D mesh, Material3D material, Texture3D? texture = null)
    {
        Mesh = mesh;
        Material = material;
        Texture = texture;
        Transform = Mat4.Identity();
        NormalMatrix = Transform.NormalMatrix();
    }

    public Mesh3D Mesh { get; }

    public Material3D Material { get; }

    public Texture3D? Texture { get; set; }

    public Mat4 Transform { get; private set; }

    public Mat3 NormalMatrix { get; private set; }

    public bool Enabled { get; set; } = true;

    public void SetTransform(Mat4 transform)
    {
        Transform = transform;
        NormalMatrix = transform.NormalMatrix();
    }
}

public sealed class Scene3D
{
    public List<SceneItem> Items { get; } = [];

    public List<Light3D> Lights { get; } = [];

    /// <summary>Lumière ambiante du ciel (haut) et du sol (bas).</summary>
    public Vec3 AmbientSky = new(.30f, .34f, .44f);

    public Vec3 AmbientGround = new(.10f, .072f, .048f);

    public float Ambient = .62f;

    public Vec3 ClearColor = new(.015f, .011f, .009f);

    public float Exposure = 1.05f;

    public float Vignette = .34f;

    public Vec3 EnvTop = new(.60f, .67f, .80f);

    public Vec3 EnvHorizon = new(.34f, .28f, .22f);

    public Vec3 EnvBottom = new(.05f, .036f, .028f);

    public Vec3 SampleEnvironment(Vec3 direction)
    {
        float y = Math.Clamp(direction.Y, -1f, 1f);
        return y >= 0f
            ? Vec3.Lerp(EnvHorizon, EnvTop, MathF.Sqrt(y))
            : Vec3.Lerp(EnvHorizon, EnvBottom, MathF.Sqrt(-y));
    }
}

/// <summary>
/// Rasteriseur logiciel : transformation perspective, clipping du plan proche,
/// z-buffer, ombres portées par shadow map et éclairage par pixel.
/// Aucune dépendance GPU : le rendu est identique sur toutes les machines.
/// </summary>
public sealed class Renderer3D
{
    private const int ShadowSize = 512;

    // La shadow map est partagée par toutes les bandes de rendu : elle est
    // construite une fois par image, puis lue par chaque cœur.
    private static readonly float[] ShadowDepth = new float[ShadowSize * ShadowSize];
    private static Mat4 _shadowViewProjection = Mat4.Identity();
    private static Vec3 _shadowLightDirection = Vec3.Up;
    private static bool _hasShadowLight;

    private readonly List<SceneItem> _ordered = [];
    private readonly ClipVertex[] _clipIn = new ClipVertex[4];
    private readonly ClipVertex[] _clipOut = new ClipVertex[4];
    private readonly ScreenVert[] _screen = new ScreenVert[4];

    private int[] _color = [];
    private float[] _depth = [];
    private int[] _output = [];
    private Vec3[] _worldPosition = [];
    private Vec3[] _worldNormal = [];
    private Vec3[] _viewPosition = [];
    private float[] _uvU = [];
    private float[] _uvV = [];
    private Bitmap? _bitmap;

    private int _width;
    private int _height;
    private int _pixelTop;
    private int _pixelRows;
    private Camera3D _camera = new();
    private Scene3D _scene = new();
    private Mat4 _projection = Mat4.Identity();
    private float _lod;
    private int _shadedPixels;

    public static string Diagnostics = string.Empty;

    /// <summary>Première ligne rendue, en pixels internes.</summary>
    public int BandTop { get; set; }

    /// <summary>Hauteur de la bande rendue (0 = image entière).</summary>
    public int BandRows { get; set; }

    /// <summary>Réutilise la shadow map déjà construite (rendu en bandes).</summary>
    public bool ReuseShadowMap { get; set; }

    public int[] BandOutput => _output;

    public int BandOutputTop { get; private set; }

    public int BandOutputRows { get; private set; }

    /// <summary>Construit la shadow map une fois pour toutes les bandes.</summary>
    public void BuildShadowMap(Scene3D scene)
    {
        _scene = scene;
        PrepareShadow(scene);
        if (_hasShadowLight) RenderShadowMap();
    }

    public Bitmap Render(Scene3D scene, Camera3D camera, int outputWidth, int outputHeight, float renderScale)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        _scene = scene;
        _camera = camera;

        outputWidth = Math.Max(16, outputWidth);
        outputHeight = Math.Max(16, outputHeight);
        renderScale = Math.Clamp(renderScale, .4f, 2f);

        int width = Math.Max(16, (int)MathF.Round(outputWidth * renderScale));
        int height = Math.Max(16, (int)MathF.Round(outputHeight * renderScale));

        int bandTop = Math.Clamp(BandTop, 0, height - 1);
        int bandBottom = BandRows > 0 ? Math.Min(height, bandTop + BandRows) : height;
        int rows = Math.Max(1, bandBottom - bandTop);

        if (width != _width || rows != _pixelRows)
        {
            _width = width;
            _pixelRows = rows;
            _color = new int[width * rows];
            _depth = new float[width * rows];
        }
        _height = height;
        _pixelTop = bandTop;

        int outTop = Math.Clamp((int)MathF.Round(bandTop / renderScale), 0, outputHeight - 1);
        int outBottom = Math.Clamp((int)MathF.Round(bandBottom / renderScale), outTop + 1, outputHeight);
        int outRows = outBottom - outTop;

        if (_bitmap is null || _bitmap.Width != outputWidth || _bitmap.Height != outRows)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(outputWidth, outRows, PixelFormat.Format32bppArgb);
        }
        BandOutputTop = outTop;
        BandOutputRows = outRows;

        camera.Aspect = width / (float)height;

        Array.Fill(_color, PackColor(scene.ClearColor));
        Array.Fill(_depth, float.MaxValue);

        if (!ReuseShadowMap)
        {
            PrepareShadow(scene);
            if (_hasShadowLight) RenderShadowMap();
        }

        // Transparents en dernier, dans l'ordre de déclaration.
        _ordered.Clear();
        foreach (SceneItem item in scene.Items)
        {
            if (!item.Enabled) continue;
            if (!item.Material.Additive && !item.Material.AlphaBlend) _ordered.Add(item);
        }
        foreach (SceneItem item in scene.Items)
        {
            if (!item.Enabled) continue;
            if (item.Material.Additive || item.Material.AlphaBlend) _ordered.Add(item);
        }

        Mat4 view = camera.View;
        _projection = camera.Projection;
        long afterShadow = clock.ElapsedMilliseconds;
        foreach (SceneItem item in _ordered) DrawItem(item, view);
        long afterMain = clock.ElapsedMilliseconds;

        Resolve(outputWidth, outRows);
        Diagnostics = $"ombre {afterShadow} ms · scène {afterMain - afterShadow} ms ({_shadedPixels} px) · resolve {clock.ElapsedMilliseconds - afterMain} ms · {width}x{height}";
        _shadedPixels = 0;
        return _bitmap;
    }

    // ------------------------------------------------------------- ombres

    private void PrepareShadow(Scene3D scene)
    {
        _hasShadowLight = false;
        foreach (Light3D light in scene.Lights)
        {
            if (light.Point || !light.CastShadow) continue;

            _shadowLightDirection = light.Direction.Normalized;
            Vec3 center = new(0f, -.06f, 0f);
            Vec3 eye = center + (_shadowLightDirection * 3.2f);
            Vec3 up = MathF.Abs(_shadowLightDirection.Y) > .95f ? new Vec3(0f, 0f, 1f) : Vec3.Up;
            Mat4 view = Mat4.LookAt(eye, center, up);
            Mat4 projection = Mat4.Ortho(-1.15f, 1.15f, -1.15f, 1.15f, .4f, 6.5f);
            _shadowViewProjection = projection * view;
            _hasShadowLight = true;
            break;
        }
    }

    private void RenderShadowMap()
    {
        Array.Fill(ShadowDepth, 2f);
        foreach (SceneItem item in _scene.Items)
        {
            if (!item.Enabled || !item.Material.CastShadow) continue;
            if (item.Material.Additive || item.Material.AlphaBlend) continue;

            Mesh3D mesh = item.Mesh;
            int triangles = mesh.Indices.Length / 3;
            for (int t = 0; t < triangles; t++)
            {
                Vec3 a = _shadowViewProjection.TransformPoint(LocalVertex(mesh, item, mesh.Indices[t * 3]));
                Vec3 b = _shadowViewProjection.TransformPoint(LocalVertex(mesh, item, mesh.Indices[(t * 3) + 1]));
                Vec3 c = _shadowViewProjection.TransformPoint(LocalVertex(mesh, item, mesh.Indices[(t * 3) + 2]));

                float ax = ((a.X * .5f) + .5f) * ShadowSize;
                float ay = (1f - ((a.Y * .5f) + .5f)) * ShadowSize;
                float bx = ((b.X * .5f) + .5f) * ShadowSize;
                float by = (1f - ((b.Y * .5f) + .5f)) * ShadowSize;
                float cx = ((c.X * .5f) + .5f) * ShadowSize;
                float cy = (1f - ((c.Y * .5f) + .5f)) * ShadowSize;

                float area = ((bx - ax) * (cy - ay)) - ((cx - ax) * (by - ay));
                if (MathF.Abs(area) < 1e-5f) continue;

                float az = (a.Z * .5f) + .5f;
                float bz = (b.Z * .5f) + .5f;
                float cz = (c.Z * .5f) + .5f;

                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
                int maxX = Math.Min(ShadowSize - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
                int maxY = Math.Min(ShadowSize - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
                if (minX > maxX || minY > maxY) continue;

                float inverseArea = 1f / area;
                for (int y = minY; y <= maxY; y++)
                {
                    float py = y + .5f;
                    for (int x = minX; x <= maxX; x++)
                    {
                        float px = x + .5f;
                        float w0 = (((cx - bx) * (py - by)) - ((cy - by) * (px - bx))) * inverseArea;
                        float w1 = (((ax - cx) * (py - cy)) - ((ay - cy) * (px - cx))) * inverseArea;
                        float w2 = 1f - w0 - w1;
                        if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

                        float depth = (w0 * az) + (w1 * bz) + (w2 * cz);
                        int index = (y * ShadowSize) + x;
                        if (depth < ShadowDepth[index]) ShadowDepth[index] = depth;
                    }
                }
            }
        }
    }

    private static Vec3 LocalVertex(Mesh3D mesh, SceneItem item, int index)
    {
        int p = index * 3;
        return item.Transform.TransformPoint(new Vec3(mesh.Positions[p], mesh.Positions[p + 1], mesh.Positions[p + 2]));
    }

    /// <summary>Ombre portée douce (3x3) de la lumière principale.</summary>
    private float SampleShadow(Vec3 world, Vec3 normal)
    {
        if (!_hasShadowLight) return 1f;

        Vec3 offset = world + (normal * .012f);
        Vec3 projected = _shadowViewProjection.TransformPoint(offset);
        float u = (projected.X * .5f) + .5f;
        float v = 1f - ((projected.Y * .5f) + .5f);
        if (u < .005f || u > .995f || v < .005f || v > .995f) return 1f;

        float depth = ((projected.Z * .5f) + .5f) - (.0018f + (.0042f * (1f - Math.Clamp(Vec3.Dot(normal, _shadowLightDirection), 0f, 1f))));
        int centerX = (int)(u * ShadowSize);
        int centerY = (int)(v * ShadowSize);

        float lit = 0f;
        for (int oy = 0; oy < 2; oy++)
        {
            int y = Math.Clamp(centerY + oy, 0, ShadowSize - 1) * ShadowSize;
            for (int ox = 0; ox < 2; ox++)
            {
                float stored = ShadowDepth[y + Math.Clamp(centerX + ox, 0, ShadowSize - 1)];
                lit += depth <= stored ? 1f : 0f;
            }
        }
        return lit * .25f;
    }

    // ---------------------------------------------------------- géométrie

    private void EnsureScratch(int count)
    {
        if (_worldPosition.Length >= count) return;
        _worldPosition = new Vec3[count];
        _worldNormal = new Vec3[count];
        _viewPosition = new Vec3[count];
        _uvU = new float[count];
        _uvV = new float[count];
    }

    private void DrawItem(SceneItem item, Mat4 view)
    {
        Mesh3D mesh = item.Mesh;
        int count = mesh.VertexCount;
        EnsureScratch(count);

        for (int i = 0; i < count; i++)
        {
            int p = i * 3;
            Vec3 local = new(mesh.Positions[p], mesh.Positions[p + 1], mesh.Positions[p + 2]);
            Vec3 world = item.Transform.TransformPoint(local);
            _worldPosition[i] = world;
            _worldNormal[i] = item.NormalMatrix.Transform(new Vec3(mesh.Normals[p], mesh.Normals[p + 1], mesh.Normals[p + 2])).Normalized;
            _viewPosition[i] = view.TransformPoint(world);
            _uvU[i] = mesh.Uvs[i * 2];
            _uvV[i] = mesh.Uvs[(i * 2) + 1];
        }

        int triangles = mesh.Indices.Length / 3;
        float nearPlane = -_camera.Near;

        for (int t = 0; t < triangles; t++)
        {
            int i0 = mesh.Indices[t * 3];
            int i1 = mesh.Indices[(t * 3) + 1];
            int i2 = mesh.Indices[(t * 3) + 2];

            float d0 = nearPlane - _viewPosition[i0].Z;
            float d1 = nearPlane - _viewPosition[i1].Z;
            float d2 = nearPlane - _viewPosition[i2].Z;

            int vertexCount;
            if (d0 >= 0f && d1 >= 0f && d2 >= 0f)
            {
                _clipIn[0] = MakeClip(i0);
                _clipIn[1] = MakeClip(i1);
                _clipIn[2] = MakeClip(i2);
                _clipOut[0] = _clipIn[0];
                _clipOut[1] = _clipIn[1];
                _clipOut[2] = _clipIn[2];
                vertexCount = 3;
            }
            else
            {
                if (d0 < 0f && d1 < 0f && d2 < 0f) continue;

                _clipIn[0] = MakeClip(i0);
                _clipIn[1] = MakeClip(i1);
                _clipIn[2] = MakeClip(i2);
                vertexCount = 0;
                for (int k = 0; k < 3; k++)
                {
                    ClipVertex current = _clipIn[k];
                    ClipVertex next = _clipIn[(k + 1) % 3];
                    float dc = nearPlane - current.View.Z;
                    float dn = nearPlane - next.View.Z;

                    if (dc >= 0f)
                    {
                        _clipOut[vertexCount] = current;
                        vertexCount++;
                    }
                    if ((dc >= 0f) != (dn >= 0f) && vertexCount < 4)
                    {
                        float fraction = dc / (dc - dn);
                        _clipOut[vertexCount] = ClipVertex.Lerp(current, next, fraction);
                        vertexCount++;
                    }
                }
                if (vertexCount < 3) continue;
            }

            int valid = 0;
            bool failed = false;
            for (int k = 0; k < vertexCount; k++)
            {
                if (!Project(_clipOut[k], out ScreenVert screen))
                {
                    failed = true;
                    break;
                }
                _screen[valid] = screen;
                valid++;
            }
            if (failed || valid < 3) continue;

            _lod = ComputeLod(_screen[0], _screen[1], _screen[2], item.Texture);
            for (int k = 1; k < valid - 1; k++)
                Rasterize(_screen[0], _screen[k], _screen[k + 1], item);
        }
    }

    private ClipVertex MakeClip(int index) => new()
    {
        View = _viewPosition[index],
        World = _worldPosition[index],
        Normal = _worldNormal[index],
        U = _uvU[index],
        V = _uvV[index]
    };

    private bool Project(ClipVertex vertex, out ScreenVert screen)
    {
        Vec4 clip = _projection.Transform(new Vec4(vertex.View.X, vertex.View.Y, vertex.View.Z, 1f));
        if (clip.W <= 1e-6f)
        {
            screen = default;
            return false;
        }

        float inverseW = 1f / clip.W;
        float ndcX = clip.X * inverseW;
        float ndcY = clip.Y * inverseW;
        screen = new ScreenVert
        {
            X = ((ndcX * .5f) + .5f) * _width,
            Y = (1f - ((ndcY * .5f) + .5f)) * _height,
            InverseW = inverseW,
            World = vertex.World,
            Normal = vertex.Normal,
            U = vertex.U,
            V = vertex.V
        };
        return true;
    }

    private static float ComputeLod(in ScreenVert a, in ScreenVert b, in ScreenVert c, Texture3D? texture)
    {
        if (texture is null || texture.LevelCount <= 1) return 0f;

        float area = MathF.Abs(((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y))) * .5f;
        if (area < .0005f) return 0f;

        float uvArea = MathF.Abs(((b.U - a.U) * (c.V - a.V)) - ((c.U - a.U) * (b.V - a.V)));
        if (uvArea < 1e-9f) return 0f;

        float texelsPerPixel = (uvArea * texture.Width * texture.Height) / area;
        return Math.Clamp(MathF.Log2(MathF.Max(1f, texelsPerPixel)) * .5f, 0f, texture.LevelCount - 1f);
    }

    // ------------------------------------------------------------ rastérisation

    private void Rasterize(in ScreenVert a, in ScreenVert b, in ScreenVert c, SceneItem item)
    {
        float area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (MathF.Abs(area) < 1e-6f) return;
        if (!item.Material.TwoSided && area >= 0f) return;

        float minXf = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float maxXf = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float minYf = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        float maxYf = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (maxXf < 0f || maxYf < 0f || minXf > _width || minYf > _height) return;

        int minX = Math.Max(0, (int)MathF.Floor(minXf));
        int maxX = Math.Min(_width - 1, (int)MathF.Ceiling(maxXf));
        int minY = Math.Max(_pixelTop, (int)MathF.Floor(minYf));
        int maxY = Math.Min(_pixelTop + _pixelRows - 1, (int)MathF.Ceiling(maxYf));
        if (minX > maxX || minY > maxY) return;

        float inverseArea = 1f / area;
        Material3D material = item.Material;
        Texture3D? texture = item.Texture;
        bool writeDepth = !material.Additive && !material.AlphaBlend;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + .5f;
            int row = (y - _pixelTop) * _width;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + .5f;
                float w0 = (((c.X - b.X) * (py - b.Y)) - ((c.Y - b.Y) * (px - b.X))) * inverseArea;
                float w1 = (((a.X - c.X) * (py - c.Y)) - ((a.Y - c.Y) * (px - c.X))) * inverseArea;
                float w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

                float inverseW = (w0 * a.InverseW) + (w1 * b.InverseW) + (w2 * c.InverseW);
                if (inverseW <= 1e-8f) continue;
                float depthValue = 1f / inverseW;
                int index = row + x;
                if (depthValue >= _depth[index]) continue;

                float scale = 1f / inverseW;
                float alphaWeight0 = w0 * a.InverseW * scale;
                float alphaWeight1 = w1 * b.InverseW * scale;
                float alphaWeight2 = w2 * c.InverseW * scale;

                Vec3 world = (a.World * alphaWeight0) + (b.World * alphaWeight1) + (c.World * alphaWeight2);
                Vec3 normal = (a.Normal * alphaWeight0) + (b.Normal * alphaWeight1) + (c.Normal * alphaWeight2);
                float u = (a.U * alphaWeight0) + (b.U * alphaWeight1) + (c.U * alphaWeight2);
                float v = (a.V * alphaWeight0) + (b.V * alphaWeight1) + (c.V * alphaWeight2);

                _shadedPixels++;
                ShadePixel(index, world, normal, u, v, material, texture, x, y);
                if (writeDepth) _depth[index] = depthValue;
            }
        }
    }

    // -------------------------------------------------------------- éclairage

    private void ShadePixel(int index, Vec3 world, Vec3 normal, float u, float v, Material3D material, Texture3D? texture, int pixelX, int pixelY)
    {
        Vec3 albedo = material.Tint;
        float alpha = 1f;
        if (texture is not null)
        {
            texture.Sample(u, v, _lod, out Vec3 sample, out float sampleAlpha);
            albedo = albedo * sample;
            alpha = sampleAlpha;
        }

        Vec3 viewDirection = (_camera.Position - world).Normalized;
        Vec3 n = normal.Normalized;
        if (material.TwoSided && Vec3.Dot(n, viewDirection) < 0f) n = -n;

        float metal = Math.Clamp(material.Metal, 0f, 1f);
        float roughness = Math.Clamp(material.Roughness, .04f, 1f);
        float shininess = Math.Clamp((2f / (roughness * roughness * roughness * roughness)) - 2f, 2f, 4000f);
        float specularNorm = (shininess + 8f) / 25.1f;

        Vec3 diffuseAlbedo = albedo * (1f - metal);
        Vec3 specularTint = Vec3.Lerp(new Vec3(.045f), albedo, metal);
        float[] specularLut = material.SpecularLut;
        float materialSpecular = material.Specular;

        float hemisphere = (n.Y * .5f) + .5f;
        Vec3 ambient = Vec3.Lerp(_scene.AmbientGround, _scene.AmbientSky, Math.Clamp(hemisphere, 0f, 1f)) * _scene.Ambient;
        Vec3 color = albedo * ambient * (1f - (.35f * metal));

        int lightCount = _scene.Lights.Count;
        for (int i = 0; i < lightCount; i++)
        {
            Light3D light = _scene.Lights[i];
            Vec3 toLight;
            float attenuation;

            if (light.Point)
            {
                Vec3 delta = light.Position - world;
                float distance = delta.Length;
                if (distance > light.Range || distance < 1e-5f) continue;
                toLight = delta / distance;
                float falloff = Math.Clamp(1f - (distance / light.Range), 0f, 1f);
                attenuation = light.Intensity * falloff * falloff;
            }
            else
            {
                toLight = light.Direction.Normalized;
                attenuation = light.Intensity;
            }

            if (attenuation <= 0f) continue;

            float lambert = Vec3.Dot(n, toLight);
            if (lambert <= 0f) continue;

            float shadow = light.CastShadow ? SampleShadow(world, n) : 1f;
            if (shadow <= .002f) continue;

            Vec3 radiance = light.Color * (attenuation * shadow);
            color += diffuseAlbedo * (lambert * radiance);

            Vec3 halfway = (toLight + viewDirection).Normalized;
            float specular = specularLut[(int)(Math.Clamp(Vec3.Dot(n, halfway), 0f, 1f) * 255f)] * materialSpecular;
            float specularFacing = 1f - Math.Clamp(Vec3.Dot(halfway, viewDirection), 0f, 1f);
            float specularFacing2 = specularFacing * specularFacing;
            float fresnel = .04f + (.96f * specularFacing2 * specularFacing2 * specularFacing);
            color += radiance * specularTint * (specular * (.15f + fresnel));
        }

        // Reflet d'environnement : c'est lui qui donne le « métal chromé ».
        float facing = 1f - Math.Clamp(Vec3.Dot(n, viewDirection), 0f, 1f);
        float environmentAmount = (1f - roughness) * (1f - roughness);
        if (environmentAmount > .03f || metal > 0f)
        {
            Vec3 environment = _scene.SampleEnvironment(Vec3.Reflect(-viewDirection, n));
            float rim = facing * facing * facing * facing * facing;
            color += environment * specularTint * (environmentAmount * ((metal * 1.15f) + (rim * .55f)));
            color += environment * (rim * .03f * (1f - metal));
        }

        if (material.Emissive > 0f) color += (albedo * material.Emissive);

        WritePixel(index, color, alpha, material, pixelX, pixelY);
    }

    private void WritePixel(int index, Vec3 color, float alpha, Material3D material, int pixelX, int pixelY)
    {
        Vec3 exposed = color * _scene.Exposure;

        if (_scene.Vignette > 0f)
        {
            float nx = (((pixelX + .5f) / _width) * 2f) - 1f;
            float ny = (((pixelY + .5f) / _height) * 2f) - 1f;
            float falloff = Math.Clamp((nx * nx * .5f) + (ny * ny * .7f), 0f, 1f);
            exposed = exposed * (1f - (_scene.Vignette * falloff));
        }

        int packed = PackLinear(exposed);

        if (!material.Additive && !material.AlphaBlend)
        {
            _color[index] = packed;
            return;
        }

        int destination = _color[index];
        float dr = ((destination >> 16) & 255) / 255f;
        float dg = ((destination >> 8) & 255) / 255f;
        float db = (destination & 255) / 255f;
        float sr = ((packed >> 16) & 255) / 255f;
        float sg = ((packed >> 8) & 255) / 255f;
        float sb = (packed & 255) / 255f;

        float weight = Math.Clamp(alpha, 0f, 1f);
        if (material.Additive)
        {
            dr = Math.Clamp(dr + (sr * weight), 0f, 1f);
            dg = Math.Clamp(dg + (sg * weight), 0f, 1f);
            db = Math.Clamp(db + (sb * weight), 0f, 1f);
        }
        else
        {
            dr = (dr * (1f - weight)) + (sr * weight);
            dg = (dg * (1f - weight)) + (sg * weight);
            db = (db * (1f - weight)) + (sb * weight);
        }

        _color[index] = unchecked((int)0xFF000000)
                        | ((int)Math.Clamp(MathF.Round(dr * 255f), 0f, 255f) << 16)
                        | ((int)Math.Clamp(MathF.Round(dg * 255f), 0f, 255f) << 8)
                        | (int)Math.Clamp(MathF.Round(db * 255f), 0f, 255f);
    }

    private static int PackLinear(Vec3 linear) => unchecked((int)0xFF000000)
                                                    | (ColorUtil.LinearToSrgb(ColorUtil.ToneMap(linear.X)) << 16)
                                                    | (ColorUtil.LinearToSrgb(ColorUtil.ToneMap(linear.Y)) << 8)
                                                    | ColorUtil.LinearToSrgb(ColorUtil.ToneMap(linear.Z));

    private static int PackColor(Vec3 linear) => PackLinear(linear);

    // ------------------------------------------------------------- résolution

    private void Resolve(int outputWidth, int outputHeight)
    {
        int expected = outputWidth * outputHeight;
        if (_output.Length < expected) _output = new int[expected];

        if (_width == outputWidth && _pixelRows == outputHeight)
        {
            Array.Copy(_color, _output, expected);
        }
        else
        {
            float stepX = _width / (float)outputWidth;
            float stepY = _pixelRows / (float)outputHeight;

            for (int y = 0; y < outputHeight; y++)
            {
                float sourceY = ((y + .5f) * stepY) - .5f;
                int y0 = (int)MathF.Floor(sourceY);
                float fy = sourceY - y0;
                int y1 = Math.Clamp(y0 + 1, 0, _pixelRows - 1);
                y0 = Math.Clamp(y0, 0, _pixelRows - 1);

                int row0 = y0 * _width;
                int row1 = y1 * _width;
                int targetRow = y * outputWidth;

                for (int x = 0; x < outputWidth; x++)
                {
                    float sourceX = ((x + .5f) * stepX) - .5f;
                    int x0 = (int)MathF.Floor(sourceX);
                    float fx = sourceX - x0;
                    int x1 = Math.Clamp(x0 + 1, 0, _width - 1);
                    x0 = Math.Clamp(x0, 0, _width - 1);

                    _output[targetRow + x] = Bilinear(
                        _color[row0 + x0], _color[row0 + x1],
                        _color[row1 + x0], _color[row1 + x1],
                        fx, fy);
                }
            }
        }

        BitmapData data = _bitmap!.LockBits(new Rectangle(0, 0, outputWidth, outputHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < outputHeight; y++)
                Marshal.Copy(_output, y * outputWidth, IntPtr.Add(data.Scan0, y * data.Stride), outputWidth);
        }
        finally
        {
            _bitmap!.UnlockBits(data);
        }
    }

    private static int Bilinear(int c00, int c10, int c01, int c11, float fx, float fy)
    {
        float r0 = (((c00 >> 16) & 255) * (1f - fx)) + (((c10 >> 16) & 255) * fx);
        float r1 = (((c01 >> 16) & 255) * (1f - fx)) + (((c11 >> 16) & 255) * fx);
        float g0 = (((c00 >> 8) & 255) * (1f - fx)) + (((c10 >> 8) & 255) * fx);
        float g1 = (((c01 >> 8) & 255) * (1f - fx)) + (((c11 >> 8) & 255) * fx);
        float b0 = ((c00 & 255) * (1f - fx)) + ((c10 & 255) * fx);
        float b1 = ((c01 & 255) * (1f - fx)) + ((c11 & 255) * fx);

        int red = (int)((r0 * (1f - fy)) + (r1 * fy));
        int green = (int)((g0 * (1f - fy)) + (g1 * fy));
        int blue = (int)((b0 * (1f - fy)) + (b1 * fy));

        return unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private struct ClipVertex
    {
        public Vec3 View;
        public Vec3 World;
        public Vec3 Normal;
        public float U;
        public float V;

        public static ClipVertex Lerp(in ClipVertex a, in ClipVertex b, float t) => new()
        {
            View = Vec3.Lerp(a.View, b.View, t),
            World = Vec3.Lerp(a.World, b.World, t),
            Normal = Vec3.Lerp(a.Normal, b.Normal, t),
            U = a.U + ((b.U - a.U) * t),
            V = a.V + ((b.V - a.V) * t)
        };
    }

    private struct ScreenVert
    {
        public float X;
        public float Y;
        public float InverseW;
        public Vec3 World;
        public Vec3 Normal;
        public float U;
        public float V;
    }
}
