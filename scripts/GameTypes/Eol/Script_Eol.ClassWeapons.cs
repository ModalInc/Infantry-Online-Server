using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using InfServer.Bots;
using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-16, owner) "class specific weps": a class bot only carries guns its class may buy - the same
    /// rule a player meets in the shop (the item's skill logic, checked against the class skill whose default
    /// vehicle is the bot's class; attribute / exp / points requirements count as met). Read from the zone's
    /// .itm / .rpg at the first use, so it follows any item edits. The mix: a harder-hitting main gun with a
    /// pistol or SMG as the close weapon about half the time.
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Pistols and machine pistols (ammo "Ammo - Pistol"), for the close slot.</summary>
        static readonly int[] PoolPistol = { 1035, 1081, 1108, 3016, 1303, 3051, 1305 };

        Dictionary<int, HashSet<int>> _classLegalWeapons;
        static readonly Regex LogicToken = new Regex(@"(!?)([%@#-]?)(\d+)");

        /// <summary>The guns (curated pools only) a class may use; null = class unknown (no filter).</summary>
        HashSet<int> ClassLegalWeapons(int cls)
        {
            if (_classLegalWeapons == null)
                _classLegalWeapons = new Dictionary<int, HashSet<int>>();
            HashSet<int> set;
            if (_classLegalWeapons.TryGetValue(cls, out set))
                return set;
            set = null;
            try
            {
                VehInfo veh = _arena._server._assets.getVehicleByID(cls);
                List<SkillInfo> skills = _arena._server._assets.getSkillInfos;
                List<int> classSkills = new List<int>();
                if (skills != null)
                    foreach (SkillInfo s in skills)
                        if (s != null && s.DefaultVehicleId == cls)
                            classSkills.Add(s.SkillId);
                if (veh != null && classSkills.Count > 0)
                {
                    set = new HashSet<int>();
                    foreach (int id in AllCuratedGuns())
                    {
                        ItemInfo it = Itm(id);
                        if (it != null && ClassMayUse(it.skillLogic, veh.ClassId, classSkills))
                            set.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] class weapons for {0}: {1}", cls, ex.Message);
                set = null;
            }
            _classLegalWeapons[cls] = set;
            if (set != null)
                Log.write(TLog.Normal, "[Eol] class {0}: {1} legal guns", cls, set.Count);
            return set;
        }

        IEnumerable<int> AllCuratedGuns()
        {
            foreach (int[] pool in new[] { PoolRifle, PoolSmg, PoolShotgun, PoolCarbine, PoolHeavy, PoolPlasma, PoolPlasmaSmg, PoolGauss, PoolLaser, PoolProjector, PoolPistol, AssassinClose, AssassinFar, Grenades, PoolProton })
                foreach (int id in pool)
                    yield return id;
            yield return ItmKamenevAPmk2;
        }

        /// <summary>May this class bot carry <paramref name="itemId"/>? (Not a class / unknown: yes.)</summary>
        bool ClassMayCarry(Bots.Bot b, int itemId)
        {
            if (b == null || b._type == null)
                return true;
            HashSet<int> legal = ClassLegalWeapons(b._type.Id);
            return legal == null || legal.Count == 0 || legal.Contains(itemId);
        }

        /// <summary>A grenade the class may throw (0 = none).</summary>
        int PickClassGrenade(Bots.Bot b)
        {
            int start = _arena._rand.Next(Grenades.Length);
            for (int i = 0; i < Grenades.Length; i++)
            {
                int g = Grenades[(start + i) % Grenades.Length];
                if (ClassMayCarry(b, g))
                    return g;
            }
            return 0;
        }

        static bool ClassMayUse(string logic, int classId, List<int> classSkills)
        {
            string s = (logic ?? "").Replace(" ", "").Trim('"');
            if (s.Length == 0)
                return true;
            string b = LogicToken.Replace(s, m =>
            {
                bool val;
                string pre = m.Groups[2].Value;
                int n = int.Parse(m.Groups[3].Value);
                if (pre == "%") val = classId == n;
                else if (pre == "-" || pre == "@" || pre == "#") val = true;   // attribute / exp / points: trained
                else val = classSkills.Contains(n);
                return (val ^ (m.Groups[1].Value == "!")) ? "1" : "0";
            });
            try
            {
                int p = 0;
                return LogicOr(b.TrimStart('&'), ref p);
            }
            catch { return false; }
        }

        static bool LogicOr(string s, ref int p) { bool v = LogicAnd(s, ref p); while (p < s.Length && s[p] == '|') { p++; bool w = LogicAnd(s, ref p); v = v || w; } return v; }
        static bool LogicAnd(string s, ref int p) { bool v = LogicAtom(s, ref p); while (p < s.Length && s[p] == '&') { p++; bool w = LogicAtom(s, ref p); v = v && w; } return v; }
        static bool LogicAtom(string s, ref int p)
        {
            if (p >= s.Length) return false;
            if (s[p] == '!') { p++; return !LogicAtom(s, ref p); }
            if (s[p] == '(') { p++; bool v = LogicOr(s, ref p); if (p < s.Length && s[p] == ')') p++; return v; }
            return s[p++] == '1';
        }

        /// <summary>A random gun of <paramref name="pool"/> the class may use (0 = none).</summary>
        int PickLegal(int[] pool, HashSet<int> legal)
        {
            List<int> ok = new List<int>();
            foreach (int id in pool)
                if (legal.Contains(id))
                    ok.Add(id);
            return ok.Count == 0 ? 0 : ok[_arena._rand.Next(ok.Count)];
        }

        /// <summary>
        /// Swap anything the class can't buy for something it can: close = pistol / SMG / shotgun (a pistol or SMG
        /// about half the time, for the real-player mix), far = a harder hitter of the class.
        /// </summary>
        void EnforceClassWeapons(int cls, ref int close, ref int far, bool mix = true)
        {
            HashSet<int> legal = ClassLegalWeapons(cls);
            if (legal == null || legal.Count == 0)
                return;
            Random r = _arena._rand;
            int[][] closePools = { PoolPistol, PoolSmg, PoolPlasmaSmg, PoolShotgun };
            int[][] farPools = { PoolRifle, PoolHeavy, PoolPlasma, PoolGauss, PoolLaser, PoolProjector, PoolCarbine, PoolShotgun };

            if (!legal.Contains(close) || (mix && r.NextDouble() < 0.5))
            {
                int pick = r.NextDouble() < 0.5 ? PickLegal(PoolPistol, legal) : 0;
                for (int i = 0; pick == 0 && i < closePools.Length; i++)
                    pick = PickLegal(closePools[(i + r.Next(closePools.Length)) % closePools.Length], legal);
                if (pick != 0)
                    close = pick;
                else if (!legal.Contains(close))
                    close = 0;
            }
            if (!legal.Contains(far))
            {
                int pick = 0, start = r.Next(farPools.Length);
                for (int i = 0; pick == 0 && i < farPools.Length; i++)
                    pick = PickLegal(farPools[(start + i) % farPools.Length], legal);
                far = pick != 0 ? pick : close;
            }
            if (close == 0)
                close = far;
        }
    }
}
