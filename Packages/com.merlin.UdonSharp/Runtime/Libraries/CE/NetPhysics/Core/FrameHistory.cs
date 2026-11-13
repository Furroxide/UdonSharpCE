using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Ring buffer of recent simulation frames: this client's predicted state and local input for each.
    /// Intended to be allocation-free during gameplay.
    /// </summary>
    [PublicAPI]
    public class FrameHistory
    {
        private readonly int _capacity;
        private readonly PhysicsSnapshot[] _snapshots;
        private readonly InputFrame[] _inputs;

        private int _count;
        private int _oldestFrame;

        public int Capacity => _capacity;
        public int Count => _count;
        public int OldestFrame => _oldestFrame;
        public int NewestFrame => _count == 0 ? _oldestFrame : _oldestFrame + _count - 1;

        public FrameHistory(int capacity, int maxEntities)
        {
            _capacity = capacity > 0 ? capacity : 1;

            _snapshots = new PhysicsSnapshot[_capacity];
            _inputs = new InputFrame[_capacity];

            for (int i = 0; i < _capacity; i++)
            {
                _snapshots[i] = new PhysicsSnapshot(maxEntities);
                _inputs[i] = new InputFrame();
            }

            _count = 0;
            _oldestFrame = 0;
        }

        public void Clear()
        {
            _count = 0;
            _oldestFrame = 0;
        }

        public void Record(int frame, PhysicsSnapshot state, InputFrame input)
        {
            // A frame the window already holds is replaced in place. It used to fall through to the
            // append bookkeeping below, so every re-recorded frame grew the count or pushed the oldest
            // frame forward although no new frame had arrived. A handful of re-recorded runs slid the
            // window into the future, after which no incoming packet named a frame it would admit to
            // holding and every one of them was treated as having no history at all.
            bool rewrite = _count > 0 && frame >= _oldestFrame && frame <= NewestFrame;

            // A frame that neither rewrites nor directly extends the window breaks the run. Slots are
            // addressed by frame number, so keeping the old bounds across a gap would leave the window
            // claiming frames whose slots still hold data from a whole lap earlier.
            if (!rewrite && _count > 0 && frame != NewestFrame + 1)
                _count = 0;

            int index = Mod(frame, _capacity);

            _snapshots[index].CopyFrom(state);
            _snapshots[index].Frame = frame;
            _inputs[index].CopyFrom(input);

            if (rewrite)
                return;

            if (_count == 0)
            {
                _oldestFrame = frame;
                _count = 1;
                return;
            }

            if (_count < _capacity)
            {
                _count++;
            }
            else
            {
                // Overwrite oldest
                _oldestFrame++;
            }
        }

        public PhysicsSnapshot GetState(int frame)
        {
            if (!ContainsFrame(frame))
                return null;

            return _snapshots[Mod(frame, _capacity)];
        }

        public PhysicsSnapshot GetStateUnchecked(int frame)
        {
            return _snapshots[Mod(frame, _capacity)];
        }

        public InputFrame GetInput(int frame)
        {
            if (!ContainsFrame(frame))
                return InputFrame.Empty;

            return _inputs[Mod(frame, _capacity)];
        }

        public int GetInputRange(int startFrame, int endFrame, InputFrame[] output)
        {
            if (output == null || _count == 0)
                return 0;

            int written = 0;
            for (int f = startFrame; f <= endFrame && written < output.Length; f++)
            {
                if (ContainsFrame(f))
                {
                    if (output[written] == null)
                        output[written] = new InputFrame();
                    output[written].CopyFrom(_inputs[Mod(f, _capacity)]);
                    written++;
                }
            }

            return written;
        }

        /// <summary>
        /// Shifts one entity's recorded state in every held frame from <paramref name="fromFrame"/> on,
        /// and returns how many frames were touched.
        /// </summary>
        /// <remarks>
        /// A correction measured against frame F is applied to the entity as it stands now. The frames
        /// recorded between F and now still describe the uncorrected prediction, and the next packet is
        /// compared against one of them: left alone, it would measure the same error again and the
        /// correction would be applied a second time, then a third, once per packet until the packets
        /// caught up with the present. Moving the recorded frames by the same amount is what makes one
        /// error cost exactly one correction.
        /// </remarks>
        public int OffsetEntity(int entityId, int fromFrame, Vector3 positionDelta, Quaternion rotationDelta, Vector3 velocityDelta, Vector3 angularVelocityDelta)
        {
            if (_count == 0)
                return 0;

            int newest = NewestFrame;
            int start = fromFrame > _oldestFrame ? fromFrame : _oldestFrame;
            int shifted = 0;

            for (int f = start; f <= newest; f++)
            {
                PhysicsSnapshot snapshot = _snapshots[Mod(f, _capacity)];

                int index = snapshot.IndexOfEntity(entityId);
                if (index < 0)
                    continue;

                snapshot.OffsetEntity(index, positionDelta, rotationDelta, velocityDelta, angularVelocityDelta);
                shifted++;
            }

            return shifted;
        }

        private bool ContainsFrame(int frame)
        {
            if (_count == 0)
                return false;

            return frame >= _oldestFrame && frame <= NewestFrame;
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}
