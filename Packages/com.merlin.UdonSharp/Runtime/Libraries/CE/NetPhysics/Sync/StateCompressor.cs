using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Compresses and decompresses <see cref="PhysicsSnapshot"/> data for efficient network transmission.
    /// Uses quantization and bitpacking optimized for VRChat's sync constraints.
    /// </summary>
    [PublicAPI]
    public class StateCompressor
    {
        // Quantization parameters
        public float PositionPrecision = 0.01f; // Hint: positions quantize to 16-bit within bounds.
        public float VelocityPrecision = 0.1f;  // 10 cm/s
        public float RotationPrecision = 0.01f; // Hint: rotations packed via smallest-three.

        // Bounds for position encoding (smaller values = more precision for same bits).
        public Vector3 WorldMin = new Vector3(-100, -10, -100);
        public Vector3 WorldMax = new Vector3(100, 50, 100);

        // Header: frame (4) + entityCount (4)
        private const int HeaderBytes = 8;

        // Entity: id (2) + pos (6) + rot (6) + vel (5) + angVel (5) = 24 bytes
        public const int BytesPerEntity = 24;

        // Smallest-three quaternion domain (other three components live within +/- sqrt(0.5)).
        private const float QuaternionMaxComponent = 0.70710678118f;
        private const float QuaternionInvRange = 1f / (2f * QuaternionMaxComponent);

        public byte[] Compress(PhysicsSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            int entityCount = snapshot.EntityCount;
            int totalBytes = HeaderBytes + entityCount * BytesPerEntity;
            byte[] data = new byte[totalBytes];

            int offset = 0;
            WriteInt32(data, ref offset, snapshot.Frame);
            WriteInt32(data, ref offset, entityCount);

            for (int i = 0; i < entityCount; i++)
            {
                WriteUInt16(data, ref offset, (ushort)snapshot.EntityIds[i]);
                WritePosition(data, ref offset, snapshot.Positions[i]);
                WriteRotation(data, ref offset, snapshot.Rotations[i]);
                WritePackedVector3_12Bit(data, ref offset, snapshot.Velocities[i], VelocityPrecision);
                WritePackedVector3_12Bit(data, ref offset, snapshot.AngularVelocities[i], VelocityPrecision);
            }

            return data;
        }

        public void Decompress(byte[] data, PhysicsSnapshot snapshot)
        {
            if (data == null || snapshot == null || data.Length < HeaderBytes)
                return;

            int offset = 0;
            snapshot.Frame = ReadInt32(data, ref offset);
            int entityCount = ReadInt32(data, ref offset);
            snapshot.EntityCount = entityCount;

            snapshot.Initialize(entityCount);

            for (int i = 0; i < entityCount; i++)
            {
                snapshot.EntityIds[i] = ReadUInt16(data, ref offset);
                snapshot.Positions[i] = ReadPosition(data, ref offset);
                snapshot.Rotations[i] = ReadRotation(data, ref offset);
                snapshot.Velocities[i] = ReadPackedVector3_12Bit(data, ref offset, VelocityPrecision);
                snapshot.AngularVelocities[i] = ReadPackedVector3_12Bit(data, ref offset, VelocityPrecision);
            }
        }

        private void WritePosition(byte[] data, ref int offset, Vector3 pos)
        {
            Vector3 min = WorldMin;
            Vector3 max = WorldMax;
            Vector3 range = max - min;

            float nx = range.x != 0f ? Mathf.Clamp01((pos.x - min.x) / range.x) : 0f;
            float ny = range.y != 0f ? Mathf.Clamp01((pos.y - min.y) / range.y) : 0f;
            float nz = range.z != 0f ? Mathf.Clamp01((pos.z - min.z) / range.z) : 0f;

            WriteUInt16(data, ref offset, (ushort)Mathf.RoundToInt(nx * 65535f));
            WriteUInt16(data, ref offset, (ushort)Mathf.RoundToInt(ny * 65535f));
            WriteUInt16(data, ref offset, (ushort)Mathf.RoundToInt(nz * 65535f));
        }

        private Vector3 ReadPosition(byte[] data, ref int offset)
        {
            Vector3 min = WorldMin;
            Vector3 max = WorldMax;
            Vector3 range = max - min;

            float nx = ReadUInt16(data, ref offset) / 65535f;
            float ny = ReadUInt16(data, ref offset) / 65535f;
            float nz = ReadUInt16(data, ref offset) / 65535f;

            return new Vector3(
                min.x + nx * range.x,
                min.y + ny * range.y,
                min.z + nz * range.z);
        }

        /// <summary>
        /// Smallest-three quaternion encoding into 6 bytes.
        /// Layout: 3x15-bit quantized components + 2-bit largest-index + 1 unused bit.
        /// </summary>
        private void WriteRotation(byte[] data, ref int offset, Quaternion rot)
        {
            rot = NormalizeQuaternion(rot);

            // Find index of largest absolute component
            int largestIndex = 0;
            float largestAbs = Mathf.Abs(rot.x);
            float ay = Mathf.Abs(rot.y);
            float az = Mathf.Abs(rot.z);
            float aw = Mathf.Abs(rot.w);

            if (ay > largestAbs) { largestIndex = 1; largestAbs = ay; }
            if (az > largestAbs) { largestIndex = 2; largestAbs = az; }
            if (aw > largestAbs) { largestIndex = 3; }

            // Ensure largest component is positive (q and -q represent same rotation)
            float largestValue = GetQuaternionComponent(rot, largestIndex);
            if (largestValue < 0f)
            {
                rot.x = -rot.x;
                rot.y = -rot.y;
                rot.z = -rot.z;
                rot.w = -rot.w;
            }

            float a, b, c;
            GetSmallestThree(rot, largestIndex, out a, out b, out c);

            ushort qa = QuantizeQuaternionComponent(a);
            ushort qb = QuantizeQuaternionComponent(b);
            ushort qc = QuantizeQuaternionComponent(c);

            ulong packed =
                (ulong)qa |
                ((ulong)qb << 15) |
                ((ulong)qc << 30) |
                ((ulong)largestIndex << 45);

            // Write 48 bits little-endian
            data[offset++] = (byte)(packed);
            data[offset++] = (byte)(packed >> 8);
            data[offset++] = (byte)(packed >> 16);
            data[offset++] = (byte)(packed >> 24);
            data[offset++] = (byte)(packed >> 32);
            data[offset++] = (byte)(packed >> 40);
        }

        private Quaternion ReadRotation(byte[] data, ref int offset)
        {
            ulong packed =
                (ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32) |
                ((ulong)data[offset + 5] << 40);

            offset += 6;

            ushort qa = (ushort)(packed & 0x7FFF);
            ushort qb = (ushort)((packed >> 15) & 0x7FFF);
            ushort qc = (ushort)((packed >> 30) & 0x7FFF);
            int largestIndex = (int)((packed >> 45) & 0x3);

            float a = DequantizeQuaternionComponent(qa);
            float b = DequantizeQuaternionComponent(qb);
            float c = DequantizeQuaternionComponent(qc);

            float sum = a * a + b * b + c * c;
            float largest = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));

            Quaternion rot;
            switch (largestIndex)
            {
                case 0: rot = new Quaternion(largest, a, b, c); break;
                case 1: rot = new Quaternion(a, largest, b, c); break;
                case 2: rot = new Quaternion(a, b, largest, c); break;
                default: rot = new Quaternion(a, b, c, largest); break;
            }

            return rot;
        }

        private static void WritePackedVector3_12Bit(byte[] data, ref int offset, Vector3 v, float precision)
        {
            if (precision <= 0f)
                precision = 0.0001f;

            int qx = Mathf.RoundToInt(v.x / precision);
            int qy = Mathf.RoundToInt(v.y / precision);
            int qz = Mathf.RoundToInt(v.z / precision);

            qx = Mathf.Clamp(qx, -2047, 2047);
            qy = Mathf.Clamp(qy, -2047, 2047);
            qz = Mathf.Clamp(qz, -2047, 2047);

            uint ux = (uint)(qx + 2048);
            uint uy = (uint)(qy + 2048);
            uint uz = (uint)(qz + 2048);

            ulong packed = (ulong)ux | ((ulong)uy << 12) | ((ulong)uz << 24);

            data[offset++] = (byte)(packed);
            data[offset++] = (byte)(packed >> 8);
            data[offset++] = (byte)(packed >> 16);
            data[offset++] = (byte)(packed >> 24);
            data[offset++] = (byte)(packed >> 32);
        }

        private static Vector3 ReadPackedVector3_12Bit(byte[] data, ref int offset, float precision)
        {
            if (precision <= 0f)
                precision = 0.0001f;

            ulong packed =
                (ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32);

            offset += 5;

            int ux = (int)(packed & 0xFFF);
            int uy = (int)((packed >> 12) & 0xFFF);
            int uz = (int)((packed >> 24) & 0xFFF);

            int qx = ux - 2048;
            int qy = uy - 2048;
            int qz = uz - 2048;

            return new Vector3(qx * precision, qy * precision, qz * precision);
        }

        private static ushort QuantizeQuaternionComponent(float value)
        {
            float clamped = Mathf.Clamp(value, -QuaternionMaxComponent, QuaternionMaxComponent);
            float normalized = (clamped + QuaternionMaxComponent) * QuaternionInvRange; // 0..1
            int quant = Mathf.RoundToInt(normalized * 32767f);
            if (quant < 0) quant = 0;
            if (quant > 32767) quant = 32767;
            return (ushort)quant;
        }

        private static float DequantizeQuaternionComponent(ushort quant)
        {
            float normalized = quant / 32767f;
            return normalized * (2f * QuaternionMaxComponent) - QuaternionMaxComponent;
        }

        private static void GetSmallestThree(Quaternion q, int largestIndex, out float a, out float b, out float c)
        {
            switch (largestIndex)
            {
                case 0: a = q.y; b = q.z; c = q.w; break;
                case 1: a = q.x; b = q.z; c = q.w; break;
                case 2: a = q.x; b = q.y; c = q.w; break;
                default: a = q.x; b = q.y; c = q.z; break;
            }
        }

        private static float GetQuaternionComponent(Quaternion q, int index)
        {
            switch (index)
            {
                case 0: return q.x;
                case 1: return q.y;
                case 2: return q.z;
                default: return q.w;
            }
        }

        private static Quaternion NormalizeQuaternion(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag <= 0f)
                return Quaternion.identity;

            float inv = 1f / mag;
            q.x *= inv;
            q.y *= inv;
            q.z *= inv;
            q.w *= inv;
            return q;
        }

        private static void WriteInt32(byte[] data, ref int offset, int value)
        {
            data[offset++] = (byte)value;
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

        private static void WriteUInt16(byte[] data, ref int offset, ushort value)
        {
            data[offset++] = (byte)value;
            data[offset++] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            ushort value = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return value;
        }
    }
}

