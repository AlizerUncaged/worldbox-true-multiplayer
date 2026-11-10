using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace WorldboxMP.Server.Data
{
    /// <summary>MongoDB-backed accounts (the official instance).</summary>
    public class MongoPlayerStore : IPlayerStore
    {
        private readonly string _connectionString;
        private IMongoDatabase _db;
        private IMongoCollection<PlayerRecord> _players;
        private IMongoCollection<SessionRecord> _sessions;
        private IMongoCollection<AdminRecord> _admins;
        private IMongoCollection<BanRecord> _bans;

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
            _sessions = _db.GetCollection<SessionRecord>("sessions");
            _admins = _db.GetCollection<AdminRecord>("admins");
            _bans = _db.GetCollection<BanRecord>("bans");
            _db.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
            try
            {
                _players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                    Builders<PlayerRecord>.IndexKeys.Ascending(p => p.UsernameLower),
                    new CreateIndexOptions { Unique = true, Name = "uniq_username_lower" }));
                _players.Indexes.CreateOne(new CreateIndexModel<PlayerRecord>(
                    Builders<PlayerRecord>.IndexKeys.Ascending(p => p.Id4),
                    new CreateIndexOptions { Unique = true, Name = "uniq_id4" }));
                // TTL index: Mongo deletes expired sessions by itself.
                _sessions.Indexes.CreateOne(new CreateIndexModel<SessionRecord>(
                    Builders<SessionRecord>.IndexKeys.Ascending(x => x.ExpiresAt),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_expires" }));
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

        public PlayerRecord FindById4(string id4) =>
            _players.Find(p => p.Id4 == id4).FirstOrDefault();

        public void SaveSession(string tokenHash, string id4, DateTime expiresUtc) =>
            _sessions.ReplaceOne(x => x.TokenHash == tokenHash,
                new SessionRecord { TokenHash = tokenHash, Id4 = id4, ExpiresAt = expiresUtc },
                new ReplaceOptions { IsUpsert = true });

        public string FindSession(string tokenHash)
        {
            var s = _sessions.Find(x => x.TokenHash == tokenHash).FirstOrDefault();
            return s != null && s.ExpiresAt > DateTime.UtcNow ? s.Id4 : null;
        }

        public void DeleteSession(string tokenHash) =>
            _sessions.DeleteOne(x => x.TokenHash == tokenHash);

        public void DeleteSessionsOf(string id4) =>
            _sessions.DeleteMany(x => x.Id4 == id4);

        public List<PlayerRecord> SearchPlayers(string query, int limit)
        {
            var q = (query ?? "").Trim().ToLowerInvariant();
            var filter = q.Length == 0
                ? Builders<PlayerRecord>.Filter.Empty
                : Builders<PlayerRecord>.Filter.Regex(p => p.UsernameLower, new BsonRegularExpression(Regex.Escape(q)));
            return _players.Find(filter).SortByDescending(p => p.CreatedAt).Limit(limit).ToList();
        }

        public long CountPlayers() => _players.EstimatedDocumentCount();

        public void UpdatePassword(string id4, string hash, string salt) =>
            _players.UpdateOne(p => p.Id4 == id4,
                Builders<PlayerRecord>.Update.Set(p => p.PasswordHash, hash).Set(p => p.PasswordSalt, salt));

        public AdminRecord FindAdmin(string usernameLower) =>
            _admins.Find(a => a.UsernameLower == usernameLower).FirstOrDefault();

        public bool AnyAdmin() => _admins.CountDocuments(Builders<AdminRecord>.Filter.Empty) > 0;

        public void SaveAdmin(AdminRecord admin) =>
            _admins.ReplaceOne(a => a.UsernameLower == admin.UsernameLower, admin, new ReplaceOptions { IsUpsert = true });

        public List<BanRecord> ListBans() =>
            _bans.Find(Builders<BanRecord>.Filter.Empty).SortByDescending(b => b.CreatedAt).ToList();

        public void AddBan(BanRecord ban) => _bans.InsertOne(ban);

        public void RemoveBan(string id) => _bans.DeleteOne(b => b.Id == id);
    }
}
