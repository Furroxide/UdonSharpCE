using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Handles client prediction corrections via rollback and resimulation.
    /// </summary>
    [PublicAPI]
    public class RollbackManager
    {
        private NetPhysicsWorld _world;
        private FrameHistory _history;

        private readonly RollbackDecider _decider = new RollbackDecider();
        private InputFrame[] _tempInputs;

        public float PositionThreshold = 0.05f;
        public int MaxRollbackFrames = 30;

        public bool IsRollingBack { get; private set; }
        public int RollbackFrame { get; private set; }

        public void Initialize(NetPhysicsWorld world, FrameHistory history)
        {
            _world = world;
            _history = history;
            _tempInputs = new InputFrame[Mathf.Max(1, MaxRollbackFrames + 4)];
            for (int i = 0; i < _tempInputs.Length; i++)
                _tempInputs[i] = new InputFrame();
        }

        /// <summary>
        /// Processes authoritative server state and performs rollback if needed.
        /// This is a synchronous operation that completes within a single frame.
        /// </summary>
        public void ProcessServerState(int serverFrame, PhysicsSnapshot serverState)
        {
            if (_world == null || _history == null || serverState == null)
                return;

            // None of these paths touch the frame counter any more. The counter is derived from the
            // server clock, so rewriting it to chase an incoming packet only pushed this client back
            // out of its own history window: the next packet then missed again, snapped again, and the
            // world sat in a permanent snap-per-packet loop with a warning on every one.
            PhysicsSnapshot localState = _history.GetState(serverFrame);
            if (localState == null)
            {
                // The packet describes a frame this client no longer holds -- it hitched, or the state
                // took an unusually long trip. Nothing to resimulate against, so correct and move on.
                serverState.ApplyAsCorrection(_world.Entities, _world.EntityCount);
                return;
            }

            float positionError = localState.CalculateDivergence(serverState);
            int framesOld = (_world.CurrentFrame - 1) - serverFrame;

            if (positionError < PositionThreshold)
                return;

            if (framesOld > MaxRollbackFrames)
            {
                serverState.ApplyAsCorrection(_world.Entities, _world.EntityCount);
                return;
            }

            RollbackStrategy strategy = _decider.Decide(positionError, 0f, framesOld);
            if (strategy == RollbackStrategy.None)
                return;

            if (strategy == RollbackStrategy.Snap || strategy == RollbackStrategy.SmoothBlend)
            {
                // Small divergences are not worth resimulating the whole window for; the entity eases
                // them in. Large ones the entity turns into the hard snap they need to be.
                serverState.ApplyAsCorrection(_world.Entities, _world.EntityCount);
                return;
            }

            PerformRollback(serverFrame, serverState);
        }

        private void PerformRollback(int serverFrame, PhysicsSnapshot serverState)
        {
            if (IsRollingBack)
                return;

            IsRollingBack = true;
            RollbackFrame = serverFrame;

            int presentFrame = _world.CurrentFrame;

            // 1) Restore authoritative state at serverFrame. This one stays an exact write: the
            //    resimulation below has to start from the authoritative state, not from a state that is
            //    still easing toward it.
            serverState.Restore(_world.Entities, _world.EntityCount);

            // 2) Resimulate from serverFrame+1 up to presentFrame-1
            _world.SetCurrentFrameInternal(serverFrame + 1);

            int inputCount = _history.GetInputRange(serverFrame + 1, presentFrame - 1, _tempInputs);
            for (int i = 0; i < inputCount; i++)
            {
                _world.SimulateSingleTick(_tempInputs[i]);
            }

            // Ensure we end at the same frame we started from.
            _world.SetCurrentFrameInternal(presentFrame);

            IsRollingBack = false;
        }
    }
}
