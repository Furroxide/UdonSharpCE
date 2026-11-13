using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Server-side input buffering with simple throttle control.
    /// Buffers smooth packet jitter by repeating or consuming extra inputs.
    /// </summary>
    /// <remarks>
    /// Player ids grow across joins and leaves. Slots remember the full id and probe for a free
    /// entry when ids share a hash bucket, so active players 1 and 81 keep independent input.
    /// RemovePlayer releases departed players; a full table drops a new arrival instead of
    /// stealing an active player's history.
    /// </remarks>
    [PublicAPI]
    public class InputBuffer
    {
        // Slots, not a ceiling on player ids -- see the remarks above.
        public const int DefaultMaxPlayers = 80;
        public const int DefaultBufferCapacity = 32;

        // Share of the newest sample folded into the input lag estimate per tick.
        private const float InputLagSmoothing = 0.1f;

        // Lag past which the estimate is reported as unknown. It only climbs this far when a driver has
        // stopped sending and the last input is being repeated, and it has to fit the byte it is sent in.
        private const int MaxInputLagFrames = 254;

        [Header("Buffer Targets")]
        public int TargetBufferSize = 3;
        public int MinBufferSize = 1;
        public int MaxBufferSize = 8;
        public ThrottleMode Mode = ThrottleMode.Downstream;

        private readonly int _maxPlayers;
        private readonly int _capacity;

        private readonly int[] _slotPlayerIds;
        private readonly InputFrame[][] _playerBuffers;
        private readonly int[] _counts;

        private readonly int[] _lastConsumedFrame;
        private readonly InputFrame[] _lastConsumedInput;
        private readonly int[] _throttleHints;

        private readonly float[] _inputLag;
        private readonly bool[] _hasInputLag;

        public InputBuffer(int maxPlayers = DefaultMaxPlayers, int capacity = DefaultBufferCapacity)
        {
            _maxPlayers = Mathf.Max(1, maxPlayers);
            _capacity = Mathf.Max(1, capacity);

            _playerBuffers = new InputFrame[_maxPlayers][];
            for (int i = 0; i < _maxPlayers; i++)
            {
                _playerBuffers[i] = new InputFrame[_capacity];
                for (int j = 0; j < _capacity; j++)
                    _playerBuffers[i][j] = new InputFrame();
            }

            _slotPlayerIds = new int[_maxPlayers];
            _counts = new int[_maxPlayers];
            _lastConsumedFrame = new int[_maxPlayers];
            _lastConsumedInput = new InputFrame[_maxPlayers];
            _throttleHints = new int[_maxPlayers];
            _inputLag = new float[_maxPlayers];
            _hasInputLag = new bool[_maxPlayers];

            for (int i = 0; i < _maxPlayers; i++)
            {
                _slotPlayerIds[i] = -1;
                _lastConsumedFrame[i] = int.MinValue;
                _lastConsumedInput[i] = new InputFrame();
            }
        }

        public int GetBufferSize(int playerId)
        {
            int slot = FindSlot(playerId);
            return slot >= 0 ? _counts[slot] : 0;
        }

        public int GetThrottleHint(int playerId)
        {
            int slot = FindSlot(playerId);
            return slot >= 0 ? _throttleHints[slot] : 0;
        }

        /// <summary>
        /// How many frames behind the simulation the input being applied for this player was stamped,
        /// smoothed over recent ticks, or -1 when there is no usable figure.
        /// </summary>
        /// <remarks>
        /// This is the delay between a driver sampling an input and the authority acting on it. The
        /// authority reports it with the car's state so the driver can compare that state against its
        /// own prediction for the frame the input belonged to, rather than for a frame at which it had
        /// already applied a dozen newer ones. It is smoothed because the buffer repeats and skips
        /// inputs to ride out jitter: the stamp of the input in hand hops about from tick to tick while
        /// the car itself keeps moving one tick per tick.
        /// </remarks>
        public int GetInputLag(int playerId)
        {
            int slot = FindSlot(playerId);
            if (slot < 0 || !_hasInputLag[slot])
                return -1;

            int lag = Mathf.RoundToInt(_inputLag[slot]);
            if (lag < 0)
                lag = 0;

            return lag > MaxInputLagFrames ? -1 : lag;
        }

        public void EnqueueInput(int playerId, InputFrame input)
        {
            if (input == null)
                return;

            int slot = ClaimSlot(playerId);
            if (slot < 0)
                return;

            if (input.FrameNumber <= _lastConsumedFrame[slot])
                return;

            var buffer = _playerBuffers[slot];
            int count = _counts[slot];

            // Drop if full and too old to be useful.
            if (count >= _capacity)
            {
                // If the incoming input is older than our oldest buffered input, ignore.
                if (input.FrameNumber <= buffer[0].FrameNumber)
                {
                    UpdateThrottleHint(slot);
                    return;
                }

                // Otherwise drop the oldest to make room.
                for (int i = 1; i < count; i++)
                    buffer[i - 1].CopyFrom(buffer[i]);
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
                    buffer[i].CopyFrom(input);
                    _counts[slot] = count;
                    UpdateThrottleHint(slot);
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
                buffer[i].CopyFrom(buffer[i - 1]);

            buffer[insertIndex].CopyFrom(input);
            _counts[slot] = count + 1;

            UpdateThrottleHint(slot);
        }

        public void EnqueueInputs(int playerId, InputFrame[] inputs, int inputCount)
        {
            if (inputs == null)
                return;

            int count = Mathf.Clamp(inputCount, 0, inputs.Length);
            for (int i = 0; i < count; i++)
                EnqueueInput(playerId, inputs[i]);
        }

        /// <summary>
        /// Returns the next buffered input without removing it.
        /// Returns the last consumed input if the buffer is empty, and neutral input for a player with
        /// no slot.
        /// </summary>
        public InputFrame PeekInput(int playerId)
        {
            // A player with no slot has no last-consumed entry either. This used to index one anyway,
            // and an out-of-range read is an exception Udon cannot catch.
            int slot = FindSlot(playerId);
            if (slot < 0)
                return InputFrame.Empty;

            if (_counts[slot] <= 0)
                return _lastConsumedInput[slot];

            return _playerBuffers[slot][0];
        }

        /// <summary>
        /// Removes and returns the next buffered input.
        /// Returns the last consumed input if the buffer is empty, and neutral input for a player with
        /// no slot.
        /// </summary>
        public InputFrame DequeueInput(int playerId)
        {
            int slot = FindSlot(playerId);
            if (slot < 0)
                return InputFrame.Empty;

            return DequeueSlot(slot);
        }

        /// <summary>
        /// Gets input for a player for the current tick.
        /// Depending on throttle state, may repeat or consume extra inputs.
        /// </summary>
        public InputFrame ConsumeInput(int playerId, int currentFrame)
        {
            int slot = FindSlot(playerId);
            if (slot < 0)
                return InputFrame.Empty;

            InputFrame applied = SelectInput(slot);

            // Nothing has ever arrived for this player while the cursor is still at its initial value,
            // and the frame in hand is then a blank with no stamp to measure against.
            if (_counts[slot] > 0 || _lastConsumedFrame[slot] != int.MinValue)
                TrackInputLag(slot, applied.FrameNumber, currentFrame);

            return applied;
        }

        private InputFrame SelectInput(int slot)
        {
            int count = _counts[slot];
            if (count <= 0)
                return _lastConsumedInput[slot];

            // Upstream: server consumes normally; client adjusts its send/sim rate.
            if (Mode == ThrottleMode.Upstream)
                return DequeueSlot(slot);

            int hint = _throttleHints[slot];

            // Buffer low: repeat without consuming (consume 0).
            if (hint < 0)
                return _playerBuffers[slot][0];

            // Buffer high: consume one extra if we can.
            if (hint > 0 && count > 1)
                DequeueSlot(slot);

            return DequeueSlot(slot);
        }

        private InputFrame DequeueSlot(int slot)
        {
            int count = _counts[slot];
            if (count <= 0)
                return _lastConsumedInput[slot];

            var buffer = _playerBuffers[slot];

            InputFrame input = _lastConsumedInput[slot];
            input.CopyFrom(buffer[0]);
            for (int i = 1; i < count; i++)
                buffer[i - 1].CopyFrom(buffer[i]);

            _counts[slot] = count - 1;

            _lastConsumedFrame[slot] = input.FrameNumber;

            UpdateThrottleHint(slot);

            return input;
        }

        private void TrackInputLag(int slot, int appliedFrame, int currentFrame)
        {
            int lag = currentFrame - appliedFrame;
            if (lag < 0)
                lag = 0;
            if (lag > MaxInputLagFrames + 1)
                lag = MaxInputLagFrames + 1;

            if (!_hasInputLag[slot])
            {
                _inputLag[slot] = lag;
                _hasInputLag[slot] = true;
                return;
            }

            _inputLag[slot] += (lag - _inputLag[slot]) * InputLagSmoothing;
        }

        private void UpdateThrottleHint(int slot)
        {
            int count = _counts[slot];
            int target = Mathf.Max(1, TargetBufferSize);

            int lowThreshold = Mathf.Max(1, target - 1);
            int highThreshold = target + 2;

            if (count < lowThreshold && count >= 0)
                _throttleHints[slot] = -1;
            else if (count > highThreshold)
                _throttleHints[slot] = 1;
            else
                _throttleHints[slot] = 0;

            // Clamp into configured min/max range for safety.
            if (count < MinBufferSize)
                _throttleHints[slot] = -1;
            if (count > MaxBufferSize)
                _throttleHints[slot] = 1;
        }

        /// <summary>
        /// Slot currently held by this player, or -1. Never takes a slot away from anybody.
        /// </summary>
        private int FindSlot(int playerId)
        {
            if (playerId < 0) return -1;
            int origin = playerId % _maxPlayers;
            for (int offset = 0; offset < _maxPlayers; offset++)
            {
                int slot = (origin + offset) % _maxPlayers;
                if (_slotPlayerIds[slot] == playerId) return slot;
            }
            return -1;
        }

        private int ClaimSlot(int playerId)
        {
            if (playerId < 0) return -1;
            int existing = FindSlot(playerId);
            if (existing >= 0) return existing;
            int origin = playerId % _maxPlayers;
            int slot = -1;
            for (int offset = 0; offset < _maxPlayers; offset++)
            {
                int candidate = (origin + offset) % _maxPlayers;
                if (_slotPlayerIds[candidate] < 0) { slot = candidate; break; }
            }
            // A full table must not evict another active player's input.
            if (slot < 0) return -1;
            _slotPlayerIds[slot] = playerId;
            _counts[slot] = 0;
            _lastConsumedFrame[slot] = int.MinValue;
            _lastConsumedInput[slot].CopyFrom(null);
            _throttleHints[slot] = 0;
            _inputLag[slot] = 0f;
            _hasInputLag[slot] = false;
            return slot;
        }

        /// <summary>Releases a departed player's slot so a later join can reuse its capacity.</summary>
        public void RemovePlayer(int playerId)
        {
            int slot = FindSlot(playerId);
            if (slot < 0) return;
            _slotPlayerIds[slot] = -1;
            _counts[slot] = 0;
            _lastConsumedFrame[slot] = int.MinValue;
            _lastConsumedInput[slot].CopyFrom(null);
            _throttleHints[slot] = 0;
            _inputLag[slot] = 0f;
            _hasInputLag[slot] = false;
        }
    }
}
