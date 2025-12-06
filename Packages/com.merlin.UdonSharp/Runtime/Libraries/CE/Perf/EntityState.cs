using JetBrains.Annotations;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// State of an entity in the ECS-Lite system.
    /// </summary>
    [PublicAPI]
    public enum EntityState
    {
        /// <summary>
        /// Entity slot is available for reuse.
        /// </summary>
        Free = 0,

        /// <summary>
        /// Entity is active and can have components.
        /// </summary>
        Active = 1,

        /// <summary>
        /// Entity is marked for destruction at end of frame.
        /// </summary>
        PendingDestroy = 2,

        /// <summary>
        /// Entity is disabled but retains its components.
        /// Disabled entities are skipped by systems.
        /// </summary>
        Disabled = 3
    }
}
