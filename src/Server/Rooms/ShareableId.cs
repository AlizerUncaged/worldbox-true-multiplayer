using System;
using System.Security.Cryptography;

namespace WorldboxMP.Server.Rooms
{
    /// <summary>
    /// Generates 4-character uppercase alphanumeric room ids (A-Z, 0-9).
    /// 36^4 = 1,679,616 possible ids - far more than will ever be in use, so
    /// random generation with a collision retry in <see cref="RoomManager"/> is
    /// comfortably sufficient.
    /// </summary>
    public static class ShareableId
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        public static string Generate(int length = 4)
        {
            Span<char> chars = stackalloc char[length];
            Span<byte> bytes = stackalloc byte[length];
            RandomNumberGenerator.Fill(bytes);
            for (int i = 0; i < length; i++)
                chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }
    }
}
