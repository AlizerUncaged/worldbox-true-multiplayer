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
    /// (room membership, identification, etc.) uses <see cref="Id4"/> — a fully
    /// random Guid v4 — instead.
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
