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
    }
}
