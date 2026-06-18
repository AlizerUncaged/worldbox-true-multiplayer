#if DEBUG
using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using WorldboxMP.Client.Mirror;

namespace WorldboxMP.Client.Dev
{
    /// <summary>
    /// Hail-mary diagnostics for city/village sync. Dumps EVERY field + object reference of the
    /// first couple of cities on BOTH king and follower every ~2s, so we can diff exactly which
    /// properties hold correct data (name, leader, zones=territory, member units, population,
    /// culture/kingdom links) vs which are empty/stale. Logs to wbmp-client-&lt;pid&gt;.log.
    /// </summary>
    internal static class CityDiag
    {
        private static float _t;
        // Cached reflection handles (resolved lazily; names verified against the real DLL).
        private static FieldInfo _cityData, _cityZones, _cityUnits, _cityStatus, _cityLeader;
        private static MethodInfo _cityNameGet, _getPopPeople;
        private static MethodInfo _idGet, _leaderIdGet, _founderIdGet, _founderNameGet, _kingdomIdGet, _cultureIdGet, _langIdGet, _relIdGet;
        private static FieldInfo _dataZones;
        private static FieldInfo _statusPopGet, _statusFamGet;

        public static void Tick(float dt)
        {
            _t += dt;
            if (_t < 2f) return;
            _t = 0f;
            try
            {
                var mm = MirrorManager.Instance;
                string role = mm == null ? "?" : (mm.IsKing ? "KING" : (mm.IsFollower ? "FOLLOWER" : "idle"));
                var cm = World.world?.cities;
                if (cm == null) return;
                // Enumerate the manager (-> _hashset), NOT the `list` field (rebuilt only by the
                // game sim's checkLists(), which the follower skips -> stale empty `list`).
                var list = cm as IEnumerable;
                if (list == null) { ModLog.Info($"[CITYDIAG] role={role} cities not enumerable (mgr={cm.GetType().Name})"); return; }

                int n = 0, total = 0;
                foreach (var c in list) { total++; }
                foreach (var city in list)
                {
                    if (city == null) continue;
                    if (n++ >= 2) break;
                    Dump(role, city, total);
                }
                if (total == 0) ModLog.Info($"[CITYDIAG] role={role} NO cities in list");
            }
            catch (Exception e) { ModLog.Warn("[CITYDIAG] " + e); }
        }

