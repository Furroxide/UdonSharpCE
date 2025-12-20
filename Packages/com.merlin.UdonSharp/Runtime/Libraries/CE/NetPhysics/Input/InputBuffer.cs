using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Server-side input buffering with simple throttle control.
    /// Buffers smooth packet jitter by repeating or consuming extra inputs.
    /// </summary>
    [PublicAPI]
    public class InputBuffer
    {
        // VRChat instances can have up to ~80 players; playerId values are typically 1..MaxPlayers.
        public const int DefaultMaxPlayers = 80;
        public const int DefaultBufferCapacity = 32;

        [Header("Buffer Targets")]
        public int TargetBufferSize = 3;
        public int MinBufferSize = 1;
        public int MaxBufferSize = 8;
        public ThrottleMode Mode = ThrottleMode.Downstream;

        private readonly int _maxPlayers;
        private readonly int _capacity;

        private readonly InputFrame[][] _playerBuffers;
        private readonly int[] _counts;

        private readonly int[] _lastConsumedFrame;
        private readonly InputFrame[] _lastConsumedInput;
        private readonly int[] _throttleHints;

        public InputBuffer(int maxPlayers = DefaultMaxPlayers, int capacity = DefaultBufferCapacity)
        {
            _maxPlayers = Mathf.Max(1, maxPlayers);
            _capacity = Mathf.Max(1, capacity);

            _playerBuffers = new InputFrame[_maxPlayers][];
            for (int i = 0; i < _maxPlayers; i++)
                _playerBuffers[i] = new InputFrame[_capacity];

            _counts = new int[_maxPlayers];
            _lastConsumedFrame = new int[_maxPlayers];
            _lastConsumedInput = new InputFrame[_maxPlayers];
            _throttleHints = new int[_maxPlayers];

            for (int i = 0; i < _maxPlayers; i++)
                _lastConsumedFrame[i] = int.MinValue;
        }

        public int GetBufferSize(int playerId) => IsValidPlayer(playerId) ? _counts[playerId] : 0;

        public int GetThrottleHint(int playerId) => IsValidPlayer(playerId) ? _throttleHints[playerId] : 0;

        public void EnqueueInput(int playerId, InputFrame input)
        {
            if (!IsValidPlayer(playerId))
                return;

            if (input.FrameNumber <= _lastConsumedFrame[playerId])
                return;

            var buffer = _playerBuffers[playerId];
            int count = _counts[playerId];

            // Drop if full and too old to be useful.
            if (count >= _capacity)
            {
                // If the incoming input is older than our oldest buffered input, ignore.
                if (input.FrameNumber <= buffer[0].FrameNumber)
                {
                    UpdateThrottleHint(playerId);
                    return;
                }

                // Otherwise drop the oldest to make room.
                for (int i = 1; i < count; i++)
                    buffer[i - 1] = buffer[i];
                count--;
            }

            // Find insertion point (keep ascending order).
            int insertIndex = count;
            for (int i = 0; i < count; i++)
            {
                int existingFrame = buffer[i].FrameNumber;

                // Replace if duplicate frame.
                if (existingFrame == input.FrameNumber)
                {
                    buffer[i] = input;
                    _counts[playerId] = count;
                    UpdateThrottleHint(playerId);
                    return;
                }

                if (existingFrame > input.FrameNumber)
                {
                    insertIndex = i;
                    break;
                }
            }

            // Shift and insert.
            for (int i = count; i > insertIndex; i--)
                buffer[i] = buffer[i - 1];

            buffer[insertIndex] = input;
            _counts[playerId] = count + 1;

            UpdateThrottleHint(playerId);
        }

        public void EnqueueInputs(int playerId, InputFrame[] inputs, int inputCount)
        {
            if (!IsValidPlayer(playerId) || inputs == null)
                return;

            int count = Mathf.Clamp(inputCount, 0, inputs.Length);
            for (int i = 0; i < count; i++)
                EnqueueInput(playerId, inputs[i]);
        }

        /// <summary>
        /// Returns the next buffered input without removing it.
        /// </summary>
        public InputFrame PeekInput(int playerId)
        {
            if (!IsValidPlayer(playerId) || _counts[playerId] <= 0)
                return _lastConsumedInput[playerId];

            return _playerBuffers[playerId][0];
        }

        /// <summary>
        /// Removes and returns the next buffered input.
        /// Returns the last consumed input if the buffer is empty.
        /// </summary>
        public InputFrame DequeueInput(int playerId)
        {
            if (!IsValidPlayer(playerId) || _counts[playerId] <= 0)
                return _lastConsumedInput[playerId];

            var buffer = _playerBuffers[playerId];
            int count = _counts[playerId];

            InputFrame input = buffer[0];
            for (int i = 1; i < count; i++)
                buffer[i - 1] = buffer[i];

            _counts[playerId] = count - 1;

            _lastConsumedFrame[playerId] = input.FrameNumber;
            _lastConsumedInput[playerId] = input;

            UpdateThrottleHint(playerId);

            return input;
        }

        /// <summary>
        /// Gets input for a player for the current tick.
        /// Depending on throttle state, may repeat or consume extra inputs.
        /// </summary>
        public InputFrame ConsumeInput(int playerId, int currentFrame)
        {
            if (!IsValidPlayer(playerId))
                return default;

            int count = _counts[playerId];
            if (count <= 0)
                return _lastConsumedInput[playerId];

            // Upstream: server consumes normally; client adjusts its send/sim rate.
            if (Mode == ThrottleMode.Upstream)
                return DequeueInput(playerId);

            int hint = _throttleHints[playerId];

            // Buffer low: repeat without consuming (consume 0).
            if (hint < 0 && count > 0)
                return _playerBuffers[playerId][0];

            // Buffer high: consume one extra if we can.
            if (hint > 0 && count > 1)
                DequeueInput(playerId);

            return DequeueInput(playerId);
        }

        private void UpdateThrottleHint(int playerId)
        {
            int count = _counts[playerId];
            int target = Mathf.Max(1, TargetBufferSize);

            int lowThreshold = Mathf.Max(1, target - 1);
            int highThreshold = target + 2;

            if (count < lowThreshold && count >= 0)
                _throttleHints[playerId] = -1;
            else if (count > highThreshold)
                _throttleHints[playerId] = 1;
            else
                _throttleHints[playerId] = 0;

            // Clamp into configured min/max range for safety.
            if (count < MinBufferSize)
                _throttleHints[playerId] = -1;
            if (count > MaxBufferSize)
                _throttleHints[playerId] = 1;
        }

        private bool IsValidPlayer(int playerId) => playerId >= 0 && playerId < _maxPlayers;
    }
}
