using System;
using System.Security.Cryptography;

namespace JsUnitySync.UnityPackage
{

/// <summary>ULID-compatible Crockford ids accepted by the collaboration server; no project file is touched.</summary>
public static class StableIdFactory
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { bytes[i] = (byte)timestamp; timestamp >>= 8; }
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes.Slice(6, 10));
        Span<char> output = stackalloc char[26];
        for (var symbol = 0; symbol < output.Length; symbol++)
        {
            var value = 0;
            for (var bit = 0; bit < 5; bit++)
            {
                var sourceBit = symbol * 5 + bit - 2; // ULID is 128 bits encoded in 130 bits.
                value <<= 1;
                if (sourceBit >= 0 && sourceBit < 128)
                    value |= (bytes[sourceBit >> 3] >> (7 - (sourceBit & 7))) & 1;
            }
            output[symbol] = Alphabet[value];
        }
        return new string(output);
    }

    public static bool IsValid(string value)
    {
        if (value is null || value.Length != 26 || value[0] > '7') return false;
        foreach (var c in value)
            if (Alphabet.IndexOf(c) < 0) return false;
        return true;
    }
}
}
