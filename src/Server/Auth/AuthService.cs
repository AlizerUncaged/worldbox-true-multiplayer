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
        /// <summary>Fresh session token (only when one was requested), else empty.</summary>
        public readonly string Token;

        private AuthResult(bool ok, string message, PlayerRecord player, string token)
        {
            Ok = ok; Message = message; Player = player; Token = token ?? "";
        }

        public static AuthResult Fail(string message) => new AuthResult(false, message, null, null);
        public static AuthResult Success(PlayerRecord player, string token = null) => new AuthResult(true, "OK", player, token);
    }

    /// <summary>
    /// Account registration and login against the <c>players</c> collection.
    /// Passwords are stored as PBKDF2-SHA256 (100k iterations) with a per-user salt.
    /// Calls are synchronous and slow on purpose (PBKDF2): the server runs them on its
    /// dedicated auth worker thread, never on the network poll thread.
    /// "Remember me" sessions are random 256-bit tokens; only their SHA-256 is stored.
    /// </summary>
    public class AuthService
    {
        private const int Iterations = 100_000;
        private const int SaltBytes = 16;
        private const int KeyBytes = 32;

        private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

        private readonly IPlayerStore _db;

        public AuthService(IPlayerStore db) => _db = db;

        public AuthResult Register(string username, string password, bool remember)
        {
            username = (username ?? "").Trim();
            password = password ?? "";

            if (username.Length < 3 || username.Length > 24)
                return AuthResult.Fail("Username must be 3-24 characters.");
            foreach (char c in username)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                    return AuthResult.Fail("Username: letters, digits, _ - . only.");
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

            return AuthResult.Success(record, remember ? NewSession(record.Id4) : null);
        }

        public AuthResult Login(string username, string password, bool remember)
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

            return AuthResult.Success(player, remember ? NewSession(player.Id4) : null);
        }

        /// <summary>Log in with a remembered session. The token stays the same; its
        /// expiry slides forward so an active player is never logged out.</summary>
        public AuthResult TokenLogin(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 128)
                return AuthResult.Fail("Session expired - please log in again.");
            string hash = HashToken(token);
            string id4 = _db.FindSession(hash);
            var player = id4 != null ? _db.FindById4(id4) : null;
            if (player == null)
                return AuthResult.Fail("Session expired - please log in again.");
            _db.SaveSession(hash, id4, DateTime.UtcNow + SessionLifetime);
            _db.TouchLastLogin(player);
            return AuthResult.Success(player, token);
        }

        public void Logout(string token)
        {
            if (!string.IsNullOrEmpty(token) && token.Length <= 128)
                _db.DeleteSession(HashToken(token));
        }

        private string NewSession(string id4)
        {
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _db.SaveSession(HashToken(token), id4, DateTime.UtcNow + SessionLifetime);
            return token;
        }

        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        internal static (string hash, string salt) HashPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
            return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
        }

        internal static bool VerifyPassword(string password, string storedHash, string storedSalt)
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
