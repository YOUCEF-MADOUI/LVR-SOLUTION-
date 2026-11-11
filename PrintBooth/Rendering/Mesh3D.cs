namespace Livereon.Modules.PrintBooth.Rendering;

/// <summary>Maillage triangulé : sommets (position, normale, uv) et indices.</summary>
public sealed class Mesh3D
{
    public float[] Positions = [];
    public float[] Normals = [];
    public float[] Uvs = [];
    public int[] Indices = [];

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;
}

public sealed class MeshBuilder
{
    private readonly List<float> _positions = [];
    private readonly List<float> _normals = [];
    private readonly List<float> _uvs = [];
    private readonly List<int> _indices = [];

    public int Vertex(Vec3 position, Vec3 normal, float u, float v)
    {
        _positions.Add(position.X);
        _positions.Add(position.Y);
        _positions.Add(position.Z);
        _normals.Add(normal.X);
        _normals.Add(normal.Y);
        _normals.Add(normal.Z);
        _uvs.Add(u);
        _uvs.Add(v);
        return (_positions.Count / 3) - 1;
    }

    public void Triangle(int a, int b, int c)
    {
        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
    }

    public void Quad(int a, int b, int c, int d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    /// <summary>Ajoute un quadrilatère plat avec sa normale et ses uv.</summary>
    public void FlatQuad(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, Vec3 normal)
    {
        int a = Vertex(p0, normal, 0f, 0f);
        int b = Vertex(p1, normal, 1f, 0f);
        int c = Vertex(p2, normal, 1f, 1f);
        int d = Vertex(p3, normal, 0f, 1f);
        Quad(a, b, c, d);
    }

    public Mesh3D Build() => new()
    {
        Positions = [.. _positions],
        Normals = [.. _normals],
        Uvs = [.. _uvs],
        Indices = [.. _indices]
    };
}

/// <summary>Générateurs de formes : le nécessaire pour l'imprimante et son décor.</summary>
public static class Primitives
{
    /// <summary>Boîte centrée, faces plates et normales franches.</summary>
    public static Mesh3D Box(float width, float height, float depth)
    {
        float x = width * .5f;
        float y = height * .5f;
        float z = depth * .5f;
        MeshBuilder builder = new();

        builder.FlatQuad(new Vec3(-x, -y, z), new Vec3(x, -y, z), new Vec3(x, y, z), new Vec3(-x, y, z), new Vec3(0, 0, 1));
        builder.FlatQuad(new Vec3(x, -y, -z), new Vec3(-x, -y, -z), new Vec3(-x, y, -z), new Vec3(x, y, -z), new Vec3(0, 0, -1));
        builder.FlatQuad(new Vec3(x, -y, z), new Vec3(x, -y, -z), new Vec3(x, y, -z), new Vec3(x, y, z), new Vec3(1, 0, 0));
        builder.FlatQuad(new Vec3(-x, -y, -z), new Vec3(-x, -y, z), new Vec3(-x, y, z), new Vec3(-x, y, -z), new Vec3(-1, 0, 0));
        builder.FlatQuad(new Vec3(-x, y, z), new Vec3(x, y, z), new Vec3(x, y, -z), new Vec3(-x, y, -z), new Vec3(0, 1, 0));
        builder.FlatQuad(new Vec3(-x, -y, -z), new Vec3(x, -y, -z), new Vec3(x, -y, z), new Vec3(-x, -y, z), new Vec3(0, -1, 0));

        return builder.Build();
    }

    /// <summary>
    /// Boîte aux arêtes adoucies : c'est l'enveloppe d'une sphère de rayon
    /// <paramref name="radius"/> roulant sur une boîte intérieure. Donne le
    /// rendu d'une pièce moulée en plastique, impossible à obtenir avec six faces.
    /// </summary>
    public static Mesh3D RoundedBox(float width, float height, float depth, float radius, int segmentsU = 36, int segmentsV = 18)
    {
        float x = (width * .5f) - radius;
        float y = (height * .5f) - radius;
        float z = (depth * .5f) - radius;
        Vec3 inner = new(MathF.Max(.0005f, x), MathF.Max(.0005f, y), MathF.Max(.0005f, z));
        float reach = 24f;
        MeshBuilder builder = new();

        for (int v = 0; v <= segmentsV; v++)
        {
            float theta = MathF.PI * v / segmentsV;
            for (int u = 0; u <= segmentsU; u++)
            {
                float phi = MathF.PI * 2f * u / segmentsU;
                Vec3 direction = new(
                    MathF.Sin(theta) * MathF.Cos(phi),
                    MathF.Cos(theta),
                    MathF.Sin(theta) * MathF.Sin(phi));

                Vec3 probe = direction * reach;
                Vec3 core = new(
                    Math.Clamp(probe.X, -inner.X, inner.X),
                    Math.Clamp(probe.Y, -inner.Y, inner.Y),
                    Math.Clamp(probe.Z, -inner.Z, inner.Z));
                Vec3 offset = probe - core;
                Vec3 normal = offset.Length > 1e-6f ? offset.Normalized : direction;
                builder.Vertex(core + (normal * radius), normal, u / (float)segmentsU, v / (float)segmentsV);
            }
        }

        int stride = segmentsU + 1;
        for (int v = 0; v < segmentsV; v++)
        {
            for (int u = 0; u < segmentsU; u++)
            {
                int a = (v * stride) + u;
                int b = a + 1;
                int c = a + stride + 1;
                int d = a + stride;
                builder.Quad(a, b, c, d);
            }
        }

        return builder.Build();
    }

