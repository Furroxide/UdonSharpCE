using JetBrains.Annotations;
using UdonSharp.CE.DevTools;
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
        }

        /// <summary>
        /// Processes authoritative server state and performs rollback if needed.
        /// This is a synchronous operation that completes within a single frame.
        /// </summary>
        public void ProcessServerState(int serverFrame, PhysicsSnapshot serverState)
        {
            if (_world == null || _history == null || serverState == null)
                return;

            PhysicsSnapshot localState = _history.GetState(serverFrame);
            if (localState == null)
            {
                // Too old - snap.
                CELogger.Warning("NetPhysics", $"Server frame {serverFrame} not in history; snapping.");
                serverState.Restore(_world.Entities, _world.EntityCount);
                _world.SetCurrentFrameInternal(serverFrame + 1);
                return;
            }

            float positionError = localState.CalculateDivergence(serverState);
            int framesOld = (_world.CurrentFrame - 1) - serverFrame;

            if (positionError < PositionThreshold)
                return;

            if (framesOld > MaxRollbackFrames)
            {
                // Too far behind - snap.
                CELogger.Warning("NetPhysics", $"Server frame {serverFrame} is {framesOld} frames old; snapping.");
                serverState.Restore(_world.Entities, _world.EntityCount);
                _world.SetCurrentFrameInternal(serverFrame + 1);
                return;
            }

            RollbackStrategy strategy = _decider.Decide(positionError, 0f, framesOld);
            if (strategy == RollbackStrategy.None)
                return;

            if (strategy == RollbackStrategy.Snap)
            {
                serverState.Restore(_world.Entities, _world.EntityCount);
                _world.SetCurrentFrameInternal(serverFrame + 1);
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

            // 1) Restore authoritative state at serverFrame
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

