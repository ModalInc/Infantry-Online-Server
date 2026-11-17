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

        // (2026-11-17, owner: "the bot weapons they should all have 3 weapons, 1 is the small arms such as the pistols,
        // the second is the main weapon such as a AR / LMG / cannon / fire support, and the third is a misc weapon such
        // melee weapons or laser or sonic or shotgun ... they should rotate around in the fight with the main being the
        // hardest hitting")
        /// <summary>Sidearms: the pistols + the Unittech SP700q sonic pistol.</summary>
        static readonly int[] PoolSidearm = { 1035, 1081, 1108, 3016, 1303, 3051, 1305, 1509 };
        /// <summary>Melee: Combat Knife / Axe / Sword, Chain + Magna Sword, Chain Fist, MagnaFist, Electro Stunner.</summary>
        static readonly int[] PoolMelee = { 1123, 1013, 1080, 1075, 1076, 1174, 1173, 1065 };
        /// <summary>Sonic: Unittech SR2000k rifle, SW2002.</summary>
        static readonly int[] PoolSonic = { 1128, 1039 };

        /// <summary>
        /// The three guns of a human-class bot, all class-legal: a sidearm (pistol, else an SMG), the main gun - the
        /// hardest hitting of the class's rifles / heavies / plasma / gauss / projectors (one of its best two, for
        /// variety; <paramref name="flavour"/> = the class's own pick, kept when it is that good), and a misc gun
        /// (melee, laser, sonic or shotgun). 0 = none for that slot.
        /// </summary>
        void PickThreeWeapons(int cls, int flavour, out int side, out int main, out int misc)
        {
            Random r = _arena._rand;
            HashSet<int> legal = ClassLegalWeapons(cls);
            Func<int, bool> ok = id => id != 0 && (legal == null || legal.Count == 0 || legal.Contains(id)) && Itm(id) != null;

            List<int> sides = new List<int>();
            foreach (int id in PoolSidearm) if (ok(id)) sides.Add(id);
            if (sides.Count == 0) foreach (int[] p in new[] { PoolSmg, PoolPlasmaSmg }) foreach (int id in p) if (ok(id)) sides.Add(id);
            side = sides.Count > 0 ? sides[r.Next(sides.Count)] : 0;

            List<int> mains = new List<int>();
            foreach (int[] p in new[] { PoolRifle, PoolHeavy, PoolPlasma, PoolGauss, PoolProjector, PoolCarbine })
                foreach (int id in p) if (ok(id) && !mains.Contains(id) && id != side) mains.Add(id);
            mains.Sort((a, b) => GunDps(b).CompareTo(GunDps(a)));
            main = mains.Count == 0 ? 0 : mains.Count == 1 ? mains[0] : mains[r.Next(2)];
            if (ok(flavour) && mains.Count > 1 && mains.IndexOf(flavour) >= 0 && mains.IndexOf(flavour) < 2)
                main = flavour;

            List<int> miscs = new List<int>();
            foreach (int[] p in new[] { PoolMelee, PoolLaser, PoolSonic, PoolShotgun })
                foreach (int id in p) if (ok(id) && id != main && id != side && !miscs.Contains(id)) miscs.Add(id);
            misc = miscs.Count > 0 ? miscs[r.Next(miscs.Count)] : 0;

            if (main == 0) main = side != 0 ? side : misc;
            if (side == 0) side = main;
        }

        /// <summary>Rough sustained damage per second of a gun (all damage types, every child of a multi-shot, clip + reload).</summary>
        double GunDps(int id)
        {
            ItemInfo it = Itm(id);
            double dmg = 0; int delay = 0, clip = 1, reload = 0;
            Func<ItemInfo.Projectile, double> hit = pj => pj == null ? 0 : (double)pj.kineticDamageInner + pj.explosiveDamageInner + pj.electronicDamageInner + pj.psionicDamageInner + pj.bypassDamageInner + pj.energyDamageInner;
            if (it is ItemInfo.Projectile pr) { dmg = hit(pr); delay = pr.fireDelay; clip = Math.Max(1, pr.ammoCapacity); reload = pr.reloadDelayNormal; }
            else if (it is ItemInfo.MultiUse mu)
            {
                foreach (var ch in mu.childItems) dmg += hit(Itm(ch.id) as ItemInfo.Projectile);
                delay = mu.fireDelay; clip = Math.Max(1, mu.ammoCapacity); reload = mu.reloadDelayNormal;
            }
            if (dmg <= 0) return 0;
            double cycle = clip * Math.Max(5, delay) + Math.Max(0, reload);   // (game ticks)
            return dmg * clip / cycle;
        }

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
            foreach (int[] pool in new[] { PoolRifle, PoolSmg, PoolShotgun, PoolCarbine, PoolHeavy, PoolPlasma, PoolPlasmaSmg, PoolGauss, PoolLaser, PoolProjector, PoolPistol, AssassinClose, AssassinFar, Grenades, PoolProton, PoolSidearm, PoolMelee, PoolSonic })
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
