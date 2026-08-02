using System;
using MongoDB.Driver;

namespace WorldboxMP.Server.Data
{
    /// <summary>MongoDB-backed accounts (the official instance).</summary>
    public class MongoPlayerStore : IPlayerStore
    {
        private readonly string _connectionString;
        private IMongoDatabase _db;
        private IMongoCollection<PlayerRecord> _players;

        public MongoPlayerStore(string connectionString) => _connectionString = connectionString;

        public string Describe
        {
            get
            {
                try { var url = new MongoUrl(_connectionString); return $"MongoDB @ {url.Server} / {url.DatabaseName ?? "wmulti"}"; }
                catch { return "MongoDB"; }
            }
        }

        public void Init()
        {
            var url = new MongoUrl(_connectionString);
            // Fail fast instead of hanging if the cluster is unreachable/paused.
            var settings = MongoClientSettings.FromUrl(url);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(12);
            settings.ConnectTimeout = TimeSpan.FromSeconds(12);
            settings.SocketTimeout = TimeSpan.FromSeconds(12);
            var client = new MongoClient(settings);
            _db = client.GetDatabase(url.DatabaseName ?? "wmulti");
            _players = _db.GetCollection<PlayerRecord>("players");
            _db.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
            try
            {
                _players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                    Builders<PlayerRecord>.IndexKeys.Ascending(p => p.UsernameLower),
                    new CreateIndexOptions { Unique = true, Name = "uniq_username_lower" }));
                _players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                    Builders<PlayerRecord>.IndexKeys.Ascending(p => p.Id4),
                    new CreateIndexOptions { Unique = true, Name = "uniq_id4" }));
            }
            catch (Exception e) { Console.WriteLine("WARN: index setup skipped: " + e.Message); }
        }

        public PlayerRecord FindByUsernameLower(string usernameLower) =>
            _players.Find(p => p.UsernameLower == usernameLower).FirstOrDefault();

        public bool TryInsert(PlayerRecord record)
        {
            try { _players.InsertOne(record); return true; }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                return false;   // lost a race on the unique index
            }
        }

        public void TouchLastLogin(PlayerRecord player) =>
            _players.UpdateOne(
                Builders<PlayerRecord>.Filter.Eq(p => p.Id, player.Id),
                Builders<PlayerRecord>.Update.Set(p => p.LastLogin, DateTime.UtcNow));
    }
}
