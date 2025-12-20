using JetBrains.Annotations;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Ring buffer storing snapshots and local inputs for rollback.
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
            int index = Mod(frame, _capacity);

            _snapshots[index].CopyFrom(state);
            _snapshots[index].Frame = frame;
            _inputs[index] = input;

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
                return default;

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
                    output[written] = _inputs[Mod(f, _capacity)];
                    written++;
                }
            }

            return written;
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

