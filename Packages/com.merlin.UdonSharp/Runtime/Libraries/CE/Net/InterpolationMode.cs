using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Interpolation modes for synced variables.
    /// Extends UdonSyncMode with additional options for smoother synchronization.
    /// </summary>
    [PublicAPI]
    public enum InterpolationMode
    {
        /// <summary>
        /// No interpolation. Values snap to the latest synced value.
        /// Maps to UdonSyncMode.None.
        /// </summary>
        None = 0,

        /// <summary>
        /// Linear interpolation between synced values.
        /// Good for position/rotation with constant velocity.
        /// Maps to UdonSyncMode.Linear.
        /// </summary>
        Linear = 1,

        /// <summary>
        /// Smooth interpolation with easing.
        /// Good for general-purpose smoothing.
        /// Maps to UdonSyncMode.Smooth.
        /// </summary>
        Smooth = 2
    }
}
