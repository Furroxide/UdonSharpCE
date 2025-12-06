using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Procgen
{
    /// <summary>
    /// Coherent noise functions for procedural generation.
    ///
    /// All functions are deterministic and produce identical results across clients
    /// when initialized with the same seed.
    /// </summary>
    /// <remarks>
    /// Includes:
    /// - Perlin noise (classic gradient noise)
    /// - Simplex noise (more efficient, fewer artifacts)
    /// - Worley/cellular noise
    /// - Fractal combinations (fBm, ridged)
    /// </remarks>
    /// <example>
    /// <code>
    /// // Initialize once with a seed
    /// CENoise.Initialize(worldSeed);
    ///
    /// // Generate terrain height
    /// float height = CENoise.Fractal2D(x * 0.1f, z * 0.1f, 4, 0.5f);
    ///
    /// // Generate cave density
    /// float density = CENoise.Perlin3D(x * 0.05f, y * 0.05f, z * 0.05f);
    /// </code>
    /// </example>
    [PublicAPI]
    public static class CENoise
    {
        #region State

        /// <summary>
        /// Permutation table for noise functions.
        /// </summary>
        private static int[] _perm;

        /// <summary>
        /// Extended permutation table (doubled for wrapping).
        /// </summary>
        private static int[] _permMod12;

        /// <summary>
        /// Whether the noise system has been initialized.
        /// </summary>
        private static bool _initialized;

        #endregion

        #region Constants

        /// <summary>
        /// Gradient vectors for 3D Perlin noise.
        /// </summary>
        private static readonly Vector3[] Grad3 = {
            new Vector3(1, 1, 0), new Vector3(-1, 1, 0), new Vector3(1, -1, 0), new Vector3(-1, -1, 0),
            new Vector3(1, 0, 1), new Vector3(-1, 0, 1), new Vector3(1, 0, -1), new Vector3(-1, 0, -1),
            new Vector3(0, 1, 1), new Vector3(0, -1, 1), new Vector3(0, 1, -1), new Vector3(0, -1, -1)
        };

        /// <summary>
        /// Simplex skew factor for 2D.
        /// </summary>
        private const float F2 = 0.3660254037844386f; // 0.5f * (Mathf.Sqrt(3f) - 1f)
        private const float G2 = 0.21132486540518713f; // (3f - Mathf.Sqrt(3f)) / 6f

        /// <summary>
        /// Simplex skew factors for 3D.
        /// </summary>
        private const float F3 = 1f / 3f;
        private const float G3 = 1f / 6f;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the noise system with a seed.
        /// Call once at world start before using noise functions.
        /// </summary>
        /// <param name="seed">The seed for deterministic generation.</param>
        public static void Initialize(int seed)
        {
            var rng = new CERandom(seed);

            // Create base permutation table
            _perm = new int[256];
            for (int i = 0; i < 256; i++)
            {
                _perm[i] = i;
            }

            // Shuffle the permutation table
            rng.Shuffle(_perm);

            // Create extended table for fast modulo
            _permMod12 = new int[512];
            for (int i = 0; i < 512; i++)
            {
                _permMod12[i] = _perm[i & 255] % 12;
            }

            _initialized = true;
        }

        /// <summary>
        /// Ensures the noise system is initialized with a default seed.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (!_initialized)
            {
                Initialize(0);
            }
        }

        /// <summary>
        /// Gets whether the noise system has been initialized.
        /// </summary>
        public static bool IsInitialized => _initialized;

        #endregion

        #region Perlin Noise

        /// <summary>
        /// 1D Perlin noise. Returns value in [-1, 1].
        /// </summary>
        public static float Perlin1D(float x)
        {
            EnsureInitialized();

            int X = FloorToInt(x) & 255;
            x -= Mathf.Floor(x);

            float u = Fade(x);

            int a = _perm[X];
            int b = _perm[X + 1 & 255];

            return Lerp(Grad1D(a, x), Grad1D(b, x - 1), u);
        }

        /// <summary>
        /// 2D Perlin noise. Returns value in [-1, 1].
        /// </summary>
        public static float Perlin2D(float x, float y)
        {
            EnsureInitialized();

            int X = FloorToInt(x) & 255;
            int Y = FloorToInt(y) & 255;

            x -= Mathf.Floor(x);
            y -= Mathf.Floor(y);

            float u = Fade(x);
            float v = Fade(y);

            int aa = _perm[_perm[X] + Y & 255];
            int ab = _perm[_perm[X] + Y + 1 & 255];
            int ba = _perm[_perm[X + 1 & 255] + Y & 255];
            int bb = _perm[_perm[X + 1 & 255] + Y + 1 & 255];

            float x1 = Lerp(Grad2D(aa, x, y), Grad2D(ba, x - 1, y), u);
            float x2 = Lerp(Grad2D(ab, x, y - 1), Grad2D(bb, x - 1, y - 1), u);

            return Lerp(x1, x2, v);
        }

        /// <summary>
        /// 3D Perlin noise. Returns value in [-1, 1].
        /// </summary>
        public static float Perlin3D(float x, float y, float z)
        {
            EnsureInitialized();

            int X = FloorToInt(x) & 255;
            int Y = FloorToInt(y) & 255;
            int Z = FloorToInt(z) & 255;

            x -= Mathf.Floor(x);
            y -= Mathf.Floor(y);
            z -= Mathf.Floor(z);

            float u = Fade(x);
            float v = Fade(y);
            float w = Fade(z);

            int aaa = _perm[_perm[_perm[X] + Y & 255] + Z & 255];
            int aba = _perm[_perm[_perm[X] + Y + 1 & 255] + Z & 255];
            int aab = _perm[_perm[_perm[X] + Y & 255] + Z + 1 & 255];
            int abb = _perm[_perm[_perm[X] + Y + 1 & 255] + Z + 1 & 255];
            int baa = _perm[_perm[_perm[X + 1 & 255] + Y & 255] + Z & 255];
            int bba = _perm[_perm[_perm[X + 1 & 255] + Y + 1 & 255] + Z & 255];
            int bab = _perm[_perm[_perm[X + 1 & 255] + Y & 255] + Z + 1 & 255];
            int bbb = _perm[_perm[_perm[X + 1 & 255] + Y + 1 & 255] + Z + 1 & 255];

            float x1 = Lerp(Grad3D(aaa, x, y, z), Grad3D(baa, x - 1, y, z), u);
            float x2 = Lerp(Grad3D(aba, x, y - 1, z), Grad3D(bba, x - 1, y - 1, z), u);
            float y1 = Lerp(x1, x2, v);

            x1 = Lerp(Grad3D(aab, x, y, z - 1), Grad3D(bab, x - 1, y, z - 1), u);
            x2 = Lerp(Grad3D(abb, x, y - 1, z - 1), Grad3D(bbb, x - 1, y - 1, z - 1), u);
            float y2 = Lerp(x1, x2, v);

            return Lerp(y1, y2, w);
        }

        #endregion

        #region Simplex Noise

        /// <summary>
        /// 2D Simplex noise. Returns value in approximately [-1, 1].
        /// More efficient than Perlin with fewer directional artifacts.
        /// </summary>
        public static float Simplex2D(float x, float y)
        {
            EnsureInitialized();

            float s = (x + y) * F2;
            int i = FloorToInt(x + s);
            int j = FloorToInt(y + s);

            float t = (i + j) * G2;
            float X0 = i - t;
            float Y0 = j - t;
            float x0 = x - X0;
            float y0 = y - Y0;

            int i1, j1;
            if (x0 > y0)
            {
                i1 = 1;
                j1 = 0;
            }
            else
            {
                i1 = 0;
                j1 = 1;
            }

            float x1 = x0 - i1 + G2;
            float y1 = y0 - j1 + G2;
            float x2 = x0 - 1f + 2f * G2;
            float y2 = y0 - 1f + 2f * G2;

            int ii = i & 255;
            int jj = j & 255;

            float n0 = 0f, n1 = 0f, n2 = 0f;

            float t0 = 0.5f - x0 * x0 - y0 * y0;
            if (t0 >= 0)
            {
                t0 *= t0;
                int gi0 = _permMod12[ii + _perm[jj]];
                n0 = t0 * t0 * Dot2D(Grad3[gi0], x0, y0);
            }

            float t1 = 0.5f - x1 * x1 - y1 * y1;
            if (t1 >= 0)
            {
                t1 *= t1;
                int gi1 = _permMod12[ii + i1 + _perm[jj + j1 & 255]];
                n1 = t1 * t1 * Dot2D(Grad3[gi1], x1, y1);
            }

            float t2 = 0.5f - x2 * x2 - y2 * y2;
            if (t2 >= 0)
            {
                t2 *= t2;
                int gi2 = _permMod12[ii + 1 + _perm[jj + 1 & 255]];
                n2 = t2 * t2 * Dot2D(Grad3[gi2], x2, y2);
            }

            return 70f * (n0 + n1 + n2);
        }

        /// <summary>
        /// 3D Simplex noise. Returns value in approximately [-1, 1].
        /// </summary>
        public static float Simplex3D(float x, float y, float z)
        {
            EnsureInitialized();

            float s = (x + y + z) * F3;
            int i = FloorToInt(x + s);
            int j = FloorToInt(y + s);
            int k = FloorToInt(z + s);

            float t = (i + j + k) * G3;
            float X0 = i - t;
            float Y0 = j - t;
            float Z0 = k - t;
            float x0 = x - X0;
            float y0 = y - Y0;
            float z0 = z - Z0;

            int i1, j1, k1, i2, j2, k2;

            if (x0 >= y0)
            {
                if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
                else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
                else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
            }
            else
            {
                if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
                else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
                else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            }

            float x1 = x0 - i1 + G3;
            float y1 = y0 - j1 + G3;
            float z1 = z0 - k1 + G3;
            float x2 = x0 - i2 + 2f * G3;
            float y2 = y0 - j2 + 2f * G3;
            float z2 = z0 - k2 + 2f * G3;
            float x3 = x0 - 1f + 3f * G3;
            float y3 = y0 - 1f + 3f * G3;
            float z3 = z0 - 1f + 3f * G3;

            int ii = i & 255;
            int jj = j & 255;
            int kk = k & 255;

            float n0 = 0f, n1 = 0f, n2 = 0f, n3 = 0f;

            float t0 = 0.6f - x0 * x0 - y0 * y0 - z0 * z0;
            if (t0 >= 0)
            {
                t0 *= t0;
                int gi0 = _permMod12[ii + _perm[jj + _perm[kk]]];
                n0 = t0 * t0 * Vector3.Dot(Grad3[gi0], new Vector3(x0, y0, z0));
            }

            float t1 = 0.6f - x1 * x1 - y1 * y1 - z1 * z1;
            if (t1 >= 0)
            {
                t1 *= t1;
                int gi1 = _permMod12[ii + i1 + _perm[jj + j1 + _perm[kk + k1 & 255] & 255]];
                n1 = t1 * t1 * Vector3.Dot(Grad3[gi1], new Vector3(x1, y1, z1));
            }

            float t2 = 0.6f - x2 * x2 - y2 * y2 - z2 * z2;
            if (t2 >= 0)
            {
                t2 *= t2;
                int gi2 = _permMod12[ii + i2 + _perm[jj + j2 + _perm[kk + k2 & 255] & 255]];
                n2 = t2 * t2 * Vector3.Dot(Grad3[gi2], new Vector3(x2, y2, z2));
            }

            float t3 = 0.6f - x3 * x3 - y3 * y3 - z3 * z3;
            if (t3 >= 0)
            {
                t3 *= t3;
                int gi3 = _permMod12[ii + 1 + _perm[jj + 1 + _perm[kk + 1 & 255] & 255]];
                n3 = t3 * t3 * Vector3.Dot(Grad3[gi3], new Vector3(x3, y3, z3));
            }

            return 32f * (n0 + n1 + n2 + n3);
        }

        #endregion

        #region Worley (Cellular) Noise

        /// <summary>
        /// 2D Worley (cellular) noise. Returns distance to nearest feature point.
        /// Good for creating cell patterns, cracks, or stone textures.
        /// </summary>
        public static float Worley2D(float x, float y)
        {
            EnsureInitialized();

            int xi = FloorToInt(x);
            int yi = FloorToInt(y);

            float minDist = float.MaxValue;

            // Check 3x3 cell neighborhood
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int cellX = xi + dx;
                    int cellY = yi + dy;

                    // Generate feature point in this cell
                    int hash = Hash2D(cellX, cellY);
                    float px = cellX + (hash & 255) / 255f;
                    float py = cellY + ((hash >> 8) & 255) / 255f;

                    float dist = (x - px) * (x - px) + (y - py) * (y - py);
                    if (dist < minDist)
                        minDist = dist;
                }
            }

            return Mathf.Sqrt(minDist);
        }

        /// <summary>
        /// 3D Worley (cellular) noise.
        /// </summary>
        public static float Worley3D(float x, float y, float z)
        {
            EnsureInitialized();

            int xi = FloorToInt(x);
            int yi = FloorToInt(y);
            int zi = FloorToInt(z);

            float minDist = float.MaxValue;

            // Check 3x3x3 cell neighborhood
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int cellX = xi + dx;
                        int cellY = yi + dy;
                        int cellZ = zi + dz;

                        int hash = Hash3D(cellX, cellY, cellZ);
                        float px = cellX + (hash & 255) / 255f;
                        float py = cellY + ((hash >> 8) & 255) / 255f;
                        float pz = cellZ + ((hash >> 16) & 255) / 255f;

                        float dist = (x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz);
                        if (dist < minDist)
                            minDist = dist;
                    }
                }
            }

            return Mathf.Sqrt(minDist);
        }

        #endregion

        #region Fractal Noise

        /// <summary>
        /// Fractal Brownian Motion (fBm) using Perlin noise.
        /// Combines multiple octaves for natural-looking terrain.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <param name="octaves">Number of noise layers (1-8 recommended).</param>
        /// <param name="persistence">Amplitude decay per octave (0.5 typical).</param>
        /// <param name="lacunarity">Frequency multiplier per octave (2.0 typical).</param>
        /// <returns>Fractal noise value, approximately in [-1, 1].</returns>
        public static float Fractal2D(float x, float y, int octaves = 4, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxValue = 0f;

            for (int i = 0; i < octaves; i++)
            {
                total += Perlin2D(x * frequency, y * frequency) * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        /// <summary>
        /// 3D Fractal Brownian Motion.
        /// </summary>
        public static float Fractal3D(float x, float y, float z, int octaves = 4, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxValue = 0f;

            for (int i = 0; i < octaves; i++)
            {
                total += Perlin3D(x * frequency, y * frequency, z * frequency) * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        /// <summary>
        /// Ridged multifractal noise. Creates sharp ridges, good for mountains.
        /// </summary>
        public static float Ridged2D(float x, float y, int octaves = 4, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxValue = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(Perlin2D(x * frequency, y * frequency));
                n *= n; // Square for sharper ridges
                total += n * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        /// <summary>
        /// 3D Ridged multifractal noise.
        /// </summary>
        public static float Ridged3D(float x, float y, float z, int octaves = 4, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxValue = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(Perlin3D(x * frequency, y * frequency, z * frequency));
                n *= n;
                total += n * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        /// <summary>
        /// Turbulence noise. Absolute value creates billowy/cloud-like appearance.
        /// </summary>
        public static float Turbulence2D(float x, float y, int octaves = 4, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxValue = 0f;

            for (int i = 0; i < octaves; i++)
            {
                total += Mathf.Abs(Perlin2D(x * frequency, y * frequency)) * amplitude;
                maxValue += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        #endregion

        #region Helper Functions

        private static int FloorToInt(float x)
        {
            return x >= 0 ? (int)x : (int)x - 1;
        }

        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + t * (b - a);
        }

        private static float Grad1D(int hash, float x)
        {
            return (hash & 1) == 0 ? x : -x;
        }

        private static float Grad2D(int hash, float x, float y)
        {
            int h = hash & 3;
            return ((h & 1) == 0 ? x : -x) + ((h & 2) == 0 ? y : -y);
        }

        private static float Grad3D(int hash, float x, float y, float z)
        {
            Vector3 g = Grad3[hash % 12];
            return g.x * x + g.y * y + g.z * z;
        }

        private static float Dot2D(Vector3 g, float x, float y)
        {
            return g.x * x + g.y * y;
        }

        private static int Hash2D(int x, int y)
        {
            int h = x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }

        private static int Hash3D(int x, int y, int z)
        {
            int h = x * 374761393 + y * 668265263 + z * 1440670387;
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }

        #endregion
    }
}
