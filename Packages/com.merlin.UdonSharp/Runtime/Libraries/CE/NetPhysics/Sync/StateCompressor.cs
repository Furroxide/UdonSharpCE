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
        public float PositionPrecision = 0.01f; // Not read: positions always quantize to 16 bits per axis across the bounds.
        public float VelocityPrecision = 0.1f;  // 10 cm/s
        public float RotationPrecision = 0.01f; // Not read: rotations are always packed smallest-three at 15 bits.

        // World-space box positions are quantized into (smaller = more precision for the same bits).
        // A position outside it is clamped onto its surface, silently, and arrives as a different
        // place: an arena built away from the origin had everything in it pinned to the nearest face
        // of this box on every client but the authority. NetPhysicsWorld therefore sets these from its
        // own position and StateBoundsMin/Max; the values here only suit a simulation at the origin.
        public Vector3 WorldMin = new Vector3(-100, -10, -100);
        public Vector3 WorldMax = new Vector3(100, 150, 100);

        // Set once a non-finite value has been replaced, so the warning is logged once per compressor
        // rather than for every body in every packet.
        private bool _reportedNonFinite;

        // Header: frame (4) + entityCount (4)
        public const int HeaderBytes = 8;

        // Entity: id (2) + pos (6) + rot (6) + vel (5) + angVel (5) + input lag (1) = 25 bytes
        public const int BytesPerEntity = 25;

        // Entity id written for a slot that names no entity. Ids are a ushort on the wire.
        private const int WireInvalidEntityId = 65535;

        // Input lag written when the authority has no usable figure; everything below it is a frame count.
        private const int WireUnknownInputLag = 255;

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
                // Range-checked before the cast: Udon lowers a narrowing cast to System.Convert, which
                // throws on a value that does not fit, and an exception halts the world's behaviour for
                // the rest of the session.
                int entityId = snapshot.EntityIds[i];
                if (entityId < 0 || entityId > WireInvalidEntityId)
                    entityId = WireInvalidEntityId;

                int inputLag = snapshot.InputLagFrames[i];
                if (inputLag < 0 || inputLag >= WireUnknownInputLag)
                    inputLag = WireUnknownInputLag;

                WriteUInt16(data, ref offset, (ushort)entityId);
                WritePosition(data, ref offset, snapshot.Positions[i]);
                WriteRotation(data, ref offset, snapshot.Rotations[i]);
                WritePackedVector3_12Bit(data, ref offset, snapshot.Velocities[i], VelocityPrecision);
                WritePackedVector3_12Bit(data, ref offset, snapshot.AngularVelocities[i], VelocityPrecision);
                data[offset++] = (byte)inputLag;
            }

            return data;
        }

        /// <summary>
        /// Reads a packet into <paramref name="snapshot"/>. Returns false and leaves the snapshot
        /// untouched when the payload cannot hold the entity count it claims.
        /// </summary>
        public bool Decompress(byte[] data, PhysicsSnapshot snapshot)
        {
            if (data == null || snapshot == null || data.Length < HeaderBytes)
                return false;

            int offset = 0;
            int frame = ReadInt32(data, ref offset);
            int entityCount = ReadInt32(data, ref offset);

            // The entity count arrives from the wire, and Udon has no exception handling: a truncated
            // or garbage packet that claimed more entities than it carried used to read past the end
            // of the array, and the resulting IndexOutOfRange permanently halts the Udon program that
            // hits it. A negative count did the same via a huge Initialize allocation.
            int maxByPayload = (data.Length - HeaderBytes) / BytesPerEntity;
            if (entityCount < 0 || entityCount > maxByPayload)
                return false;

            snapshot.Frame = frame;
            snapshot.EntityCount = entityCount;
            snapshot.Initialize(entityCount);

            for (int i = 0; i < entityCount; i++)
            {
                int entityId = ReadUInt16(data, ref offset);
                snapshot.EntityIds[i] = entityId == WireInvalidEntityId ? PhysicsSnapshot.InvalidEntityId : entityId;
                snapshot.Positions[i] = ReadPosition(data, ref offset);
                snapshot.Rotations[i] = ReadRotation(data, ref offset);
                snapshot.Velocities[i] = ReadPackedVector3_12Bit(data, ref offset, VelocityPrecision);
                snapshot.AngularVelocities[i] = ReadPackedVector3_12Bit(data, ref offset, VelocityPrecision);

                int inputLag = data[offset++];
                snapshot.InputLagFrames[i] = inputLag == WireUnknownInputLag ? PhysicsSnapshot.UnknownInputLag : inputLag;
            }

            return true;
        }

        private void WritePosition(byte[] data, ref int offset, Vector3 pos)
        {
            Vector3 min = WorldMin;
            Vector3 range = WorldMax - min;

            WriteUInt16(data, ref offset, QuantizePositionAxis(pos.x, min.x, range.x));
            WriteUInt16(data, ref offset, QuantizePositionAxis(pos.y, min.y, range.y));
            WriteUInt16(data, ref offset, QuantizePositionAxis(pos.z, min.z, range.z));
        }

        /// <summary>
        /// One coordinate as a step 0..65535 across its axis of the box.
        /// </summary>
        /// <remarks>
        /// A NaN coordinate used to reach the narrowing cast as int.MinValue: Clamp01 works by comparing,
        /// every comparison with NaN is false, so it handed the NaN straight back, and rounding it gave
        /// int.MinValue. Udon lowers that cast to a System.Convert, which throws, so one NaN on one body
        /// halted the authority's world for the rest of the session and every other body's state with
        /// it. A NaN now travels as the middle of the axis; an infinity is clamped onto the face like
        /// any other far-off position.
        /// </remarks>
        private ushort QuantizePositionAxis(float value, float min, float range)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                ReportNonFinite();

            float normalized = range != 0f ? (value - min) / range : 0f;
            if (float.IsNaN(normalized))
                normalized = 0.5f;

            int step = Mathf.RoundToInt(Mathf.Clamp01(normalized) * 65535f);
            if (step < 0)
                step = 0;
            if (step > 65535)
                step = 65535;

            return (ushort)step;
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
            // A NaN or infinite component survives normalisation and arrives as four numbers that are not
            // a rotation at all. The sum of the components is non-finite whenever one of them is.
            float sum = rot.x + rot.y + rot.z + rot.w;
            if (float.IsNaN(sum) || float.IsInfinity(sum))
            {
                ReportNonFinite();
                rot = Quaternion.identity;
            }

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
            data[offset++] = (byte)(packed & 0xFF);
            data[offset++] = (byte)((packed >> 8) & 0xFF);
            data[offset++] = (byte)((packed >> 16) & 0xFF);
            data[offset++] = (byte)((packed >> 24) & 0xFF);
            data[offset++] = (byte)((packed >> 32) & 0xFF);
            data[offset++] = (byte)((packed >> 40) & 0xFF);
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

        private void WritePackedVector3_12Bit(byte[] data, ref int offset, Vector3 v, float precision)
        {
            if (precision <= 0f)
                precision = 0.0001f;

            int qx = QuantizeSigned12(v.x / precision);
            int qy = QuantizeSigned12(v.y / precision);
            int qz = QuantizeSigned12(v.z / precision);

            uint ux = (uint)(qx + 2048);
            uint uy = (uint)(qy + 2048);
            uint uz = (uint)(qz + 2048);

            ulong packed = (ulong)ux | ((ulong)uy << 12) | ((ulong)uz << 24);

            data[offset++] = (byte)(packed & 0xFF);
            data[offset++] = (byte)((packed >> 8) & 0xFF);
            data[offset++] = (byte)((packed >> 16) & 0xFF);
            data[offset++] = (byte)((packed >> 24) & 0xFF);
            data[offset++] = (byte)((packed >> 32) & 0xFF);
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

        /// <summary>
        /// A scaled velocity component as a whole number in -2047..2047.
        /// </summary>
        /// <remarks>
        /// Clamped as a float, before rounding. An infinity rounds to int.MinValue, so clamping the
        /// result sent every infinite component as the most negative value whatever its sign; a NaN has
        /// no value to send at all and goes as zero.
        /// </remarks>
        private int QuantizeSigned12(float scaled)
        {
            if (float.IsNaN(scaled))
            {
                ReportNonFinite();
                return 0;
            }

            if (float.IsInfinity(scaled))
                ReportNonFinite();

            if (scaled > 2047f)
                scaled = 2047f;
            else if (scaled < -2047f)
                scaled = -2047f;

            return Mathf.RoundToInt(scaled);
        }

        /// <summary>
        /// Logs, once per compressor, that a non-finite value was about to be sent. The value has already
        /// been replaced by then; the warning is for whatever upstream produced it.
        /// </summary>
        private void ReportNonFinite()
        {
            if (_reportedNonFinite)
                return;

            _reportedNonFinite = true;
            Debug.LogWarning("[CE.NetPhysics] StateCompressor: replaced a NaN or infinite position, rotation or velocity before sending it. A body in the simulation has non-finite state.");
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
            // Tested for being positive rather than against zero, because NaN fails every comparison; and
            // a magnitude that overflowed to infinity would scale every component to zero.
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(mag > 0f) || float.IsInfinity(mag))
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

        private static void WriteUInt16(byte[] data, ref int offset, ushort value)
        {
            data[offset++] = (byte)(value & 0xFF);
            data[offset++] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            ushort value = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return value;
        }
    }
}
