using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Compact representation of player input for a single simulation frame.
    /// </summary>
    [PublicAPI]
    public struct InputFrame
    {
        // Frame ID
        public int FrameNumber;
        public float Timestamp;

        // Analog (-128..127, 0..255)
        public sbyte Throttle;
        public sbyte Steering;
        public byte Boost;

        // Digital bitfield
        public byte Buttons;

        // Dodge direction (-128..127)
        public sbyte DodgeX;
        public sbyte DodgeY;

        public const byte BUTTON_JUMP = 1;
        public const byte BUTTON_DODGE = 2;
        public const byte BUTTON_HANDBRAKE = 4;
        public const byte BUTTON_BOOST = 8;
        public const byte BUTTON_USE = 16;

        /// <summary>
        /// Network-packed size in bytes (FrameNumber + 6 bytes input).
        /// Timestamp is intentionally excluded.
        /// </summary>
        public const int NetworkSizeBytes = 10;

        public bool IsJumping => (Buttons & BUTTON_JUMP) != 0;
        public bool IsDodging => (Buttons & BUTTON_DODGE) != 0;
        public bool IsBoosting => (Buttons & BUTTON_BOOST) != 0 && Boost > 0;
        public bool IsHandbraking => (Buttons & BUTTON_HANDBRAKE) != 0;

        public Vector2 DodgeDirection => new Vector2(DodgeX / 127f, DodgeY / 127f);

        public void WriteNetworkBytes(byte[] data, ref int offset)
        {
            WriteInt32(data, ref offset, FrameNumber);
            data[offset++] = (byte)(Throttle & 0xFF);
            data[offset++] = (byte)(Steering & 0xFF);
            data[offset++] = Boost;
            data[offset++] = Buttons;
            data[offset++] = (byte)(DodgeX & 0xFF);
            data[offset++] = (byte)(DodgeY & 0xFF);
        }

        public static InputFrame ReadNetworkBytes(byte[] data, ref int offset)
        {
            InputFrame frame = new InputFrame();
            frame.FrameNumber = ReadInt32(data, ref offset);
            frame.Throttle = (sbyte)data[offset++];
            frame.Steering = (sbyte)data[offset++];
            frame.Boost = data[offset++];
            frame.Buttons = data[offset++];
            frame.DodgeX = (sbyte)data[offset++];
            frame.DodgeY = (sbyte)data[offset++];
            return frame;
        }

        private static void WriteInt32(byte[] data, ref int offset, int value)
        {
            data[offset++] = (byte)(value);
            data[offset++] = (byte)(value >> 8);
            data[offset++] = (byte)(value >> 16);
            data[offset++] = (byte)(value >> 24);
        }

        private static int ReadInt32(byte[] data, ref int offset)
        {
            int value = data[offset]
                        | (data[offset + 1] << 8)
                        | (data[offset + 2] << 16)
                        | (data[offset + 3] << 24);
            offset += 4;
            return value;
        }
    }
}

