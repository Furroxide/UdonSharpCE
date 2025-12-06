using System;
using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Strategy for resolving sync conflicts.
    /// </summary>
    [PublicAPI]
    public enum MergeStrategy
    {
        /// <summary>
        /// Last received value wins (default VRChat behavior).
        /// </summary>
        LastWriteWins,

        /// <summary>
        /// Owner's value always wins.
        /// </summary>
        OwnerWins,

        /// <summary>
        /// Master's value always wins.
        /// </summary>
        MasterWins,

        /// <summary>
        /// Higher numeric value wins.
        /// </summary>
        HigherWins,

        /// <summary>
        /// Lower numeric value wins.
        /// </summary>
        LowerWins,

        /// <summary>
        /// Values are added together.
        /// </summary>
        Additive,

        /// <summary>
        /// Custom resolver callback is used.
        /// </summary>
        Custom
    }

    /// <summary>
    /// Result of an ownership request.
    /// </summary>
    [PublicAPI]
    public enum OwnershipResult
    {
        /// <summary>
        /// Ownership was granted.
        /// </summary>
        Granted,

        /// <summary>
        /// Already the owner.
        /// </summary>
        AlreadyOwner,

        /// <summary>
        /// Request was denied by current owner.
        /// </summary>
        Denied,

        /// <summary>
        /// Object doesn't support ownership transfer.
        /// </summary>
        NotSupported,

        /// <summary>
        /// Request is pending.
        /// </summary>
        Pending
    }

    /// <summary>
    /// Attribute to specify merge strategy for a synced field.
    /// </summary>
    /// <remarks>
    /// When network conditions cause conflicting values,
    /// the specified strategy determines which value is kept.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class ScoreManager : UdonSharpBehaviour
    /// {
    ///     [UdonSynced]
    ///     [MergeStrategy(MergeStrategy.HigherWins)]
    ///     public int highScore;
    ///
    ///     [UdonSynced]
    ///     [MergeStrategy(MergeStrategy.Additive)]
    ///     public int totalCoins;
    ///
    ///     [UdonSynced]
    ///     [MergeStrategy(MergeStrategy.MasterWins)]
    ///     public int gamePhase;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class MergeStrategyAttribute : Attribute
    {
        /// <summary>
        /// The merge strategy to use.
        /// </summary>
        public MergeStrategy Strategy { get; }

        /// <summary>
        /// Name of custom resolver method if Strategy is Custom.
        /// Method signature: T Resolve(T local, T remote)
        /// </summary>
        public string CustomResolverMethod { get; set; }

        public MergeStrategyAttribute(MergeStrategy strategy)
        {
            Strategy = strategy;
        }
    }

    /// <summary>
    /// Utilities for resolving network synchronization conflicts.
    ///
    /// Provides methods for ownership management and value conflict resolution.
    /// </summary>
    /// <remarks>
    /// VRChat's networking uses eventual consistency, which can lead to
    /// conflicting values when multiple players modify data simultaneously.
    /// This class provides utilities to handle these conflicts gracefully.
    ///
    /// Common patterns:
    /// - Use OwnerWins for single-authority objects (e.g., player's own data)
    /// - Use MasterWins for game state (e.g., game phase, rules)
    /// - Use HigherWins for scores, achievements
    /// - Use Additive for counters, cumulative values
    /// </remarks>
    [PublicAPI]
    public static class ConflictResolver
    {
        #region Ownership Management

        /// <summary>
        /// Attempts to take ownership of a behaviour.
        /// </summary>
        /// <param name="behaviour">The behaviour to take ownership of.</param>
        /// <returns>Whether ownership was successfully taken or already owned.</returns>
        public static bool TakeOwnership(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return false;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            if (Networking.IsOwner(localPlayer, behaviour.gameObject))
                return true;

            Networking.SetOwner(localPlayer, behaviour.gameObject);
            return Networking.IsOwner(localPlayer, behaviour.gameObject);
        }

        /// <summary>
        /// Attempts to take ownership of a GameObject.
        /// </summary>
        public static bool TakeOwnership(GameObject target)
        {
            if (target == null)
                return false;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            if (Networking.IsOwner(localPlayer, target))
                return true;

            Networking.SetOwner(localPlayer, target);
            return Networking.IsOwner(localPlayer, target);
        }

        /// <summary>
        /// Checks if the local player owns a behaviour.
        /// </summary>
        public static bool IsLocalOwner(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return false;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            return Networking.IsOwner(localPlayer, behaviour.gameObject);
        }

        /// <summary>
        /// Checks if the local player owns a GameObject.
        /// </summary>
        public static bool IsLocalOwner(GameObject target)
        {
            if (target == null)
                return false;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            return Networking.IsOwner(localPlayer, target);
        }

        /// <summary>
        /// Gets the owner of a behaviour.
        /// </summary>
        public static VRCPlayerApi GetOwner(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return null;

            return Networking.GetOwner(behaviour.gameObject);
        }

        /// <summary>
        /// Transfers ownership to a specific player.
        /// Only works if local player is current owner.
        /// </summary>
        public static bool TransferOwnership(UdonSharpBehaviour behaviour, VRCPlayerApi newOwner)
        {
            if (behaviour == null || newOwner == null)
                return false;

            if (!IsLocalOwner(behaviour))
                return false;

            Networking.SetOwner(newOwner, behaviour.gameObject);
            return true;
        }

        #endregion

        #region Value Resolution - Integers

        /// <summary>
        /// Resolves an integer conflict using the specified strategy.
        /// </summary>
        public static int ResolveInt(int local, int remote, MergeStrategy strategy)
        {
            switch (strategy)
            {
                case MergeStrategy.LastWriteWins:
                    return remote;

                case MergeStrategy.OwnerWins:
                case MergeStrategy.MasterWins:
                    // These need context - caller should handle
                    return remote;

                case MergeStrategy.HigherWins:
                    return local > remote ? local : remote;

                case MergeStrategy.LowerWins:
                    return local < remote ? local : remote;

                case MergeStrategy.Additive:
                    return local + remote;

                default:
                    return remote;
            }
        }

        /// <summary>
        /// Resolves an integer conflict with ownership context.
        /// </summary>
        public static int ResolveInt(
            int local,
            int remote,
            MergeStrategy strategy,
            bool isLocalOwner,
            bool isLocalMaster)
        {
            switch (strategy)
            {
                case MergeStrategy.OwnerWins:
                    return isLocalOwner ? local : remote;

                case MergeStrategy.MasterWins:
                    return isLocalMaster ? local : remote;

                default:
                    return ResolveInt(local, remote, strategy);
            }
        }

        #endregion

        #region Value Resolution - Floats

        /// <summary>
        /// Resolves a float conflict using the specified strategy.
        /// </summary>
        public static float ResolveFloat(float local, float remote, MergeStrategy strategy)
        {
            switch (strategy)
            {
                case MergeStrategy.LastWriteWins:
                    return remote;

                case MergeStrategy.HigherWins:
                    return local > remote ? local : remote;

                case MergeStrategy.LowerWins:
                    return local < remote ? local : remote;

                case MergeStrategy.Additive:
                    return local + remote;

                default:
                    return remote;
            }
        }

        /// <summary>
        /// Resolves a float conflict with ownership context.
        /// </summary>
        public static float ResolveFloat(
            float local,
            float remote,
            MergeStrategy strategy,
            bool isLocalOwner,
            bool isLocalMaster)
        {
            switch (strategy)
            {
                case MergeStrategy.OwnerWins:
                    return isLocalOwner ? local : remote;

                case MergeStrategy.MasterWins:
                    return isLocalMaster ? local : remote;

                default:
                    return ResolveFloat(local, remote, strategy);
            }
        }

        #endregion

        #region Value Resolution - Vectors

        /// <summary>
        /// Resolves a Vector3 conflict using the specified strategy.
        /// For HigherWins/LowerWins, uses magnitude.
        /// </summary>
        public static Vector3 ResolveVector3(Vector3 local, Vector3 remote, MergeStrategy strategy)
        {
            switch (strategy)
            {
                case MergeStrategy.LastWriteWins:
                    return remote;

                case MergeStrategy.HigherWins:
                    return local.sqrMagnitude > remote.sqrMagnitude ? local : remote;

                case MergeStrategy.LowerWins:
                    return local.sqrMagnitude < remote.sqrMagnitude ? local : remote;

                case MergeStrategy.Additive:
                    return local + remote;

                default:
                    return remote;
            }
        }

        /// <summary>
        /// Resolves a Vector3 conflict with ownership context.
        /// </summary>
        public static Vector3 ResolveVector3(
            Vector3 local,
            Vector3 remote,
            MergeStrategy strategy,
            bool isLocalOwner,
            bool isLocalMaster)
        {
            switch (strategy)
            {
                case MergeStrategy.OwnerWins:
                    return isLocalOwner ? local : remote;

                case MergeStrategy.MasterWins:
                    return isLocalMaster ? local : remote;

                default:
                    return ResolveVector3(local, remote, strategy);
            }
        }

        #endregion

        #region Value Resolution - Booleans

        /// <summary>
        /// Resolves a boolean conflict.
        /// HigherWins = prefer true, LowerWins = prefer false.
        /// </summary>
        public static bool ResolveBool(bool local, bool remote, MergeStrategy strategy)
        {
            switch (strategy)
            {
                case MergeStrategy.LastWriteWins:
                    return remote;

                case MergeStrategy.HigherWins:
                    return local || remote;

                case MergeStrategy.LowerWins:
                    return local && remote;

                default:
                    return remote;
            }
        }

        /// <summary>
        /// Resolves a boolean conflict with ownership context.
        /// </summary>
        public static bool ResolveBool(
            bool local,
            bool remote,
            MergeStrategy strategy,
            bool isLocalOwner,
            bool isLocalMaster)
        {
            switch (strategy)
            {
                case MergeStrategy.OwnerWins:
                    return isLocalOwner ? local : remote;

                case MergeStrategy.MasterWins:
                    return isLocalMaster ? local : remote;

                default:
                    return ResolveBool(local, remote, strategy);
            }
        }

        #endregion

        #region Value Resolution - Strings

        /// <summary>
        /// Resolves a string conflict.
        /// HigherWins = longer string, LowerWins = shorter string.
        /// </summary>
        public static string ResolveString(string local, string remote, MergeStrategy strategy)
        {
            local = local ?? "";
            remote = remote ?? "";

            switch (strategy)
            {
                case MergeStrategy.LastWriteWins:
                    return remote;

                case MergeStrategy.HigherWins:
                    return local.Length > remote.Length ? local : remote;

                case MergeStrategy.LowerWins:
                    return local.Length < remote.Length ? local : remote;

                case MergeStrategy.Additive:
                    return local + remote;

                default:
                    return remote;
            }
        }

        /// <summary>
        /// Resolves a string conflict with ownership context.
        /// </summary>
        public static string ResolveString(
            string local,
            string remote,
            MergeStrategy strategy,
            bool isLocalOwner,
            bool isLocalMaster)
        {
            switch (strategy)
            {
                case MergeStrategy.OwnerWins:
                    return isLocalOwner ? local : remote;

                case MergeStrategy.MasterWins:
                    return isLocalMaster ? local : remote;

                default:
                    return ResolveString(local, remote, strategy);
            }
        }

        #endregion

        #region Array Resolution

        /// <summary>
        /// Resolves an integer array conflict using element-wise strategy.
        /// </summary>
        public static int[] ResolveIntArray(int[] local, int[] remote, MergeStrategy strategy)
        {
            if (local == null) return remote;
            if (remote == null) return local;

            int length = Mathf.Max(local.Length, remote.Length);
            int[] result = new int[length];

            for (int i = 0; i < length; i++)
            {
                int localVal = i < local.Length ? local[i] : 0;
                int remoteVal = i < remote.Length ? remote[i] : 0;
                result[i] = ResolveInt(localVal, remoteVal, strategy);
            }

            return result;
        }

        /// <summary>
        /// Resolves a float array conflict using element-wise strategy.
        /// </summary>
        public static float[] ResolveFloatArray(float[] local, float[] remote, MergeStrategy strategy)
        {
            if (local == null) return remote;
            if (remote == null) return local;

            int length = Mathf.Max(local.Length, remote.Length);
            float[] result = new float[length];

            for (int i = 0; i < length; i++)
            {
                float localVal = i < local.Length ? local[i] : 0f;
                float remoteVal = i < remote.Length ? remote[i] : 0f;
                result[i] = ResolveFloat(localVal, remoteVal, strategy);
            }

            return result;
        }

        /// <summary>
        /// Resolves a boolean array conflict using element-wise strategy.
        /// </summary>
        public static bool[] ResolveBoolArray(bool[] local, bool[] remote, MergeStrategy strategy)
        {
            if (local == null) return remote;
            if (remote == null) return local;

            int length = Mathf.Max(local.Length, remote.Length);
            bool[] result = new bool[length];

            for (int i = 0; i < length; i++)
            {
                bool localVal = i < local.Length && local[i];
                bool remoteVal = i < remote.Length && remote[i];
                result[i] = ResolveBool(localVal, remoteVal, strategy);
            }

            return result;
        }

        #endregion
    }
}
