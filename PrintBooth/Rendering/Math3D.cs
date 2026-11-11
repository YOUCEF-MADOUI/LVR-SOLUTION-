namespace Livereon.Modules.PrintBooth.Rendering;

/// <summary>Petit vecteur 2D immuable, utilisé pour les coordonnées de texture.</summary>
public readonly struct Vec2
{
    public readonly float X;
    public readonly float Y;

    public Vec2(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>Vecteur 3D immuable (float) : base de toute la géométrie du moteur.</summary>
public readonly struct Vec3
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Vec3(float value)
    {
        X = value;
        Y = value;
        Z = value;
    }

    public static readonly Vec3 Zero = new(0f, 0f, 0f);
    public static readonly Vec3 One = new(1f, 1f, 1f);
    public static readonly Vec3 Up = new(0f, 1f, 0f);
    public static readonly Vec3 Right = new(1f, 0f, 0f);
    public static readonly Vec3 Forward = new(0f, 0f, 1f);

    public float Length => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));

    public Vec3 Normalized
    {
        get
        {
            float length = Length;
            return length > 1e-8f ? new Vec3(X / length, Y / length, Z / length) : new Vec3(0f, 1f, 0f);
        }
    }

    public Vec3 Saturated => new(Math.Clamp(X, 0f, 1f), Math.Clamp(Y, 0f, 1f), Math.Clamp(Z, 0f, 1f));

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, float b) => new(a.X * b, a.Y * b, a.Z * b);

    public static Vec3 operator *(float a, Vec3 b) => new(a * b.X, a * b.Y, a * b.Z);

    public static Vec3 operator *(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static Vec3 operator /(Vec3 a, float b) => new(a.X / b, a.Y / b, a.Z / b);

    public static float Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + ((b - a) * t);

    /// <summary>Réflexion d'un vecteur incident autour d'une normale.</summary>
    public static Vec3 Reflect(Vec3 direction, Vec3 normal) => direction - (normal * (2f * Dot(direction, normal)));

    /// <summary>Teinte exponentielle, utilisée pour les lumières et les reflets.</summary>
    public Vec3 Pow(float exponent) => new(MathF.Pow(X, exponent), MathF.Pow(Y, exponent), MathF.Pow(Z, exponent));
}

public readonly struct Vec4
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;
    public readonly float W;

    public Vec4(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }
}

/// <summary>Matrice 3x3 (normales).</summary>
public readonly struct Mat3
{
    public readonly float[] M;

    public Mat3(float[] values) => M = values;

    public Vec3 Transform(Vec3 v) => new(
        (M[0] * v.X) + (M[1] * v.Y) + (M[2] * v.Z),
        (M[3] * v.X) + (M[4] * v.Y) + (M[5] * v.Z),
        (M[6] * v.X) + (M[7] * v.Y) + (M[8] * v.Z));
}

/// <summary>
/// Matrice 4x4 ligne par ligne, appliquée à des vecteurs colonnes : p' = M * p.
/// Les opérations renvoient toujours une nouvelle matrice (immuable).
/// </summary>
public sealed class Mat4
{
    public readonly float[] M;

    private Mat4(float[] values) => M = values;

