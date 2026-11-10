using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WorldboxMP.Server.Data
{
    /// <summary>
    /// A player account in the <c>players</c> collection.
    ///
    /// The internal Mongo <see cref="Id"/> (ObjectId) is sequential-ish and
    /// enumerable, so it is never exposed externally. Everything outward-facing
    /// (room membership, identification, etc.) uses <see cref="Id4"/> - a fully
    /// random Guid v4 - instead.
    /// </summary>
    public class PlayerRecord
    {
        /// <summary>Internal MongoDB id. Never sent to clients.</summary>
        [BsonId]
        public ObjectId Id { get; set; }

        /// <summary>External, non-enumerable id (Guid v4) used everywhere outside the DB.</summary>
        [BsonElement("id4")]
        public string Id4 { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("username")]
        public string Username { get; set; } = "";

        /// <summary>Lowercased username for case-insensitive uniqueness/lookup.</summary>
        [BsonElement("username_lower")]
        public string UsernameLower { get; set; } = "";

        [BsonElement("password_hash")]
        public string PasswordHash { get; set; } = "";

        [BsonElement("password_salt")]
        public string PasswordSalt { get; set; } = "";

        [BsonElement("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("last_login")]
        public DateTime? LastLogin { get; set; }
    }
}

namespace WorldboxMP.Server.Data
{
    /// <summary>A "remember me" session in the <c>sessions</c> collection. Keyed by the
    /// SHA-256 of the token; the token itself only ever exists on the player's machine.</summary>
    public class SessionRecord
    {
        [BsonId]
        public string TokenHash { get; set; } = "";

        [BsonElement("id4")]
        public string Id4 { get; set; } = "";

        [BsonElement("expires_at")]
        public DateTime ExpiresAt { get; set; }
    }
}

namespace WorldboxMP.Server.Data
{
    /// <summary>An admin-panel login (separate from game accounts).</summary>
    public class AdminRecord
    {
        [BsonId]
        public string UsernameLower { get; set; } = "";

        [BsonElement("username")]
        public string Username { get; set; } = "";

        [BsonElement("password_hash")]
        public string PasswordHash { get; set; } = "";

        [BsonElement("password_salt")]
        public string PasswordSalt { get; set; } = "";
    }

    /// <summary>A ban on an account (Kind "account", Value = id4) or an address (Kind "ip").</summary>
    public class BanRecord
    {
        [BsonId]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [BsonElement("kind")]
        public string Kind { get; set; } = "";

        [BsonElement("value")]
        public string Value { get; set; } = "";

        /// <summary>Human label for the panel (username for account bans).</summary>
        [BsonElement("label")]
        public string Label { get; set; } = "";

        [BsonElement("reason")]
        public string Reason { get; set; } = "";

        [BsonElement("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("expires_at")]
        public DateTime? ExpiresAt { get; set; }

        public bool IsActive => !ExpiresAt.HasValue || ExpiresAt.Value > DateTime.UtcNow;
    }
}
