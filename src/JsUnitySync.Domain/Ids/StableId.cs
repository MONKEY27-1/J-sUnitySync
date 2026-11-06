using System;
using System.Security.Cryptography;

namespace JsUnitySync.Domain.Ids
{
    /// <summary>
    /// A ULID: 128-bit sortable identifier — 48 bits of Unix epoch milliseconds
    /// followed by 80 bits of cryptographic randomness, encoded as 26 Crockford
    /// base-32 characters. This is the stable identity of every synchronized
    /// object, event, and session in J'sUnitySync. Ids survive renames,
    /// reparenting and Unity asset reimports.
    /// </summary>
    public readonly struct StableId : IEquatable<StableId>, IComparable<StableId>
    {
        public const int CharLength = 26;

        // Crockford base-32 alphabet (excludes I, L, O, U).
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        // The full 128-bit value is (_hi << 64) | _lo = (timestamp << 80) | randomness.
        private readonly ulong _hi;
        private readonly ulong _lo;

        private StableId(ulong hi, ulong lo)
        {
            _hi = hi;
            _lo = lo;
        }

        /// <summary>Unix epoch milliseconds embedded in this id.</summary>
        public long TimestampMilliseconds => (long)(_hi >> 16);

        public static StableId NewId()
        {
            Span<byte> random = stackalloc byte[10];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(random);
            }
            ulong ts = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ulong hi = (ts << 16) | ((ulong)random[0] << 8) | random[1];
            ulong lo = 0;
            for (int i = 2; i < 10; i++)
                lo = (lo << 8) | random[i];
            return new StableId(hi, lo);
        }

        public static StableId Parse(string text) =>
            TryParse(text, out var id)
                ? id
                : throw new FormatException($"'{text}' is not a valid {CharLength}-character StableId.");

        public static bool TryParse(string text, out StableId id)
        {
            id = default;
            if (text is null || text.Length != CharLength)
                return false;

            Span<byte> buf = stackalloc byte[16];
            buf.Clear();

            int v0 = DecodeChar(text[0]);
            if (v0 < 0 || v0 > 7) return false; // only 3 significant bits
            buf[0] = (byte)(v0 << 5);

            for (int i = 1; i < CharLength; i++)
            {
                int v = DecodeChar(text[i]);
                if (v < 0) return false;
                WriteBits(buf, 125 - 5 * i, v);
            }

            ulong hi = 0, lo = 0;
            for (int i = 0; i < 8; i++) hi = (hi << 8) | buf[i];
            for (int i = 8; i < 16; i++) lo = (lo << 8) | buf[i];
            id = new StableId(hi, lo);
            return true;
        }

        /// <summary>Writes <paramref name="value"/> (5 bits) at the given LSB-based bit index, MSB-first within the group.</summary>
        private static void WriteBits(Span<byte> buf, int lsbIndex, int value)
        {
            for (int k = 0; k < 5; k++)
            {
                int bitPosition = lsbIndex + 4 - k; // MSB-first within the group
                int bit = (value >> (4 - k)) & 1;
                int byteIndex = 15 - (bitPosition >> 3); // buffer is big-endian
                int bitInByte = bitPosition & 7;
                if (bit == 1) buf[byteIndex] |= (byte)(1 << bitInByte);
            }
        }

        private static int ReadBits(ReadOnlySpan<byte> buf, int lsbIndex)
        {
            int value = 0;
            for (int k = 0; k < 5; k++)
            {
                int bitPosition = lsbIndex + 4 - k;
                int bit = (buf[15 - (bitPosition >> 3)] >> (bitPosition & 7)) & 1;
                value = (value << 1) | bit;
            }
            return value;
        }

        private static int DecodeChar(char c)
        {
            switch (c)
            {
                case >= '0' and <= '9': return c - '0';
                case 'A': case 'a': return 10;
                case 'B': case 'b': return 11;
                case 'C': case 'c': return 12;
                case 'D': case 'd': return 13;
                case 'E': case 'e': return 14;
                case 'F': case 'f': return 15;
                case 'G': case 'g': return 16;
                case 'H': case 'h': return 17;
                case 'J': case 'j': return 18;
                case 'K': case 'k': return 19;
                case 'M': case 'm': return 20;
                case 'N': case 'n': return 21;
                case 'P': case 'p': return 22;
                case 'Q': case 'q': return 23;
                case 'R': case 'r': return 24;
                case 'S': case 's': return 25;
                case 'T': case 't': return 26;
                case 'V': case 'v': return 27;
                case 'W': case 'w': return 28;
                case 'X': case 'x': return 29;
                case 'Y': case 'y': return 30;
                case 'Z': case 'z': return 31;
                default: return -1;
            }
        }

        public override string ToString()
        {
            Span<byte> buf = stackalloc byte[16];
            ulong hi = _hi, lo = _lo;
            for (int i = 0; i < 8; i++) buf[i] = (byte)(hi >> (56 - 8 * i));
            for (int i = 0; i < 8; i++) buf[8 + i] = (byte)(lo >> (56 - 8 * i));

            Span<char> chars = stackalloc char[CharLength];
            chars[0] = Alphabet[buf[0] >> 5]; // top 3 significant bits
            for (int i = 1; i < CharLength; i++)
                chars[i] = Alphabet[ReadBits(buf, 125 - 5 * i)];
            return new string(chars);
        }

        public bool Equals(StableId other) => _hi == other._hi && _lo == other._lo;
        public override bool Equals(object? obj) => obj is StableId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_hi, _lo);
        public int CompareTo(StableId other)
        {
            int c = _hi.CompareTo(other._hi);
            return c != 0 ? c : _lo.CompareTo(other._lo);
        }
        public static bool operator ==(StableId left, StableId right) => left.Equals(right);
        public static bool operator !=(StableId left, StableId right) => !left.Equals(right);
        public static bool operator <(StableId left, StableId right) => left.CompareTo(right) < 0;
        public static bool operator >(StableId left, StableId right) => left.CompareTo(right) > 0;
        public static implicit operator string(StableId id) => id.ToString();
    }
}