    public static Mat4 Identity() => new(
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    ]);

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        float[] result = new float[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                float sum = 0f;
                for (int k = 0; k < 4; k++) sum += a.M[(row * 4) + k] * b.M[(k * 4) + column];
                result[(row * 4) + column] = sum;
            }
        }
        return new Mat4(result);
    }

    public Vec4 Transform(Vec4 v) => new(
        (M[0] * v.X) + (M[1] * v.Y) + (M[2] * v.Z) + (M[3] * v.W),
        (M[4] * v.X) + (M[5] * v.Y) + (M[6] * v.Z) + (M[7] * v.W),
        (M[8] * v.X) + (M[9] * v.Y) + (M[10] * v.Z) + (M[11] * v.W),
        (M[12] * v.X) + (M[13] * v.Y) + (M[14] * v.Z) + (M[15] * v.W));

    public Vec3 TransformPoint(Vec3 p)
    {
        Vec4 r = Transform(new Vec4(p.X, p.Y, p.Z, 1f));
        if (MathF.Abs(r.W) < 1e-8f) return new Vec3(r.X, r.Y, r.Z);
        float inverse = 1f / r.W;
        return new Vec3(r.X * inverse, r.Y * inverse, r.Z * inverse);
    }

    public Vec3 TransformDirection(Vec3 d) => new(
        (M[0] * d.X) + (M[1] * d.Y) + (M[2] * d.Z),
        (M[4] * d.X) + (M[5] * d.Y) + (M[6] * d.Z),
        (M[8] * d.X) + (M[9] * d.Y) + (M[10] * d.Z));

    /// <summary>Matrice des normales : transposée de l'inverse du bloc 3x3.</summary>
    public Mat3 NormalMatrix()
    {
        float a = M[0], b = M[1], c = M[2];
        float d = M[4], e = M[5], f = M[6];
        float g = M[8], h = M[9], i = M[10];

        float determinant = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        if (MathF.Abs(determinant) < 1e-9f) return new Mat3([1, 0, 0, 0, 1, 0, 0, 0, 1]);

        float inverse = 1f / determinant;
        float i00 = ((e * i) - (f * h)) * inverse;
        float i01 = ((c * h) - (b * i)) * inverse;
        float i02 = ((b * f) - (c * e)) * inverse;
        float i10 = ((f * g) - (d * i)) * inverse;
        float i11 = ((a * i) - (c * g)) * inverse;
        float i12 = ((c * d) - (a * f)) * inverse;
        float i20 = ((d * h) - (e * g)) * inverse;
        float i21 = ((b * g) - (a * h)) * inverse;
        float i22 = ((a * e) - (b * d)) * inverse;

        // Transposée : les colonnes de l'inverse deviennent les lignes.
        return new Mat3([i00, i10, i20, i01, i11, i21, i02, i12, i22]);
    }

    public static Mat4 Translation(Vec3 t) => new(
    [
        1, 0, 0, t.X,
        0, 1, 0, t.Y,
        0, 0, 1, t.Z,
        0, 0, 0, 1
    ]);

    public static Mat4 Scale(Vec3 s) => new(
    [
        s.X, 0, 0, 0,
        0, s.Y, 0, 0,
        0, 0, s.Z, 0,
        0, 0, 0, 1
    ]);

    public static Mat4 RotationX(float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Mat4(
        [
            1, 0, 0, 0,
            0, cos, -sin, 0,
            0, sin, cos, 0,
            0, 0, 0, 1
        ]);
    }

    public static Mat4 RotationY(float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Mat4(
        [
            cos, 0, sin, 0,
            0, 1, 0, 0,
            -sin, 0, cos, 0,
            0, 0, 0, 1
        ]);
    }

    public static Mat4 RotationZ(float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Mat4(
        [
            cos, -sin, 0, 0,
            sin, cos, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ]);
    }

    public static Mat4 LookAt(Vec3 eye, Vec3 target, Vec3 up)
    {
        Vec3 forward = (target - eye).Normalized;
        Vec3 side = Vec3.Cross(forward, up).Normalized;
        if (side.Length < 1e-6f) side = Vec3.Cross(forward, new Vec3(1f, 0f, 0f)).Normalized;
        Vec3 realUp = Vec3.Cross(side, forward);

        return new Mat4(
        [
            side.X, side.Y, side.Z, -Vec3.Dot(side, eye),
            realUp.X, realUp.Y, realUp.Z, -Vec3.Dot(realUp, eye),
            -forward.X, -forward.Y, -forward.Z, Vec3.Dot(forward, eye),
            0, 0, 0, 1
        ]);
    }

    public static Mat4 Perspective(float fovYRadians, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovYRadians * .5f);
        return new Mat4(
        [
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), (2f * far * near) / (near - far),
            0, 0, -1, 0
        ]);
    }

    public static Mat4 Ortho(float left, float right, float bottom, float top, float near, float far) => new(
    [
        2f / (right - left), 0, 0, -(right + left) / (right - left),
        0, 2f / (top - bottom), 0, -(top + bottom) / (top - bottom),
        0, 0, -2f / (far - near), -(far + near) / (far - near),
        0, 0, 0, 1
    ]);
}

/// <summary>Conversions sRGB / linéaire par tables (indispensable en temps réel).</summary>
public static class ColorUtil
{
    private static readonly float[] SrgbToLinearTable = BuildSrgbToLinear();
    private static readonly byte[] LinearToSrgbTable = BuildLinearToSrgb();

    private static float[] BuildSrgbToLinear()
    {
        float[] table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float value = i / 255f;
            table[i] = value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
        }
        return table;
    }

    private static byte[] BuildLinearToSrgb()
    {
        byte[] table = new byte[4097];
        for (int i = 0; i <= 4096; i++)
        {
            float value = i / 4096f;
            float encoded = value <= .0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1f / 2.4f)) - .055f;
            table[i] = (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
        }
        return table;
    }

    public static float SrgbToLinear(byte value) => SrgbToLinearTable[value];

    public static float SrgbToLinear(int value) => SrgbToLinearTable[Math.Clamp(value, 0, 255)];

    public static byte LinearToSrgb(float value)
    {
        int index = (int)(Math.Clamp(value, 0f, 1f) * 4096f);
        return LinearToSrgbTable[Math.Clamp(index, 0, 4096)];
    }

    /// <summary>Courbe de film ACES (approximation de Narkowicz).</summary>
    public static float ToneMap(float value)
    {
        value = MathF.Max(0f, value);
        float mapped = (value * ((2.51f * value) + .03f)) / ((value * ((2.43f * value) + .59f)) + .14f);
        return Math.Clamp(mapped, 0f, 1f);
    }
}
