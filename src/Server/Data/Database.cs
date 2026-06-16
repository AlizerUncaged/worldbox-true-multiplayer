using System;
using MongoDB.Driver;

namespace WorldboxMP.Server.Data
{
    /// <summary>Thin wrapper over the MongoDB connection and collections.</summary>
    public class Database
    {
        public IMongoDatabase Db { get; }
        public IMongoCollection<PlayerRecord> Players { get; }

        public Database(string connectionString)
        {
            var url = new MongoUrl(connectionString);
            var client = new MongoClient(url);
            // The db name is taken from the connection string (e.g. ".../wmulti").
            Db = client.GetDatabase(url.DatabaseName ?? "wmulti");
            Players = Db.GetCollection<PlayerRecord>("players");
            EnsureIndexes();
        }

        private void EnsureIndexes()
        {
            // Unique username (case-insensitive via the lowercased field) and unique id4.
            Players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                Builders<PlayerRecord>.IndexKeys.Ascending(p => p.UsernameLower),
                new CreateIndexOptions { Unique = true, Name = "uniq_username_lower" }));
            Players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                Builders<PlayerRecord>.IndexKeys.Ascending(p => p.Id4),
                new CreateIndexOptions { Unique = true, Name = "uniq_id4" }));
        }

        /// <summary>Verifies connectivity by pinging the server. Throws on failure.</summary>
        public void Ping()
        {
            Db.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        }
    }
}