        private static void Dump(string role, object city, int totalCities)
        {
            var ct = city.GetType();
            _cityData      = _cityData      ?? AccessTools.Field(ct, "data");
            _cityZones     = _cityZones     ?? AccessTools.Field(ct, "zones");
            _cityUnits     = _cityUnits     ?? AccessTools.Field(ct, "units");
            _cityStatus    = _cityStatus    ?? AccessTools.Field(ct, "status");
            _cityLeader    = _cityLeader    ?? AccessTools.Field(ct, "leader");
            _cityNameGet   = _cityNameGet   ?? AccessTools.PropertyGetter(ct, "name");
            _getPopPeople  = _getPopPeople  ?? AccessTools.Method(ct, "getPopulationPeople");

            string name = "?"; try { name = (string)_cityNameGet?.Invoke(city, null); } catch (Exception e) { name = "ERR:" + e.Message; }

            object data = null; try { data = _cityData?.GetValue(city); } catch { }
            long id = -1, leaderID = -1, founderID = -1, kingdomID = -1, cultureID = -1, langID = -1, relID = -1;
            string founderName = "?"; int dataZoneCount = -1;
            if (data != null)
            {
                var dt = data.GetType();
                _idGet         = _idGet         ?? AccessTools.PropertyGetter(dt, "id");
                _leaderIdGet   = _leaderIdGet   ?? AccessTools.PropertyGetter(dt, "leaderID");
                _founderIdGet  = _founderIdGet  ?? AccessTools.PropertyGetter(dt, "founder_id");
                _founderNameGet= _founderNameGet?? AccessTools.PropertyGetter(dt, "founder_name");
                _kingdomIdGet  = _kingdomIdGet  ?? AccessTools.PropertyGetter(dt, "kingdomID");
                _cultureIdGet  = _cultureIdGet  ?? AccessTools.PropertyGetter(dt, "id_culture");
                _langIdGet     = _langIdGet     ?? AccessTools.PropertyGetter(dt, "id_language");
                _relIdGet      = _relIdGet      ?? AccessTools.PropertyGetter(dt, "id_religion");
                _dataZones     = _dataZones     ?? AccessTools.Field(dt, "zones");
                try { id        = Convert.ToInt64(_idGet?.Invoke(data, null) ?? -1L); } catch { }
                try { leaderID  = Convert.ToInt64(_leaderIdGet?.Invoke(data, null) ?? -1L); } catch { }
                try { founderID = Convert.ToInt64(_founderIdGet?.Invoke(data, null) ?? -1L); } catch { }
                try { founderName = (string)_founderNameGet?.Invoke(data, null) ?? "?"; } catch { }
                try { kingdomID = Convert.ToInt64(_kingdomIdGet?.Invoke(data, null) ?? -1L); } catch { }
                try { cultureID = Convert.ToInt64(_cultureIdGet?.Invoke(data, null) ?? -1L); } catch { }
                try { langID    = Convert.ToInt64(_langIdGet?.Invoke(data, null) ?? -1L); } catch { }
                try { relID     = Convert.ToInt64(_relIdGet?.Invoke(data, null) ?? -1L); } catch { }
                try { dataZoneCount = (_dataZones?.GetValue(data) as ICollection)?.Count ?? -1; } catch { }
            }

            int liveZones = -1; try { liveZones = (_cityZones?.GetValue(city) as ICollection)?.Count ?? -1; } catch { }
            int unitCount = -1; try { unitCount = (_cityUnits?.GetValue(city) as ICollection)?.Count ?? -1; } catch { }
            int popPeople = -1; try { popPeople = Convert.ToInt32(_getPopPeople?.Invoke(city, null) ?? -1); } catch { }

            // status.population / families
            int statusPop = -1, statusFam = -1;
            try
            {
                var status = _cityStatus?.GetValue(city);
                if (status != null)
                {
                    _statusPopGet = _statusPopGet ?? AccessTools.Field(status.GetType(), "population");
                    _statusFamGet = _statusFamGet ?? AccessTools.Field(status.GetType(), "families");
                    statusPop = Convert.ToInt32(_statusPopGet?.GetValue(status) ?? -1);
                    statusFam = Convert.ToInt32(_statusFamGet?.GetValue(status) ?? -1);
                }
            }
            catch { }

            // leader actor resolution (why "???"): is the leaderID actor present on this client?
            bool leaderFound = false; string leaderName = "?"; object leaderRef = null;
            try { leaderRef = _cityLeader?.GetValue(city); } catch { }
            try
            {
                if (leaderID > 0)
                {
                    var la = World.world.units.get(leaderID);
                    leaderFound = la != null;
                    if (la != null) leaderName = la.name;
                }
            }
            catch (Exception e) { leaderName = "ERR:" + e.Message; }

            ModLog.Info($"[CITYDIAG] role={role} cities={totalCities} | id={id} name='{name}' " +
                $"leaderID={leaderID} leaderField={(leaderRef != null ? "set" : "null")} leaderActorFound={leaderFound} leaderName='{leaderName}' " +
                $"founderID={founderID} founderName='{founderName}' kingdomID={kingdomID} cultureID={cultureID} langID={langID} relID={relID} " +
                $"liveZones={liveZones} dataZones={dataZoneCount} unitMembers={unitCount} getPopPeople={popPeople} status.pop={statusPop} status.fam={statusFam}");
        }
    }
}
#endif
