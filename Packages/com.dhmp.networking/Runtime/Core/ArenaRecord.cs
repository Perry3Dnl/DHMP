using System;
using System.Text;

namespace DHMP.Unity
{
    public enum ArenaMessage : byte { Join = 1, Welcome = 2, Input = 3, State = 4, Leave = 5 }

    // DUNA/1: explicitly application-owned records, not DHMP framing metadata.
    public struct ArenaRecord
    {
        public const int Size = 128;
        public static readonly Guid SchemaId = new Guid("b2908d48-f766-4c78-aac7-e312513ea701");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public ArenaMessage Kind;
        public bool Jump, Grounded;
        public uint PlayerId, Sequence, AcknowledgedInput, SentAt;
        public ulong Session, JoinNonce;
        public MotorState State;
        public float MoveX, MoveZ;
        public string Name;

        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Player";
            var builder = new StringBuilder();
            int bytes = 0;
            foreach (char c in name.Trim())
            {
                if (char.IsControl(c) || char.IsSurrogate(c) || c == '<' || c == '>') continue;
                int size = c < 128 ? 1 : c < 2048 ? 2 : 3;
                if (bytes + size > 32) break;
                builder.Append(c); bytes += size;
            }
            return builder.Length == 0 ? "Player" : builder.ToString();
        }

        public void Encode(byte[] destination, int offset = 0)
        {
            if (destination == null || offset < 0 || offset > destination.Length - Size)
                throw new ArgumentException("A complete arena record is required.");
            Array.Clear(destination, offset, Size);
            destination[offset] = (byte)Kind; destination[offset + 1] = 1;
            destination[offset + 2] = (byte)((Jump ? 1 : 0) | (Grounded ? 2 : 0));
            DhmpWire.Write32(destination, offset + 4, PlayerId);
            DhmpWire.Write32(destination, offset + 8, Sequence);
            DhmpWire.Write32(destination, offset + 12, AcknowledgedInput);
            DhmpWire.Write64(destination, offset + 16, Session);
            DhmpWire.Write64(destination, offset + 24, JoinNonce);
            DhmpWire.WriteFloat(destination, offset + 32, State.X);
            DhmpWire.WriteFloat(destination, offset + 36, State.Y);
            DhmpWire.WriteFloat(destination, offset + 40, State.Z);
            DhmpWire.WriteFloat(destination, offset + 44, State.VerticalVelocity);
            DhmpWire.WriteFloat(destination, offset + 48, State.Yaw);
            DhmpWire.WriteFloat(destination, offset + 52, MoveX);
            DhmpWire.WriteFloat(destination, offset + 56, MoveZ);
            DhmpWire.Write32(destination, offset + 60, SentAt);
            // Input records avoid string processing and allocations on the tick path.
            if (!string.IsNullOrEmpty(Name))
            {
                string normalized = NormalizeName(Name);
                destination[offset + 64] = (byte)Utf8.GetBytes(normalized, 0, normalized.Length, destination, offset + 65);
            }
        }

        public static bool TryDecode(byte[] bytes, int offset, out ArenaRecord record)
        {
            record = default;
            if (bytes == null || offset < 0 || offset > bytes.Length - Size || bytes[offset] < 1 || bytes[offset] > 5 ||
                bytes[offset + 1] != 1 || (bytes[offset + 2] & ~3) != 0 || bytes[offset + 3] != 0 || bytes[offset + 64] > 32)
                return false;
            int nameLength = bytes[offset + 64];
            for (int i = 65 + nameLength; i < Size; i++) if (bytes[offset + i] != 0) return false;
            for (int i = 32; i <= 56; i += 4) if (!DhmpWire.Finite(DhmpWire.ReadFloat(bytes, offset + i))) return false;
            string name;
            try { name = nameLength == 0 ? string.Empty : Utf8.GetString(bytes, offset + 65, nameLength); }
            catch (DecoderFallbackException) { return false; }
            record = new ArenaRecord
            {
                Kind = (ArenaMessage)bytes[offset], Jump = (bytes[offset + 2] & 1) != 0, Grounded = (bytes[offset + 2] & 2) != 0,
                PlayerId = DhmpWire.Read32(bytes, offset + 4), Sequence = DhmpWire.Read32(bytes, offset + 8),
                AcknowledgedInput = DhmpWire.Read32(bytes, offset + 12), Session = DhmpWire.Read64(bytes, offset + 16),
                JoinNonce = DhmpWire.Read64(bytes, offset + 24), SentAt = DhmpWire.Read32(bytes, offset + 60),
                State = new MotorState { X = DhmpWire.ReadFloat(bytes, offset + 32), Y = DhmpWire.ReadFloat(bytes, offset + 36),
                    Z = DhmpWire.ReadFloat(bytes, offset + 40), VerticalVelocity = DhmpWire.ReadFloat(bytes, offset + 44),
                    Yaw = DhmpWire.ReadFloat(bytes, offset + 48), Grounded = (bytes[offset + 2] & 2) != 0 },
                MoveX = DhmpWire.ReadFloat(bytes, offset + 52), MoveZ = DhmpWire.ReadFloat(bytes, offset + 56), Name = name
            };
            return Math.Abs(record.MoveX) <= 1 && Math.Abs(record.MoveZ) <= 1 &&
                Math.Abs(record.State.X) <= 128 && Math.Abs(record.State.Y) <= 128 && Math.Abs(record.State.Z) <= 128 &&
                Math.Abs(record.State.VerticalVelocity) <= 128 && record.State.Yaw >= 0 && record.State.Yaw < 360;
        }

        public static bool TryDecodePacket(byte[] bytes, int length, ArenaRecord[] records, out int count)
        {
            count = 0;
            if (bytes == null || length > bytes.Length || !DhmpWire.ValidDataLength(length) || records == null || records.Length < length / Size)
                return false;
            int total = length / Size;
            for (int i = 0; i < total; i++)
                if (!TryDecode(bytes, i * Size, out records[i])) return false;
            count = total; // No record is published until the entire packet has passed validation.
            return true;
        }
    }
}
