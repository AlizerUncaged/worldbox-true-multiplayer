using System;
using System.Security.Cryptography;
using WorldboxMP.Server.Data;

namespace WorldboxMP.Server.Auth
{
    /// <summary>Result of a register/login attempt.</summary>
    public readonly struct AuthResult
    {
        public readonly bool Ok;
        public readonly string Message;
        public readonly PlayerRecord Player;

        private AuthResult(bool ok, string message, PlayerRecord player)
        {
            Ok = ok; Message = message; Player = player;
        }

        public static AuthResult Fail(string message) => new AuthResult(false, message, null);
        public static AuthResult Success(PlayerRecord player) => new AuthResult(true, "OK", player);
    }

    /// <summary>
    /// Account registration and login against the <c>players</c> collection.
    /// Passwords are stored as PBKDF2-SHA256 (100k iterations) with a per-user salt.
    /// Calls are synchronous on purpose - they run on the network poll thread.
    /// </summary>
    public class AuthService
    {
        private const int Iterations = 100_000;
        private const int SaltBytes = 16;
        private const int KeyBytes = 32;

        private readonly IPlayerStore _db;

        public AuthService(IPlayerStore db) => _db = db;

        public AuthResult Register(string username, string password)
        {
            username = (username ?? "").Trim();
            password = password ?? "";

            if (username.Length < 3 || username.Length > 24)
                return AuthResult.Fail("Username must be 3-24 characters.");
            if (password.Length < 4 || password.Length > 64)
                return AuthResult.Fail("Password must be 4-64 characters.");

            string lower = username.ToLowerInvariant();
            if (_db.FindByUsernameLower(lower) != null)
                return AuthResult.Fail("Username is already taken.");

            var (hash, salt) = HashPassword(password);
            var record = new PlayerRecord
            {
                Id4 = Guid.NewGuid().ToString(),
                Username = username,
                UsernameLower = lower,
                PasswordHash = hash,
                PasswordSalt = salt,
                CreatedAt = DateTime.UtcNow,
            };

            if (!_db.TryInsert(record))   // race-safe: unique index/constraint in the store
                return AuthResult.Fail("Username is already taken.");

            return AuthResult.Success(record);
        }

        public AuthResult Login(string username, string password)
        {
            username = (username ?? "").Trim();
            password = password ?? "";

            string lower = username.ToLowerInvariant();
            var player = _db.FindByUsernameLower(lower);
            if (player == null)
                return AuthResult.Fail("Invalid username or password.");

            if (!VerifyPassword(password, player.PasswordHash, player.PasswordSalt))
                return AuthResult.Fail("Invalid username or password.");

            _db.TouchLastLogin(player);

            return AuthResult.Success(player);
        }

        private static (string hash, string salt) HashPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
            return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
        }

        private static bool VerifyPassword(string password, string storedHash, string storedSalt)
        {
            if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
                return false;
            byte[] salt = Convert.FromBase64String(storedSalt);
            byte[] expected = Convert.FromBase64String(storedHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}
