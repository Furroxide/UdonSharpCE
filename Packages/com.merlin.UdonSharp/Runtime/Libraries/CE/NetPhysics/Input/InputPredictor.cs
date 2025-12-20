using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Lightweight input prediction for remote players when inputs are missing.
    /// Strategy: hold last known input and decay analog values toward zero over time.
    /// </summary>
    [PublicAPI]
    public class InputPredictor
    {
        public float AnalogDecayPerSecond = 4f;

        private readonly InputFrame[] _lastInputs;
        private readonly float[] _lastInputTimes;

        public InputPredictor(int maxPlayers = InputBuffer.DefaultMaxPlayers)
        {
            int players = Mathf.Max(1, maxPlayers);
            _lastInputs = new InputFrame[players];
            _lastInputTimes = new float[players];
        }

        public void UpdateLastKnown(int playerId, InputFrame input, float time)
        {
            if (!IsValidPlayer(playerId))
                return;

            _lastInputs[playerId] = input;
            _lastInputTimes[playerId] = time;
        }

        public InputFrame Predict(int playerId, int frameNumber, float time)
        {
            if (!IsValidPlayer(playerId))
                return default;

            InputFrame input = _lastInputs[playerId];
            input.FrameNumber = frameNumber;
            input.Timestamp = time;

            float dt = Mathf.Max(0f, time - _lastInputTimes[playerId]);
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

        private bool IsValidPlayer(int playerId) => playerId >= 0 && playerId < _lastInputs.Length;
    }
}

