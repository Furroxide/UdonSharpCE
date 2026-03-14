using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Compact representation of player input for a single simulation frame.
    /// </summary>
    [PublicAPI]
    public class InputFrame
    {
        private static InputFrame _empty;

        public static InputFrame Empty
        {
            get
            {
                if (_empty == null)
                    _empty = new InputFrame();
                return _empty;
            }
        }

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

        public void CopyFrom(InputFrame other)
        {
            if (other == null)
            {
                FrameNumber = 0;
                Timestamp = 0f;
                Throttle = 0;
                Steering = 0;
                Boost = 0;
                Buttons = 0;
                DodgeX = 0;
                DodgeY = 0;
                return;
            }

            FrameNumber = other.FrameNumber;
            Timestamp = other.Timestamp;
            Throttle = other.Throttle;
            Steering = other.Steering;
            Boost = other.Boost;
            Buttons = other.Buttons;
            DodgeX = other.DodgeX;
            DodgeY = other.DodgeY;
        }

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
            frame.Throttle = ReadSByte(data[offset++]);
            frame.Steering = ReadSByte(data[offset++]);
            frame.Boost = data[offset++];
            frame.Buttons = data[offset++];
            frame.DodgeX = ReadSByte(data[offset++]);
            frame.DodgeY = ReadSByte(data[offset++]);
            return frame;
        }

        private static void WriteInt32(byte[] data, ref int offset, int value)
        {
            data[offset++] = (byte)(value & 0xFF);
            data[offset++] = (byte)((value >> 8) & 0xFF);
            data[offset++] = (byte)((value >> 16) & 0xFF);
            data[offset++] = (byte)((value >> 24) & 0xFF);
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

        private static sbyte ReadSByte(byte value)
        {
            return value > 127 ? (sbyte)(value - 256) : (sbyte)value;
        }
    }
}
