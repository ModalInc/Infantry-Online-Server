using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-26, owner: "replace the other swarm location with a Pirate Raiders team, they have 12 pirates on their team, the
    /// same defensives and hp as the queen/king, except the commander is their leader, who has 2500HP. the team itself should
    /// consist of random classes of the pirates ... use the normal skins ... the commander should have the hand maser and pdw
    /// as weapons, along with chainsaw [the F10 shop's Chain Sword] as the melee ... the only person showing hp is the
    /// commander ... names something like Commander X, Infantry x, scout x, heavy weapons x ... behave similar to the bugs" /
    /// "the pirate raiders should be same squishyness as the aliens" / "the pirate commander can also regain health same as
    /// queen" / "the pirate raiders will spawn in the dropship as well, and then on their commander" / "same reward if they
    /// kill the commander" / "SHOULD NOT change anything about the pirate base bots")
    ///
    /// The second swarm run (Script_Eol.AlienSwarm.cs): the Commander is the "nest" - a bot on veh 838 "Pirate Commander"
    /// (the Captain's body, the Queen's armour, 2500 HP) that wanders round his landing spot and heals like the Queen
    /// (Bots/AlienHorde/Queen.cs). 12 raiders guard him from the same guard posts, leash and rally as the bugs; they come
    /// in through the dropship and out of its portal beside him. Normal class bodies (Infantry, Heavy Weapons, Infiltrator =
    /// sniper, Jump Trooper = scout, one Sergeant like the bugs' Chief) with each one's guns, but a bug's HP and armour, so
    /// they take as much killing as the aliens. The team is the .cfg's "Pirate Raiders" (dark jackets). Killing the
    /// Commander pays the killing team the same as the Queen / King.
    /// The base pirates (Titanium Pirates) and their Defender / Roamer bodies are untouched.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool PirateRaidersEnabled = true;
        public const string PirateRaidersTeamName = "Pirate Raiders";
        public const string PirateCommanderTitle = "Pirate Commander";
        /// <summary>veh 838 in PioneerStation.veh: the Captain's body, the Queen's armour, 2500 HP.</summary>
        public const ushort PirateCommanderVehId = 838;
        const int PirateSergeantCls = ClsSergeant;
        const int ItmPirateHandMaser = 3056, ItmPiratePdw = 1514, ItmChainSword = 1075;
        /// <summary>The raider classes, kept even (12 = the Sergeant + about 3 of each).</summary>
        static readonly int[] PirateRaiderClasses = { ClsInfantry, ClsHeavy, ClsInfiltrator, ClsJumpTrooper };
        /// <summary>
        /// Each raider class takes hits like this bug (its HP and armour): Heavy Weapons = Arachnomorph (250), Infantry =
        /// Electromorph (250), Scout = Predator (200), Sniper = Warrior (200), the Sergeant = the Alien Chief (250).
        /// </summary>
        static readonly Dictionary<int, ushort> PirateToughAs = new Dictionary<int, ushort>
        {
            { ClsHeavy, 149 }, { ClsInfantry, 153 }, { ClsJumpTrooper, 154 }, { ClsInfiltrator, 155 }, { ClsSergeant, SwarmChiefVehId },
        };
        static readonly string[] PirateNames = { "Rourke", "Vex", "Skull", "Mako", "Brick", "Ratchet", "Scab", "Grimm", "Jagger",
            "Dutch", "Viper", "Knuckles", "Boone", "Kray", "Slade", "Rook", "Crowe", "Hatch", "Tusk", "Mongrel", "Cutter", "Diesel",
            "Gash", "Wolfe", "Rusty", "Fang", "Stitch", "Bones", "Grub", "Spike", "Vargas", "Moss", "Jinx", "Cobb", "Hex", "Rocco" };

        static string PirateRank(int cls)
        {
            switch (cls)
            {
                case ClsHeavy: return "Heavy Weapons";
                case ClsInfiltrator: return "Sniper";
                case ClsJumpTrooper: return "Scout";
                case ClsSergeant: return "Sergeant";
                case PirateCommanderVehId: return "Commander";
                default: return "Infantry";
            }
        }

        /// <summary>The raider's name ("Commander Vex") - the alias it was made with.</summary>
        string PirateNameOf(Vehicle v)
        {
            AlienHorde a = v as AlienHorde;
            return a != null && !string.IsNullOrEmpty(a.FixedName) ? a.FixedName : PirateCommanderTitle;
        }

        /// <summary>"Rank Name" with a name no living bot or player has.</summary>
        string PickPirateName(int cls)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._alias != null) taken.Add(b._alias);
            foreach (InfServer.Game.Player p in _arena.Players)
                if (p != null && p._alias != null) taken.Add(p._alias);
            string rank = PirateRank(cls);
            int start = _arena._rand.Next(PirateNames.Length);
            for (int i = 0; i < PirateNames.Length; i++)
            {
                string n = rank + " " + PirateNames[(start + i) % PirateNames.Length];
                if (!taken.Contains(n))
                    return n;
            }
            return rank + " " + (100 + _arena._rand.Next(900));
        }

        /// <summary>The raider class the living crew has fewest of (random between ties).</summary>
        int PickPirateRaiderClass()
        {
            Dictionary<int, int> n = new Dictionary<int, int>();
            foreach (int c in PirateRaiderClasses) n[c] = 0;
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._team == botTeamSwarm && !b.bCondemned && (!b.IsDead || b.bDropshipRespawnPending) && b._type != null && n.ContainsKey(b._type.Id))
                    n[b._type.Id]++;
            int min = int.MaxValue;
            foreach (int c in n.Values) if (c < min) min = c;
            List<int> fewest = new List<int>();
            foreach (KeyValuePair<int, int> kv in n) if (kv.Value == min) fewest.Add(kv.Key);
            return fewest[_arena._rand.Next(fewest.Count)];
        }

        /// <summary>A pirate bot of this class / body, named, on the Pirate Raiders team (AlienHorde brain = the bugs' ways).</summary>
        AlienHorde NewPirateBot(int cls, ushort vehId, Helpers.ObjectState at)
        {
            if (!(_arena._server._assets.getVehicleByID(vehId) is VehInfo.Car))
            {
                Log.write(TLog.Warning, "[Eol] Pirate Raiders: vehicle {0} missing / not a Car - skipped.", vehId);
                return null;
            }
            string name = PickPirateName(cls);
            _arena.NextBotAlias = name;
            AlienHorde p = _arena.newBot(typeof(AlienHorde), vehId, botTeamSwarm, null, at, this, null) as AlienHorde;
            _arena.NextBotAlias = null;
            if (p == null)
                return null;
            p.FixedName = name;
            return p;
        }

        /// <summary>The Commander: the run's "nest". Hand Maser + PDW, the Chain Sword up close, heals like the Queen.</summary>
        Vehicle SpawnPirateCommander(Helpers.ObjectState site)
        {
            AlienHorde c = NewPirateBot(PirateCommanderVehId, PirateCommanderVehId, site);
            if (c == null)
                return null;
            EquipCloseFar(c, ItmPiratePdw, ItmPirateHandMaser);   // (close = PDW, far = Hand Maser)
            c.CombatWeaponSelector = null;   // (the human guns by range - not the bugs' own picker)
            ItemInfo saw = Itm(ItmChainSword);
            WeaponController ws = new WeaponController(c._state, new WeaponController.WeaponSettings());
            if (saw != null && ws.equip(saw))
            {
                ws.Owner = c;
                EolBotBrain.SetMiscWeapon(c, ws);
                EolBotBrain.SetPreferMelee(c, true);   // (swings it whenever someone is in reach)
            }
            ApplyPirateToughness(c);
            c.bRespawn = false;   // (killing him ends the raid)
            c.ShowHealthToAll = true;   // (owner: only the Commander shows his HP)
            Log.write(TLog.Normal, "[Eol] {0} landed at {1},{2}.", c.FixedName, site.positionX, site.positionY);
            return c;
        }

        /// <summary>One raider: in the dropship (it walks to the portal and comes out by the Commander), else straight out by him.</summary>
        bool SpawnPirateRaider(int now, bool needSergeant)
        {
            Helpers.ObjectState at = PickDropshipSpawn();
            bool viaDropship = at != null;
            if (at == null && _swarmNest != null && _swarmNest._state != null)
                at = findOpenWarpBot(_arena, _swarmNest._state.positionX, _swarmNest._state.positionY, 220, 28);
            if (at == null)
                return false;
            int cls = needSergeant ? PirateSergeantCls : PickPirateRaiderClass();
            AlienHorde p = NewPirateBot(cls, (ushort)cls, at);
            if (p == null)
                return false;
            p.SkillModeOverride = "veteran";   // (as the bugs)
            ApplyPirateLoadout(p, cls);
            ApplyPirateToughness(p);
            EnableBotDropshipRespawn(p, 0);   // dies -> corpse -> back in the dropship, same raider
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(p);
            if (viaDropship)
                BeginSwarmDeploy(p, now);
            else
                p.DamageImmuneUntilTick = NonZeroTick(now + BotSpawnProtectMs);
            return true;
        }

        /// <summary>The class's guns (the same pick as the human squads), sometimes a few grenades. No cloak, no player armour.</summary>
        void ApplyPirateLoadout(AlienHorde p, int cls)
        {
            try
            {
                int side, main, misc;
                PickClassWeapons(cls, p._team, out side, out main, out misc);
                EquipCloseFar(p, side, main);
                p.CombatWeaponSelector = null;
                if (misc != 0)
                {
                    ItemInfo mi = Itm(misc);
                    WeaponController wm = new WeaponController(p._state, new WeaponController.WeaponSettings());
                    if (mi != null && wm.equip(mi)) { wm.Owner = p; EolBotBrain.SetMiscWeapon(p, wm); }
                }
                int gid = _arena._rand.NextDouble() < 0.35 ? PickClassGrenade(p) : 0;
                ItemInfo g = gid != 0 ? Itm(gid) : null;
                if (g != null)
                {
                    WeaponController grenade = new WeaponController(p._state, new WeaponController.WeaponSettings());
                    if (grenade.equip(g)) { grenade.Owner = p; EolBotBrain.SetExtraWeapons(p, null, grenade, 2 + _arena._rand.Next(2)); }
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] Pirate Raiders loadout failed: " + ex.Message);
            }
        }

        /// <summary>
        /// As tough as the aliens: a raider takes hits with its bug's armour and has its HP (the HP never shows - only the
        /// Commander's does); the Commander has his own veh's (the Queen's armour, 2500 HP). Run again after every respawn.
        /// </summary>
        public void ApplyPirateToughness(Bots.Bot b)
        {
            if (b == null || b._type == null || b._state == null)
                return;
            b.ExtraArmorIgnore = 0;
            b.ExtraArmorPercent = 0;
            b.ExtraArmorPercentBypassEnergy = 0;
            b.DamageTakenPercent = AlienDamageTakenPercent;
            ushort bugId;
            VehInfo bug = PirateToughAs.TryGetValue(b._type.Id, out bugId) ? _arena._server._assets.getVehicleByID(bugId) : null;
            b.ArmorSource = bug;   // (null = its own veh: the Commander)
            int hp = bug != null ? bug.Hitpoints : b._type.Hitpoints;
            if (hp > 0)
            {
                b._state.health = (short)Math.Min(short.MaxValue, hp);
                b._peakHealth = (short)Math.Min(short.MaxValue, hp);
            }
        }
    }
}
