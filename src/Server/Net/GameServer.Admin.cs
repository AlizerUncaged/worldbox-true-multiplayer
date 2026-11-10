using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteNetLib;
using WorldboxMP.Server.Data;
using WorldboxMP.Server.Rooms;
using WorldboxMP.Shared.Protocol;

namespace WorldboxMP.Server.Net
{
    /// <summary>
    /// What the admin panel can see and do. The panel runs on Kestrel threads, so every
    /// call is marshalled: game state through the poll thread (<see cref="OnMain{T}"/>),
    /// the database through the auth worker (<see cref="OnDb{T}"/>). No locks anywhere.
    /// </summary>
    public partial class GameServer
    {
        private readonly IPlayerStore _db;
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        // Active bans, poll thread only. Source of truth is the database.
        private readonly Dictionary<string, BanRecord> _ipBans = new();
        private readonly Dictionary<string, BanRecord> _accountBans = new();

        private void LoadBans()
        {
            foreach (var b in _db.ListBans()) TrackBan(b);
        }

        private void TrackBan(BanRecord b)
        {
            if (!b.IsActive) return;
            if (b.Kind == "ip") _ipBans[b.Value] = b;
            else if (b.Kind == "account") _accountBans[b.Value] = b;
        }

        private static bool Active(Dictionary<string, BanRecord> set, string key, out string reason)
        {
            reason = null;
            if (key == null || !set.TryGetValue(key, out var b)) return false;
            if (!b.IsActive) { set.Remove(key); return false; }
            reason = BanMessage(b);
            return true;
        }

        private static string BanMessage(BanRecord b)
        {
            string msg = "You are banned from this server";
            if (b.ExpiresAt.HasValue) msg += $" until {b.ExpiresAt.Value:yyyy-MM-dd HH:mm} UTC";
            if (!string.IsNullOrWhiteSpace(b.Reason)) msg += ": " + b.Reason;
            return msg + ".";
        }

        private bool IsIpBanned(string ip, out string reason) => Active(_ipBans, ip, out reason);
        private bool IsAccountBanned(string id4, out string reason) => Active(_accountBans, id4, out reason);

        // ── Thread marshalling ──────────────────────────────────────────────
        public Task<T> OnMain<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainQueue.Enqueue(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task;
        }

