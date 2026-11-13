using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Classification of a divergence between predicted and authoritative state, as returned by
    /// <see cref="RollbackDecider"/>.
    /// </summary>
    /// <remarks>
    /// <c>PartialRollback</c> and <c>FullRollback</c> name a rewind-and-replay that Udon cannot perform
    /// (it has no way to step PhysX), so no code path acts on them: every correction this library
    /// applies is either eased in or taken at once.
    /// </remarks>
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

