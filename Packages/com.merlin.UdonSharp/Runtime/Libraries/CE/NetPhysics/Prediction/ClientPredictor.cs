using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// High-level client prediction helper that couples a world, history, and rollback manager.
    /// </summary>
    [PublicAPI]
    public class ClientPredictor
    {
        private readonly NetPhysicsWorld _world;
        private readonly FrameHistory _history;
        private readonly RollbackManager _rollback;

        public FrameHistory History => _history;
        public RollbackManager Rollback => _rollback;

        public ClientPredictor(NetPhysicsWorld world, FrameHistory history)
        {
            _world = world;
            _history = history;
            _rollback = new RollbackManager();
            _rollback.Initialize(world, history);
        }

        public void RecordState(int frame, PhysicsSnapshot state, InputFrame input)
        {
            _history.Record(frame, state, input);
        }

        /// <summary>
        /// Processes authoritative server state and performs rollback if divergence is detected.
        /// </summary>
        public void ReceiveServerState(int frame, PhysicsSnapshot serverState)
        {
            _rollback.ProcessServerState(frame, serverState);
        }

        public bool NeedsRollback(PhysicsSnapshot local, PhysicsSnapshot server)
        {
            if (local == null || server == null)
                return true;

            return local.CalculateDivergence(server) > _rollback.PositionThreshold;
        }
    }
}