        public Task<T> OnDb<T>(Func<IPlayerStore, T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _authJobs.Add(() => { try { tcs.SetResult(f(_db)); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task;
        }

        // ── Reads ───────────────────────────────────────────────────────────
        public Task<object> AdminOverview() => OnMain<object>(() =>
        {
            var online = _sessions.Values.Where(s => s.IsAuthenticated).Select(s => new
            {
                id4 = s.Id4,
                username = s.Username,
                ip = s.Ip,
                room = s.CurrentRoom?.ShareableId ?? "",
                roomName = s.CurrentRoom?.Name ?? "",
                role = s.CurrentRoom?.Get(s.Id4)?.Role.ToString() ?? "",
                connectedAt = s.ConnectedAtUtc,
                pingMs = s.Peer.RoundTripTime,
            }).OrderBy(p => p.username, StringComparer.OrdinalIgnoreCase).ToList();

            var rooms = _rooms.All.Select(r => new
            {
                id = r.ShareableId,
                name = r.Name,
                lobby = r.IsPermanent,
                visibility = r.Visibility.ToString(),
                hasPassword = r.HasPassword,
                count = r.Count,
                max = r.MaxPlayers,
                king = r.GetKing()?.Username ?? "",
                members = r.Members.Select(m => new { id4 = m.Id4, username = m.Username, role = m.Role.ToString() }).ToList(),
            }).OrderByDescending(r => r.lobby).ThenBy(r => r.lobby ? r.id : "").ThenByDescending(r => r.count).ToList();

            return new
            {
                uptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                port = _port,
                protocol = NetProtocol.ConnectKey,
                connections = _sessions.Count,
                online,
                rooms,
                bans = _ipBans.Count + _accountBans.Count,
            };
        });

        public Task<object> AdminAccounts(string query) => OnDb<object>(db =>
        {
            var list = db.SearchPlayers(query, 100);
            return new
            {
                total = db.CountPlayers(),
                accounts = list.Select(p => new { id4 = p.Id4, username = p.Username, createdAt = p.CreatedAt, lastLogin = p.LastLogin }).ToList(),
            };
        });

        public Task<List<BanRecord>> AdminBans() => OnDb(db => db.ListBans());

        // ── Actions ─────────────────────────────────────────────────────────
        /// <summary>Take a player out of their room (they stay connected).</summary>
        public Task<bool> AdminKickFromRoom(string id4, string reason) => OnMain(() =>
        {
            var s = FindOnline(id4);
            if (s?.CurrentRoom == null) return false;
            _rooms.LeaveRoom(s, notify: true, reason: Reasonable(reason, "Removed from the room by a server admin."));
            return true;
        });

        /// <summary>Drop a player's connection (their client shows the reason).</summary>
        public Task<bool> AdminDisconnect(string id4, string reason) => OnMain(() =>
        {
            var s = FindOnline(id4);
            if (s == null) return false;
            DropSession(s, Reasonable(reason, "Disconnected by a server admin."));
            return true;
        });

        public Task<bool> AdminCloseRoom(string roomId, string reason) => OnMain(() =>
        {
            var room = _rooms.Find(roomId);
            if (room == null) return false;
            _rooms.CloseRoom(room, Reasonable(reason, "The room was closed by a server admin."));
            Log($"[admin] closed room {roomId}.");
            return true;
        });

        public Task<int> AdminAnnounce(string text) => OnMain(() =>
        {
            text = SafeText.Clean(text, 200);
            if (text.Length == 0) return 0;
            int n = 0;
            foreach (var s in _sessions.Values)
                if (s.IsAuthenticated) { s.Peer.Send(_proc, new ServerNotice { Text = text }); n++; }
            Log($"[admin] announcement to {n}: {text}");
            return n;
        });

        /// <summary>Ban an account (value = id4) or an address, then drop matching sessions.</summary>
        public async Task<BanRecord> AdminBan(string kind, string value, string label, string reason, double? hours)
        {
            if (kind != "ip" && kind != "account") throw new ArgumentException("kind must be ip or account");
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.Length > 64) throw new ArgumentException("missing value");
            var ban = new BanRecord
            {
                Kind = kind,
                Value = value,
                Label = SafeText.Clean(label, 64),
                Reason = SafeText.Clean(reason, 200),
                ExpiresAt = hours.HasValue && hours.Value > 0 ? DateTime.UtcNow.AddHours(hours.Value) : null,
            };
            await OnDb(db => { db.AddBan(ban); return true; });
            await OnMain(() =>
            {
                TrackBan(ban);
                foreach (var s in _sessions.Values.ToList())
                    if ((kind == "ip" && s.Ip == value) || (kind == "account" && s.Id4 == value))
                        DropSession(s, BanMessage(ban));
                Log($"[admin] banned {kind} {(kind == "ip" ? value : ban.Label)}.");
                return true;
            });
            return ban;
        }

        public async Task<bool> AdminUnban(string banId)
        {
            await OnDb(db => { db.RemoveBan(banId); return true; });
            return await OnMain(() =>
            {
                foreach (var set in new[] { _ipBans, _accountBans })
                    foreach (var k in set.Where(kv => kv.Value.Id == banId).Select(kv => kv.Key).ToList())
                        set.Remove(k);
                Log("[admin] removed a ban.");
                return true;
            });
        }

        /// <summary>Forget every remembered session of an account and drop it if online.</summary>
        public async Task<bool> AdminLogoutEverywhere(string id4)
        {
            await OnDb(db => { db.DeleteSessionsOf(id4); return true; });
            return await OnMain(() =>
            {
                var s = FindOnline(id4);
                if (s != null) DropSession(s, "You were signed out by a server admin.");
                return true;
            });
        }

        public async Task<bool> AdminSetPassword(string id4, string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 4 || password.Length > 64)
                throw new ArgumentException("Password must be 4-64 characters.");
            return await OnDb(db =>
            {
                if (db.FindById4(id4) == null) return false;
                var (hash, salt) = Auth.AuthService.HashPassword(password);
                db.UpdatePassword(id4, hash, salt);
                db.DeleteSessionsOf(id4);
                return true;
            });
        }

        // ── Helpers (poll thread) ───────────────────────────────────────────
        private Session FindOnline(string id4) =>
            string.IsNullOrEmpty(id4) ? null : _sessions.Values.FirstOrDefault(s => s.Id4 == id4);

        private void DropSession(Session s, string reason)
        {
            _rooms.LeaveRoom(s, notify: false);
            _sessions.Remove(s.Peer);
            try { _net.DisconnectPeer(s.Peer, Reason(reason)); } catch { }
            Log($"[admin] dropped {(s.IsAuthenticated ? s.Username : s.Ip)}: {reason}");
        }

        private static string Reasonable(string reason, string fallback)
        {
            reason = SafeText.Clean(reason, 200);
            return reason.Length == 0 ? fallback : reason;
        }
    }
}
