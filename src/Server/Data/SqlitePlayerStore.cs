using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WorldboxMP.Server.Data
{
    /// <summary>
    /// SQLite-backed accounts: the zero-dependency option for self-hosted instances
    /// ("Database:Provider": "sqlite"). One file next to the exe, schema auto-created.
    /// All calls run on the single auth worker thread, so one open connection is fine.
    /// </summary>
    public class SqlitePlayerStore : IPlayerStore
    {
        private readonly string _connectionString;
        private SqliteConnection _conn;

        public SqlitePlayerStore(string connectionString) => _connectionString = connectionString;

        public string Describe
        {
            get
            {
                try { return "SQLite @ " + new SqliteConnectionStringBuilder(_connectionString).DataSource; }
                catch { return "SQLite"; }
            }
        }

        public void Init()
        {
            _conn = new SqliteConnection(_connectionString);
            _conn.Open();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS players (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    id4            TEXT NOT NULL UNIQUE,
                    username       TEXT NOT NULL,
                    username_lower TEXT NOT NULL UNIQUE,
                    password_hash  TEXT NOT NULL,
                    password_salt  TEXT NOT NULL,
                    created_at     TEXT NOT NULL,
                    last_login     TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS sessions (
                    token_hash  TEXT PRIMARY KEY,
                    id4         TEXT NOT NULL,
                    expires_at  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sessions_id4 ON sessions(id4);
                CREATE TABLE IF NOT EXISTS admins (
                    username_lower TEXT PRIMARY KEY,
                    username       TEXT NOT NULL,
                    password_hash  TEXT NOT NULL,
                    password_salt  TEXT NOT NULL,
                    created_at     TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS bans (
                    id          TEXT PRIMARY KEY,
                    kind        TEXT NOT NULL,
                    value       TEXT NOT NULL,
                    label       TEXT NOT NULL,
                    reason      TEXT NOT NULL,
                    created_at  TEXT NOT NULL,
                    expires_at  TEXT NULL
                );
                DELETE FROM sessions WHERE expires_at < strftime('%Y-%m-%dT%H:%M:%fZ','now');";
            cmd.ExecuteNonQuery();
        }

        private const string PlayerColumns = "id4, username, username_lower, password_hash, password_salt, created_at, last_login";

        public PlayerRecord FindByUsernameLower(string usernameLower) =>
            FindOne($"SELECT {PlayerColumns} FROM players WHERE username_lower = $v LIMIT 1", usernameLower);

        public PlayerRecord FindById4(string id4) =>
            FindOne($"SELECT {PlayerColumns} FROM players WHERE id4 = $v LIMIT 1", id4);

        private PlayerRecord FindOne(string sql, string value)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$v", value);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadPlayer(r) : null;
        }

        public bool TryInsert(PlayerRecord record)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO players (id4, username, username_lower, password_hash, password_salt, created_at)
                                VALUES ($id4, $u, $ul, $h, $s, $c)";
            cmd.Parameters.AddWithValue("$id4", record.Id4);
            cmd.Parameters.AddWithValue("$u", record.Username);
            cmd.Parameters.AddWithValue("$ul", record.UsernameLower);
            cmd.Parameters.AddWithValue("$h", record.PasswordHash);
            cmd.Parameters.AddWithValue("$s", record.PasswordSalt);
            cmd.Parameters.AddWithValue("$c", record.CreatedAt.ToString("o"));
            try { cmd.ExecuteNonQuery(); return true; }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT
            {
                return false;   // username_lower unique constraint - taken
            }
        }

        public void TouchLastLogin(PlayerRecord player)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE players SET last_login = $t WHERE id4 = $id4";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id4", player.Id4);
            cmd.ExecuteNonQuery();
        }

        public void SaveSession(string tokenHash, string id4, DateTime expiresUtc)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO sessions (token_hash, id4, expires_at) VALUES ($h, $id4, $e)
                                ON CONFLICT(token_hash) DO UPDATE SET expires_at = excluded.expires_at";
            cmd.Parameters.AddWithValue("$h", tokenHash);
            cmd.Parameters.AddWithValue("$id4", id4);
            cmd.Parameters.AddWithValue("$e", expiresUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            cmd.ExecuteNonQuery();
        }

        public string FindSession(string tokenHash)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id4, expires_at FROM sessions WHERE token_hash = $h LIMIT 1";
            cmd.Parameters.AddWithValue("$h", tokenHash);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var expires = DateTime.Parse(r.GetString(1), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            return expires > DateTime.UtcNow ? r.GetString(0) : null;
        }

        public void DeleteSession(string tokenHash) =>
            Exec("DELETE FROM sessions WHERE token_hash = $a", tokenHash);

        public void DeleteSessionsOf(string id4) =>
            Exec("DELETE FROM sessions WHERE id4 = $a", id4);

        // ── Admin panel ─────────────────────────────────────────────────────
        public List<PlayerRecord> SearchPlayers(string query, int limit)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {PlayerColumns} FROM players WHERE username_lower LIKE $q ESCAPE '\\' ORDER BY id DESC LIMIT $n";
            string q = (query ?? "").Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", "%" + q + "%");
            cmd.Parameters.AddWithValue("$n", limit);
            var list = new List<PlayerRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadPlayer(r));
            return list;
        }

        public long CountPlayers()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM players";
            return (long)cmd.ExecuteScalar();
        }

        public void UpdatePassword(string id4, string hash, string salt)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE players SET password_hash = $h, password_salt = $s WHERE id4 = $id4";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$s", salt);
            cmd.Parameters.AddWithValue("$id4", id4);
            cmd.ExecuteNonQuery();
        }

        public AdminRecord FindAdmin(string usernameLower)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT username, password_hash, password_salt FROM admins WHERE username_lower = $u";
            cmd.Parameters.AddWithValue("$u", usernameLower);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new AdminRecord { Username = r.GetString(0), UsernameLower = usernameLower, PasswordHash = r.GetString(1), PasswordSalt = r.GetString(2) };
        }

        public bool AnyAdmin()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM admins";
            return (long)cmd.ExecuteScalar() > 0;
        }

        public void SaveAdmin(AdminRecord a)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO admins (username_lower, username, password_hash, password_salt, created_at)
                                VALUES ($ul, $u, $h, $s, $c)
                                ON CONFLICT(username_lower) DO UPDATE SET password_hash = excluded.password_hash, password_salt = excluded.password_salt";
            cmd.Parameters.AddWithValue("$ul", a.UsernameLower);
            cmd.Parameters.AddWithValue("$u", a.Username);
            cmd.Parameters.AddWithValue("$h", a.PasswordHash);
            cmd.Parameters.AddWithValue("$s", a.PasswordSalt);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        public List<BanRecord> ListBans()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, kind, value, label, reason, created_at, expires_at FROM bans ORDER BY created_at DESC";
            var list = new List<BanRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new BanRecord
                {
                    Id = r.GetString(0), Kind = r.GetString(1), Value = r.GetString(2), Label = r.GetString(3), Reason = r.GetString(4),
                    CreatedAt = DateTime.Parse(r.GetString(5), null, DateTimeStyles.RoundtripKind),
                    ExpiresAt = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6), null, DateTimeStyles.RoundtripKind),
                });
            return list;
        }

        public void AddBan(BanRecord b)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO bans (id, kind, value, label, reason, created_at, expires_at)
                                VALUES ($id, $k, $v, $l, $r, $c, $e)";
            cmd.Parameters.AddWithValue("$id", b.Id);
            cmd.Parameters.AddWithValue("$k", b.Kind);
            cmd.Parameters.AddWithValue("$v", b.Value);
            cmd.Parameters.AddWithValue("$l", b.Label ?? "");
            cmd.Parameters.AddWithValue("$r", b.Reason ?? "");
            cmd.Parameters.AddWithValue("$c", b.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$e", b.ExpiresAt.HasValue ? b.ExpiresAt.Value.ToString("o") : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public void RemoveBan(string id) => Exec("DELETE FROM bans WHERE id = $a", id);

        private void Exec(string sql, string a)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$a", a);
            cmd.ExecuteNonQuery();
        }

        private static PlayerRecord ReadPlayer(SqliteDataReader r) => new PlayerRecord
        {
            Id4 = r.GetString(0),
            Username = r.GetString(1),
            UsernameLower = r.GetString(2),
            PasswordHash = r.GetString(3),
            PasswordSalt = r.GetString(4),
            CreatedAt = DateTime.Parse(r.GetString(5), null, DateTimeStyles.RoundtripKind),
            LastLogin = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6), null, DateTimeStyles.RoundtripKind),
        };
    }
}
