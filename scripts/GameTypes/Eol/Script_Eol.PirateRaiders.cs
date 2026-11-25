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
    /// (the base pirate Captain's body, darker, the Queen's armour, 2500 HP) that wanders round his landing spot and heals
    /// like the Queen (Bots/AlienHorde/Queen.cs). 12 raiders guard him from the same guard posts, leash and rally as the bugs;
    /// they come in through the dropship and out of its portal beside him.
    /// (2026-11-26, owner: "they have to have those Pirate skins like the base pirates have" / "change the skin color ... darker
    /// more thugs type" / "pirate names instead of class names, except the commander") the raiders are their own darker
    /// copies of the base pirate bodies (veh 839-843, with those bodies' pirate guns): Raider (AR + AC), Heavy (MG x2), Scout
    /// (PDW x2), Sniper (PCAR + Carbine), one Sergeant (Flamer + AR, like the bugs' Chief) - with a bug's HP and armour, so
    /// they take as much killing as the aliens. Pirate names; the Commander is "Commander <name>". Killing the Commander pays
    /// the killing team the same as the Queen / King.
    /// The base pirates (Titanium Pirates) and their veh 140-147 / 156-158 / 162 are untouched.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool PirateRaidersEnabled = true;
        public const string PirateRaidersTeamName = "Pirate Raiders";
        public const string PirateCommanderTitle = "Pirate Commander";
        /// <summary>veh 838 in PioneerStation.veh: the base pirate Captain's body (darker), the Queen's armour, 2500 HP.</summary>
        public const ushort PirateCommanderVehId = 838;
        /// <summary>Darker copies of the base pirate bodies, with their pirate guns: Raider (AR + AC, from 158), Heavy (MG x2,
        /// 143), Scout (PDW x2, 146), Sniper (PCAR + Carbine, 141), Sergeant (Flamer + AR, 140).</summary>
        const int PirateRaiderVeh = 839, PirateHeavyVeh = 840, PirateScoutVeh = 841, PirateSniperVeh = 842, PirateSergeantCls = 843;
        const int ItmPirateHandMaser = 3056, ItmPiratePdw = 1514, ItmChainSword = 1075;
        /// <summary>The raider bodies, kept even (12 = the Sergeant + about 3 of each).</summary>
        static readonly int[] PirateRaiderClasses = { PirateRaiderVeh, PirateHeavyVeh, PirateScoutVeh, PirateSniperVeh };
        /// <summary>
        /// Each raider takes hits like this bug (its HP and armour): Heavy = Arachnomorph (250), Raider = Electromorph (250),
        /// Scout = Predator (200), Sniper = Warrior (200), the Sergeant = the Alien Chief (250).
        /// </summary>
        static readonly Dictionary<int, ushort> PirateToughAs = new Dictionary<int, ushort>
        {
            { PirateHeavyVeh, 149 }, { PirateRaiderVeh, 153 }, { PirateScoutVeh, 154 }, { PirateSniperVeh, 155 }, { PirateSergeantCls, SwarmChiefVehId },
        };
        /// <summary>Pirate names for the raiders (owner: "pirate names instead of class names").</summary>
        static readonly string[] PirateNames = { "Redbeard", "Blackjack", "Calico Jack", "Scurvy Pete", "One-Eye Mako", "Iron Hook",
            "Mad Dog Rourke", "Bilge Rat", "Cutlass Vex", "Dead-Eye Dutch", "Saltbones", "Peg-Leg Slade", "Black Bart", "Bloody Bess",
            "Skull Kray", "Gutter Grimm", "Rusty Anchor", "Barnacle Bill", "Smuggler Jinx", "Gunpowder Gash", "Long John Cobb",
            "Sea Dog Boone", "Plank Walker", "Grog Tusk", "Mutiny Moss", "Shark-Tooth Fang", "Powder Monkey", "Scallywag Stitch",
            "Crowsnest Crowe", "Buccaneer Hex", "Davy Jones", "Cannonball Rocco", "Rumrunner Vargas", "Jolly Roger", "Kraken Wolfe",
            "Sly Ratchet", "Brigand Brick", "Corsair Knuckles", "Marauder Jagger", "Hatchet Hatch" };
        /// <summary>The Commander's second name.</summary>
        static readonly string[] PirateCommanderNames = { "Blackbeard", "Kidd", "Morgan", "Drake", "Flint", "Silver", "Teach",
            "Bonny", "Vane", "Rackham", "Roberts", "Bellamy", "Kraken", "Ironsides", "Dread", "Cutthroat" };

        /// <summary>The raider's name ("Commander Vex") - the alias it was made with.</summary>
        string PirateNameOf(Vehicle v)
        {
            AlienHorde a = v as AlienHorde;
            return a != null && !string.IsNullOrEmpty(a.FixedName) ? a.FixedName : PirateCommanderTitle;
        }

        /// <summary>A pirate name no living bot or player has ("Commander Blackbeard" for the Commander).</summary>
        string PickPirateName(int cls)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._alias != null) taken.Add(b._alias);
            foreach (InfServer.Game.Player p in _arena.Players)
                if (p != null && p._alias != null) taken.Add(p._alias);
            bool cmd = cls == PirateCommanderVehId;
            string[] pool = cmd ? PirateCommanderNames : PirateNames;
            int start = _arena._rand.Next(pool.Length);
            for (int i = 0; i < pool.Length; i++)
            {
                string n = (cmd ? "Commander " : "") + pool[(start + i) % pool.Length];
                if (!taken.Contains(n))
                    return n;
            }
            return (cmd ? "Commander " : "Pirate ") + (100 + _arena._rand.Next(900));
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
            ApplyPirateGrenades(p);   // (their body's own pirate guns are equipped already, as the base pirates')
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

        /// <summary>~35 % carry 2-3 grenades (frag / incendiary / EMP).</summary>
        void ApplyPirateGrenades(AlienHorde p)
        {
            try
            {
                int gid = _arena._rand.NextDouble() < 0.35 ? Grenades[_arena._rand.Next(Grenades.Length)] : 0;
                ItemInfo g = gid != 0 ? Itm(gid) : null;
                if (g != null)
                {
                    WeaponController grenade = new WeaponController(p._state, new WeaponController.WeaponSettings());
                    if (grenade.equip(g)) { grenade.Owner = p; EolBotBrain.SetExtraWeapons(p, null, grenade, 2 + _arena._rand.Next(2)); }
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] Pirate Raiders grenades failed: " + ex.Message);
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