    /// <summary>Cylindre (ou cône tronqué) d'axe Y, centré, avec ses deux opercules.</summary>
    public static Mesh3D Cylinder(float radiusBottom, float radiusTop, float height, int segments = 40, bool caps = true)
    {
        MeshBuilder builder = new();
        float half = height * .5f;
        float slope = (radiusBottom - radiusTop) / MathF.Max(1e-5f, height);
        float sideLength = MathF.Sqrt(1f + (slope * slope));

        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float angle = t * MathF.PI * 2f;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            Vec3 normal = new Vec3(cos, slope, sin) / sideLength;

            builder.Vertex(new Vec3(cos * radiusBottom, -half, sin * radiusBottom), normal, t, 1f);
            builder.Vertex(new Vec3(cos * radiusTop, half, sin * radiusTop), normal, t, 0f);
        }

        for (int i = 0; i < segments; i++)
        {
            int b0 = i * 2;
            int t0 = b0 + 1;
            int b1 = b0 + 2;
            int t1 = b0 + 3;
            builder.Quad(b0, t0, t1, b1);
        }

        if (!caps) return builder.Build();

        int topCenter = builder.Vertex(new Vec3(0f, half, 0f), Vec3.Up, .5f, .5f);
        int bottomCenter = builder.Vertex(new Vec3(0f, -half, 0f), new Vec3(0f, -1f, 0f), .5f, .5f);
        int topStart = builder.Vertex(Vec3.Zero, Vec3.Zero, 0f, 0f);
        for (int i = 0; i <= segments; i++)
        {
            float angle = i / (float)segments * MathF.PI * 2f;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            builder.Vertex(new Vec3(cos * radiusTop, half, sin * radiusTop), Vec3.Up, cos * .5f + .5f, sin * .5f + .5f);
            builder.Vertex(new Vec3(cos * radiusBottom, -half, sin * radiusBottom), new Vec3(0f, -1f, 0f), cos * .5f + .5f, sin * .5f + .5f);
        }

        for (int i = 0; i < segments; i++)
        {
            int topA = topStart + 1 + (i * 2);
            int topB = topStart + 3 + (i * 2);
            builder.Triangle(topCenter, topB, topA);
        }

        int bottomStart = topStart + 2;
        for (int i = 0; i < segments; i++)
        {
            int bottomA = bottomStart + (i * 2);
            int bottomB = bottomStart + 2 + (i * 2);
            builder.Triangle(bottomCenter, bottomA, bottomB);
        }

        return builder.Build();
    }

    /// <summary>Surface paramétrique : uv = (u, v), normales calculées par différences.</summary>
    public static Mesh3D Grid(int segmentsU, int segmentsV, Func<float, float, Vec3> point, bool flip)
    {
        MeshBuilder builder = new();
        int stride = segmentsU + 1;
        Vec3[,] positions = new Vec3[stride, segmentsV + 1];
        Vec3[,] normals = new Vec3[stride, segmentsV + 1];
        int[,] indices = new int[stride, segmentsV + 1];
        const float epsilon = .0025f;

        for (int v = 0; v <= segmentsV; v++)
        {
            float fv = v / (float)segmentsV;
            for (int u = 0; u <= segmentsU; u++)
            {
                float fu = u / (float)segmentsU;
                positions[u, v] = point(fu, fv);

                Vec3 alongU = point(MathF.Min(1f, fu + epsilon), fv) - point(MathF.Max(0f, fu - epsilon), fv);
                Vec3 alongV = point(fu, MathF.Min(1f, fv + epsilon)) - point(fu, MathF.Max(0f, fv - epsilon));
                Vec3 normal = Vec3.Cross(alongU, alongV).Normalized;
                if (flip) normal = -normal;
                normals[u, v] = normal;
            }
        }

        for (int v = 0; v <= segmentsV; v++)
        {
            for (int u = 0; u <= segmentsU; u++)
                indices[u, v] = builder.Vertex(positions[u, v], normals[u, v], u / (float)segmentsU, v / (float)segmentsV);
        }

        for (int v = 0; v < segmentsV; v++)
        {
            for (int u = 0; u < segmentsU; u++)
            {
                int a = indices[u, v];
                int b = indices[u, v + 1];
                int c = indices[u + 1, v + 1];
                int d = indices[u + 1, v];
                if (flip) builder.Quad(a, b, c, d);
                else builder.Quad(a, d, c, b);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Plan vertical face à +Z. Les uv sont retournés verticalement pour que
    /// v = 0 corresponde au haut de l'image, comme dans un bitmap.
    /// </summary>
    public static Mesh3D Plane(float width, float height, int segments = 2)
    {
        Mesh3D mesh = Grid(segments, segments, (u, v) => new Vec3((u - .5f) * width, (v - .5f) * height, 0f), false);
        for (int i = 1; i < mesh.Uvs.Length; i += 2) mesh.Uvs[i] = 1f - mesh.Uvs[i];
        return mesh;
    }
}
