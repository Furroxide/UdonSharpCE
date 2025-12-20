using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Strategy for handling divergence between predicted and authoritative state.
    /// </summary>
    [PublicAPI]
    public enum RollbackStrategy
    {
        None = 0,
        SmoothBlend = 1,
        PartialRollback = 2,
        FullRollback = 3,
        Snap = 4,
    }
}

