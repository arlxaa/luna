using System.Drawing.Imaging;

namespace Luna.UI;

/// <summary>
/// Procedural moon surface: fractal value noise for the maria plus soft craters, mapped to pale
/// blue tones. Produces a lit texture (slices) and a shadowed one (grooves) from the same terrain
/// so the ring reads as one continuous moon.
/// </summary>
internal static class Moon
{
    public sealed record Surface(Bitmap Lit, Bitmap Shadow);

    static readonly Dictionary<int, Surface> cache = new();

    // Palette taken from the artwork: pale lunar white-blue, blue-grey maria, deep groove blue.
    static readonly (Color light, Color dark) LitTones = (Color.FromArgb(242, 247, 255), Color.FromArgb(150, 174, 215));
    static readonly (Color light, Color dark) ShadowTones = (Color.FromArgb(112, 140, 189), Color.FromArgb(70, 95, 145));

    public static Surface Get(int size)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(size, out var surface))
                cache[size] = surface = Make(size, seed: 7);
            return surface;
        }
    }

    static Surface Make(int size, int seed)
    {
        var tone = Terrain(size, seed);
        return new Surface(Paint(size, tone, LitTones), Paint(size, tone, ShadowTones));
    }

    /// <summary>0 = bright highland, 1 = dark mare, per pixel.</summary>
    public static float[] Terrain(int size, int seed)
    {
        var rng = new Random(seed);
        var craters = Enumerable.Range(0, 22).Select(_ => (
            x: (float)rng.NextDouble() * size, y: (float)rng.NextDouble() * size,
            r: size * (0.02f + 0.07f * (float)rng.NextDouble()))).ToArray();

        var tone = new float[size * size];
        float freq = 2.6f / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float n = Fbm(x * freq, y * freq, seed);
                float t = SmoothStep(0.36f, 0.72f, n) * 0.9f;
                foreach (var c in craters)
                {
                    float dx = x - c.x, dy = y - c.y;
                    float d = MathF.Sqrt(dx * dx + dy * dy) / c.r;
                    if (d < 1f) t += 0.16f * (1 - d * d);                 // shadowed bowl
                    else if (d < 1.18f) t -= 0.10f * (1 - (d - 1) / 0.18f); // sunlit rim
                }
                t += (Hash(x, y, seed + 99) - 0.5f) * 0.05f;               // fine grain
                tone[y * size + x] = Math.Clamp(t, 0, 1);
            }
        }
        return tone;
    }

    static Bitmap Paint(int size, float[] tone, (Color light, Color dark) pal)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var px = new byte[size * size * 4];
        for (int i = 0; i < tone.Length; i++)
        {
            float t = tone[i];
            px[i * 4 + 0] = (byte)(pal.light.B + (pal.dark.B - pal.light.B) * t);
            px[i * 4 + 1] = (byte)(pal.light.G + (pal.dark.G - pal.light.G) * t);
            px[i * 4 + 2] = (byte)(pal.light.R + (pal.dark.R - pal.light.R) * t);
            px[i * 4 + 3] = 255;
        }
        for (int y = 0; y < size; y++)
            System.Runtime.InteropServices.Marshal.Copy(px, y * size * 4, data.Scan0 + y * data.Stride, size * 4);
        bmp.UnlockBits(data);
        return bmp;
    }

    static float Fbm(float x, float y, int seed)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (int o = 0; o < 5; o++)
        {
            sum += amp * Noise(x, y, seed + o * 31);
            norm += amp;
            x *= 2.03f; y *= 2.03f; amp *= 0.5f;
        }
        return sum / norm;
    }

    static float Noise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        float u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)x * 0x27D4EB2Du ^ (uint)y * 0x165667B1u ^ (uint)seed * 0x9E3779B9u;
            h ^= h >> 15; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
