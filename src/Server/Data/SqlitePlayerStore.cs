using System;
using Microsoft.Data.Sqlite;

namespace WorldboxMP.Server.Data
{
    /// <summary>
    /// SQLite-backed accounts: the zero-dependency option for self-hosted instances
    /// ("DatabaseBrand": "sqlite"). One file next to the exe, schema auto-created.
    /// All calls run on the single network poll thread, so one open connection is fine.
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
                );";
            cmd.ExecuteNonQuery();
        }

        public PlayerRecord FindByUsernameLower(string usernameLower)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id4, username, username_lower, password_hash, password_salt, created_at, last_login FROM players WHERE username_lower = $u LIMIT 1";
            cmd.Parameters.AddWithValue("$u", usernameLower);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new PlayerRecord
            {
                Id4 = r.GetString(0),
                Username = r.GetString(1),
                UsernameLower = r.GetString(2),
                PasswordHash = r.GetString(3),
                PasswordSalt = r.GetString(4),
                CreatedAt = DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                LastLogin = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
            };
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
    }
}
