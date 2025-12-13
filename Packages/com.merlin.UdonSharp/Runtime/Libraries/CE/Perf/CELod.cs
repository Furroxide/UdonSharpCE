using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Level-of-Detail helper for managing update frequencies based on distance.
    ///
    /// Enables significant performance gains by reducing update frequency for
    /// distant entities while maintaining full fidelity for nearby ones.
    /// </summary>
    /// <remarks>
    /// LOD-based updates are essential for high-entity-count worlds:
    /// - Close entities: Update every frame
    /// - Medium distance: Update every 2-4 frames
    /// - Far entities: Update every 8-16 frames
    /// - Very far: Update every 30+ frames or skip entirely
    ///
    /// This can reduce effective per-frame entity count by 70-90% while
    /// maintaining visual quality for entities the player is looking at.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class EnemyManager : UdonSharpBehaviour
    /// {
    ///     private CELod lod;
    ///     private Vector3[] positions;
    ///
    ///     void Start()
    ///     {
    ///         lod = new CELod(4);
    ///         lod.SetLevel(0, 0f, 10f, 1);    // 0-10m: every frame
    ///         lod.SetLevel(1, 10f, 30f, 3);   // 10-30m: every 3 frames
    ///         lod.SetLevel(2, 30f, 100f, 10); // 30-100m: every 10 frames
    ///         lod.SetLevel(3, 100f, float.MaxValue, 30); // 100m+: every 30 frames
    ///     }
    ///
    ///     void Update()
    ///     {
    ///         Vector3 playerPos = localPlayer.GetPosition();
    ///         lod.Update(Time.frameCount);
    ///
    ///         for (int i = 0; i < enemyCount; i++)
    ///         {
    ///             if (lod.ShouldUpdate(playerPos, positions[i]))
    ///             {
    ///                 UpdateEnemy(i);
    ///             }
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class CELod
    {
        #region Fields

        /// <summary>
        /// Number of LOD levels.
        /// </summary>
        private readonly int _levelCount;

        /// <summary>
        /// Minimum distance for each LOD level.
        /// </summary>
        private readonly float[] _minDistances;

        /// <summary>
        /// Maximum distance for each LOD level.
        /// </summary>
        private readonly float[] _maxDistances;

        /// <summary>
        /// Squared min distances for faster comparison.
        /// </summary>
        private readonly float[] _minDistancesSq;

        /// <summary>
        /// Squared max distances for faster comparison.
        /// </summary>
        private readonly float[] _maxDistancesSq;

        /// <summary>
        /// Update interval (in frames) for each LOD level.
        /// </summary>
        private readonly int[] _updateIntervals;

        /// <summary>
        /// Current frame number (set by Update).
        /// </summary>
        private int _currentFrame;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of LOD levels.
        /// </summary>
        public int LevelCount => _levelCount;

        /// <summary>
        /// Gets the current frame number.
        /// </summary>
        public int CurrentFrame => _currentFrame;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new LOD manager with the specified number of levels.
        /// </summary>
        /// <param name="levelCount">Number of LOD levels (2-8).</param>
        public CELod(int levelCount = 4)
        {
            if (levelCount < 2) levelCount = 2;
            if (levelCount > 8) levelCount = 8;

            _levelCount = levelCount;
            _minDistances = new float[levelCount];
            _maxDistances = new float[levelCount];
            _minDistancesSq = new float[levelCount];
            _maxDistancesSq = new float[levelCount];
            _updateIntervals = new int[levelCount];
            _currentFrame = 0;

            // Set default levels
            SetDefaultLevels();
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Sets a LOD level's configuration.
        /// </summary>
        /// <param name="level">Level index (0 = closest).</param>
        /// <param name="minDistance">Minimum distance for this level.</param>
        /// <param name="maxDistance">Maximum distance for this level.</param>
        /// <param name="updateInterval">Frames between updates (1 = every frame).</param>
        public void SetLevel(int level, float minDistance, float maxDistance, int updateInterval)
        {
            if (level < 0 || level >= _levelCount) return;

            _minDistances[level] = minDistance;
            _maxDistances[level] = maxDistance;
            _minDistancesSq[level] = minDistance * minDistance;
            _maxDistancesSq[level] = maxDistance * maxDistance;
            _updateIntervals[level] = Mathf.Max(1, updateInterval);
        }

        /// <summary>
        /// Sets default LOD levels with reasonable values.
        /// </summary>
        public void SetDefaultLevels()
        {
            switch (_levelCount)
            {
                case 2:
                    SetLevel(0, 0f, 20f, 1);
                    SetLevel(1, 20f, float.MaxValue, 5);
                    break;

                case 3:
                    SetLevel(0, 0f, 15f, 1);
                    SetLevel(1, 15f, 50f, 3);
                    SetLevel(2, 50f, float.MaxValue, 10);
                    break;

                case 4:
                    SetLevel(0, 0f, 10f, 1);
                    SetLevel(1, 10f, 30f, 3);
                    SetLevel(2, 30f, 100f, 10);
                    SetLevel(3, 100f, float.MaxValue, 30);
                    break;

                default:
                    // For 5+ levels, spread evenly
                    float distStep = 200f / (_levelCount - 1);
                    for (int i = 0; i < _levelCount; i++)
                    {
                        float minDist = i * distStep;
                        float maxDist = (i == _levelCount - 1) ? float.MaxValue : (i + 1) * distStep;
                        int interval = Mathf.Max(1, (int)Mathf.Pow(2, i));
                        SetLevel(i, minDist, maxDist, interval);
                    }
                    break;
            }
        }

        #endregion

        #region Update Checks

        /// <summary>
        /// Updates the current frame counter. Call once per frame.
        /// </summary>
        /// <param name="frameCount">Current frame number (Time.frameCount).</param>
        public void Update(int frameCount)
        {
            _currentFrame = frameCount;
        }

        /// <summary>
        /// Gets the LOD level for a given distance.
        /// </summary>
        /// <param name="distance">Distance to check.</param>
        /// <returns>LOD level index (0 = closest).</returns>
        public int GetLevel(float distance)
        {
            float distSq = distance * distance;

            for (int i = 0; i < _levelCount; i++)
            {
                if (distSq < _maxDistancesSq[i])
                {
                    return i;
                }
            }

            return _levelCount - 1;
        }

        /// <summary>
        /// Gets the LOD level for a distance (using squared distance).
        /// </summary>
        /// <param name="distanceSq">Squared distance to check.</param>
        /// <returns>LOD level index.</returns>
        public int GetLevelFromSqDist(float distanceSq)
        {
            for (int i = 0; i < _levelCount; i++)
            {
                if (distanceSq < _maxDistancesSq[i])
                {
                    return i;
                }
            }

            return _levelCount - 1;
        }

        /// <summary>
        /// Checks if an entity at the given position should update this frame.
        /// </summary>
        /// <param name="referencePos">Reference position (e.g., player).</param>
        /// <param name="entityPos">Entity position.</param>
        /// <returns>True if the entity should update.</returns>
        public bool ShouldUpdate(Vector3 referencePos, Vector3 entityPos)
        {
            float distSq = (entityPos - referencePos).sqrMagnitude;
            int level = GetLevelFromSqDist(distSq);
            int interval = _updateIntervals[level];

            return (_currentFrame % interval) == 0;
        }

        /// <summary>
        /// Checks if an entity at the given distance should update this frame.
        /// </summary>
        /// <param name="distance">Distance to reference.</param>
        /// <returns>True if the entity should update.</returns>
        public bool ShouldUpdate(float distance)
        {
            int level = GetLevel(distance);
            int interval = _updateIntervals[level];

            return (_currentFrame % interval) == 0;
        }

        /// <summary>
        /// Checks if an entity at the given distance should update, with staggering.
        /// Uses entity ID to stagger updates across frames for smoother performance.
        /// </summary>
        /// <param name="distance">Distance to reference.</param>
        /// <param name="entityId">Entity ID for staggering.</param>
        /// <returns>True if the entity should update.</returns>
        public bool ShouldUpdateStaggered(float distance, int entityId)
        {
            int level = GetLevel(distance);
            int interval = _updateIntervals[level];

            return ((_currentFrame + entityId) % interval) == 0;
        }

        /// <summary>
        /// Gets the update interval for a given distance.
        /// </summary>
        /// <param name="distance">Distance to check.</param>
        /// <returns>Frames between updates.</returns>
        public int GetUpdateInterval(float distance)
        {
            int level = GetLevel(distance);
            return _updateIntervals[level];
        }

        #endregion

        #region Utility

        /// <summary>
        /// Gets the minimum distance for a LOD level.
        /// </summary>
        public float GetLevelMinDistance(int level)
        {
            if (level < 0 || level >= _levelCount) return 0f;
            return _minDistances[level];
        }

        /// <summary>
        /// Gets the maximum distance for a LOD level.
        /// </summary>
        public float GetLevelMaxDistance(int level)
        {
            if (level < 0 || level >= _levelCount) return float.MaxValue;
            return _maxDistances[level];
        }

        /// <summary>
        /// Gets the update interval for a LOD level.
        /// </summary>
        public int GetLevelInterval(int level)
        {
            if (level < 0 || level >= _levelCount) return 1;
            return _updateIntervals[level];
        }

        #endregion
    }
}
