using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Heuristic selection of rollback strategy based on error and age of the authoritative update.
    /// </summary>
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

