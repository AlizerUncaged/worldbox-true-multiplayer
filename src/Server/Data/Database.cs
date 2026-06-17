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
            // Fail fast instead of hanging if the cluster is unreachable/paused.
            var settings = MongoClientSettings.FromUrl(url);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(12);
            settings.ConnectTimeout = TimeSpan.FromSeconds(12);
            settings.SocketTimeout = TimeSpan.FromSeconds(12);
            var client = new MongoClient(settings);
            Db = client.GetDatabase(url.DatabaseName ?? "wmulti");
            Players = Db.GetCollection<PlayerRecord>("players");
        }

        /// <summary>Create indexes (non-fatal — logs and continues if it can't).</summary>
        public void EnsureIndexes()
        {
            try
            {
                // Unique username (case-insensitive via the lowercased field) and unique id4.
                Players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                    Builders<PlayerRecord>.IndexKeys.Ascending(p => p.UsernameLower),
                    new CreateIndexOptions { Unique = true, Name = "uniq_username_lower" }));
                Players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                    Builders<PlayerRecord>.IndexKeys.Ascending(p => p.Id4),
                    new CreateIndexOptions { Unique = true, Name = "uniq_id4" }));
            }
            catch (Exception e) { Console.WriteLine("WARN: index setup skipped: " + e.Message); }
        }

        /// <summary>Verifies connectivity by pinging the server. Throws on failure.</summary>
        public void Ping()
        {
            Db.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        }
    }
}
