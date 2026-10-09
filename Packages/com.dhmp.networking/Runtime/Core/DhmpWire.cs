using System;

namespace DHMP.Unity
{
    // Portable Control V1 codec. Interoperability tests compare this with DHMP.Protocol.
    // None of these control fields are prepended to data packets.
    public static class DhmpWire
    {
        public const byte DataProtocol = 253;
        public const byte ControlProtocol = 254;
        public const int ControlSize = 32;
        public const int MaximumPayload = 1152; // Nine 128-byte records, below IPv6's 1280 MTU.

        public static byte[] Control(byte type, uint correlation, byte reason = 0)
        {
            if (correlation == 0 || type < 1 || type > 3 || reason > 5 || (type == 3) != (reason != 0))
                throw new ArgumentException("Invalid Control V1 message.");
            var bytes = new byte[ControlSize];
            bytes[0] = 68; bytes[1] = 72; bytes[2] = 77; bytes[3] = 67;
            bytes[4] = 1; bytes[5] = type; bytes[6] = 1; bytes[7] = reason;
            Write16(bytes, 8, ArenaRecord.Size);
            Write16(bytes, 10, MaximumPayload);
            WriteGuid(bytes, 12, ArenaRecord.SchemaId);
            Write32(bytes, 28, correlation);
            return bytes;
        }

        public static bool TryControl(byte[] bytes, int length, out ControlMessage message)
        {
            message = default;
            if (bytes == null || length != ControlSize || length > bytes.Length ||
                bytes[0] != 68 || bytes[1] != 72 || bytes[2] != 77 || bytes[3] != 67 || bytes[4] != 1)
                return false;
            byte type = bytes[5], reason = bytes[7];
            if (type < 1 || type > 3 || reason > 5 || (type == 3) != (reason != 0) ||
                bytes[6] == 0 || Read16(bytes, 8) == 0 || Read16(bytes, 10) == 0 || Read32(bytes, 28) == 0)
                return false;
            message = new ControlMessage(type, reason, bytes[6], Read16(bytes, 8),
                Read16(bytes, 10), ReadGuid(bytes, 12), Read32(bytes, 28));
            return true;
        }

        public static byte CompatibilityError(ControlMessage message)
        {
            if (message.Version != 1) return 1;
            if (message.RecordSize != ArenaRecord.Size) return 2;
            if (message.Schema != ArenaRecord.SchemaId) return 3;
            if (message.MaximumPayload < ArenaRecord.Size) return 4;
            return 0;
        }

        public static bool ValidDataLength(int length) =>
            length > 0 && length <= MaximumPayload && length % ArenaRecord.Size == 0;

        public static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
        internal static ushort Read16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        internal static uint Read32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        internal static ulong Read64(byte[] b, int o) => ((ulong)Read32(b, o) << 32) | Read32(b, o + 4);
        internal static void Write16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        internal static void Write32(byte[] b, int o, uint v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        internal static void Write64(byte[] b, int o, ulong v) { Write32(b, o, (uint)(v >> 32)); Write32(b, o + 4, (uint)v); }
        internal static float ReadFloat(byte[] b, int o) => BitConverter.Int32BitsToSingle(unchecked((int)Read32(b, o)));
        internal static void WriteFloat(byte[] b, int o, float v) => Write32(b, o, unchecked((uint)BitConverter.SingleToInt32Bits(v)));
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static void WriteGuid(byte[] b, int o, Guid id)
        {
            byte[] raw = id.ToByteArray();
            b[o] = raw[3]; b[o + 1] = raw[2]; b[o + 2] = raw[1]; b[o + 3] = raw[0];
            b[o + 4] = raw[5]; b[o + 5] = raw[4]; b[o + 6] = raw[7]; b[o + 7] = raw[6];
            Array.Copy(raw, 8, b, o + 8, 8);
        }
        internal static Guid ReadGuid(byte[] b, int o) => new Guid(
            unchecked((int)Read32(b, o)), unchecked((short)Read16(b, o + 4)), unchecked((short)Read16(b, o + 6)),
            b[o + 8], b[o + 9], b[o + 10], b[o + 11], b[o + 12], b[o + 13], b[o + 14], b[o + 15]);
    }

    public readonly struct ControlMessage
    {
        public readonly byte Type, Reason, Version;
        public readonly int RecordSize, MaximumPayload;
        public readonly Guid Schema;
        public readonly uint Correlation;
        public ControlMessage(byte type, byte reason, byte version, int recordSize, int maximumPayload, Guid schema, uint correlation)
        { Type = type; Reason = reason; Version = version; RecordSize = recordSize; MaximumPayload = maximumPayload; Schema = schema; Correlation = correlation; }
    }
}
