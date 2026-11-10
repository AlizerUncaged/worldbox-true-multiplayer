using System;
using System.Collections.Generic;

namespace WorldboxMP.Server.Data
{
    /// <summary>
    /// The complete persistence surface of the server (accounts only, deliberately
    /// tiny). Two implementations: MongoDB for the official instance, SQLite for
    /// zero-dependency self-hosting. Selected by "DatabaseBrand" in appsettings.json
    /// (or WBMP_DATABASE_BRAND).
    /// </summary>
    public interface IPlayerStore
    {
        /// <summary>Human-readable target for startup logs (never includes credentials).</summary>
        string Describe { get; }

        /// <summary>Connect / create schema / ensure indexes. Throws on failure (fail fast).</summary>
        void Init();

        /// <summary>Case-insensitive account lookup (pass the lowercased username). Null if absent.</summary>
        PlayerRecord FindByUsernameLower(string usernameLower);

        /// <summary>Insert a new account. False if the username is already taken (race-safe:
        /// backed by a unique index/constraint, not just a prior lookup).</summary>
        bool TryInsert(PlayerRecord record);

        /// <summary>Stamp a successful login time.</summary>
        void TouchLastLogin(PlayerRecord player);

        /// <summary>Lookup by external id. Null if absent.</summary>
        PlayerRecord FindById4(string id4);

        /// <summary>Store (or refresh the expiry of) a "remember me" session. Only the
        /// SHA-256 of the token is ever stored, never the token itself.</summary>
        void SaveSession(string tokenHash, string id4, DateTime expiresUtc);

        /// <summary>The account id of a live (unexpired) session, or null.</summary>
        string FindSession(string tokenHash);

        /// <summary>Revoke one session (logout). No-op if absent.</summary>
        void DeleteSession(string tokenHash);

        /// <summary>Revoke every remembered session of an account (admin "log out everywhere").</summary>
        void DeleteSessionsOf(string id4);

        // ── Admin panel ─────────────────────────────────────────────────────
        /// <summary>Accounts whose name contains <paramref name="query"/> (all when empty), newest first.</summary>
        List<PlayerRecord> SearchPlayers(string query, int limit);
        long CountPlayers();
        void UpdatePassword(string id4, string hash, string salt);

        AdminRecord FindAdmin(string usernameLower);
        bool AnyAdmin();
        void SaveAdmin(AdminRecord admin);

        List<BanRecord> ListBans();
        void AddBan(BanRecord ban);
        void RemoveBan(string id);
    }
}
