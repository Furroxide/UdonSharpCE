using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Lightweight input prediction for remote players when inputs are missing.
    /// Strategy: hold the last known input across the gap a healthy link leaves between packets, then
    /// decay analog values toward zero.
    /// </summary>
    /// <remarks>
    /// Player ids grow across joins and leaves. Slots remember the full id and probe for a free
    /// entry when ids share a hash bucket, so active players 1 and 81 keep independent input.
    /// RemovePlayer releases departed players; a full table drops a new arrival instead of
    /// stealing an active player's history.
    /// </remarks>
    [PublicAPI]
    public class InputPredictor
    {
        public float AnalogDecayPerSecond = 4f;

        /// <summary>
        /// Shortest time the last known input is held at full strength before it starts to decay.
        /// </summary>
        /// <remarks>
        /// Decay used to start the instant an input arrived. Input arrives in packets a dozen times a
        /// second, so between two perfectly on-time packets a held throttle had already sagged by a
        /// quarter, and a remote car was driven at well under the throttle its driver was holding --
        /// a prediction error manufactured on a link with nothing wrong with it. Decay is for input
        /// that has gone missing, so it waits until a packet is actually late.
        /// </remarks>
        public float HoldSeconds = 0.15f;

        /// <summary>
        /// How many of the sender's observed packet intervals to hold for, when that is longer than
        /// <see cref="HoldSeconds"/>. A sender throttled to a few packets a second leaves gaps longer
        /// than any fixed hold worth having.
        /// </summary>
        public float HoldPacketIntervals = 1.5f;

        // Longest packet interval believed. A driver who stopped and started again did not thereby
        // become a sender whose every input should be held for seconds.
        private const float MaxPacketInterval = 1f;

        private readonly int[] _slotPlayerIds;
        private readonly InputFrame[] _lastInputs;
        private readonly float[] _lastInputTimes;
        private readonly float[] _packetIntervals;
        private readonly bool[] _hasInput;
        private readonly InputFrame[] _predictedInputs;

        public InputPredictor(int maxPlayers = InputBuffer.DefaultMaxPlayers)
        {
            int players = Mathf.Max(1, maxPlayers);
            _slotPlayerIds = new int[players];
            _lastInputs = new InputFrame[players];
            _lastInputTimes = new float[players];
            _packetIntervals = new float[players];
            _hasInput = new bool[players];
            _predictedInputs = new InputFrame[players];

            for (int i = 0; i < players; i++)
            {
                _slotPlayerIds[i] = -1;
                _lastInputs[i] = new InputFrame();
                _predictedInputs[i] = new InputFrame();
            }
        }

        public void UpdateLastKnown(int playerId, InputFrame input, float time)
        {
            int slot = ClaimSlot(playerId);
            if (slot < 0)
                return;

            // Every frame of one packet is reported with the same time, so only a later time marks
            // a new packet.
            if (_hasInput[slot] && time > _lastInputTimes[slot])
            {
                float interval = time - _lastInputTimes[slot];
                _packetIntervals[slot] = interval > MaxPacketInterval ? MaxPacketInterval : interval;
            }

            _lastInputs[slot].CopyFrom(input);
            _lastInputTimes[slot] = time;
            _hasInput[slot] = true;
        }

        /// <summary>
        /// Seconds this player's last known input is held before it starts to decay.
        /// </summary>
        public float GetHoldSeconds(int playerId)
        {
            int slot = FindSlot(playerId);
            if (slot < 0)
                return HoldSeconds;

            float byInterval = _packetIntervals[slot] * HoldPacketIntervals;
            return byInterval > HoldSeconds ? byInterval : HoldSeconds;
        }

        public InputFrame Predict(int playerId, int frameNumber, float time)
        {
            int slot = FindSlot(playerId);
            if (slot < 0 || !_hasInput[slot])
                return InputFrame.Empty;

            InputFrame input = _predictedInputs[slot];
            input.CopyFrom(_lastInputs[slot]);
            input.FrameNumber = frameNumber;
            input.Timestamp = time;

            float byInterval = _packetIntervals[slot] * HoldPacketIntervals;
            float hold = byInterval > HoldSeconds ? byInterval : HoldSeconds;

            float dt = time - _lastInputTimes[slot] - hold;
            if (dt <= 0f || AnalogDecayPerSecond <= 0f)
                return input;

            float decay = Mathf.Exp(-AnalogDecayPerSecond * dt);

            input.Throttle = (sbyte)Mathf.RoundToInt(input.Throttle * decay);
            input.Steering = (sbyte)Mathf.RoundToInt(input.Steering * decay);
            input.Boost = (byte)Mathf.RoundToInt(input.Boost * decay);

            if (input.Boost == 0)
                input.Buttons = (byte)(input.Buttons & ~InputFrame.BUTTON_BOOST);

            // Dodge direction decays too.
            input.DodgeX = (sbyte)Mathf.RoundToInt(input.DodgeX * decay);
            input.DodgeY = (sbyte)Mathf.RoundToInt(input.DodgeY * decay);

            return input;
        }

        private int FindSlot(int playerId)
        {
            if (playerId < 0) return -1;
            int origin = playerId % _slotPlayerIds.Length;
            for (int offset = 0; offset < _slotPlayerIds.Length; offset++)
            {
                int slot = (origin + offset) % _slotPlayerIds.Length;
                if (_slotPlayerIds[slot] == playerId) return slot;
            }
            return -1;
        }

        private int ClaimSlot(int playerId)
        {
            if (playerId < 0) return -1;
            int existing = FindSlot(playerId);
            if (existing >= 0) return existing;
            int origin = playerId % _slotPlayerIds.Length;
            int slot = -1;
            for (int offset = 0; offset < _slotPlayerIds.Length; offset++)
            {
                int candidate = (origin + offset) % _slotPlayerIds.Length;
                if (_slotPlayerIds[candidate] < 0) { slot = candidate; break; }
            }
            // A full table must not evict another active player's input.
            if (slot < 0) return -1;
            _slotPlayerIds[slot] = playerId;
            _lastInputs[slot].CopyFrom(null);
            _predictedInputs[slot].CopyFrom(null);
            _lastInputTimes[slot] = 0f;
            _packetIntervals[slot] = 0f;
            _hasInput[slot] = false;
            return slot;
        }

        /// <summary>Releases a departed player's slot so a later join can reuse its capacity.</summary>
        public void RemovePlayer(int playerId)
        {
            int slot = FindSlot(playerId);
            if (slot < 0) return;
            _slotPlayerIds[slot] = -1;
            _lastInputs[slot].CopyFrom(null);
            _predictedInputs[slot].CopyFrom(null);
            _lastInputTimes[slot] = 0f;
            _packetIntervals[slot] = 0f;
            _hasInput[slot] = false;
        }
    }
}
