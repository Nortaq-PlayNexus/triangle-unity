using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// Seeded value noise for the terrain.
    ///
    /// Separate from <see cref="TriangleProceduralTextures"/>'s internal lattice on
    /// purpose. That one is memoised per octave and constrained to a 256px texture
    /// period; this one is sampled at arbitrary continuous positions across a
    /// 260-metre hillside, where caching a lattice would mean a grid sized to the
    /// terrain rather than to a texture.
    ///
    /// Deterministic for a given seed so the slice is reproducible: the same hillside
    /// every time the dream assembles, which matters when a player is learning a route
    /// through it.
    /// </summary>
    public static class TriangleTerrainNoise
    {
        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                // Two large primes, mixed and avalanched. Deterministic across
                // platforms, which integer hashing with float intermediates is not.
                int h = x * 73856093 ^ y * 19349663 ^ seed * 83492791;
                h ^= h >> 13;
                h *= 1274126177;
                h ^= h >> 16;
                return (h & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        private static float Smooth(float t)
        {
            // Quintic, rather than the cubic used for the texture octaves. The extra
            // terms flatten the first and second derivatives at the cell boundaries, and
            // on a 260 m hillside those boundaries are the ridges the eye actually
            // follows - cubic leaves a visible crease along every lattice line.
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        /// <summary>Tiling-free value noise in 0..1.</summary>
        public static float Noise(float x, float y, int seed)
        {
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = Smooth(x - x0);
            float fy = Smooth(y - y0);

            float v00 = Hash(x0, y0, seed);
            float v10 = Hash(x0 + 1, y0, seed);
            float v01 = Hash(x0, y0 + 1, seed);
            float v11 = Hash(x0 + 1, y0 + 1, seed);

            float top = Mathf.Lerp(v00, v10, fx);
            float bottom = Mathf.Lerp(v01, v11, fx);
            return Mathf.Lerp(top, bottom, fy);
        }

        /// <summary>Fractional Brownian motion, 0..1. The general-purpose fBm.</summary>
        public static float Fbm(float x, float y, int seed, int octaves, float lacunarity = 2.03f, float gain = 0.5f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;

            for (int i = 0; i < octaves; i++)
            {
                sum += Noise(x, y, seed + i * 101) * amplitude;
                total += amplitude;
                amplitude *= gain;
                x *= lacunarity;
                y *= lacunarity;
            }

            return total < 0.0001f ? 0f : sum / total;
        }

        /// <summary>
        /// Ridged fBm in 0..1: crests and valleys with a direction to them.
        ///
        /// This is what the terrain's height field is built from. Plain fBm produces
        /// rounded domes that read as a lumpy surface rather than as landscape; folding
        /// each octave through 1 - |2n - 1| puts a sharp crease along every zero
        /// crossing, and those creases are what the eye reads as ridgelines.
        /// </summary>
        public static float RidgedFbm(float x, float y, int seed, int octaves, float lacunarity = 2.03f, float gain = 0.5f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = Noise(x, y, seed + i * 101);
                n = 1f - Mathf.Abs(n * 2f - 1f);
                sum += n * n * amplitude;

                total += amplitude;
                amplitude *= gain;
                x *= lacunarity;
                y *= lacunarity;
            }

            return total < 0.0001f ? 0f : sum / total;
        }
    }
}
