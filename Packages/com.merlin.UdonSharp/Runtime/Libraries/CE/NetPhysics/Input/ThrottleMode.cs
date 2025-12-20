using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Input throttling mode.
    /// </summary>
    [PublicAPI]
    public enum ThrottleMode
    {
        /// <summary>
        /// Client adjusts its send/sim rate based on server hints.
        /// </summary>
        Upstream = 0,

        /// <summary>
        /// Server adjusts input consumption rate (repeats/consumes extra).
        /// </summary>
        Downstream = 1,
    }
}

