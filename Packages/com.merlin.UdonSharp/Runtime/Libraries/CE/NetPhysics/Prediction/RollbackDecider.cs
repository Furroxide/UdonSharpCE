using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Classifies a divergence by its size and the age of the authoritative update.
    /// </summary>
    /// <remarks>
    /// A standalone classifier: nothing in this library calls it. <see cref="RollbackManager"/>
    /// measures each entity's error against its own speed-aware tolerance, and the entity itself
    /// chooses between easing a correction in and jumping (<c>CorrectionSnapDistance</c>). The two
    /// rollback results describe a rewind-and-replay that cannot be performed from Udon.
    /// </remarks>
    [PublicAPI]
    public class RollbackDecider
    {
        // Thresholds (meters)
        public float IgnoreThreshold = 0.02f;
        public float BlendThreshold = 0.10f;
        public float PartialThreshold = 0.30f;
        public float FullThreshold = 1.00f;

        public int SnapAfterFramesOld = 30;

        public RollbackStrategy Decide(float positionError, float velocityError, int framesOld)
        {
            if (framesOld > SnapAfterFramesOld)
                return RollbackStrategy.Snap;

            if (positionError < IgnoreThreshold)
                return RollbackStrategy.None;

            if (positionError < BlendThreshold)
                return RollbackStrategy.SmoothBlend;

            if (positionError < PartialThreshold)
                return RollbackStrategy.PartialRollback;

            if (positionError < FullThreshold)
                return RollbackStrategy.FullRollback;

            return RollbackStrategy.Snap;
        }
    }
}

