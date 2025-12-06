using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Procgen
{
    /// <summary>
    /// Deterministic pseudo-random number generator using xorshift128+.
    ///
    /// Produces identical sequences across all clients given the same seed,
    /// enabling synchronized procedural generation in multiplayer worlds.
    /// </summary>
    /// <remarks>
    /// xorshift128+ provides:
    /// - Determinism: Same seed = same sequence on all clients
    /// - Speed: Very fast compared to System.Random
    /// - Quality: Passes BigCrush statistical tests
    /// - Period: 2^128 - 1 (effectively infinite for game purposes)
    /// </remarks>
    /// <example>
    /// <code>
    /// // Same seed produces same results everywhere
    /// var rng = new CERandom(worldSeed);
    ///
    /// float x = rng.NextFloat();           // Same on all clients
    /// int index = rng.Range(0, 10);        // Same on all clients
    /// Vector3 pos = rng.InsideUnitSphere();// Same on all clients
    ///
    /// // Shuffle an array deterministically
    /// int[] deck = { 1, 2, 3, 4, 5 };
    /// rng.Shuffle(deck);  // Same order on all clients
    /// </code>
    /// </example>
    [PublicAPI]
    public class CERandom
    {
        #region State

        /// <summary>
        /// First state variable for xorshift128+.
        /// </summary>
        private ulong _s0;

        /// <summary>
        /// Second state variable for xorshift128+.
        /// </summary>
        private ulong _s1;

        /// <summary>
        /// Original seed for resetting.
        /// </summary>
        private readonly int _seed;

        #endregion

        #region Construction

        /// <summary>
        /// Creates a new deterministic random generator with the given seed.
        /// </summary>
        /// <param name="seed">The seed value. Same seed = same sequence.</param>
        public CERandom(int seed)
        {
            _seed = seed;
            Reset();
        }

        /// <summary>
        /// Creates a random generator with a seed from the current time.
        /// Note: This is NOT deterministic across clients.
        /// </summary>
        public CERandom() : this((int)System.DateTime.Now.Ticks)
        {
        }

        /// <summary>
        /// Resets the generator to its initial state with the original seed.
        /// </summary>
        public void Reset()
        {
            // Initialize state using splitmix64 for good initial distribution
            ulong z = (ulong)_seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            _s0 = z ^ (z >> 31);

            z = _s0 + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            _s1 = z ^ (z >> 31);

            // Ensure state is never all zeros
            if (_s0 == 0 && _s1 == 0)
            {
                _s0 = 1;
            }
        }

        /// <summary>
        /// Sets the generator state from two 64-bit values.
        /// Useful for saving/restoring state.
        /// </summary>
        public void SetState(ulong s0, ulong s1)
        {
            _s0 = s0;
            _s1 = s1;

            if (_s0 == 0 && _s1 == 0)
            {
                _s0 = 1;
            }
        }

        /// <summary>
        /// Gets the current state for saving.
        /// </summary>
        public void GetState(out ulong s0, out ulong s1)
        {
            s0 = _s0;
            s1 = _s1;
        }

        /// <summary>
        /// Gets the original seed.
        /// </summary>
        public int Seed => _seed;

        #endregion

        #region Core Generation

        /// <summary>
        /// Generates the next 64-bit random value (xorshift128+ algorithm).
        /// </summary>
        private ulong NextULong()
        {
            ulong s0 = _s0;
            ulong s1 = _s1;
            ulong result = s0 + s1;

            s1 ^= s0;
            _s0 = RotateLeft(s0, 55) ^ s1 ^ (s1 << 14);
            _s1 = RotateLeft(s1, 36);

            return result;
        }

        /// <summary>
        /// Rotates bits left.
        /// </summary>
        private static ulong RotateLeft(ulong x, int k)
        {
            return (x << k) | (x >> (64 - k));
        }

        #endregion

        #region Integer Generation

        /// <summary>
        /// Returns a random 32-bit integer.
        /// </summary>
        public int NextInt()
        {
            return (int)(NextULong() >> 33);
        }

        /// <summary>
        /// Returns a non-negative random 32-bit integer.
        /// </summary>
        public int NextIntPositive()
        {
            return (int)(NextULong() >> 34);
        }

        /// <summary>
        /// Returns a random integer in the range [min, maxExclusive).
        /// </summary>
        /// <param name="min">Inclusive minimum value.</param>
        /// <param name="maxExclusive">Exclusive maximum value.</param>
        public int Range(int min, int maxExclusive)
        {
            if (maxExclusive <= min)
                return min;

            long range = (long)maxExclusive - min;
            ulong threshold = (ulong)(-(long)range % range);

            ulong r;
            do
            {
                r = NextULong();
            }
            while (r < threshold);

            return min + (int)(r % (ulong)range);
        }

        /// <summary>
        /// Returns a random integer in the range [min, max] (both inclusive).
        /// </summary>
        public int RangeInclusive(int min, int max)
        {
            return Range(min, max + 1);
        }

        #endregion

        #region Float Generation

        /// <summary>
        /// Returns a random float in [0, 1).
        /// </summary>
        public float NextFloat()
        {
            // Use upper 24 bits for full float precision
            return (NextULong() >> 40) * (1.0f / 16777216.0f);
        }

        /// <summary>
        /// Returns a random float in [0, 1].
        /// </summary>
        public float NextFloatInclusive()
        {
            return (NextULong() >> 40) * (1.0f / 16777215.0f);
        }

        /// <summary>
        /// Returns a random float in the range [min, max).
        /// </summary>
        public float Range(float min, float max)
        {
            return min + NextFloat() * (max - min);
        }

        /// <summary>
        /// Returns a random double in [0, 1).
        /// </summary>
        public double NextDouble()
        {
            // Use upper 53 bits for full double precision
            return (NextULong() >> 11) * (1.0 / 9007199254740992.0);
        }

        /// <summary>
        /// Returns a random double in the range [min, max).
        /// </summary>
        public double Range(double min, double max)
        {
            return min + NextDouble() * (max - min);
        }

        #endregion

        #region Boolean Generation

        /// <summary>
        /// Returns a random boolean (50/50 chance).
        /// </summary>
        public bool NextBool()
        {
            return (NextULong() & 1) == 1;
        }

        /// <summary>
        /// Returns true with the given probability.
        /// </summary>
        /// <param name="probability">Probability of returning true (0 to 1).</param>
        public bool Chance(float probability)
        {
            return NextFloat() < probability;
        }

        #endregion

        #region Vector Generation

        /// <summary>
        /// Returns a random point inside a unit circle (2D).
        /// </summary>
        public Vector2 InsideUnitCircle()
        {
            float angle = NextFloat() * Mathf.PI * 2f;
            float radius = Mathf.Sqrt(NextFloat());
            return new Vector2(
                Mathf.Cos(angle) * radius,
                Mathf.Sin(angle) * radius
            );
        }

        /// <summary>
        /// Returns a random point on the edge of a unit circle (2D).
        /// </summary>
        public Vector2 OnUnitCircle()
        {
            float angle = NextFloat() * Mathf.PI * 2f;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        /// <summary>
        /// Returns a random point inside a unit sphere (3D).
        /// </summary>
        public Vector3 InsideUnitSphere()
        {
            // Use rejection sampling for uniform distribution
            float x, y, z, sqrMag;
            do
            {
                x = NextFloat() * 2f - 1f;
                y = NextFloat() * 2f - 1f;
                z = NextFloat() * 2f - 1f;
                sqrMag = x * x + y * y + z * z;
            }
            while (sqrMag > 1f || sqrMag < 0.0001f);

            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Returns a random point on the surface of a unit sphere (3D).
        /// </summary>
        public Vector3 OnUnitSphere()
        {
            // Use Marsaglia's method for uniform distribution
            float u = NextFloat() * 2f - 1f;
            float theta = NextFloat() * Mathf.PI * 2f;
            float sqrtOneMinusU2 = Mathf.Sqrt(1f - u * u);

            return new Vector3(
                sqrtOneMinusU2 * Mathf.Cos(theta),
                sqrtOneMinusU2 * Mathf.Sin(theta),
                u
            );
        }

        /// <summary>
        /// Returns a random unit direction (normalized Vector3).
        /// </summary>
        public Vector3 Direction()
        {
            return OnUnitSphere();
        }

        /// <summary>
        /// Returns a random rotation.
        /// </summary>
        public Quaternion RotationUniform()
        {
            // Use Shoemake's method for uniform quaternion distribution
            float u0 = NextFloat();
            float u1 = NextFloat() * Mathf.PI * 2f;
            float u2 = NextFloat() * Mathf.PI * 2f;

            float sqrtU0 = Mathf.Sqrt(u0);
            float sqrtOneMinusU0 = Mathf.Sqrt(1f - u0);

            return new Quaternion(
                sqrtOneMinusU0 * Mathf.Sin(u1),
                sqrtOneMinusU0 * Mathf.Cos(u1),
                sqrtU0 * Mathf.Sin(u2),
                sqrtU0 * Mathf.Cos(u2)
            );
        }

        /// <summary>
        /// Returns a random point inside a bounds volume.
        /// </summary>
        public Vector3 PointInBounds(Bounds bounds)
        {
            return new Vector3(
                Range(bounds.min.x, bounds.max.x),
                Range(bounds.min.y, bounds.max.y),
                Range(bounds.min.z, bounds.max.z)
            );
        }

        /// <summary>
        /// Returns a random point inside a rect.
        /// </summary>
        public Vector2 PointInRect(Rect rect)
        {
            return new Vector2(
                Range(rect.xMin, rect.xMax),
                Range(rect.yMin, rect.yMax)
            );
        }

        #endregion

        #region Array Operations

        /// <summary>
        /// Returns a random element from an array.
        /// </summary>
        public T Choose<T>(T[] array)
        {
            if (array == null || array.Length == 0)
                return default;

            return array[Range(0, array.Length)];
        }

        /// <summary>
        /// Returns a random index for an array.
        /// </summary>
        public int ChooseIndex<T>(T[] array)
        {
            if (array == null || array.Length == 0)
                return -1;

            return Range(0, array.Length);
        }

        /// <summary>
        /// Shuffles an array in-place using Fisher-Yates algorithm.
        /// </summary>
        public void Shuffle<T>(T[] array)
        {
            if (array == null || array.Length < 2)
                return;

            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = Range(0, i + 1);
                T temp = array[i];
                array[i] = array[j];
                array[j] = temp;
            }
        }

        /// <summary>
        /// Returns a weighted random index based on weights array.
        /// </summary>
        /// <param name="weights">Array of non-negative weights.</param>
        /// <returns>Index selected by weight, or -1 if all weights are zero.</returns>
        public int WeightedChoice(float[] weights)
        {
            if (weights == null || weights.Length == 0)
                return -1;

            float total = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                total += weights[i];
            }

            if (total <= 0f)
                return -1;

            float target = NextFloat() * total;
            float accumulated = 0f;

            for (int i = 0; i < weights.Length; i++)
            {
                accumulated += weights[i];
                if (target < accumulated)
                    return i;
            }

            return weights.Length - 1;
        }

        #endregion

        #region Color Generation

        /// <summary>
        /// Returns a random color with full saturation.
        /// </summary>
        public Color ColorHSV()
        {
            return Color.HSVToRGB(NextFloat(), 1f, 1f);
        }

        /// <summary>
        /// Returns a random color with specified saturation and value ranges.
        /// </summary>
        public Color ColorHSV(float saturationMin, float saturationMax, float valueMin, float valueMax)
        {
            return Color.HSVToRGB(
                NextFloat(),
                Range(saturationMin, saturationMax),
                Range(valueMin, valueMax)
            );
        }

        /// <summary>
        /// Returns a random RGB color.
        /// </summary>
        public Color ColorRGB()
        {
            return new Color(NextFloat(), NextFloat(), NextFloat());
        }

        /// <summary>
        /// Returns a random RGBA color.
        /// </summary>
        public Color ColorRGBA()
        {
            return new Color(NextFloat(), NextFloat(), NextFloat(), NextFloat());
        }

        #endregion
    }
}
