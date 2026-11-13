// CTF:X server-side bots - MANAGER (config, teams, spawn/respawn, stats, commands, hooks). The brain is CTFXBot.cs.
//
// Optional add-on: CTF.cs never references this file at compile time. It calls CTFXBotManager.Attach by reflection at the
// end of init and exposes four static hooks that Attach fills (poll, *ctfxbots, bot death, human killed by a bot).
// Config: scripts/GameTypes/CTF/CTFX/CTFXBots.txt (never pushed). Missing file or enabled=0 = nothing is wired.
// Everything CTF:X lives in this CTFX folder (the server compiles every .cs under scripts\ recursively, into one assembly).
//
// Engine facts this code relies on (verified against the source, do not re-derive):
//  - A dead Bot must NOT be polled through base.poll(): its dead branch spawns the drop item on every poll. A corpse stays
//    until its slot respawns. A Bot that is not polled for more than 600 ms is destroyed by the engine.
//  - Vehicle.destroy only marks a vehicle condemned (and tells clients); Arena.poll drops it from the vehicle and bot lists
//    on its next pass, before the script poll. ObjTracker.Remove removes BY ID, so a new bot must never take the id of one
//    that is still listed (the removal would unlist the new one). newBot takes the first free id at or after the private
//    Arena._lastVehicleKey.
//  - Bot kills reach Player.Death with killer==null, then Player.BotKill(victim, bot-or-null).
//  - Engine rewards for killing a bot come from the zone [Bot] cfg (ctfpl.cfg has none = 0), so our killCash/Exp/Points
//    payout does not double-pay.
//  - newBot ids start at 5001; newBot returns null when the vehicle is not a Car or vehicles are full.
//  - Script_CTF statics are shared by every arena running the script: only one arena is attached at a time.
//  - Pathfinder callbacks run on the pathing thread (the bot hands results over under a lock).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal enum CTFXStyle { Post, Guard, Assault, Roam, Sniper, Drain, Adhd, Stormer, Engineer, Bountier, Maser }

    internal enum CTFXKind { Titan, Collective, Private, Solo }

    /// <summary>A named region: one or more centers (tiles) and a radius (tiles).</summary>
    internal sealed class CTFXZone
    {
        public string Name;
        public int R;
        public int[] Cx;
        public int[] Cy;
        public CTFXZone(string name, int r, int[] cx, int[] cy) { Name = name; R = r; Cx = cx; Cy = cy; }
    }

    /// <summary>A spawn area: center in tiles, size in pixels.</summary>
    internal sealed class CTFXArea
    {
        public string Name;
        public int Cx, Cy, W, H;
        public CTFXArea(string name, int cx, int cy, int w, int h) { Name = name; Cx = cx; Cy = cy; W = w; H = h; }
    }

    /// <summary>One seat in a group. A bot fills it; when the bot dies the same slot respawns.</summary>
    internal sealed class CTFXSlot
    {
        public CTFXBotGroup Group;
        public CTFXStyle Style;
        public int Index;        // position inside the group
        public string Name;      // player-list callsign, kept across respawns
        public int StyleIndex;   // position among the slots of the same style (0 = squad leader kit)
        public int PostIndex;    // round-robin post number (Post style)
        public ushort BotId;     // live bot, 0 = none
        public ushort FixedId;   // the vehicle id this seat keeps for life (its first body's); every respawn takes it again
        public ushort RowId;     // botPlayerIds=low: the id below 5000 every body of this seat takes (0 = engine-picked, 5001+)
        public CTFXHuman Human;  // the seat's player-like traits (CTFXHuman.cs), rolled at its first spawn, kept every life
        public Player Entry;     // player-list row (name + team uniform), id = FixedId; outlives every body
        public HashSet<Player> Holders = new HashSet<Player>();   // 10-24: the players whose client holds Entry; only they get its leave / team change
        public CTFXIntent Intent; // its activity (CTFXCommand): anchor, defend, skirmish, raid, wander, hunt; null = not a base team's
        public int Kills, Deaths, Streak, BestStreak, BountyPeak;   // the skill spread (CTFXSpread.cs)
        public long Ms10k, Ms30k;
        // the damage ledger (Travis 10-15: "some bots were very very tanky"): every hit's HP damage split into what the energy shield,
        // the armour (body + utilities) and the under-2-HP floor kept off, and what landed; plus what healed it
        public int TkHits, TkStims, TkEnergizers, TkRecalls;
        public double TkRaw, TkShield, TkArmour, TkFloor, TkHp, TkHealed, TkEnergyAtHit;
        public int Bounty;        // its bounty this life (the zone [Bounty] rules, as a player's): grows with kills, back to Start on death
        public bool Courier;      // 10-17: the team's infiltrator flag courier (CTFXFlags: beelines for flags, runs them to the engineers, sweeps for enemy infiltrators)
        public int ForceClass;    // mustHave: this seat always plays this class (skill id), 0 = its style rolls it (CTFXBot.EnsureClasses)
        public Player AttachTo;   // a human it answered (attack call, or his summon): it fights around him until AttachUntil (CTFXChat)
        public int AttachUntil;
        public bool AttachSummoned; // 10-21: he summoned it with his own summoner - it stays with him whatever its job (CTFXChat.Step)
        public Vehicle Ghost;    // last body: out of the server lists, still on clients until the next body replaces it
        public int IdWaits;      // respawn polls spent waiting for FixedId to come free
        public int Fails;
        public bool Retired;
        public bool Queued;
        public bool Benched;      // 10-22 (Galaxy / Travis): a human took a seat on its team: it idles in the dropship until he leaves (CTFXRoom.cs)
        public CTFXBotGroup RoomHome; // 10-22: the group it was displaced from (benched, or sent to another team's group); null = at home
        public Team HomeTeam;     // 10-22: the team its line seated it on; CTFXRoom counts a team's seats by it
        public string BuildName;  // MiniTP: the zone build (CTFXBuilds.txt) this seat buys each life, null = the class's hand kit
        public string Role;       // MiniTP: SL MED ENG DHVY DINF OHVY OINF JT INFIL
        public bool Attack;       // MiniTP: an attack-group seat
        public bool MiniOut;      // MiniTP fill-to-N: a human took a seat on the team, this one is stood down (Retired too); it returns when he leaves
    }

    /// <summary>One team= line (or one solo bot): its own team, home zone, squad leaders.</summary>
    internal sealed class CTFXBotGroup
    {
        public CTFXKind Kind;
        public Team Team;
        public CTFXZone Home;
        public string HomeEnd;      // a base end of the route book (brown-top ...): spawns, goals and retreat stay in it
        public string Quad;         // its building (brown / blue / purple / green): the quadrant the group is locked to
        public bool Attackers;      // Collective with home=brown: spawns in area F or C
        public int NearestArea;
        public readonly List<CTFXSlot> Slots = new List<CTFXSlot>();
        public readonly ushort[] Leader = new ushort[16];   // squad leader bot id per style (0 = none)
        public string CoreOf;       // home = <quad>-core: lives in the building's middle hall (CoreGoal), raids its top end
        public CTFXStorm Storm;     // the storm cycle (CTFXSquads), null when the group has no assault slots + stormer
        public bool Outside;        // home=outside: brawls in the neutral landing areas A-H (CTFXDropship.OutsideGoal)
        public bool Ambush;         // zapper:N - a cloaked SciOps that lies in wait on a straggler's path (CTFXBounty ambush)
        public bool DropPack;       // droppack:N - the drop pack Jump Trooper sniper bountier (Travis 10-15)
    }

    internal sealed class CTFXGroupSpec
    {
        public CTFXKind Kind;
        public string Home = "";
        public bool Ambush;
        public bool DropPack;
        public readonly List<KeyValuePair<CTFXStyle, int>> Styles = new List<KeyValuePair<CTFXStyle, int>>();
        public int Total() { int n = 0; foreach (KeyValuePair<CTFXStyle, int> kv in Styles) n += kv.Value; return n; }
    }

    internal sealed class CTFXConfig
    {
        public bool Enabled;
        public bool AutoStart = true;
        public string SelfRoute = "auto";
        public int StatsEverySec = 30;
        public int KillCash = 75, KillExp = 5, KillPoints = 100;
        public int ShotgunPellets = 9;     // class research: the SG and ACW fire 9 pellets
        public bool Doors = true;      // doors=0 switches the door navigation (CTFXDoors.cs) off
        public int UpdateMs = 200;     // updateMs: bot state routing cadence; 200 = the engine's own 5 Hz (no extra packets), lower = smoother, more packets
        public int UpdateNearMs = 200; // updateNearMs: the cadence for a bot within nearPx of a human (player or spectator); 200 = off
        public int NearPx = 800;
        public int NearCap = 12;       // nearCap: at most this many bots per human get the fast cadence (the nearest)
        public int FarPx = 1200, UpdateFarMs = 400;   // farPx / updateFarMs: a bot no human is within farPx of routes this slowly
        public bool Ammo = true;       // ammo=0: unlimited ammo (no stock, no rebuy)
        public double Shop = 0.0;      // shop: share of lives that buy one of their class's zone builds (CTFXShop, CTFXBuilds.txt)
        public double AmmoScale = 1.0; // ammoScale: multiplies every starting stock (testing: 0.05 runs bots dry fast)
        public double KnockScale = 1.0; // knockScale: multiplies every knockback push on bots (calibrate from the [CTFXKb] human lines)
        public string BotAntiwarpBots = "flag"; // botAntiwarpBots: flag = an enemy bot's anti-warp holds only a bot carrying a flag; on = every bot; off = none
        // rename=Bex 69>Skunky 69 (repeat the line or comma-separate): a generated callsign shown under another name (Travis 10-21: a
        // player called Skunky asked for one). Names are picked when the bots attach (boot, or the arena re-opening).
        public readonly Dictionary<string, string> Renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string NameWrap = "";      // nameWrap: two characters around the callsign players see ({} -> "{Jett 45}"); empty = plain
        // Travis 10-21 ("could the bots' vision range be too small?"): viewScale multiplies the 420 x 320 px view box, engageScale the
        // style engage radii (420-700 px) and the travelling caps; 1 = as before, 0.5-2.5 allowed. *ctfxbots reload + *ctfxbots on.
        public double ViewScale = 1.0, EngageScale = 1.0;
        public bool Stun = true;       // stun=0: bots ignore anti-effects (stunner, tranq, EMP, haywire ...)
        public bool UseRoutes = true;  // routes=0: walk with the engine pathfinder instead of the route book (CTFXRoutes.txt)
        public bool BotPlayers = true; // botPlayers=0: no player-list entries (names, team uniforms) for the bots
        public bool LowIds = true;     // botPlayerIds=low|vehicle: bodies (and rows) take ids below 5000, as players' bodies do, or the
                                       // engine's 5001+ (the old client never treats 5000+ as a player: no name, uniform, radar, warp)
        public bool Squads = true;     // squads=0: no teleport grouping, blink storms or defender recalls (CTFXSquads.cs)
        public bool WarpFx;            // warpFx=1: bot warps send clients the item-used notice (every client plays the warp sound: a
                                       // player's own warp only reloads his own item, so it is off by default, Travis 10-12)
        public bool Command = true;    // command=0: no team commander / intents (CTFXCommand.cs): the styles' own goals
        public int Anchors = 2, AlarmAt = 2;
        public double DefendRatio = 1.5, PauseScale = 1.0, HerdK = 1.2;
        public bool Tune = true;       // tune=0: the commander's knobs stay as configured (no self-tuning, CTFXTune.txt ignored)
        public bool Dropship;          // dropship=1: bodies start in the CTF:X dropship and leave through a MapEnter portal (CTFXDropship.cs)
        public double OutsideShare = 0.15;   // outsideShare: at most this share of the base teams' bots brawl in the landing areas at once
        public double BrownWander = 0.35;    // brownWander: share of the other teams' wanders that go through the brown building
        public int BrownTarget = 30;          // brownTarget: below this many bots in the brown building, other teams' free bots are sent there
        public bool DropshipHeadsAll = true;   // dropshipHeads=all|players: do bots count as heads for the spawn-area tiers
        public bool MakeRoom = true;   // makeRoom=on|off (10-22): a human joining a bot team sends one of its bots out (CTFXRoom.cs)
        public int TeamCap = 12;       // teamcap=N: no Titan / Collective / private team line seats more than N bots (0 = no cap)
        public int Count;              // count=N: autostart scales the team lines to N bots (0 = the lines as written)
        public int PerSide = 14;       // perside=N (MiniTP profile): bots per team, 5 / 7 / 10 / 14 (humans take seats from them)
        public string FocusEnd = "brown-top";      // focusEnd / focusShare: the base everyone vies for
        public double FocusShare = 0.45;
        public string MustHave = "Cpt,Med,Eng";   // mustHave: classes every team of mustHaveMin+ bots carries (Travis 10-13: a summoner, a medic,
        public int MustHaveMin = 7;                // an engineer on every big team)
        public double FlagShare = 0.72; // flagShare: share of each team's free bots on flag business (Travis 10-14 eve: 70-75 %)
        public bool MineFriendly;      // mineFriendly=1 (MiniTP): bots also lay the mines whose damage mode hurts their own team (bullet / plasma / AP); 0 = only the enemy-only ones (EMP, grapeshot)
        public bool Utilities = true;  // utilities=0: no armour / generator / energy item on bots (A/B for the 10-15 tankiness report)
        public bool ClanOn;            // clan=1: the elite clan forms and takes top brown (CTFXClan.cs)
        public bool MilkTest;          // milktest=1: hand the risen clan every flag once, to test the milk (CTFXMilk.cs)
        public bool MilkOn;            // milk=1: the risen clan milks the flag instead of letting the game reset (CTFXMilk.cs)
        public bool Packs;             // packs=1: movement packs on long trips (CTFXPacks.cs); off until Travis has seen it in his client
        public bool Flags = true;      // flags=0: the bots leave the event flags alone (CTFXFlags.cs)
        public bool Chat = true, Listen = true;  // chat=0: bots never speak; listen=0: they ignore what humans say (CTFXChat.cs)
        public int ChatTeamGap = 45, ChatBotGap = 180;   // seconds: one callout per team / per bot at most
        public string AttackBongs = "24";      // attackBongs: chat sound numbers that mean "attacking" (the %24 macro)
        public string SummonBongs = "27";      // summonBongs: sound numbers that mean a summon request (a "Summon ..." macro)
        public string EnemySummonBongs = "";   // enemySummonBongs: sound numbers that mean "summon me to the enemy"
        public string HomeSummonBongs = "";    // homeSummonBongs: sound numbers that mean "summon me home"
        public string UnderAttackBongs = "28"; // underAttackBongs: sound numbers that mean "we are under attack" (a desperate call home)
        public string SightedBongs = "26";     // sightedBongs: sound numbers that mean "enemy sighted" (a lighter warning at home)
        public string TdTiers = "veteran,ace"; // tdTiers: the bot tiers that carry a Teleport Disruptor (rookie,regular,veteran,ace)
        public bool TurretSelfTest;    // turretSelfTest=1: knock one bot-built turret down 60 s after a start (rebuild check)
        public readonly List<CTFXGroupSpec> Groups = new List<CTFXGroupSpec>();
        public readonly List<CTFXGroupSpec> Solos = new List<CTFXGroupSpec>();
        public readonly List<int[]> Posts = new List<int[]>();
        public readonly List<string[]> Turrets = new List<string[]>();   // turret=<end>,<set>,<role>,<x>,<y> (CTFXTurrets.cs)

        public static CTFXConfig Load()
        {
            return LoadFrom(Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXBots.txt")))));
        }

        /// <summary>MiniTP/MiniTPBots.txt (same key=value grammar; null when the file is missing).</summary>
        public static CTFXConfig LoadMiniTP()
        {
            return LoadFrom(Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("MiniTP", "MiniTPBots.txt")))));
        }

        static CTFXConfig LoadFrom(string path)
        {
            if (!File.Exists(path))
                return null;
            CTFXConfig c = new CTFXConfig();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw;
                int cm = line.IndexOf("//", StringComparison.Ordinal);
                if (cm >= 0)
                    line = line.Substring(0, cm);
                line = line.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || eq <= 0)
                    continue;
                try { c.Apply(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim()); }
                catch (Exception) { }   // lenient: a bad line is skipped
            }
            return c;
        }

        void Apply(string key, string val)
        {
            switch (key)
            {
                case "enabled": Enabled = ParseBool(val); break;
                case "autostart": AutoStart = ParseBool(val); break;
                case "makeroom": MakeRoom = ParseBool(val); break;
                case "teamcap": TeamCap = Math.Max(0, Math.Min(60, ParseInt(val, 12))); break;
                case "count": Count = Math.Max(0, Math.Min(300, ParseInt(val, 0))); break;
                case "perside": PerSide = Math.Max(2, Math.Min(14, ParseInt(val, 14))); break;
                case "selfrouteshots": SelfRoute = val.ToLowerInvariant(); break;
                case "statseverysec": StatsEverySec = Math.Max(5, ParseInt(val, 30)); break;
                case "killcash": KillCash = ParseInt(val, 75); break;
                case "killexp": KillExp = ParseInt(val, 5); break;
                case "killpoints": KillPoints = ParseInt(val, 100); break;
                case "shotgunpellets": ShotgunPellets = Math.Max(1, ParseInt(val, 9)); break;
                case "doors": Doors = ParseBool(val); break;
                case "updatems": UpdateMs = Math.Max(20, Math.Min(1000, ParseInt(val, 200))); break;
                case "updatenearms": UpdateNearMs = Math.Max(20, Math.Min(1000, ParseInt(val, 200))); break;
                case "nearpx": NearPx = Math.Max(100, Math.Min(4000, ParseInt(val, 800))); break;
                case "nearcap": NearCap = Math.Max(0, Math.Min(100, ParseInt(val, 12))); break;
                case "farpx": FarPx = Math.Max(400, Math.Min(8000, ParseInt(val, 1200))); break;
                case "updatefarms": UpdateFarMs = Math.Max(200, Math.Min(1000, ParseInt(val, 400))); break;
                case "ammo": Ammo = ParseBool(val); break;
                case "ammoscale": AmmoScale = ParseScale(val); break;
                case "knockscale": KnockScale = ParseScale(val); break;
                case "botantiwarpbots": BotAntiwarpBots = val.Trim().ToLowerInvariant(); break;
                case "viewscale": ViewScale = Math.Max(0.5, Math.Min(2.5, ParseScale(val))); break;
                case "engagescale": EngageScale = Math.Max(0.5, Math.Min(2.5, ParseScale(val))); break;
                case "rename":
                    foreach (string pair in val.Split(','))
                    {
                        int gt = pair.IndexOf('>');
                        if (gt <= 0) continue;
                        string from = pair.Substring(0, gt).Trim(), to = pair.Substring(gt + 1).Trim();
                        if (from.Length > 0 && to.Length > 0 && to.Length <= 24) Renames[from] = to;
                    }
                    break;
                case "namewrap": NameWrap = val.Trim().Length == 2 ? val.Trim() : ""; break;
                case "shop": { double v; if (double.TryParse(val.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) Shop = Math.Max(0.0, Math.Min(1.0, v)); } break;
                case "stun": Stun = ParseBool(val); break;
                case "routes": UseRoutes = ParseBool(val); break;
                case "botplayers": BotPlayers = ParseBool(val); break;
                case "botplayerids": LowIds = val.Trim().ToLowerInvariant() != "vehicle"; break;
                case "squads": Squads = ParseBool(val); break;
                case "warpfx": WarpFx = ParseBool(val); break;
                case "command": Command = ParseBool(val); break;
                case "anchors": Anchors = Math.Max(0, Math.Min(4, ParseInt(val, 2))); break;
                case "alarmat": AlarmAt = Math.Max(1, ParseInt(val, 2)); break;
                case "defendratio": DefendRatio = Math.Max(0.5, Math.Min(3.0, ParseScale(val))); break;
                case "pausescale": PauseScale = Math.Max(0.2, Math.Min(2.0, ParseScale(val))); break;
                case "herdk": HerdK = Math.Max(0.0, Math.Min(4.0, ParseScale(val))); break;
                case "tune": Tune = ParseBool(val); break;
                case "dropship": Dropship = ParseBool(val); break;
                case "outsideshare": OutsideShare = Math.Max(0.0, Math.Min(1.0, ParseScale(val))); break;
                case "browntarget": { int bt; if (int.TryParse(val.Trim(), out bt)) BrownTarget = Math.Max(0, Math.Min(100, bt)); } break;   // a count: ParseScale caps at 10
                case "brownwander": BrownWander = Math.Max(0.0, Math.Min(1.0, ParseScale(val))); break;
                case "dropshipheads": DropshipHeadsAll = val.Trim().ToLowerInvariant() != "players"; break;
                case "musthave": MustHave = val; break;
                case "focusend": FocusEnd = val.Trim().Length == 0 || val.Trim() == "none" ? null : val.Trim().ToLowerInvariant(); break;
                case "focusshare": FocusShare = Math.Max(0.0, Math.Min(1.0, ParseScale(val))); break;
                case "musthavemin": MustHaveMin = Math.Max(1, ParseInt(val, 7)); break;
                case "flags": Flags = ParseBool(val); break;
                case "packs": Packs = ParseBool(val); break;
                case "clan": ClanOn = ParseBool(val); break;
                case "milk": MilkOn = ParseBool(val); break;
                case "milktest": MilkTest = ParseBool(val); break;
                case "utilities": Utilities = ParseBool(val); break;
                case "minefriendly": MineFriendly = ParseBool(val); break;
                case "flagshare": FlagShare = Math.Max(0.0, Math.Min(1.0, ParseScale(val))); break;
                case "chat": Chat = ParseBool(val); break;
                case "listen": Listen = ParseBool(val); break;
                case "chatteamgap": ChatTeamGap = Math.Max(5, ParseInt(val, 45)); break;
                case "chatbotgap": ChatBotGap = Math.Max(10, ParseInt(val, 180)); break;
                case "attackbongs": AttackBongs = val; break;
                case "summonbongs": SummonBongs = val; break;
                case "enemysummonbongs": EnemySummonBongs = val; break;
                case "underattackbongs": UnderAttackBongs = val; break;
                case "sightedbongs": SightedBongs = val; break;
                case "tdtiers": TdTiers = val; break;
                case "homesummonbongs": HomeSummonBongs = val; break;
                case "turretselftest": TurretSelfTest = ParseBool(val); break;
                case "team": AddSpec(Groups, ParseSpec(val, false)); break;
                case "solo": AddSpec(Solos, ParseSpec(val, true)); break;
                case "turret": Turrets.Add(val.Split(',')); break;
                case "post":
                    {
                        string[] p = val.Split(',');
                        if (p.Length >= 2)
                            Posts.Add(new int[] { ParseInt(p[0], 0), ParseInt(p[1], 0) });
                        break;
                    }
            }
        }

        static void AddSpec(List<CTFXGroupSpec> list, CTFXGroupSpec g)
        {
            if (g != null && g.Total() > 0)
                list.Add(g);
        }

        static CTFXGroupSpec ParseSpec(string val, bool solo)
        {
            CTFXGroupSpec g = new CTFXGroupSpec();
            string[] parts = val.Split(',');
            int start = 0;
            if (solo)
                g.Kind = CTFXKind.Solo;
            else
            {
                string k = parts[0].Trim().ToLowerInvariant();
                g.Kind = k.StartsWith("titan") ? CTFXKind.Titan : k.StartsWith("coll") ? CTFXKind.Collective : CTFXKind.Private;
                start = 1;
            }
            for (int i = start; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.Length == 0)
                    continue;
                if (t.StartsWith("home=", StringComparison.OrdinalIgnoreCase))
                {
                    g.Home = t.Substring(5).Trim().ToLowerInvariant();
                    continue;
                }
                int colon = t.IndexOf(':');
                string sn = colon < 0 ? t : t.Substring(0, colon);
                int cnt = colon < 0 ? 1 : ParseInt(t.Substring(colon + 1), 0);
                CTFXStyle st;
                if (sn.Trim().ToLowerInvariant() == "zapper") g.Ambush = true;      // a bountier seat that ambushes (Zap Glove)
                if (sn.Trim().ToLowerInvariant() == "droppack") g.DropPack = true;  // the drop pack Jump Trooper sniper
                if (cnt > 0 && TryStyle(sn, out st))
                    g.Styles.Add(new KeyValuePair<CTFXStyle, int>(st, cnt));
            }
            return g;
        }

        internal static bool TryStyle(string s, out CTFXStyle st)
        {
            st = CTFXStyle.Roam;
            switch (s.Trim().ToLowerInvariant())
            {
                case "post": st = CTFXStyle.Post; return true;
                case "guard": st = CTFXStyle.Guard; return true;
                case "assault": st = CTFXStyle.Assault; return true;
                case "roam": st = CTFXStyle.Roam; return true;
                case "sniper": st = CTFXStyle.Sniper; return true;
                case "drain": st = CTFXStyle.Drain; return true;
                case "adhd": st = CTFXStyle.Adhd; return true;
                case "stormer": st = CTFXStyle.Stormer; return true;
                case "engineer": st = CTFXStyle.Engineer; return true;
                case "bountier": case "bounty": st = CTFXStyle.Bountier; return true;
                case "maser": st = CTFXStyle.Maser; return true;
                case "zapper": st = CTFXStyle.Bountier; return true;
                case "droppack": st = CTFXStyle.Bountier; return true;
            }
            return false;
        }

        static bool ParseBool(string s)
        {
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "on" || s == "yes";
        }

        static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static double ParseScale(string s)
        {
            double v;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 1.0;
            return Math.Max(0.01, Math.Min(10.0, v));
        }
    }

    /// <summary>
    /// CTF:X bot manager. One instance per process (Script_CTF statics are shared). Everything is keyed by bot _id and
    /// removed when the bot is destroyed; no Player references are kept.
    /// </summary>
    internal sealed partial class CTFXBotManager
    {
        // ---------------------------------------------------------------- map data (tiles; px = tile * 16)
        internal const int AreaX0 = 29, AreaX1 = 980, AreaY0 = 1060, AreaY1 = 2023;

        static readonly CTFXArea[] Areas = new CTFXArea[]
        {
            new CTFXArea("A", 504, 1159, 1000, 300), new CTFXArea("B", 504, 1967, 1000, 300),
            new CTFXArea("C", 919, 1587, 300, 1000), new CTFXArea("D", 83, 1587, 300, 1000),
            new CTFXArea("E", 353, 1587, 300, 1000), new CTFXArea("F", 648, 1587, 300, 1000),
            new CTFXArea("G", 501, 1422, 1000, 300), new CTFXArea("H", 501, 1702, 1000, 300)
        };
        const int AreaC = 2, AreaF = 5;

        internal static readonly Predicate<LvlInfo.Tile> NotBlocked = delegate(LvlInfo.Tile t) { return !t.Blocked; };

        // ---------------------------------------------------------------- singleton
        static CTFXBotManager _inst;

        internal readonly CTFXZone ZoneBrown, ZoneCenter, ZoneTop, ZoneBottom, ZoneLeft, ZonePockets;
        internal readonly CTFXZone[] OtherZones;   // top, bottom, left, pockets

        internal readonly Arena _arena;
        internal CTFXDoorNav Doors;               // region graph + door opening (null = door navigation off or the build failed)
        readonly Script_CTF _script;
        /// <summary>*halloween for everyone: the item a bot fires as clients should draw it (its Halloween skin), else unchanged.</summary>
        internal int HalloweenItem(int itemId) { return _script != null ? _script.HalloweenBotItem(itemId) : itemId; }
        CTFXConfig _cfg, _pendingCfg;
        internal readonly Random Rng = new Random();
        internal bool SelfRoute;
        internal int ShotgunPellets = 9;

        // Explosion knockback per projectile id. The server item loader skips column 121 (explosionRecoilVehicle), so the zone
        // .itm is read once at attach with the server CSV parser. Only items with a non-zero push are kept.
        internal struct KnockInfo { public int Force, Radius, IgnoreMs, DirPct; public bool Absolute; }
        internal readonly Dictionary<int, KnockInfo> Knock = new Dictionary<int, KnockInfo>();
        internal double KnockScale = 1.0;              // knockScale (CTFXBots.txt)
        internal double ViewScale = 1.0, EngageScale = 1.0;   // viewScale / engageScale (CTFXBots.txt), read by every bot at use
        internal int FarAnswers;                       // stats: shooters a bot turned on from beyond its engage radius (CTFXBot.ScanTargets)

        // ---- knockback calibration (Travis 10-20: bots are not pushed as far as players by the same blast). A bot shot that bursts near a
        // human: his client applies the push itself, and his next 1.5 s of positions (his own updates) show how fast it really threw
        // him. One [CTFXKb] line per event (the first 150), beside the speed the bot model gives the same hit - then set knockScale.
        // 10-21: players run ~200 px/s on their own, so each line also gives his speed over the 250-500 ms BEFORE the blast (push =
        // peak - before); every in-game human's position is sampled each 250 ms for it (KbSampleHumans).
        sealed class KbWatch { public Player P; public int T0, Item, Dist, Model, Ignore, LastX, LastY, LastT, Before; public double Peak, Early; public int Samples; }
        readonly Dictionary<Player, int[]> _kbPre = new Dictionary<Player, int[]>();   // human -> last x, y, t, previous x, y, t
        int _kbPreAt;

        void KbSampleHumans(int now)
        {
            if (KbLogged >= 150 || now - _kbPreAt < 250) return;
            _kbPreAt = now;
            if (_kbPre.Count > 32) _kbPre.Clear();                               // players come and go: start over now and then
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p == null || p._state == null) continue;
                int[] s;
                if (!_kbPre.TryGetValue(p, out s)) { s = new int[6]; _kbPre[p] = s; s[0] = p._state.positionX; s[1] = p._state.positionY; s[2] = now - 250; }
                s[3] = s[0]; s[4] = s[1]; s[5] = s[2];                            // last -> previous (250-500 ms old when a blast comes)
                s[0] = p._state.positionX; s[1] = p._state.positionY; s[2] = now;
            }
        }
        readonly List<KbWatch> _kbWatch = new List<KbWatch>();
        internal int KbLogged;

        internal void WatchHumanKnock(Player p, int item, int dist, double modelPxS, int ignoreMs)
        {
            if (KbLogged >= 150 || p == null || p._state == null || _kbWatch.Count >= 16) return;
            foreach (KbWatch w in _kbWatch) if (w.P == p) return;
            int now = Environment.TickCount, before = -1;
            int[] s;
            if (_kbPre.TryGetValue(p, out s) && now - s[5] >= 150)
            {   // how fast he was moving before it hit him (y scaled as in KbWatchStep)
                double bx = p._state.positionX - s[3], by = (p._state.positionY - s[4]) / 0.7;
                before = (int)(Math.Sqrt(bx * bx + by * by) * 1000.0 / (now - s[5]));
            }
            _kbWatch.Add(new KbWatch { P = p, T0 = now, Item = item, Dist = dist, Model = (int)modelPxS, Ignore = ignoreMs,
                LastX = p._state.positionX, LastY = p._state.positionY, LastT = now, Before = before });
        }

        void KbWatchStep(int now)
        {
            for (int i = _kbWatch.Count - 1; i >= 0; i--)
            {
                KbWatch w = _kbWatch[i];
                if (w.P == null || w.P._state == null || w.P.IsDead || w.P._arena != _arena) { _kbWatch.RemoveAt(i); continue; }
                int x = w.P._state.positionX, y = w.P._state.positionY;
                if (x != w.LastX || y != w.LastY)
                {   // a new position from his client: the speed since the last one
                    int dt = Math.Max(1, now - w.LastT);
                    double dx = x - w.LastX, dy = (y - w.LastY) / 0.7, v = Math.Sqrt(dx * dx + dy * dy) * 1000.0 / dt;
                    if (dt >= 40 && v < 5000) { if (v > w.Peak) w.Peak = v; if (now - w.T0 <= 400 && v > w.Early) w.Early = v; w.Samples++; }
                    w.LastX = x; w.LastY = y; w.LastT = now;
                }
                if (now - w.T0 < 1500) continue;
                _kbWatch.RemoveAt(i);
                if (w.Samples == 0) continue;
                KbLogged++;
                ItemInfo it = AssetManager.Manager.getItemByID(w.Item);
                Out("[CTFXKb] " + w.P._alias + " pushed by " + (it != null ? it.name : "?") + " (" + w.Item + ") at " + w.Dist + " px: before " + (w.Before >= 0 ? w.Before + " px/s" : "?") + ", peak "
                    + (int)w.Peak + " px/s, first 400 ms " + (int)w.Early + " px/s (" + w.Samples + " updates) | bot model " + w.Model
                    + " px/s x knockScale " + KnockScale.ToString("0.##") + ", top speed off " + w.Ignore + " ms");
            }
        }
        internal int Knocks;                      // knockbacks applied (stats)
        internal int UpdateMs = 200;               // bot state routing cadence (CTFXBot.poll): 200 = the engine's own, below 200 = extra packets
        internal int UpdateNearMs = 200, NearPx = 800;   // faster cadence for bots within NearPx of a human (NearHuman)
        internal long StunRoutes, FarSkips;                  // extra routes sent the moment a stun began
        internal int HumanStuns;                   // stuns from a human hit (the rest are bot-vs-bot)
        internal int OpeningUntil, OpeningWaits;
        int _lastLoop, _gapMax, _gapN;
        long _gapSum;

        string LoopText()
        {
            string t = " | server loop: arena poll gap avg " + (_gapN > 0 ? (_gapSum / _gapN).ToString() : "-") + " ms, worst " + _gapMax + " ms";
            _gapSum = 0; _gapN = 0; _gapMax = 0;
            return t;
        }   // a game start: dropship exits staggered over ~3 min until this tick (CTFXBot.EnterDropship)
        bool _wasRunning;
        internal long TargetCommits, TargetDrops;  // targets taken / given up (churn)
        internal int RechargeBacks, NoFireHere;
        internal string FocusEnd = "brown-top";    // focusEnd: the base everyone vies for (raids aim at it, holders bring flags there)
        internal double FocusShare = 0.45;         // focusShare: the share of raids aimed at it
        internal int MedicHeals, HumanHeals, Stims, Energizes, TurretRepairs;   // healing (CTFXBot.HealStep)

        /// <summary>A medic area heal: every teammate within the kit radius (500 px) that is hurt, itself included; true = someone
        /// needed it (no use wasted on a healthy team). Bots get the health here, humans the engine way (their client applies it).</summary>
        internal bool HealArea(CTFXBot medic, ItemInfo.RepairItem kit, int now)
        {
            int r = Math.Abs(kit.repairDistance);
            if (r <= 0) r = 500;
            long r2 = (long)r * r;
            List<CTFXBot> bots = null;
            List<Player> people = null;
            foreach (CTFXBot o in _bots.Values)
            {
                if (o.IsDead || o.InDropship || o._team != medic._team || o._state.health >= 0.85 * o._type.Hitpoints) continue;
                if (o == medic && !kit.repairSelf) continue;
                long dx = o._state.positionX - medic._state.positionX, dy = o._state.positionY - medic._state.positionY;
                if (dx * dx + dy * dy <= r2) (bots ?? (bots = new List<CTFXBot>())).Add(o);
            }
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p.IsDead || p._team != medic._team || p._baseVehicle == null || p._state.health >= 0.85 * p._baseVehicle._type.Hitpoints) continue;
                long dx = p._state.positionX - medic._state.positionX, dy = p._state.positionY - medic._state.positionY;
                if (dx * dx + dy * dy <= r2) (people ?? (people = new List<Player>())).Add(p);
            }
            if (bots == null && people == null) return false;
            if (!medic.SpendEnergy(kit.id)) return false;
            if (bots != null) foreach (CTFXBot o in bots)
                {
                    int h0 = o._state.health;
                    double ddx = o._state.positionX - medic._state.positionX, ddy = o._state.positionY - medic._state.positionY;
                    double fall = kit.repairDistance < 0 ? Math.Max(0.25, 1.0 - Math.Sqrt(ddx * ddx + ddy * ddy) / r) : 1.0;     // a negative repairDistance is a radius with falloff (11-08: the bots healed everybody in 500 px at full strength)
                    o._state.health = (short)Math.Min(o._type.Hitpoints, o._state.health + (int)Math.Round(kit.repairAmount * fall));
                    if (o.Slot != null) o.Slot.TkHealed += o._state.health - h0;
                }
            if (people != null && medic.Slot != null && medic.Slot.Entry != null)
                foreach (Player p in people) { p.heal(kit, medic.Slot.Entry, medic._state.positionX, medic._state.positionY); HumanHeals++; }
            return true;
        }

        /// <summary>What a medic area heal would find: how many teammates (bots and players, itself included) within r px are under half health, and the lowest health fraction among
        /// the hurt ones (1.0 = nobody hurt). No side effects: the medic chooses its kit by this (CTFXBot.HealStep).</summary>
        internal void MedicNeed(CTFXBot medic, int r, out int deep, out double worst)
        {
            deep = 0; worst = 1.0;
            long r2 = (long)r * r;
            foreach (CTFXBot o in _bots.Values)
            {
                if (o.IsDead || o.InDropship || o._team != medic._team) continue;
                double f = (double)o._state.health / Math.Max(1, o._type.Hitpoints);
                if (f >= 0.85) continue;
                long dx = o._state.positionX - medic._state.positionX, dy = o._state.positionY - medic._state.positionY;
                if (dx * dx + dy * dy > r2) continue;
                if (f < 0.5) deep++;
                if (f < worst) worst = f;
            }
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p.IsDead || p._team != medic._team || p._baseVehicle == null) continue;
                double f = (double)p._state.health / Math.Max(1, p._baseVehicle._type.Hitpoints);
                if (f >= 0.85) continue;
                long dx = p._state.positionX - medic._state.positionX, dy = p._state.positionY - medic._state.positionY;
                if (dx * dx + dy * dy > r2) continue;
                if (f < 0.5) deep++;
                if (f < worst) worst = f;
            }
        }

        /// <summary>The nearest live medic of b team within r px (not b itself).</summary>
        internal CTFXBot NearestMedic(CTFXBot b, double r)
        {
            CTFXBot best = null;
            double bd = r * r;
            foreach (CTFXBot o in _bots.Values)
            {
                if (o == b || o.IsDead || o.InDropship || o._team != b._team || o.ClassId != CTFXBot.SK_MEDIC) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }
        /// <summary>Living teammates of b within r px (RunBeatsFight: a bot with mates beside it fights instead of running).</summary>
        internal int MatesNear(CTFXBot b, double r)
        {
            int n = 0; double r2 = r * r;
            foreach (CTFXBot o in _bots.Values)
            {
                if (o == b || o.IsDead || o.InDropship || o._team != b._team) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY;
                if (dx * dx + dy * dy < r2) n++;
            }
            // Travis 10-21 (Waylander): a bot beside a human teammate counted itself alone and fell back - the humans count too
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p._state == null || p._team != b._team) continue;
                double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY;
                if (dx * dx + dy * dy < r2) n++;
            }
            return n;
        }
        internal int FallBacks, CoverRetreats, AloneFalls;                    // outnumbered seats that fell back (CTFXBot.Outnumbered)
        internal int ChipTurrets;                  // doomed bots that turned on the turret killing them (CTFXBot.ScanTargets)
        internal int StandAndFight;               // RunBeatsFight said stand: a traveller that turned to fight instead of running
        internal int Repulses, DisruptTraps;      // defensive tools used (CTFXBot.PanicStep)                // backed off to recharge: no weapon affordable with an enemy in sight
        internal long Slides;                      // key pairs swapped because the body would have hit a wall (CTFXBot.Drive)
        internal int NearGoalArrivals;             // pinned within 64 px of the goal: taken as arrived
        // with a target, why no shot (one sample per poll): 0 no LOS, 1 no weapon, 2 fire-mode hold, 3 clip/reload/interval, 4 lob
        // out of range, 5 aim not on, 6 first-shot settle, 7 energy, 8 fired
        internal readonly long[] Hold = new long[9];
        static readonly string[] HoldNames = new string[] { "no LOS", "no weapon", "prefire/stand", "reload/interval", "lob range", "aiming", "settle", "energy", "fired" };
        internal void HoldSample(int why) { if (why >= 0 && why < Hold.Length) Hold[why]++; }

        string HoldText()
        {
            long t = 0;
            foreach (long v in Hold) t += v;
            if (t == 0) return "";
            StringBuilder sb = new StringBuilder(" | with a target (polls " + t + "):");
            for (int i = 0; i < Hold.Length; i++)
                if (Hold[i] > 0) sb.Append(' ').Append(HoldNames[i]).Append(' ').Append((Hold[i] * 100 / t)).Append('%');
            sb.Append(", targets taken ").Append(TargetCommits).Append(" dropped ").Append(TargetDrops);
            sb.Append(" | nav: wall slides ").Append(Slides).Append(", stuck ").Append(StuckEvents).Append(", pinned by the goal ").Append(NearGoalArrivals)
              .Append(" | backed off to recharge ").Append(RechargeBacks).Append(", no weapon usable on the terrain ").Append(NoFireHere).Append(", heals: medic ").Append(MedicHeals).Append(" (humans ").Append(HumanHeals).Append("), stims ").Append(Stims)
              .Append(", energizers ").Append(Energizes).Append(", turret repairs ").Append(TurretRepairs)
              .Append(", heavies pulled back ").Append(Squads.PostRescues).Append(" (walked back ").Append(Squads.PostFallbacks).Append(")")
              .Append(", repulsor shoves ").Append(Repulses).Append(", turret guard repulses ").Append(Turrets.Guards)
              .Append(" (human splash shots watched ").Append(Turrets.HumanThreats).Append(", shots shoved ").Append(Shots.Repelled).Append(")").Append(", disruptor traps ").Append(DisruptTraps);
            return sb.ToString();
        }
        readonly List<int> _humanXY = new List<int>(16);  // human positions (x, y pairs), refreshed every 250 ms
        int _lastHumanXY;

        /// <summary>Does this bot get the fast cadence? Only the NearCap bots nearest each human (within NearPx), so a crowd on a
        /// human's screen cannot flood his client (Travis 10-13: up to 223 pkt/s at 100 bots when every bot within 800 px was fast).</summary>
        internal bool NearHuman(int x, int y) { return false; }
        internal bool Fast(ushort id) { return _fast.Contains(id); }
        /// <summary>No human (player or spectator) within FarPx: nobody can see it, so it routes at UpdateFarMs (Travis 10-13: at 100
        /// bots the client fell behind and bots phased about; at 50 it was smooth).</summary>
        internal bool Far(int x, int y) { return FarMs(x, y) > 0; }

        /// <summary>The route interval for a bot at (x, y) by its distance to the nearest human: 0 = normal (within FarPx),
        /// UpdateFarMs out to FarPx2, twice that beyond (radar dots only). The local server.xml routes every update to every
        /// player (routeRange 99999), so this is what keeps 100 bots within a client budget.</summary>
        internal int FarMs(int x, int y)
        {
            // Travis 10-14 VOD: bots were jumpy when he panned. At 4K the client shows about +-1900 x +-1080 px of the map, so a 1200 px
            // circle called a whole strip of on-screen bots "far" (2.5 Hz). The test is now a screen-shaped box: within ViewW x ViewH of
            // any human = normal cadence; out to twice that = UpdateFarMs; beyond = radar dots only.
            double best = double.MaxValue;
            for (int i = 0; i + 1 < _humanXY.Count; i += 2)
            {
                double ex = Math.Abs(_humanXY[i] - x) / (double)ViewW, ey = Math.Abs(_humanXY[i + 1] - y) / (double)ViewH, e = Math.Max(ex, ey);
                if (e < best) best = e;
            }
            if (best <= 1.0) return 0;
            return best <= 2.0 ? UpdateFarMs : UpdateFarMs * 2;
        }
        internal int FarPx = 1200, FarPx2 = 2500, UpdateFarMs = 400;
        internal int ViewW = 2050, ViewH = 1200;   // half the widest client view (4K: 1920 x 1080) plus a margin for the pan
        internal int NearCap = 12;
        readonly HashSet<ushort> _fast = new HashSet<ushort>();
        readonly List<KeyValuePair<long, ushort>> _near = new List<KeyValuePair<long, ushort>>(64);

        void RefreshHumans(int now)
        {
            if (now - _lastHumanXY < 100) return;
            _lastHumanXY = now;
            try { WatchJumps(); } catch (Exception ex) { LogErr("burst", ex); }
            _humanXY.Clear();
            _fast.Clear();
            long r2 = (long)NearPx * NearPx;
            foreach (Player p in _arena.Players)
            {
                // Travis 10-14: a spectator snapping to a far bot saw it stutter: the test used the spectator's own body, not the view
                Helpers.ObjectState v = p == null ? null : ViewOf(p);
                if (v == null) continue;
                _humanXY.Add(v.positionX); _humanXY.Add(v.positionY);
                _near.Clear();
                foreach (CTFXBot b in _bots.Values)
                {
                    if (b.IsDead) continue;
                    long dx = b._state.positionX - v.positionX, dy = b._state.positionY - v.positionY, d = dx * dx + dy * dy;
                    if (d <= r2) _near.Add(new KeyValuePair<long, ushort>(d, b._id));
                }
                _near.Sort(delegate(KeyValuePair<long, ushort> a, KeyValuePair<long, ushort> c) { return a.Key.CompareTo(c.Key); });
                for (int i = 0; i < _near.Count && i < NearCap; i++) _fast.Add(_near[i].Value);
            }
        }
        internal bool Ammo = true, Stun = true;    // ammo / stun knobs (CTFXBots.txt)
        internal double AmmoScale = 1.0;
        internal int Stuns, DryEvents;             // stats: anti-effect hits, whole-kit-dry rebuys
        internal int EnergyRefusals, WarpEnergyRefusals;  // stats: shots / warps a bot could not afford
        internal long ShieldMilliHp;               // stats: HP the energy shield kept off bots (thousandths)
        internal bool Utilities = true;            // utilities= knob (CTFXUtilities.cs)
        internal bool EnergyShield = true;         // zone cfg EnergyShieldMode > 0: energy scales HP damage
        internal double EnergyDefaultMax = 400;    // zone cfg EnergyDefaultMax / 1000 (a VEH energyMax below 0 takes it)
        internal int EnergyDefaultRate = 100;      // zone cfg EnergyDefaultRate (thousandths per 10 ms; a VEH energyRate below 0 takes it)
        internal bool BotPlayers = true;           // player-list entries (names + team uniforms) for the bots
        internal bool LowIds;                      // bodies take their slot's id below 5000 (decided at each start, AssignLowIds)
        readonly Dictionary<ushort, CTFXSlot> _lowSlots = new Dictionary<ushort, CTFXSlot>();   // low id -> slot (team warps to bots)
        volatile CTFXSlot[] _lowById = new CTFXSlot[0];   // [4999 - id] -> slot, replaced whole at each start (read off-thread)
        internal int WarpsToBots, KillersMapped;   // stats: team warps that landed on a bot / death reports naming a bot, handed on
        internal int Spectates, WatchedHits;

        /// <summary>A human spectator is following this bot now (its hits are logged, [CTFXHit]).</summary>
        internal bool Watched(CTFXBot b)
        {
            if (b.Slot == null || b.Slot.Entry == null) return false;
            foreach (Player p in _arena.Players)
                if (p != null && p.IsSpectator && p._spectating == b.Slot.Entry) return true;
            return false;
        }                    // stats: spectators who started following a bot
        internal long ReactMsSum;                  // stats: reaction times drawn for newly seen enemies (CTFXHuman)
        internal int Reacts;
        internal int WarpEnergyCharged;            // stats: players' team warps to a bot whose energy the server took off
        int _nameNext;
        static readonly string[] Callsigns = new string[]
        {
            "Kestrel", "Vex", "Brask", "Orrin", "Cobalt", "Juno", "Ragnar", "Mako", "Sable", "Dax", "Quill", "Rook", "Talon", "Fennick",
            "Lumen", "Pike", "Rhett", "Silo", "Tova", "Wren", "Arlo", "Bex", "Cato", "Drix", "Esme", "Faro", "Gage", "Holt", "Jett",
            "Knox", "Mira", "Nox", "Pax", "Rafe", "Tor", "Vale", "Wade", "Yara", "Zed", "Bram", "Corin", "Dune", "Ember", "Flint", "Grit",
            "Hex", "Ike", "Kai", "Lark", "Moss", "Nash", "Onyx", "Pyre", "Reed", "Slate", "Thorn", "Umber", "Vance", "Wolfe", "Yates"
        };
        internal CTFXRouteBook Routes;             // null = no route book: the engine pathfinder is used (routes=0 or no file)
        internal int RoutePlans, RouteFails;
        internal readonly List<string> EndIds = new List<string>();
        readonly Dictionary<string, List<int>> _spawnNodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        readonly List<int> _neutralNodes = new List<int>(), _caveNodes = new List<int>();

        struct SpawnReq { public CTFXSlot Slot; public int Due; }

        readonly Dictionary<ushort, CTFXBot> _bots = new Dictionary<ushort, CTFXBot>();
        readonly List<CTFXBotGroup> _groups = new List<CTFXBotGroup>();
        readonly List<SpawnReq> _spawnQ = new List<SpawnReq>();
        readonly List<ushort> _recycle = new List<ushort>();
        internal int IdsKept, IdsMoved;            // stats: respawns under the slot's own id / respawns that had to take a new one
        internal int EngineRemovals;               // stats: bodies the engine destroyed (game-start vehicle reset) that respawned quietly
        readonly HashSet<int> _badBodies = new HashSet<int>();
        readonly HashSet<string> _warned = new HashSet<string>();
        readonly List<int[]> _posts = new List<int[]>();

        // brown base inner points (tiles): BL / BR = the left / right flag + turret room, BM = top middle, BS = south middle.
        // Nudged to the nearest open tile within 6 at attach (NudgeInner); brown guards / assault leaders / roamers aim at them.
        const int InnerBL = 0, InnerBR = 1, InnerBM = 2, InnerBS = 3;
        static readonly int[][] InnerSrc = new int[][] { new int[] { 708, 1424 }, new int[] { 810, 1445 }, new int[] { 771, 1433 }, new int[] { 755, 1470 } };
        static readonly string[] InnerNames = new string[] { "BL", "BR", "BM", "BS" };
        readonly int[] _innerX = new int[] { 708, 810, 771, 755 }, _innerY = new int[] { 1424, 1445, 1433, 1470 };

        // hot spots: ring buffer of the last 20 hit / kill positions (px)
        readonly int[] _hsX = new int[20], _hsY = new int[20], _hsT = new int[20];
        int _hsHead, _hsCount;

        // delegates we put into the Script_CTF hooks (kept so Detach only clears its own)
        Action<int> _dPoll;
        Func<Player, string, bool> _dCmd;
        Action<Bot, Player, int> _dDeath;
        Action<Player, Bot> _dKilled;
        Action<Player> _dEnter;
        Func<int> _dHeads;
        Func<Player, string, string, bool> _dComm;
        Func<Player, bool> _dWatch, _dRouteKill;
        Func<int, int, string> _dPlace;
        Func<List<int[]>> _dShip;
        Func<Player, ItemInfo.RepairItem, ushort, bool> _dRepair;
        Func<Player, string, bool> _dInfo;
        internal int HealsByPlayers, HealsByPlayersHp; // stats: bots healed by players' items, hp (or energy) given

        /// <summary>Arena thread (CTF.cs Player.Repair): a player's health / energy repair item, applied to his team's bots the way the
        /// engine applies it to players - repairDistance &gt; 0 = the one target (its id, in range), &lt; 0 = everyone within that radius,
        /// 0 = himself only (no bot). Amount = repairAmount + repairPercentage of the max (the engine's own two formulas).</summary>
        bool HumanRepair(Player p, ItemInfo.RepairItem item, ushort target)
        {
            if (!_on || p == null || p._team == null || item == null || p._arena != _arena) return false;
            if (item.repairType != 0 && item.repairType != 2) return false;  // 1 = vehicle repair: the engine already reaches bots (vehicles)
            if (item.repairDistance > 0)
            {
                CTFXBot b = GetBot(target);
                if (b == null) return false;                                  // aimed at a player: the engine's job
                if (b.IsDead || b._team != p._team || b.InDropship) return true;
                if (!Helpers.isInRange(item.repairDistance, b._state, p._state)) return true;
                HealBot(b, item);
                return true;
            }
            else if (item.repairDistance < 0)
            {
                long r = -item.repairDistance;
                foreach (CTFXBot b in _bots.Values)
                {
                    if (b.IsDead || b._team != p._team || b.InDropship) continue;
                    long dx = b._state.positionX - p._state.positionX, dy = b._state.positionY - p._state.positionY;
                    if (dx * dx + dy * dy <= r * r) HealBot(b, item);
                }
            }
            return false;                                                     // area / self: the engine heals the players too
        }

        void HealBot(CTFXBot b, ItemInfo.RepairItem item)
        {
            int got = b.TakeHeal(item);
            if (got <= 0) return;
            HealsByPlayers++;
            HealsByPlayersHp += got;
        }

        /// <summary>CTF.cs WarpToCTFXSpawn: the dropship ring spots when the ring was built (null = use CTFXEvent.txt portalspawn=).</summary>
        List<int[]> ShipSpots() { return Dropship != null && Dropship.RingSpots > 0 ? Dropship.SideSpots : null; }

        /// <summary>Arena thread: a player %command; the team macros (CTFXChat.Comm) work whether or not the bots are running.</summary>
        bool CommCommand(Player player, string command, string payload)
        {
            try { return player != null && player._arena == _arena && Chat.Comm(player, command, payload); }
            catch (Exception ex) { LogErr("comm", ex); return false; }
        }

        /// <summary>Live bot bodies, when the dropship counts bots as heads (dropshipHeads=all): the CTF:X landing tiers add them.</summary>
        int BotHeads()
        {
            if (!_on || !_cfg.DropshipHeadsAll) return 0;
            int n = 0;
            foreach (CTFXBot b in LiveBots) if (!b.IsDead && !b.Released) n++;
            return n;
        }

        // state
        bool _on, _manualOff;
        internal static bool MiniOff;      // *bots off in the MiniTP profile: stays off across event changes and re-attaches until *bots on
        int _lastPoll, _lastSpawn, _lastStats, _lastHumanCheck, _lastLostCheck, _lastErr, _inactiveSince;
        int _humans, _noHumanSince, _postCounter;
        bool _noHumans;
        string _lastStatsLine = "(no stats yet)";

        // counters (stats line)
        internal int PathReq, PathFail, StuckEvents, Recycled;
        internal long PollTicks;
        internal int PollCount;
        int _pathInFlight;
        int _killsBB, _killsBH, _killsHB, _killsOther;

        // ================================================================ attach / detach
        public static void Attach(Script_CTF script, Arena arena)
        {
            try
            {
                CTFXBotManager cur = _inst;
                if (cur != null && cur._arena != arena && cur._arena != null && cur._arena._bActive)
                {
                    Out("[CTFXBots] second arena ignored: already attached to another active arena");
                    return;
                }
                if (cur != null)
                    cur.Detach("re-attach");
                // profile: MiniTP when the arena runs the MiniTP event and MiniTP/MiniTPBots.txt is enabled, else the CTF:X one (as always)
                CTFXProfile prof = CTFXProfile.Default;
                CTFXConfig cfg = null;
                if (script.CtfxEventIsMiniTP)
                {
                    cfg = CTFXConfig.LoadMiniTP();
                    if (cfg != null && cfg.Enabled) prof = CTFXProfile.MiniTP;
                    else cfg = null;
                }
                if (cfg == null)
                {
                    cfg = CTFXConfig.Load();
                    if (cfg == null) { Out("[CTFXBots] CTFXBots.txt not found: inert"); return; }
                    if (!cfg.Enabled) { Out("[CTFXBots] enabled=0: inert"); return; }
                }
                CTFXBotManager m = new CTFXBotManager(script, arena, cfg, prof);
                m.Init();
                _inst = m;
                HookPackets();
            }
            catch (Exception ex) { Shout("[CTFXBots] attach failed: " + ex); }
        }

        /// <summary>The arena's event changed (Script_CTF.CtfxAttachBots): switch profile when the wanted one differs from the running one.
        /// Same profile = nothing happens, so CTF:X-only zones are not touched by event changes.</summary>
        public static void EventChanged(Script_CTF script, Arena arena)
        {
            try
            {
                CTFXBotManager cur = _inst;
                bool wantMini = false;
                if (script.CtfxEventIsMiniTP)
                {
                    CTFXConfig mc = CTFXConfig.LoadMiniTP();
                    wantMini = mc != null && mc.Enabled;
                }
                if (cur != null && cur._arena == arena && cur.Prof.IsMiniTP == wantMini) return;
                if (cur == null && !wantMini && !script.CtfxEventIsMiniTP) return;     // nothing running, nothing wanted
                Out("[CTFXBots] event changed: " + (cur != null ? cur.Prof.Name : "none") + " -> " + (wantMini ? "minitp" : "ctfx"));
                Attach(script, arena);
            }
            catch (Exception ex) { Shout("[CTFXBots] event change failed: " + ex); }
        }

        /// <summary>The running profile (CTFXProfile.Default = CTF:X, exactly as before profiles existed).</summary>
        internal CTFXProfile Prof = CTFXProfile.Default;

        CTFXBotManager(Script_CTF script, Arena arena, CTFXConfig cfg, CTFXProfile prof)
        {
            _script = script;
            _arena = arena;
            _cfg = cfg;
            Prof = prof;
            _manualOff = prof.IsMiniTP && MiniOff;
            ZoneBrown = new CTFXZone("brown", 90, new int[] { 753 }, new int[] { 1566 });
            ZoneCenter = new CTFXZone("center", 60, new int[] { 501 }, new int[] { 1565 });
            ZoneTop = new CTFXZone("top", 90, new int[] { 500 }, new int[] { 1322 });
            ZoneBottom = new CTFXZone("bottom", 90, new int[] { 500 }, new int[] { 1804 });
            ZoneLeft = new CTFXZone("left", 90, new int[] { 251 }, new int[] { 1566 });
            int[] px = new int[Areas.Length], py = new int[Areas.Length];
            for (int i = 0; i < Areas.Length; i++) { px[i] = Areas[i].Cx; py[i] = Areas[i].Cy; }
            ZonePockets = new CTFXZone("pockets", 40, px, py);
            OtherZones = new CTFXZone[] { ZoneTop, ZoneBottom, ZoneLeft, ZonePockets };
            int t0 = Environment.TickCount - 100000;      // deadlines use now - last >= x (wrap safe), so start well in the past
            _lastPoll = t0; _lastSpawn = t0; _lastHumanCheck = t0; _lastLostCheck = t0; _lastErr = t0;
            _lastStats = Environment.TickCount;
            Shots = new CTFXShots(this);
            Squads = new CTFXSquads(this);
            Turrets = new CTFXTurrets(this);
            Cmdr = new CTFXCommand(this);
            Monitor = new CTFXMonitor(this);
            Dropship = new CTFXDropship(this);
            Chat = new CTFXChat(this);
            Flags = new CTFXFlags(this);
            Bounty = new CTFXBounty(this);
        }

        internal CTFXBounty Bounty;                // bounty packs and solo bountiers (CTFXBounty.cs)

        internal CTFXFlags Flags;                  // the event flags: the opening dash, carry home, kill transfers (CTFXFlags.cs)

        internal CTFXSlot SlotOfRow(uint id) { return LowSlot(id); }

        // ---- bot updates that carry the bot bounty. A client reads a player bounty from every update: the item field holds -bounty
        // when no item is used, but only when the packet names a player; the engine routes bots with none, so every client was told
        // 0 five to ten times a second (Travis 10-13: 0 next to every bot name). Routed here like Helpers.Update_RouteBot, with the
        // slot row as the player and its bounty set; then the object trackers as ArenaBot.pollBots does after its own route.
        static FieldInfo _fVeh, _fBots;
        static MethodInfo _mVeh, _mBots;
        internal int SelfRoutes;

        internal bool RouteSelf(CTFXBot b) { return RouteSelf(b, 0); }

        // ---- per-player routing (Travis 10-13: a player should not get bot updates from a quadrant he is not in, only what the
        // original game routed: a distance and a cone of his facing; and after a warp the bots around him at once, so bots always
        // look fluid). Each player gets a bot update when the bot is within RouteNear of where he looks from, or within RouteCone
        // inside his facing cone (+-RouteConeDeg); a shot reaches anyone within RouteCone; further out only every RadarEvery-th
        // update (to RouteRadar) or FarEvery-th (beyond), enough for radar dots. A spectator looks from the player he follows. A
        // jump of 600+ px (a warp, a respawn, a new arrival) gets every bot near the new spot sent at once (Burst).
        internal int RouteNear = 1000, RouteCone = 1800, RouteConeDeg = 70, RouteRadar = 2600, RadarEvery = 3, FarEvery = 9;
        internal long ClientSends, Bursts;
        readonly List<Player> _aud = new List<Player>(16);

        static Helpers.ObjectState ViewOf(Player p)
        {
            if (p.IsSpectator && p._spectating != null)
            {
                // Travis 10-15: "the bots fade away after a [ or ] until I inch the camera". A spectated bot is its player-list row, a
                // bare Player whose own state never moves (0,0); the view is the row's live body, else (dead, between bodies) his camera
                CTFXBotManager m = _inst;
                CTFXSlot s;
                if (m != null && m._lowSlots.TryGetValue(p._spectating._id, out s))
                {
                    CTFXBot b = s.BotId != 0 ? m.GetBot(s.BotId) : null;
                    return b != null && !b.Released && b._state != null ? b._state : p._state;
                }
                if (p._spectating._state != null) return p._spectating._state;
            }
            return p._state;
        }

        List<Player> Audience(CTFXBot b, bool weapon)
        {
            _aud.Clear();
            int n = b._state.updateNumber;
            double cone = Math.Cos(RouteConeDeg * Math.PI / 180.0);
            foreach (Player p in _arena.Players)
            {
                if (p == null || p._client == null) continue;
                Helpers.ObjectState v = ViewOf(p);
                if (v == null) continue;
                double dx = b._state.positionX - v.positionX, dy = (b._state.positionY - v.positionY) / 0.7, d = Math.Sqrt(dx * dx + dy * dy);
                bool send;
                if (d <= RouteNear) send = true;
                else if (d <= RouteCone)
                {
                    double r = v.yaw * 1.5 * Math.PI / 180.0, fx = Math.Sin(r), fy = -Math.Cos(r);
                    send = weapon || (dx * fx + dy * fy) / d >= cone;
                }
                else if (weapon) send = false;
                else if (d <= RouteRadar) send = n % RadarEvery == 0;
                else send = n % FarEvery == 0;
                if (send) _aud.Add(p);
            }
            return _aud;
        }

        void SendBot(CTFXBot b, Player p, short itemId, int now)
        {
            Player row = b.Slot.Entry;
            row._bounty = b.Slot.Bounty;
            if (p.IsSpectator || p._team == b._team)
            {
                SC_PlayerTeamUpdate tu = new SC_PlayerTeamUpdate();
                tu.tickUpdate = now; tu.player = row; tu.vehicle = b; tu.itemID = itemId; tu.activeUtilities = b._activeEquip; tu.bBot = true;
                p._client.send(tu);
            }
            else
            {
                SC_PlayerUpdate up = new SC_PlayerUpdate();
                up.tickUpdate = now; up.player = row; up.vehicle = b; up.itemID = itemId; up.activeUtilities = b._activeEquip; up.bBot = true;
                p._client.send(up);
            }
            ClientSends++;
        }

        /// <summary>Every live bot within RouteCone of p sent to him now (he warped, respawned or arrived somewhere new).</summary>
        void Burst(Player p)
        {
            Helpers.ObjectState v = ViewOf(p);
            if (v == null || p._client == null) return;
            int now = Environment.TickCount;
            foreach (CTFXBot b in _bots.Values)
            {
                if (b.IsDead || b.Slot == null || b.Slot.Entry == null) continue;
                double dx = b._state.positionX - v.positionX, dy = (b._state.positionY - v.positionY) / 0.7;
                if (dx * dx + dy * dy <= (double)RouteCone * RouteCone) SendBot(b, p, 0, now);
            }
            Bursts++;
        }

        readonly Dictionary<Player, long> _lastView = new Dictionary<Player, long>();

        /// <summary>
        /// Travis 10-24: a bot that teleports (dropship, summon, squad blink, bounty mark) kept walking on the screens that saw it,
        /// then vanished: the move is routed from the NEW spot only (CTFXBot.Teleported), so whoever was near the old one heard
        /// nothing more until the far cadence (every FarEvery-th update) caught up. They get it once now, at the new spot, standing
        /// still (Teleported zeroes the velocity).
        /// </summary>
        internal void WarpExit(CTFXBot b, int ox, int oy, int now)
        {
            try
            {
                if (b == null || b.IsDead || b.Slot == null || b.Slot.Entry == null) return;
                long dx = b._state.positionX - ox, dy = b._state.positionY - oy;
                if (dx * dx + dy * dy < 400L * 400L) return;
                long near = (long)Arena.routeRange * Arena.routeRange;
                foreach (Player p in _arena.Players)
                {
                    if (p == null || p._client == null) continue;
                    Helpers.ObjectState v = ViewOf(p);
                    if (v == null) continue;
                    double ax = v.positionX - ox, ay = (v.positionY - oy) / 0.7;
                    if (ax * ax + ay * ay > (double)RouteCone * RouteCone) continue;   // could not see the old spot
                    long nx = v.positionX - b._state.positionX, ny = v.positionY - b._state.positionY;
                    if (nx * nx + ny * ny <= near) continue;                         // the route from the new spot reached them
                    SendBot(b, p, 0, now);
                    WarpExits++;
                }
            }
            catch (Exception ex) { LogErr("warpExit", ex); }
        }
        internal long WarpExits;                   // stats: teleport exits sent to the screens that saw the old spot

        /// <summary>A burst for every player whose view jumped 600+ px since the last look (or who is new).</summary>
        void WatchJumps()
        {
            foreach (Player p in _arena.Players)
            {
                Helpers.ObjectState v = ViewOf(p);
                if (v == null) continue;
                long key = ((long)v.positionX << 32) | (uint)v.positionY, old;
                bool seen = _lastView.TryGetValue(p, out old);
                _lastView[p] = key;
                if (seen)
                {
                    long ox = old >> 32, oy = (int)(old & 0xFFFFFFFF), dx = v.positionX - ox, dy = v.positionY - oy;
                    if (dx * dx + dy * dy < 600L * 600L) continue;
                }
                Burst(p);
            }
            if (_lastView.Count > 64) _lastView.Clear();
        }

        internal bool RouteSelf(CTFXBot b, short itemId)
        {
            try
            {
                if (_fVeh == null)
                {
                    _fVeh = typeof(Arena).GetField("_vehicles", BindingFlags.NonPublic | BindingFlags.Instance);
                    _fBots = typeof(Arena).GetField("_bots", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_fVeh == null || _fBots == null) return false;
                    _mVeh = _fVeh.FieldType.GetMethod("updateObjState");
                    _mBots = _fBots.FieldType.GetMethod("updateObjState");
                    if (_mVeh == null || _mBots == null) { _fVeh = null; return false; }
                }
                b._state.updateNumber++;
                int now = Environment.TickCount;
                foreach (Player p in Audience(b, itemId != 0)) SendBot(b, p, itemId, now);
                if (!b.bCondemned)
                {
                    _mVeh.Invoke(_fVeh.GetValue(_arena), new object[] { b, b._state });
                    _mBots.Invoke(_fBots.GetValue(_arena), new object[] { b, b._state });
                }
                SelfRoutes++;
                return true;
            }
            catch (Exception ex) { LogErr("routeSelf", ex); _fVeh = null; return false; }
        }

        // ---- terrain bounty: the zone own [TerrainN] BountyAutoRate / BountyAutoMax, every 30 s, as Arena.poll gives players
        int _lastAutoBounty;

        void AutoBounty(int now)
        {
            if (now - _lastAutoBounty < 30000) return;
            _lastAutoBounty = now;
            foreach (CTFXBot b in _bots.Values)
            {
                if (b.IsDead || b.Slot == null) continue;
                CfgInfo.Terrain t = _arena.getTerrain(b._state.positionX, b._state.positionY);
                if (t == null || t.bountyAutoRate <= 0 || b.Slot.Bounty >= t.bountyAutoMax) continue;
                int add = t.bountyAutoRate < 100 ? (Rng.Next(100) < t.bountyAutoRate ? 1 : 0) : t.bountyAutoRate / 100;
                if (add > 0) AddBounty(b.Slot, add);
            }
        }

        // ---- bounty: the zone [Bounty] / [Cash] / [Experience] / [Point] rules (Logic_Rewards), applied to bots as to players
        readonly Dictionary<Player, ushort> _botKiller = new Dictionary<Player, ushort>();   // victim -> bot row id (death report, network thread)
        // victim -> his death report and the bot row that killed him (under the _botKiller lock): RouteBotKill sends it on as a player kill
        readonly Dictionary<Player, KeyValuePair<CS_VehicleDeath, ushort>> _botKillPkt = new Dictionary<Player, KeyValuePair<CS_VehicleDeath, ushort>>();

        /// <summary>Arena thread (CTF.cs Player.BotKill): the kill goes to every client as a PLAYER kill by the bot's row (the old client
        /// knows rows below 5000 as players: "X killed by Kestrel 41"). The live engine would otherwise send it as a computer kill by
        /// 0xFFFF, the placeholder the death hook leaves in the report, which no client has been tested with. True = sent (the
        /// script then tells the engine not to send its own).</summary>
        bool RouteBotKill(Player victim)
        {
            try
            {
                KeyValuePair<CS_VehicleDeath, ushort> kv;
                lock (_botKiller)
                {
                    if (victim == null || !_botKillPkt.TryGetValue(victim, out kv)) return false;
                    _botKillPkt.Remove(victim);
                }
                if (kv.Key == null || LowSlot(kv.Value) == null || victim._arena != _arena) return false;
                kv.Key.killerPlayerID = kv.Value;
                Helpers.Player_RouteKill(_arena.Players, kv.Key, victim, 0, 0, 0, 0);
                KillsRouted++;
                return true;
            }
            catch (Exception ex) { LogErr("routeKill", ex); return false; }
        }
        internal int KillsRouted;                  // stats: bot kills sent on to the clients as player kills

        CTFXBot BotKillerOf(Player victim)
        {
            ushort id;
            lock (_botKiller)
            {
                if (victim == null || !_botKiller.TryGetValue(victim, out id)) return null;
                _botKiller.Remove(victim);
            }
            CTFXSlot s = LowSlot(id);
            return s != null ? GetBot(s.BotId) : null;
        }

        void KillReward(int victimBounty, int killerBounty, out int cash, out int exp, out int pts)
        {
            CfgInfo z = _arena._server._zoneConfig;
            cash = (int)(z.cash.killReward + victimBounty * (z.cash.percentOfTarget / 1000f) + killerBounty * (z.cash.percentOfKiller / 1000f));
            exp = (int)(z.experience.killReward + victimBounty * (z.experience.percentOfTarget / 1000f) + killerBounty * (z.experience.percentOfKiller / 1000f));
            pts = (int)(z.point.killReward + victimBounty * (z.point.percentOfTarget / 1000f) + killerBounty * (z.point.percentOfKiller / 1000f));
        }

        int BountyGain(int pts)
        {
            CfgInfo z = _arena._server._zoneConfig;
            return (int)(z.bounty.fixedToKillerBounty / 1000 + pts * (z.bounty.percentToKillerBounty / 1000f));
        }

        /// <summary>
        /// The share of a kill (Logic_Rewards.calculatePlayerKillRewards, as for two players): every teammate of the killer within the
        /// zone's share radius of the death spot gets [Bounty] PercentToAssistBounty of the kill's points as bounty (ctfpl: 4 %), and a
        /// human one also SharePercent of the cash / experience / points. Bots and humans alike, whoever the killer is.
        /// </summary>
        void ShareKill(Team team, int x, int y, int cash, int exp, int pts, Player killerP, CTFXBot killerB)
        {
            if (team == null) return;
            CfgInfo z = _arena._server._zoneConfig;
            int bty = (int)(pts * (z.bounty.percentToAssistBounty / 1000f));
            int r = z.point.shareRadius;
            if (r <= 0) return;
            long r2 = (long)r * r;
            foreach (CTFXBot o in _bots.Values)
            {
                if (o == killerB || o.IsDead || o.InDropship || o.Slot == null || o._team != team) continue;
                long dx = o._state.positionX - x, dy = o._state.positionY - y;
                if (dx * dx + dy * dy > r2) continue;
                if (bty > 0) { AddBounty(o.Slot, bty); AssistShares++; }
            }
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p == killerP || p.IsDead || p._team != team) continue;
                long dx = p._state.positionX - x, dy = p._state.positionY - y;
                if (dx * dx + dy * dy > r2) continue;
                p.Cash += (int)(cash / 1000f * z.cash.sharePercent);
                p.Experience += (int)(exp / 1000f * z.experience.sharePercent);
                p.KillPoints += (int)(pts / 1000f * z.point.sharePercent);
                p.Bounty += bty;
                p.syncState();
                AssistShares++;
            }
        }

        internal int AssistShares;                          // teammates that got a share of a kill near them

        internal const int BountyMax = 30000;             // the engine cap (Player.Bounty)
        internal int BountyPeak;                            // the highest bot bounty seen this run

        void AddBounty(CTFXSlot s, int gain)
        {
            s.Bounty = Math.Min(BountyMax, s.Bounty + gain);
            if (s.Bounty > BountyPeak) BountyPeak = s.Bounty;
        }

        string BountyText()
        {
            List<int> v = new List<int>();
            foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots) if (s.BotId != 0) v.Add(s.Bounty);
            if (v.Count == 0) return "";
            v.Sort(); v.Reverse();
            int k1 = 0, k5 = 0, k10 = 0;
            foreach (int b in v) { if (b >= 1000) k1++; if (b >= 5000) k5++; if (b >= 10000) k10++; }
            StringBuilder sb = new StringBuilder(" | bounty: top");
            for (int i = 0; i < Math.Min(5, v.Count); i++) sb.Append(' ').Append(v[i]);
            sb.Append(", 1k+ ").Append(k1).Append(" 5k+ ").Append(k5).Append(" 10k+ ").Append(k10).Append(", peak this run ").Append(BountyPeak).Append(", kill shares to teammates ").Append(AssistShares).Append(", updates with bounty ").Append(SelfRoutes).Append(", far updates skipped ").Append(FarSkips).Append(" | routing: per-player sends ").Append(ClientSends)
              .Append(", warp bursts ").Append(Bursts);
            return sb.ToString();
        }

        int BountyStart() { return _arena._server._zoneConfig.bounty.start; }

        internal static int BountyOf(CTFXBot b) { return b != null && b.Slot != null ? b.Slot.Bounty : 0; }

        internal CTFXChat Chat;                    // team chat: sparse callouts, summon requests, attack calls (CTFXChat.cs)

        internal CTFXDropship Dropship;            // bodies start in the CTF:X dropship, a MapEnter portal sends them out (CTFXDropship.cs)

        internal CTFXCommand Cmdr;              // team commander + each bot's intent (CTFXCommand.cs)
        internal CTFXMonitor Monitor;              // idle / cluster / packet sampling, ctfxmon.jsonl (CTFXMonitor.cs)
        internal long BotRoutes;                   // bot state updates sent (each reaches every client in route range)

        internal CTFXTurrets Turrets;              // engineer-built turret sets, turrets vs bots (CTFXTurrets.cs)
        internal Arena Arena { get { return _arena; } }

        internal CTFXSquads Squads;                // teleport grouping, blink storms, defender recalls (CTFXSquads.cs)
        internal List<CTFXBotGroup> Groups { get { return _groups; } }

        internal CTFXShots Shots;                  // bot shots in flight (CTFXShots.cs): their damage lands where and when they do

        /// <summary>The bots in the world (corpses included): the flight sim's body list.</summary>
        internal Dictionary<ushort, CTFXBot>.ValueCollection LiveBots { get { return _bots.Values; } }

        void Init()
        {
            bool localEngine = typeof(Arena).GetField("_botTargets", BindingFlags.NonPublic | BindingFlags.Instance) != null;
            ApplyConfigDerived(localEngine);
            string assets = ValidateAssets();

            Team titan = ResolveTeam("Titan"), coll = ResolveTeam("Collective");
            int need = 0;
            foreach (CTFXGroupSpec g in _cfg.Groups) if (g.Kind == CTFXKind.Private) need++;
            foreach (CTFXGroupSpec g in _cfg.Solos) need += g.Total();
            List<Team> priv = PickPrivate(need, titan, coll);

            int total = 0;
            foreach (CTFXGroupSpec g in _cfg.Groups) total += g.Total();
            foreach (CTFXGroupSpec g in _cfg.Solos) total += g.Total();

            _dPoll = Poll; _dCmd = Command; _dDeath = OnBotDeath; _dKilled = OnPlayerKilled; _dEnter = OnPlayerEnter;
            Script_CTF.CtfxBotsPlayerEnter = _dEnter;
            _dHeads = BotHeads;
            Script_CTF.CtfxBotsHeads = _dHeads;
            _dComm = CommCommand;
            Script_CTF.CtfxBotsComm = _dComm;
            Script_CTF.CtfxBotsPoll = _dPoll;
            Script_CTF.CtfxBotsCommand = _dCmd;
            Script_CTF.CtfxBotsBotDeath = _dDeath;
            Script_CTF.CtfxBotsPlayerKilled = _dKilled;
            _dWatch = WatchBot;
            Script_CTF.CtfxBotsWatch = _dWatch;
            _dRouteKill = RouteBotKill;
            Script_CTF.CtfxBotsRouteKill = _dRouteKill;
            _dPlace = PlaceName;
            Script_CTF.CtfxBotsPlace = _dPlace;
            _dShip = ShipSpots;
            Script_CTF.CtfxBotsShipSpots = _dShip;
            _dRepair = HumanRepair;
            Script_CTF.CtfxBotsRepair = _dRepair;
            _dInfo = BotInfo;
            Script_CTF.CtfxBotsInfo = _dInfo;
            Script_CTF.CtfxBotsArena = _arena;     // the CTF.cs call sites check this: other arenas never reach the bots

            Out("[CTFXBots] attached to " + _arena._name + " | " + total + " bots in " + _cfg.Groups.Count
                + " team lines + " + _cfg.Solos.Count + " solo lines | titan=" + NameOf(titan) + " collective=" + NameOf(coll)
                + " private=[" + TeamNames(priv) + "] (" + priv.Count + "/" + need + ") | posts " + PostText()
                + " | selfRoute=" + (SelfRoute ? "on" : "off") + (localEngine ? " (local engine)" : "")
                + " | pathfinder=" + (_arena._pathfinder != null ? "yes" : "NO (straight steering)")
                + " | autostart=" + (_cfg.AutoStart ? 1 : 0) + assets);
            Out("[CTFXBots] movement: client physics (CTFXClientMove), updateMs " + UpdateMs + ", near a human (" + NearPx + " px) " + UpdateNearMs);
            Out("[CTFXBots] " + CTFXShots.Report(CTFXBot.KitWeaponIds(), CTFXBot.ClassBodyIds));
            LoadKnockback();
            Out("[CTFXBots] " + CTFXBot.GateReport());
            Out("[CTFXBots] " + CTFXBot.CloakReport());
            Out("[CTFXBots] " + CTFXBot.AmmoReport(AmmoScale, Ammo));
            NudgeInner();
            BuildDoors();
            LoadRoutes();
            if (Prof.IsMiniTP) MiniInit();
        }

        /// <summary>(Re)build the door region graph: at attach, and at a start when the arena had no doors yet at attach.</summary>
        void BuildDoors()
        {
            Doors = null;
            if (!_cfg.Doors) { Out("[CTFXBots] doors: off (doors=0)"); return; }
            try
            {
                CTFXDoorNav d = CTFXDoorNav.Build(_arena, Prof);
                d.Log = WarnOnce;
                Out(d.Summary);
                if (d.Detail != null) Out(d.Detail);
                Doors = d;
            }
            catch (Exception ex) { Doors = null; Shout("[CTFXBots] doors: build failed: " + ex.Message); }
        }

        void ApplyConfigDerived(bool localEngine)
        {
            string sr = _cfg.SelfRoute;
            SelfRoute = sr == "on" ? true : sr == "off" ? false : localEngine;
            ShotgunPellets = Math.Max(1, _cfg.ShotgunPellets);
            UpdateMs = Math.Max(20, _cfg.UpdateMs);
            UpdateNearMs = Math.Max(20, _cfg.UpdateNearMs); NearPx = _cfg.NearPx; NearCap = _cfg.NearCap; FarPx = _cfg.FarPx; UpdateFarMs = _cfg.UpdateFarMs;
            BotPlayers = _cfg.BotPlayers;
            try
            {
                CfgInfo.Soul soul = _arena._server._zoneConfig.soul;
                EnergyShield = soul.energyShieldMode > 0;
                EnergyDefaultMax = soul.energyDefaultMax / 1000.0;
                EnergyDefaultRate = soul.energyDefaultRate;
            }
            catch (Exception) { }
            Ammo = _cfg.Ammo; Stun = _cfg.Stun; AmmoScale = _cfg.AmmoScale; KnockScale = _cfg.KnockScale;
            BotTd = _cfg.BotAntiwarpBots == "on" ? 2 : _cfg.BotAntiwarpBots == "off" ? 0 : 1;
            ViewScale = _cfg.ViewScale; EngageScale = _cfg.EngageScale;
            CTFXBot.ShopShare = _cfg.Shop;
            CTFXBot.MineFriendly = _cfg.MineFriendly;
            CTFXShop.Load(Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXBuilds.txt")))), CTFXBot.KnownW);
            Log("[CTFXBots] " + CTFXShop.LoadNote + " | shop share " + _cfg.Shop);
            Cmdr.On = _cfg.Command; Cmdr.TuneOn = _cfg.Tune; Cmdr.Anchors = _cfg.Anchors; Cmdr.AlarmAt = _cfg.AlarmAt;
            Cmdr.DefendRatio = _cfg.DefendRatio; Cmdr.PauseScale = _cfg.PauseScale; Cmdr.HerdK = _cfg.HerdK;
            Dropship.On = _cfg.Dropship; Dropship.HeadsAll = _cfg.DropshipHeadsAll; Cmdr.OutsideShare = _cfg.OutsideShare; Cmdr.BrownWander = _cfg.BrownWander; Cmdr.BrownTarget = _cfg.BrownTarget;
            FocusEnd = _cfg.FocusEnd; FocusShare = _cfg.FocusShare;
            Chat.On = _cfg.Chat; Chat.Listen = _cfg.Listen; Chat.TeamGapMs = _cfg.ChatTeamGap * 1000; Chat.BotGapMs = _cfg.ChatBotGap * 1000;
            CTFXChat.Bongs(_cfg.AttackBongs, Chat.AttackBongs);
            CTFXChat.Bongs(_cfg.UnderAttackBongs, Chat.UnderAttackBongs);
            CTFXChat.Bongs(_cfg.SightedBongs, Chat.SightedBongs);
            lock (CTFXBot.TdTiers)
            {   // who carries a Teleport Disruptor (it applies to the next life of each bot)
                CTFXBot.TdTiers.Clear();
                foreach (string tier in (_cfg.TdTiers ?? "").Split(',')) if (tier.Trim().Length > 0) CTFXBot.TdTiers.Add(tier.Trim());
            }
            CTFXChat.Bongs(_cfg.SummonBongs, Chat.SummonBongs);
            CTFXChat.Bongs(_cfg.EnemySummonBongs, Chat.EnemySummonBongs);
            CTFXChat.Bongs(_cfg.HomeSummonBongs, Chat.HomeSummonBongs);
            NudgePosts();
        }

        void Detach(string why)
        {
            try { StopAll(); } catch (Exception) { }
            _on = false;
            if (Script_CTF.CtfxBotsPoll == _dPoll) Script_CTF.CtfxBotsPoll = null;
            if (Script_CTF.CtfxBotsCommand == _dCmd) Script_CTF.CtfxBotsCommand = null;
            if (Script_CTF.CtfxBotsBotDeath == _dDeath) Script_CTF.CtfxBotsBotDeath = null;
            if (Script_CTF.CtfxBotsPlayerKilled == _dKilled) Script_CTF.CtfxBotsPlayerKilled = null;
            if (Script_CTF.CtfxBotsPlayerEnter == _dEnter) Script_CTF.CtfxBotsPlayerEnter = null;
            if (Script_CTF.CtfxBotsHeads == _dHeads) Script_CTF.CtfxBotsHeads = null;
            if (Script_CTF.CtfxBotsComm == _dComm) Script_CTF.CtfxBotsComm = null;
            if (Script_CTF.CtfxBotsWatch == _dWatch) Script_CTF.CtfxBotsWatch = null;
            if (Script_CTF.CtfxBotsRouteKill == _dRouteKill) Script_CTF.CtfxBotsRouteKill = null;
            if (Script_CTF.CtfxBotsPlace == _dPlace) Script_CTF.CtfxBotsPlace = null;
            if (Script_CTF.CtfxBotsShipSpots == _dShip) Script_CTF.CtfxBotsShipSpots = null;
            if (Script_CTF.CtfxBotsRepair == _dRepair) Script_CTF.CtfxBotsRepair = null;
            if (Script_CTF.CtfxBotsInfo == _dInfo) Script_CTF.CtfxBotsInfo = null;
            if (Script_CTF.CtfxBotsArena == _arena) Script_CTF.CtfxBotsArena = null;
            if (_inst == this) _inst = null;
            Out("[CTFXBots] detached (" + why + ")");
        }

        string ValidateAssets()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                int[] bodies = new int[] { 126, 127, 128, 129, 130, 131, 301 };   // DuelBot bodies
                foreach (int id in bodies)
                {
                    VehInfo v = AssetManager.Manager.getVehicleByID(id);
                    if (!(v is VehInfo.Car)) { _badBodies.Add(id); sb.Append(" body").Append(id); }
                }
                // class bot bodies (local ctfpl.veh 240-250); without them each class falls back to its closest DuelBot
                int classBodies = 0;
                foreach (int id in CTFXBot.ClassBodyIds)
                {
                    VehInfo v = AssetManager.Manager.getVehicleByID(id);
                    if (v is VehInfo.Car && v.Name != null && v.Name.Trim((char)34, ' ').StartsWith("Bot ", StringComparison.Ordinal)) classBodies++;
                    else _badBodies.Add(id);
                }
                Out("[CTFXBots] class bot bodies: " + classBodies + "/" + CTFXBot.ClassBodyIds.Length + " found (ctfpl.veh 240-250)"
                    + (classBodies == 0 ? ", classes use the closest DuelBot body" : ""));
                int[] weps = new int[] { 1096, 3023, 3001, 3031, 3009, 3038, 1004, 3028, 1056, 1109, 1163 };
                StringBuilder names = new StringBuilder();
                foreach (int id in weps)
                {
                    ItemInfo it = AssetManager.Manager.getItemByID(id);
                    if (it == null) sb.Append(" weapon").Append(id);
                    else names.Append(id).Append('=').Append(it.name).Append(' ');
                }
                Out("[CTFXBots] weapons: " + names.ToString().Trim());
            }
            catch (Exception ex) { sb.Append(" validation-error:" + ex.Message); }
            return sb.Length == 0 ? "" : " | MISSING:" + sb;
        }

        // ================================================================ map helpers
        internal bool InBounds(int tx, int ty)
        {
            return tx >= 1 && ty >= 1 && tx < _arena._levelWidth - 1 && ty < _arena._levelHeight - 1;
        }

        /// <summary>Open tile inside the CTF:X area.</summary>
        internal bool IsOpenA(int tx, int ty)
        {
            if (tx < Prof.X0 || tx > Prof.X1 || ty < Prof.Y0 || ty > Prof.Y1 || !InBounds(tx, ty))
                return false;
            return !_arena._tiles[ty * _arena._levelWidth + tx].Blocked;
        }

        internal bool NearestOpen(int tx, int ty, int maxR, out int ox, out int oy)
        {
            ox = tx; oy = ty;
            if (IsOpenA(tx, ty)) return true;
            for (int r = 1; r <= maxR; r++)
            {
                double best = double.MaxValue;
                int bx = -1, by = -1;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        if (!IsOpenA(tx + dx, ty + dy)) continue;
                        double d = dx * dx + dy * dy;
                        if (d < best) { best = d; bx = tx + dx; by = ty + dy; }
                    }
                if (bx >= 0) { ox = bx; oy = by; return true; }
            }
            return false;
        }

        internal void NearestOpenPx(int px, int py, out int tx, out int ty)
        {
            if (!NearestOpen(px >> 4, py >> 4, 30, out tx, out ty)) { tx = ZoneCenter.Cx[0]; ty = ZoneCenter.Cy[0]; }
        }

        /// <summary>Random non-blocked tile within the zone radius (up to 6 tries, then nearest open to the center).</summary>
        internal void RandomGoal(CTFXZone z, out int tx, out int ty)
        {
            int k = z.Cx.Length == 1 ? 0 : Rng.Next(z.Cx.Length);
            for (int i = 0; i < 6; i++)
            {
                double a = Rng.NextDouble() * Math.PI * 2.0, r = z.R * Math.Sqrt(Rng.NextDouble());
                int x = z.Cx[k] + (int)Math.Round(Math.Cos(a) * r), y = z.Cy[k] + (int)Math.Round(Math.Sin(a) * r);
                if (IsOpenA(x, y)) { tx = x; ty = y; return; }
            }
            if (!NearestOpen(z.Cx[k], z.Cy[k], 30, out tx, out ty)) { tx = z.Cx[k]; ty = z.Cy[k]; }
        }

        /// <summary>Overwatch tile 30-60 tiles from the zone center with clear tile LOS to the center (sniper).</summary>
        internal void OverwatchTile(CTFXZone z, out int tx, out int ty)
        {
            int k = z.Cx.Length == 1 ? 0 : Rng.Next(z.Cx.Length);
            int cx = z.Cx[k], cy = z.Cy[k];
            NearestOpen(cx, cy, 8, out cx, out cy);
            for (int i = 0; i < 40; i++)
            {
                double a = Rng.NextDouble() * Math.PI * 2.0, r = 30.0 + Rng.NextDouble() * 30.0;
                int x = cx + (int)Math.Round(Math.Cos(a) * r), y = cy + (int)Math.Round(Math.Sin(a) * r);
                if (!IsOpenA(x, y)) continue;
                if (!Los(x * 16 + 8, y * 16 + 8, cx * 16 + 8, cy * 16 + 8)) continue;
                tx = x; ty = y;
                return;
            }
            RandomGoal(z, out tx, out ty);
        }

        internal void SpawnTile(CTFXArea a, out int tx, out int ty)
        {
            double hw = a.W / 32.0, hh = a.H / 32.0;
            for (int i = 0; i < 15; i++)
            {
                int x = a.Cx + (int)Math.Round((Rng.NextDouble() * 2.0 - 1.0) * hw);
                int y = a.Cy + (int)Math.Round((Rng.NextDouble() * 2.0 - 1.0) * hh);
                if (IsOpenA(x, y)) { tx = x; ty = y; return; }
            }
            if (!NearestOpen(a.Cx, a.Cy, 60, out tx, out ty)) { tx = a.Cx; ty = a.Cy; }
        }

        /// <summary>Tile line of sight between two pixel positions (true = no blocked tile on the line).</summary>
        internal bool Los(int x0, int y0, int x1, int y1)
        {
            int wpx = _arena._levelWidth * 16, hpx = _arena._levelHeight * 16;
            if (x0 < 0 || y0 < 0 || x1 < 0 || y1 < 0 || x0 >= wpx || x1 >= wpx || y0 >= hpx || y1 >= hpx)
                return false;
            return Helpers.calcBresenhemsPredicate(_arena, (short)x0, (short)y0, (short)x1, (short)y1, NotBlocked);
        }

        void NudgePosts()
        {
            _posts.Clear();
            List<int[]> src = _cfg.Posts.Count > 0 ? _cfg.Posts : new List<int[]> { new int[] { 690, 1440 }, new int[] { 915, 1440 } };
            foreach (int[] p in src)
            {
                int ox, oy;
                if (!NearestOpen(p[0], p[1], 6, out ox, out oy)) { ox = p[0]; oy = p[1]; Out("[CTFXBots] post " + p[0] + "," + p[1] + " has no open tile within 6"); }
                _posts.Add(new int[] { ox, oy });
            }
        }

        string PostText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (int[] p in _posts) sb.Append('(').Append(p[0]).Append(',').Append(p[1]).Append(')');
            return sb.ToString();
        }

        internal void PostTile(int idx, out int tx, out int ty)
        {
            int[] p = _posts[((idx % _posts.Count) + _posts.Count) % _posts.Count];
            tx = p[0]; ty = p[1];
        }

        /// <summary>Nudge the four brown base inner points to the nearest open tile within 6 and log the final tiles in one line.</summary>
        void NudgeInner()
        {
            StringBuilder sb = new StringBuilder("[CTFXBots] brown inner points:");
            for (int i = 0; i < InnerSrc.Length; i++)
            {
                int sx = InnerSrc[i][0], sy = InnerSrc[i][1], ox, oy;
                bool ok = NearestOpen(sx, sy, 6, out ox, out oy);
                if (!ok) { ox = sx; oy = sy; }
                _innerX[i] = ox; _innerY[i] = oy;
                sb.Append(' ').Append(InnerNames[i]).Append(' ').Append(ox).Append(',').Append(oy);
                if (!ok) sb.Append(" (no open tile within 6)");
                else if (ox != sx || oy != sy) sb.Append(" (nudged from ").Append(sx).Append(',').Append(sy).Append(')');
            }
            Out(sb.ToString());
        }

        /// <summary>
        /// Brown base goal: a random open tile within 4 tiles (and in sight, so never behind a wall) of inner point idx
        /// (idx outside 0-3 = a random one of the four); the nudged point itself when 8 tries find nothing.
        /// </summary>
        internal void InnerGoal(int idx, out int tx, out int ty)
        {
            if (idx < 0 || idx >= _innerX.Length) idx = Rng.Next(_innerX.Length);
            int cx = _innerX[idx], cy = _innerY[idx];
            for (int i = 0; i < 8; i++)
            {
                double a = Rng.NextDouble() * Math.PI * 2.0, r = 4.0 * Math.Sqrt(Rng.NextDouble());
                int x = cx + (int)Math.Round(Math.Cos(a) * r), y = cy + (int)Math.Round(Math.Sin(a) * r);
                if (IsOpenA(x, y) && Los(x * 16 + 8, y * 16 + 8, cx * 16 + 8, cy * 16 + 8)) { tx = x; ty = y; return; }
            }
            tx = cx; ty = cy;
        }

        /// <summary>Goal of an assault leader: objective = the brown zone means the base interior (BL 40% / BR 40% / BM 20%); any other zone as before.</summary>
        internal void AssaultGoal(CTFXZone objective, out int tx, out int ty)
        {
            if (objective != ZoneBrown) { RandomGoal(objective, out tx, out ty); return; }
            double u = Rng.NextDouble();
            InnerGoal(u < 0.4 ? InnerBL : (u < 0.8 ? InnerBR : InnerBM), out tx, out ty);
        }

        // ================================================================ teams
        /// <summary>The profile's team: CTF:X = FindTeam (substring, lowest id); MiniTP = the team of that exact name (36 / 37).</summary>
        Team ResolveTeam(string needle)
        {
            string exact = Prof.IsMiniTP ? (needle == "Titan" ? Prof.TitanTeam : Prof.CollectiveTeam) : null;
            if (exact == null) return FindTeam(needle);
            foreach (Team t in _arena.Teams)
                if (!t.IsSpec && t._name != null && t._name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        Team FindTeam(string needle)
        {
            Team best = null;
            foreach (Team t in _arena.Teams)
            {
                if (t.IsSpec || t._name == null || t._name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || t._id < best._id) best = t;
            }
            return best;
        }

        /// <summary>Unused cfg team slots from the highest id down; skips 0/1, Titan/Collective and any team with humans.</summary>
        List<Team> PickPrivate(int need, Team titan, Team coll)
        {
            List<Team> all = new List<Team>();
            foreach (Team t in _arena.Teams) all.Add(t);
            all.Sort(delegate(Team a, Team b) { return b._id.CompareTo(a._id); });
            List<Team> res = new List<Team>();
            foreach (Team t in all)
            {
                if (res.Count >= need) break;
                // ctfpl: 2-35 are the mix "<name> T/C" pairs (taken from the top down, so mixes starting at MOON
                // rarely meet a bot); 36+ are the Titan/Collective twins, np, ref and the league names (CKY, PT...)
                if (t._id < 2 || t._id >= 36 || t.IsSpec || t == titan || t == coll || string.IsNullOrEmpty(t._name)) continue;
                if (t._name.IndexOf("Titan", StringComparison.OrdinalIgnoreCase) >= 0 || t._name.IndexOf("Collective", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                bool humans = false;
                foreach (Player p in t.AllPlayers) { humans = true; break; }
                if (!humans) res.Add(t);
            }
            return res;
        }

        static string NameOf(Team t) { return t == null ? "NONE" : "'" + t._name + "'"; }

        static string TeamNames(List<Team> l)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Team t in l) { if (sb.Length > 0) sb.Append(", "); sb.Append(t._id).Append(':').Append(t._name); }
            return sb.ToString();
        }

        // ================================================================ groups / start / stop
        internal string SecondaryEnd, TertiaryEnd;       // this game's secondary / tertiary bases (home=secondary / tertiary), picked at each start

        CTFXBotGroup NewGroup(CTFXKind kind, string homeName)
        {
            CTFXBotGroup g = new CTFXBotGroup();
            g.Kind = kind;
            g.Home = ZoneByName(homeName);
            if (g.Home == null)
            {
                CTFXZone[] all = new CTFXZone[] { ZoneBrown, ZoneCenter, ZoneTop, ZoneBottom, ZoneLeft, ZonePockets };
                g.Home = all[Rng.Next(all.Length)];
            }
            g.Attackers = kind == CTFXKind.Collective && g.Home == ZoneBrown;
            // a base end as home (brown-top ...): the group lives in that end and is locked to its building
            string h = (homeName ?? "").Trim().ToLowerInvariant();
            // Travis 10-14 "flock to brown": one secondary base per game instead of all eight ends set up. home=secondary is that end
            // (picked at random each start among the non-brown ends), home=secondary-enemy the other end of the same building
            if (h == "secondary" || h == "secondary-enemy")
            {
                if (SecondaryEnd == null && EndIds.Count > 0)
                {
                    List<string> c = new List<string>();
                    foreach (string e in EndIds) if (!e.StartsWith("brown", StringComparison.Ordinal)) c.Add(e);
                    if (c.Count > 0) SecondaryEnd = c[Rng.Next(c.Count)];
                }
                if (SecondaryEnd != null) h = h == "secondary" ? SecondaryEnd : (OtherEnd(SecondaryEnd) ?? SecondaryEnd);
            }
            // Travis 10-17: "more activity in another quadrant with the same kind of stuff": home=tertiary is a second set-up base in a
            // different building from the secondary (and from brown), home=tertiary-enemy the other end of that building
            if (h == "tertiary" || h == "tertiary-enemy")
            {
                if (TertiaryEnd == null && EndIds.Count > 0)
                {
                    List<string> c = new List<string>();
                    string q2 = SecondaryEnd != null && SecondaryEnd.IndexOf('-') > 0 ? SecondaryEnd.Substring(0, SecondaryEnd.IndexOf('-')) : "";
                    foreach (string e in EndIds) if (!e.StartsWith("brown", StringComparison.Ordinal) && !e.StartsWith(q2 + "-", StringComparison.Ordinal)) c.Add(e);
                    if (c.Count > 0) TertiaryEnd = c[Rng.Next(c.Count)];
                }
                if (TertiaryEnd != null) h = h == "tertiary" ? TertiaryEnd : (OtherEnd(TertiaryEnd) ?? TertiaryEnd);
            }
            if (h == "outside") { g.Outside = true; g.Home = ZonePockets; g.Attackers = false; }      // the mountain sides between the buildings
            else if (Routes != null && EndIds.Count > 0)
            {
                if (h.Length == 0 && kind == CTFXKind.Solo) h = EndIds[Rng.Next(EndIds.Count)];
                if (Routes.Routes.ContainsKey(h + ":patrol"))
                {
                    g.HomeEnd = h;
                    g.Quad = h.Substring(0, h.IndexOf('-'));
                    g.Home = ZoneOfQuad(g.Quad);
                    g.Attackers = false;
                }
                else if (h.EndsWith("-core", StringComparison.Ordinal) && _coreNodes.ContainsKey(h.Substring(0, h.Length - 5)))
                {   // a building's middle hall (brown-core)
                    g.CoreOf = h.Substring(0, h.Length - 5);
                    g.Quad = g.CoreOf;
                    g.Home = ZoneOfQuad(g.Quad);
                    g.Attackers = false;
                }
            }
            double bd = double.MaxValue;
            for (int i = 0; i < Areas.Length; i++)
            {
                double dx = Areas[i].Cx - g.Home.Cx[0], dy = Areas[i].Cy - g.Home.Cy[0], d = dx * dx + dy * dy;
                if (d < bd) { bd = d; g.NearestArea = i; }
            }
            return g;
        }

        internal CTFXZone ZoneByName(string n)
        {
            switch ((n ?? "").Trim().ToLowerInvariant())
            {
                case "brown": return ZoneBrown;
                case "center": case "centre": return ZoneCenter;
                case "top": return ZoneTop;
                case "bottom": return ZoneBottom;
                case "left": return ZoneLeft;
                case "pockets": case "pocket": return ZonePockets;
            }
            return null;
        }

        void AddSlots(CTFXBotGroup g, CTFXStyle style, int count)
        {
            for (int i = 0; i < count; i++)
            {
                CTFXSlot s = new CTFXSlot();
                s.Group = g; s.Style = style; s.Index = g.Slots.Count;
                foreach (CTFXSlot o in g.Slots) if (o.Style == style) s.StyleIndex++;
                if (style == CTFXStyle.Post) s.PostIndex = _postCounter++;
                g.Slots.Add(s);
            }
        }

        /// <summary>Travis 10-17: Titan was dwarfing every team (24 vs ~10) and farming brown. No team line seats more than cap bots: the largest style gives a seat up until it fits.</summary>
        static void CapTeams(List<int> spec, List<int> counts, int nGroupEntries, int cap)
        {
            if (cap <= 0) return;
            for (int si = 0; ; si++)
            {
                int sum = 0, any = 0;
                for (int e = 0; e < nGroupEntries; e++) if (spec[e] == si) { sum += counts[e]; any++; }
                if (any == 0) { if (si > 64) break; continue; }
                while (sum > cap)
                {
                    int bi = -1;
                    for (int e = 0; e < nGroupEntries; e++) if (spec[e] == si && (bi < 0 || counts[e] > counts[bi])) bi = e;
                    counts[bi]--; sum--;
                }
            }
        }

        /// <summary>Hamilton scaling: counts sum to exactly target.</summary>
        static void ScaleCounts(List<int> counts, int total, int target)
        {
            int n = counts.Count, sum = 0;
            double[] q = new double[n];
            int[] fl = new int[n];
            for (int i = 0; i < n; i++) { q[i] = counts[i] * (double)target / total; fl[i] = (int)Math.Floor(q[i]); sum += fl[i]; }
            for (int rem = target - sum; rem > 0; rem--)
            {
                int bi = -1;
                double bf = -1.0;
                for (int i = 0; i < n; i++) { double f = q[i] - fl[i]; if (counts[i] > 0 && f > bf) { bf = f; bi = i; } }
                if (bi < 0) break;
                fl[bi]++;
                q[bi] = fl[bi];
            }
            for (int i = 0; i < n; i++) counts[i] = fl[i];
        }

        void BuildGroups(int target)
        {
            _groups.Clear();
            _postCounter = 0;
            SecondaryEnd = null; TertiaryEnd = null;
            List<int> entSpec = new List<int>(), entCount = new List<int>();
            List<CTFXStyle> entStyle = new List<CTFXStyle>();
            for (int i = 0; i < _cfg.Groups.Count; i++)
                foreach (KeyValuePair<CTFXStyle, int> kv in _cfg.Groups[i].Styles) { entSpec.Add(i); entStyle.Add(kv.Key); entCount.Add(kv.Value); }
            int nGroupEntries = entSpec.Count;
            for (int i = 0; i < _cfg.Solos.Count; i++)
                foreach (KeyValuePair<CTFXStyle, int> kv in _cfg.Solos[i].Styles) { entSpec.Add(-1 - i); entStyle.Add(kv.Key); entCount.Add(kv.Value); }
            int total = 0;
            foreach (int c in entCount) total += c;
            if (target > 0 && total > 0 && target != total)
                ScaleCounts(entCount, total, target);
            CapTeams(entSpec, entCount, nGroupEntries, _cfg.TeamCap);

            List<CTFXBotGroup> shells = new List<CTFXBotGroup>();
            for (int si = 0; si < _cfg.Groups.Count; si++)
            {
                CTFXBotGroup g = null;
                for (int e = 0; e < nGroupEntries; e++)
                {
                    if (entSpec[e] != si || entCount[e] <= 0) continue;
                    if (g == null) { g = NewGroup(_cfg.Groups[si].Kind, _cfg.Groups[si].Home); g.Ambush = _cfg.Groups[si].Ambush; g.DropPack = _cfg.Groups[si].DropPack; }
                    AddSlots(g, entStyle[e], entCount[e]);
                }
                if (g != null) shells.Add(g);
            }
            for (int e = nGroupEntries; e < entSpec.Count; e++)
                for (int k = 0; k < entCount[e]; k++)
                {
                    CTFXBotGroup g = NewGroup(CTFXKind.Solo, "");
                    AddSlots(g, entStyle[e], 1);
                    shells.Add(g);
                }

            Team titan = ResolveTeam("Titan"), coll = ResolveTeam("Collective");
            int need = 0;
            foreach (CTFXBotGroup g in shells) if (g.Kind == CTFXKind.Private || g.Kind == CTFXKind.Solo) need++;
            List<Team> priv = PickPrivate(need, titan, coll);
            int pi = 0;
            foreach (CTFXBotGroup g in shells)
            {
                if (g.Kind == CTFXKind.Titan) g.Team = titan;
                else if (g.Kind == CTFXKind.Collective) g.Team = coll;
                else if (pi < priv.Count) g.Team = priv[pi++];
                if (g.Team == null)
                {
                    foreach (CTFXSlot s in g.Slots) s.Retired = true;
                    Out("[CTFXBots] no team for a " + g.Kind + " group (" + g.Slots.Count + " bots skipped)");
                }
                _groups.Add(g);
            }
            Out("[CTFXBots] teams: titan=" + NameOf(titan) + " collective=" + NameOf(coll) + " private=[" + TeamNames(priv) + "] (" + priv.Count + "/" + need + ")" + (SecondaryEnd != null ? ", secondary base " + SecondaryEnd : ""));
        }

        int TotalSlots()
        {
            int n = 0;
            foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots) if (!s.Retired) n++;
            return n;
        }

        void Start(int target, string why)
        {
            StopAll();
            _tdTestAt = Environment.TickCount + 15000;
            if (_pendingCfg != null)
            {
                _cfg = _pendingCfg;
                _pendingCfg = null;
                ApplyConfigDerived(typeof(Arena).GetField("_botTargets", BindingFlags.NonPublic | BindingFlags.Instance) != null);
            }
            if (_cfg.Doors != (Doors != null) || (Doors != null && Doors.TotalDoors == 0)) BuildDoors();   // config changed, or no doors were loaded yet at attach
            if (Doors != null) Doors.ResetCounters();
            OpeningUntil = Environment.TickCount + 20000; OpeningWaits = 0;
            if (Prof.IsMiniTP) BuildMiniGroups(target); else BuildGroups(target);
            {   // mustHave: a Captain, a Medic, an Engineer (by default) in every team of mustHaveMin+ bots
                List<int> need = new List<int>();
                foreach (string c in (_cfg.MustHave ?? "").Split(','))
                {
                    int id = CTFXBot.ClassByShort(c.Trim());
                    if (id > 0) need.Add(id);
                }
                Dictionary<Team, List<CTFXSlot>> byTeam = new Dictionary<Team, List<CTFXSlot>>();
                foreach (CTFXBotGroup g in _groups)
                {
                    if (g.Team == null) continue;
                    List<CTFXSlot> l;
                    if (!byTeam.TryGetValue(g.Team, out l)) { l = new List<CTFXSlot>(); byTeam[g.Team] = l; }
                    l.AddRange(g.Slots);
                }
                int forced = 0;
                foreach (KeyValuePair<Team, List<CTFXSlot>> kv in byTeam)
                    if (kv.Value.Count >= _cfg.MustHaveMin) forced += CTFXBot.EnsureClasses(kv.Value, need, Rng);
                int bt = 0;
                foreach (CTFXBotGroup g in _groups)
                {
                    bool pack = false;
                    foreach (CTFXSlot s in g.Slots) if (s.Style == CTFXStyle.Drain) pack = true;
                    foreach (CTFXSlot s in g.Slots)
                        if (s.Style == CTFXStyle.Bountier && pack && s.StyleIndex >= 1) s.ForceClass = CTFXBot.SK_INFIL;   // a hunt pack runner
                    foreach (CTFXSlot s in g.Slots)
                        if (s.Style == CTFXStyle.Bountier && !pack && g.DropPack) s.ForceClass = CTFXBot.SK_JT;       // the drop pack sniper
                        else if (s.Style == CTFXStyle.Bountier && !pack && g.Ambush) s.ForceClass = CTFXBot.SK_SCIOPS;     // the Zap Glove ambusher
                        else if (s.Style == CTFXStyle.Bountier && !pack)
                        { int[] cl = new int[] { CTFXBot.SK_ENGY, CTFXBot.SK_SCIOPS, CTFXBot.SK_INFIL, CTFXBot.SK_INF }; s.ForceClass = cl[bt++ % 4]; }
                }
                Out("[CTFXBots] mustHave " + _cfg.MustHave + " in teams of " + _cfg.MustHaveMin + "+: " + forced + " seats set");
            }
            {   // Travis 10-17: every flagging team (a base end with 6+ bots) keeps one infiltrator flag courier: a roamer / assault seat switched to Infiltrator
                int couriers = 0;
                foreach (CTFXBotGroup g in _groups)
                {
                    if (g.HomeEnd == null || g.Team == null || g.Slots.Count < 6) continue;
                    CTFXSlot pick = null;
                    foreach (CTFXSlot s in g.Slots) if (s.Style == CTFXStyle.Roam && s.ForceClass == 0) { pick = s; break; }
                    if (pick == null) { bool first = true; foreach (CTFXSlot s in g.Slots) { if (s.Style == CTFXStyle.Assault && s.ForceClass == 0) { if (first) { first = false; continue; } pick = s; break; } } }
                    if (pick == null) continue;
                    pick.ForceClass = CTFXBot.SK_INFIL; pick.Courier = true; couriers++;
                }
                Out("[CTFXBots] flag couriers (infiltrators): " + couriers);
            }
            AssignLowIds();
            AssignNames();      // 10-22: every seat's callsign in roster order, so a name (and its seeded personality) is the same each boot
            RoomReset();
            Squads.Clear();
            if (_cfg.Squads) Squads.Setup(_groups, Environment.TickCount);
            Turrets.Load(_cfg.Turrets);
            Turrets.Spread();
            Turrets.SelfTest = _cfg.TurretSelfTest;
            Out("[CTFXBots] " + Turrets.Summary());
            Dropship.Load();
            Cmdr.Setup(_groups, Environment.TickCount);
            Monitor.Reset(Environment.TickCount);
            Chat.Clear();
            Bounty.Clear();
            Flags.On = _cfg.Flags; Flags.Share = _cfg.FlagShare; Packs = _cfg.Packs; Utilities = _cfg.Utilities;
            if (!Prof.IsMiniTP) { Flags.Load(); Flags.Reset(Environment.TickCount); }      // MiniTP owns its flags (CTF.cs InitializeMiniTPEvent)
            PollTicks = 0; PollCount = 0; PathReq = 0; PathFail = 0; StuckEvents = 0; Recycled = 0;
            _killsBB = 0; _killsBH = 0; _killsHB = 0; _killsOther = 0; _pathInFlight = 0;
            int now = Environment.TickCount;
            foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots) QueueSpawn(s, now);
            _on = true;
            _noHumans = false;
            _lastStats = now;
            Shout("[CTFXBots] start (" + why + "): " + TotalSlots() + " bots in " + _groups.Count + " groups" + (target > 0 ? " (scaled to " + target + ")" : ""));
        }

        void StopAll()
        {
            _on = false;    // a death event raised while destroying must not queue respawns
            Shots.Clear();
            Squads.Clear();
            Turrets.Clear();
            Cmdr.Clear();
            List<CTFXBot> list = new List<CTFXBot>(_bots.Values);
            _bots.Clear(); _spawnQ.Clear(); _recycle.Clear();
            foreach (CTFXBot b in list)
            {
                try { b.destroy(false); } catch (Exception) { }     // drops the slot's row first (CTFXBot.destroy)
                b.Release();
            }
            foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots) { DropEntry(s); DropGhost(s); s.BotId = 0; s.FixedId = 0; s.Queued = false; }
            _groups.Clear();
            _spawnQ.Clear(); _recycle.Clear();
            _hsCount = 0; _hsHead = 0;
            _pathInFlight = 0;
            RoomClear();
        }

        // ================================================================ spawn / destroy
        void QueueSpawn(CTFXSlot s, int due)
        {
            if (s.Retired || s.Queued) return;
            s.Queued = true;
            SpawnReq r;
            r.Slot = s; r.Due = due;
            _spawnQ.Add(r);
        }

        int AreaIndexFor(CTFXBotGroup g)
        {
            if (g.Attackers) return Rng.Next(2) == 0 ? AreaF : AreaC;
            if (g.Home == ZonePockets) return Rng.Next(Areas.Length);
            return g.NearestArea;
        }

        bool SpawnSlot(CTFXSlot slot, int now)
        {
            CTFXBotGroup g = slot.Group;
            // The last body (its corpse, or a bot being recycled) leaves the server lists first, quietly. The new body comes on a
            // later poll, after the arena has unlisted the old one, so it can take the same id.
            CTFXBot old = GetBot(slot.BotId);
            if (old != null) { RemoveQuietly(old); QueueSpawn(slot, now + 1); return false; }
            if (_recruits.Count > 0) { ClanSwitch(slot, now); g = slot.Group; }
            ushort want = LowIds && slot.RowId != 0 ? slot.RowId : slot.FixedId;
            if (want != 0 && !VehicleIdFree(want))
            {
                if (++slot.IdWaits <= 20) { QueueSpawn(slot, now + 100); return false; }
                want = 0;                                           // still listed after ~3 s: this body takes a new id
            }
            if (slot.Benched && !(Dropship != null && Dropship.Ready)) { QueueSpawn(slot, now + 3000); return false; }      // 10-22: benched = ship only (no ship: it waits unspawned)
            int body = 0, kit = 0, cls = 0;
            int savedForce = slot.ForceClass;
            if (FillsEngineerGap(slot)) slot.ForceClass = CTFXBot.SK_ENGY;          // Travis 10-15: switch class to fill what the team lacks
            bool rolled = g.Team != null && CTFXBot.RollLoadout(slot, Rng, _badBodies, out body, out kit, out cls);
            slot.ForceClass = savedForce;
            if (!rolled)
            {
                SpawnFailed(slot, now, "no valid body");
                return false;
            }
            int tx, ty;
            bool ship = Dropship.SpawnTile(out tx, out ty);                 // dropship=1: where a player's CTF:X body starts
            if (Mini != null) { ship = false; Mini.SpawnTile(g, out tx, out ty); }      // MiniTP: its team's dropship room
            else if (!ship && !RouteSpawnTile(g, out tx, out ty)) SpawnTile(Areas[AreaIndexFor(g)], out tx, out ty);
            Helpers.ObjectState st = new Helpers.ObjectState();
            st.positionX = (short)(tx * 16 + 8);
            st.positionY = (short)(ty * 16 + 8);
            st.yaw = (byte)Rng.Next(240);
            CTFXBot b = null;
            try { b = NewBotAt(want, body, g.Team, st); }
            catch (Exception ex) { LogErr("newBot", ex); }
            if (b == null)
            {
                SpawnFailed(slot, now, "newBot returned null (body " + body + ")");
                return false;
            }
            slot.IdWaits = 0;
            if (slot.FixedId != 0)
            {
                if (b._id == slot.FixedId) IdsKept++;
                else
                {   // the id moved: the old row and the last body leave every client, the new row follows below
                    IdsMoved++;
                    DropEntry(slot);
                    DropGhost(slot);
                    WarnOnce("a bot slot could not keep its id " + slot.FixedId + " (took " + b._id + ")");
                }
            }
            slot.FixedId = b._id;
            slot.Ghost = null;                                      // newBot's vehicle packet replaced it on every client
            b.Setup(slot, kit, cls);
            if (Mini != null) Mini.Spawned(b, now);
            if (ship) b.EnterDropship(now);
            if (BotPlayers) { try { AnnounceSlot(slot, b); } catch (Exception ex) { LogErr("announce", ex); } }
            _bots[b._id] = b;
            slot.BotId = b._id;
            try { Cmdr.Spawned(slot, b, now); } catch (Exception ex) { LogErr("intent", ex); }
            slot.Fails = 0;
            int si = (int)slot.Style;
            CTFXBot ld = GetBot(g.Leader[si]);
            if (ld == null || ld.IsDead) g.Leader[si] = b._id;
            return true;
        }

        void SpawnFailed(CTFXSlot slot, int now, string why)
        {
            slot.Fails++;
            if (slot.Fails >= 3)
            {
                slot.Retired = true;
                DropEntry(slot);
                DropGhost(slot);
                Out("[CTFXBots] slot " + slot.Style + " retired after 3 spawn failures: " + why);
            }
            else QueueSpawn(slot, now + 5000);
        }

        // ---- stable ids. A slot keeps its first body's vehicle id for life, so its player-list row never leaves and rejoins.
        // newBot hands out the first free id at or after the arena's private _lastVehicleKey: a respawn points that key at the
        // slot's id for its one newBot call, then puts it back so every other vehicle keeps counting from where it was.
        static FieldInfo _vehicleKey;
        static bool _vehicleKeyLooked;

        /// <summary>newBot under id <paramref name="want"/> (0 = the next free one). The caller has checked that the id is free.</summary>
        CTFXBot NewBotAt(ushort want, int body, Team team, Helpers.ObjectState st)
        {
            FieldInfo f = null;
            object saved = null;
            if (want != 0)
            {
                if (!_vehicleKeyLooked)
                {
                    _vehicleKeyLooked = true;
                    try { _vehicleKey = typeof(Arena).GetField("_lastVehicleKey", BindingFlags.NonPublic | BindingFlags.Instance); }
                    catch (Exception) { _vehicleKey = null; }
                    if (_vehicleKey != null && _vehicleKey.FieldType != typeof(ushort)) _vehicleKey = null;
                    if (_vehicleKey == null) Shout("[CTFXBots] Arena._lastVehicleKey not found: bots take a new id (and player-list row) each life");
                }
                f = _vehicleKey;
                if (f != null) { saved = f.GetValue(_arena); f.SetValue(_arena, want); }
            }
            try { return _arena.newBot(typeof(CTFXBot), (ushort)body, team, null, st, this) as CTFXBot; }
            finally { if (saved != null) f.SetValue(_arena, saved); }
        }

        // ---- low ids (botPlayerIds=low). The old client treats only ids below 5000 as players, and draws a man with his name, team
        // uniform and radar blip only for a body whose vehicle id IS its player's id, the way every player's own body is sent. A bot
        // body above 5000 gets none of that, and tying a separate low player row to it makes the client take the bot for a player
        // sitting in an occupiable vehicle. So each seat's bodies take one id below 5000, and its row uses the same id: the engine's
        // own packets (SC_Vehicles occupant = the bot's id, updates with player id = vehicle id) then describe a player in his own
        // body. Engine traps handled here: a team warp to an id not in the in-game player list is dropped (HookPackets warp half),
        // and a death report naming a killer below 5001 that is no player is dropped half-handled (HookPackets death half).

        /// <summary>Each seat's id, counting down from 4999. The zone hands players ids below server.xml maxPlayers only, so the block
        /// must sit at or above it; otherwise the bodies keep the engine's 5001+ ids.</summary>
        void AssignLowIds()
        {
            _lowSlots.Clear();
            int n = 0;
            foreach (CTFXBotGroup g in _groups) n += g.Slots.Count;
            LowIds = _cfg.LowIds && n > 0 && 5000 - n >= ZoneServer.maxPlayers;
            if (_cfg.LowIds && n > 0 && !LowIds)
                Shout("[CTFXBots] botPlayerIds=low needs server.xml maxPlayers <= " + (5000 - n) + " (it is " + ZoneServer.maxPlayers
                    + "): bots keep ids above 5000, which the old client never shows as players");
            CTFXSlot[] byId = new CTFXSlot[LowIds ? n : 0];
            int k = 0;
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots)
                {
                    s.RowId = LowIds ? (ushort)(4999 - k) : (ushort)0;
                    if (LowIds) { _lowSlots[s.RowId] = s; byId[k] = s; }
                    k++;
                }
            _lowById = byId;
        }

        /// <summary>The seat whose low id this is (any thread), or null.</summary>
        CTFXSlot LowSlot(uint id)
        {
            CTFXSlot[] a = _lowById;
            int i = 4999 - (int)id;
            return id < 5000 && i >= 0 && i < a.Length ? a[i] : null;
        }

        static bool _packetsHooked;

        /// <summary>Two packet handlers, added once per process. Team warps: a second handler on the item-use packet (the engine's runs
        /// first and drops a warp to a bot). Death reports: a handler put BEFORE the engine's, which hands a killer id that is a
        /// bot's low id on as one no vehicle has, above 5000: the engine then runs its bot-kill path with no bot (flag and ball
        /// reset, Player.BotKill, deaths) instead of logging an invalid player killer and returning half-way.</summary>
        static void HookPackets()
        {
            if (_packetsHooked) return;
            _packetsHooked = true;
            CS_PlayerUseItem.Handlers += OnUseItem;
            CS_RequestSpectator.Handlers += OnSpectateRequest;
            CS_Chat.Handlers += OnChat;
            CS_PlayerUpdate.Handlers += OnHumanUpdate;
            CS_VehicleDeath.Handlers = (Action<CS_VehicleDeath, Player>)Delegate.Combine(
                new Action<CS_VehicleDeath, Player>(OnDeathReport), CS_VehicleDeath.Handlers);
        }

        /// <summary>Network thread: a player update that uses an item (a shot): turret guards watch splash fire coming (CTFXTurrets).</summary>
        static void OnHumanUpdate(CS_PlayerUpdate pkt, Player player)
        {
            try
            {
                if (pkt.itemID == 0) return;
                CTFXBotManager m = _inst;
                if (m == null || player == null || player._arena != m._arena || player._team == null || player.IsSpectator) return;
                Team t = player._team;
                int x = pkt.positionX, y = pkt.positionY, id = pkt.itemID;
                byte yaw = pkt.yaw;
                player._arena.handleEvent(delegate(Arena a) { try { if (m._on && _inst == m) { m.Turrets.NoteHumanShot(t, x, y, yaw, id); m.NoteInbound(t, x, y, yaw, id); } } catch (Exception) { } });
            }
            catch (Exception) { }
        }

        static void OnDeathReport(CS_VehicleDeath pkt, Player from)
        {
            try
            {
                CTFXBotManager m = _inst;
                if (m == null || pkt.type != Helpers.KillType.Player || m.LowSlot(pkt.killerPlayerID) == null) return;
                m.Flags.NoteHumanKill(from, (ushort)pkt.killerPlayerID);      // a bot that kills a carrier takes his flag (CTFXFlags)
                lock (m._botKiller)
                {
                    if (m._botKillPkt.Count > 64) m._botKillPkt.Clear();      // reports the engine never took on (bounded)
                    m._botKiller[from] = (ushort)pkt.killerPlayerID;
                    m._botKillPkt[from] = new KeyValuePair<CS_VehicleDeath, ushort>(pkt, (ushort)pkt.killerPlayerID);
                }
                pkt.killerPlayerID = 0xFFFF;                    // never a vehicle id (the engine's allocator wraps before it)
                m.KillersMapped++;
            }
            catch (Exception) { }
        }

        /// <summary>Network thread: a spectator's right-click > spectate on a bot. The engine looks the id up among the players in game
        /// and drops it; this makes the spectator follow the bot's row, which the client follows like any player's id.</summary>
        static void OnSpectateRequest(CS_RequestSpectator pkt, Player player)
        {
            try
            {
                CTFXBotManager m = _inst;
                if (m == null || player == null || player._arena != m._arena || !player.IsSpectator || m.LowSlot(pkt.playerID) == null) return;
                ushort id = pkt.playerID;
                player._arena.handleEvent(delegate(Arena a) { m.SpectateBot(player, id); });
            }
            catch (Exception) { }
        }

        /// <summary>Arena thread (CTF.cs autowatch): a spectator who just arrived follows a live bot, one in a fight if any (else one
        /// out of the dropship), picked at random so arrivals spread over the bots. False = no bot to follow yet.</summary>
        bool WatchBot(Player player)
        {
            try
            {
                if (!_on || player == null || player._arena != _arena || !player.IsSpectator) return false;
                List<CTFXSlot> fighting = new List<CTFXSlot>(), any = new List<CTFXSlot>();
                foreach (CTFXBot b in _bots.Values)
                {
                    if (b.IsDead || b.Released || b.InDropship || b.Slot == null || b.Slot.Entry == null) continue;
                    any.Add(b.Slot);
                    if (b.Why == "fight") fighting.Add(b.Slot);
                }
                List<CTFXSlot> pool = fighting.Count > 0 ? fighting : any;
                if (pool.Count == 0) return false;
                CTFXSlot s = pool[Rng.Next(pool.Count)];
                if (!player.spectate(s.Entry)) return false;
                Spectates++;
                return true;
            }
            catch (Exception ex) { LogErr("watch", ex); return false; }
        }

        void SpectateBot(Player player, ushort id)
        {
            try
            {
                CTFXSlot s;
                if (!_on || _inst != this || !_lowSlots.TryGetValue(id, out s) || s.Entry == null || !player.IsSpectator) return;
                if (!_arena._server._zoneConfig.arena.allowSpectating && player.PermissionLevel < InfServer.Data.PlayerPermission.ArenaMod)
                {
                    player.sendMessage(-1, "Zone doesn't allow spectating players.");
                    return;
                }
                if (player.spectate(s.Entry)) Spectates++;
            }
            catch (Exception ex) { LogErr("spectate", ex); }
        }

        /// <summary>Network thread: a mod's ::*command to a bot. The engine looks the recipient up among the zone's players and drops it
        /// without a word; the bot commands are handled here (arena thread).</summary>
        static void OnChat(CS_Chat pkt, Player player)
        {
            try
            {
                CTFXBotManager m = _inst;
                if (m == null || player == null || player._arena != m._arena) return;
                if (pkt.chatType == Helpers.Chat_Type.Normal && pkt.message != null && pkt.message.StartsWith("?playerchart", StringComparison.OrdinalIgnoreCase))
                {
                    player._arena.handleEvent(delegate(Arena a) { try { if (m._on && _inst == m) m.PlayerChart(player); } catch (Exception ex) { m.LogErr("chart", ex); } });
                    return;
                }
                if (pkt.chatType == Helpers.Chat_Type.Team)
                {   // the bots of his team hear his TEAM chat (summon requests, attack calls: CTFXChat). Travis 10-21: public chat and public
                    // macros (Chat_Type.Macro, which the engine routes as public) no longer summon him or call bots in
                    string said = pkt.message ?? "";
                    byte bong = pkt.bong;
                    player._arena.handleEvent(delegate(Arena a) { try { if (m._on && _inst == m) m.Chat.Hear(player, said, bong); } catch (Exception ex) { m.LogErr("chat", ex); } });
                    return;
                }
                if (pkt.chatType != Helpers.Chat_Type.Whisper) return;
                string msg = pkt.message, to = pkt.recipient;
                if (string.IsNullOrEmpty(msg) || msg[0] != '*' || string.IsNullOrEmpty(to)) return;
                player._arena.handleEvent(delegate(Arena a) { m.BotModCommand(player, to, msg.Substring(1)); });
            }
            catch (Exception) { }
        }

        /// <summary>::*warp (you go to the bot), ::*warp a5 or x,y in tiles (the bot goes there), ::*summon (the bot comes to you).</summary>
        void BotModCommand(Player player, string to, string text)
        {
            try
            {
                if (!_on || _inst != this) return;
                CTFXSlot slot = null;
                string key = to.Trim().Trim('{', '}').Trim();                // ::{Jett 45} or ::Jett 45 (nameWrap=)
                foreach (CTFXBotGroup g in _groups)
                    foreach (CTFXSlot s in g.Slots)
                        if (s.Name != null && s.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) slot = s;
                if (slot == null || _arena.getPlayerByName(to) != null) return;     // not a bot (or a real player has the name)
                if (player.PermissionLevel < InfServer.Data.PlayerPermission.ArenaMod && player.PermissionLevelLocal < InfServer.Data.PlayerPermission.ArenaMod)
                    return;
                string cmd = text, arg = "";
                int sp = text.IndexOf(' ');
                if (sp >= 0) { cmd = text.Substring(0, sp); arg = text.Substring(sp + 1).Trim(); }
                cmd = cmd.ToLowerInvariant();
                CTFXBot b = GetBot(slot.BotId);
                if (b == null || b.IsDead) { player.sendMessage(-1, slot.Name + " is dead (respawns shortly)."); return; }
                int now = Environment.TickCount;
                if (cmd == "warp" && arg.Length == 0)
                {
                    player.warp(Helpers.ResetFlags.ResetNone, b._state, 0, -1, 0);
                    return;
                }
                if (cmd == "warp")
                {
                    int x, y;
                    if (!ParseCoord(arg, out x, out y)) { player.sendMessage(-1, "That is not a valid coord (a5 or x,y in tiles)."); return; }
                    b.Teleported(x, y, now);
                    player.sendMessage(0, slot.Name + " warped to " + (x / 16) + "," + (y / 16) + ".");
                    return;
                }
                if (cmd == "summon")
                {
                    b.Teleported(player._state.positionX, player._state.positionY, now);
                    player.sendMessage(0, slot.Name + " summoned.");
                    return;
                }
                player.sendMessage(-1, "Bots take ::*warp, ::*warp <a5 or x,y>, ::*summon.");
            }
            catch (Exception ex) { LogErr("botCommand", ex); }
        }

        /// <summary>The *warp coordinate forms: a map square ("a5", its centre, as the engine) or exact tiles ("120,340"). Pixels out.</summary>
        static bool ParseCoord(string arg, out int x, out int y)
        {
            x = 0; y = 0;
            string a = arg.ToLowerInvariant();
            try
            {
                if (a.Length >= 2 && a[0] >= 'a' && a[0] <= 'z')
                {
                    x = (a[0] - 'a') * 16 * 80 + 40 * 16;
                    y = Convert.ToInt32(a.Substring(1)) * 16 * 80 - 40 * 16;
                    return true;
                }
                string[] p = a.Split(',');
                if (p.Length != 2) return false;
                x = Convert.ToInt32(p[0].Trim()) * 16;
                y = Convert.ToInt32(p[1].Trim()) * 16;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Network thread: a team warp aimed at a bot goes on to the arena thread; everything else is ignored.</summary>
        static void OnUseItem(CS_PlayerUseItem pkt, Player player)
        {
            try
            {
                CTFXBotManager m = _inst;
                if (m == null || player == null || player._arena != m._arena || m.LowSlot(pkt.targetVehicle) == null) return;
                ItemInfo.WarpItem item = AssetManager.Manager.getItemByID(pkt.itemID) as ItemInfo.WarpItem;
                if (item == null) return;
                ushort id = (ushort)pkt.targetVehicle;
                short x = pkt.posX, y = pkt.posY;
                if (item.warpMode == ItemInfo.WarpItem.WarpMode.SummonTeam)
                {
                    player._arena.handleEvent(delegate(Arena a) { m.SummonBotByPlayer(player, item, id); });
                    return;
                }
                if (item.warpMode != ItemInfo.WarpItem.WarpMode.WarpTeam) return;
                player._arena.handleEvent(delegate(Arena a) { m.TeamWarpToBot(player, item, id, x, y); });
            }
            catch (Exception) { }
        }

        /// <summary>Arena thread: the engine's checks for an item use (Handle_CS_PlayerUseItem) and a team warp (handlePlayerWarp),
        /// the zone's Player.WarpItem rules, then the warp to the bot.</summary>
        void TeamWarpToBot(Player player, ItemInfo.WarpItem item, ushort id, short posX, short posY)
        {
            try
            {
                CTFXSlot s;
                if (!_on || !LowIds || _inst != this || !_lowSlots.TryGetValue(id, out s)) return;
                if (player._arena != _arena || player.IsSpectator || player.IsDead || player.ActiveVehicle == null) return;
                if (!Helpers.isInRange(200, player._state.positionX, player._state.positionY, posX, posY)) return;
                if (!InfServer.Logic.Logic_Assets.SkillCheck(player, item.skillLogic)) return;
                if (_arena.checkBotAntiwarp(player) != null || _arena.checkVehAntiWarp(player) != null || player.checkAntiWarp() != null)
                    return;                                         // the engine has already said who is antiwarping them
                if (!player.ActiveVehicle._type.IsWarpable) return;
                CTFXBot b = GetBot(s.BotId);
                if (b == null || b.IsDead) { player.sendMessage(0xFF, "The player you are trying to warp to is dead."); return; }
                if (b._team != player._team) return;
                if (_script != null && !_script.playerWarpItem(player, item, id, posX, posY)) return;
                // playerWarpItem finds no Player behind a bot id, so the suit teleport's auto-drop is called here (before the warp)
                if (_script != null && item.id == 21) _script.AutoDropOnWarpToBot(player, b, s.Name, CTFXBot.ClassName(b.ClassId) == "Squad Leader");
                // the old client takes a team warp's energy off itself only for a target it holds as a player; for a bot it pays
                // nothing, so the server charges it (the item's cost on the terrain the warper stands on, Suit Teleport = 500)
                double cost;
                try { cost = TerrainEnergy(item.id, _arena.getTerrainID(player._state.positionX, player._state.positionY), player._state.energy, player._state.energy); }
                catch (Exception) { cost = 0; }
                if (cost > 0) { player.setEnergy((short)Math.Max(0, player._state.energy - cost)); WarpEnergyCharged++; }
                if (item.areaEffectRadius > 0)
                {
                    foreach (Player p in _arena.getPlayersInRange(posX, posY, item.areaEffectRadius))
                        if (!p.IsDead && p._team == b._team && p.ActiveVehicle != null && p.ActiveVehicle._type.IsWarpable)
                            p.warp(Helpers.ResetFlags.ResetNone, b._state, (short)item.accuracyRadius, -1, 0);
                }
                else player.warp(Helpers.ResetFlags.ResetNone, b._state, (short)item.accuracyRadius, -1, 0);
                player._lastWarpItemUseID = item.id;
                player._lastWarpItemUse = Environment.TickCount;
                WarpsToBots++;
            }
            catch (Exception ex) { LogErr("teamWarp", ex); }
        }

        /// <summary>?playerchart again, sent after the engine one (the client shows the newest): the same columns, players and bots.
        /// A bot row shows its class as both vehicles, so a player can see who on his team carries a summoner.</summary>
        void PlayerChart(Player player)
        {
            SC_Chart chart = new SC_Chart();
            chart.title = "Online Player Information Chart (with bots)";
            chart.columns = "-Name:14,-Squad:14,-Team:14,-Main Vehicle:14,-Driving Vehicle:14,Experience:10,Lifetime Experience:10,Cash:10,-Ranking:14";
            foreach (Player p in _arena.Players)
                chart.rows.Add(String.Format("\"{0}\"\",\"\"{1}\"\",\"\"{2}\"\",\"{3}\",\"{4}\",{5},{6},{7},\"{8}\"",
                    p._alias, p._squad ?? "", p._team == null ? "" : p._team._name, p._baseVehicle == null ? "" : p._baseVehicle._type.Name,
                    p._occupiedVehicle == null ? "" : p._occupiedVehicle._type.Name, p.Experience, p.ExperienceTotal, p.Cash,
                    p._server._zoneConfig.rank.getRank(p.ExperienceTotal)));
            List<CTFXSlot> seats = new List<CTFXSlot>();
            foreach (CTFXBotGroup g in _groups) seats.AddRange(g.Slots);
            seats.Sort(delegate(CTFXSlot a, CTFXSlot b2) { return string.Compare(a.Group.Team == null ? "" : a.Group.Team._name, b2.Group.Team == null ? "" : b2.Group.Team._name, StringComparison.Ordinal); });
            foreach (CTFXSlot s in seats)
            {
                if (s.Name == null || s.Group.Team == null) continue;
                CTFXBot b = GetBot(s.BotId);
                string cls = b != null ? CTFXBot.ClassName(b.ClassId) : "(respawning)";
                chart.rows.Add(String.Format("\"{0}\"\",\"\"{1}\"\",\"\"{2}\"\",\"{3}\",\"{4}\",{5},{6},{7},\"{8}\"",
                    s.Name, "bot", s.Group.Team._name, cls, s.Style.ToString(), 0, 0, 0, (s.Human != null ? s.Human.Tier : "") + " bty " + s.Bounty));
            }
            player._client.sendReliable(chart, 1);
        }

        /// <summary>A bot summons human p (CTFXSquads.SummonHuman, before the warp): p's auto-drop, as for a player summoner.</summary>
        internal void AutoDropOnBotSummon(Player p, CTFXBot leader)
        {
            if (_script != null && leader != null && leader.Slot != null)
                _script.AutoDropOnWarpToBot(p, leader, leader.Slot.Name, CTFXBot.ClassName(leader.ClassId) == "Squad Leader");
        }

        /// <summary>Arena thread: a human summons a bot of his team with his own summoner: the engine checks as for a team warp, the
        /// bot comes (its landing within the item's accuracy) and stays with him for a while (CTFXChat attach).</summary>
        void SummonBotByPlayer(Player player, ItemInfo.WarpItem item, ushort id)
        {
            try
            {
                CTFXSlot s;
                if (!_on || !LowIds || _inst != this || !_lowSlots.TryGetValue(id, out s)) return;
                if (player._arena != _arena || player.IsSpectator || player.IsDead || player.ActiveVehicle == null) return;
                if (!InfServer.Logic.Logic_Assets.SkillCheck(player, item.skillLogic)) return;
                if (_arena.checkBotAntiwarp(player) != null || _arena.checkVehAntiWarp(player) != null || player.checkAntiWarp() != null) return;
                CTFXBot b = GetBot(s.BotId);
                if (b == null || b.IsDead) { player.sendMessage(0xFF, "The player you are trying to summon is dead."); return; }
                if (b._team != player._team || b.InDropship) return;
                string pin = AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b);
                if (pin != null) { player.sendMessage(0xFF, s.Name + " is anti-warped."); return; }
                double cost;
                try { cost = TerrainEnergy(item.id, _arena.getTerrainID(player._state.positionX, player._state.positionY), player._state.energy, player._state.energy); }
                catch (Exception) { cost = 0; }
                if (cost > 0) { player.setEnergy((short)Math.Max(0, player._state.energy - cost)); WarpEnergyCharged++; }
                int h = Math.Max(1, (item.accuracyRadius > 0 ? item.accuracyRadius : 48) / 2), lx = player._state.positionX, ly = player._state.positionY;
                for (int i = 0; i < 8; i++)
                {
                    int ox = player._state.positionX + Rng.Next(-h, h + 1), oy = player._state.positionY + Rng.Next(-h, h + 1);
                    if (BodyFits(ox, oy)) { lx = ox; ly = oy; break; }
                }
                int now = Environment.TickCount;
                b.Teleported(lx, ly, now);
                player._lastWarpItemUseID = item.id;
                player._lastWarpItemUse = now;
                Chat.ItemSummons++;
                Chat.AttachTo(b, player, now, false);
                if (b.Slot != null) b.Slot.AttachSummoned = true;               // 10-21 (Waylander): his summon stays with him, anchors included
            }
            catch (Exception ex) { LogErr("summonBot", ex); }
        }

        // ---- energy cost per terrain (the client's resolveItemTerrainEnergy): each item lists one value per terrain type, under field
        // names that differ by item kind (Projectile terrainNEnergyCost, MultiUse / Warp energyCostTerrainN, MultiUse's 6th spelled
        // energyCostTerrainTerrain6). -1 = can't be used there, 0 = free, -2 = the whole max, -3 = whatever is left, > 0 = thousandths.
        static readonly Dictionary<int, int[]> _terrainCost = new Dictionary<int, int[]>();

        static int[] TerrainCosts(int itemId)
        {
            int[] c;
            if (_terrainCost.TryGetValue(itemId, out c)) return c;
            c = new int[16];
            ItemInfo it = AssetManager.Manager.getItemByID(itemId);
            if (it != null)
                for (int i = 0; i < 16; i++)
                {
                    FieldInfo f = it.GetType().GetField("energyCostTerrain" + i) ?? it.GetType().GetField("terrain" + i + "EnergyCost")
                        ?? (i == 6 ? it.GetType().GetField("energyCostTerrainTerrain6") : null)
                        ?? it.GetType().GetField("energyUseTerrain" + (i + 1));        // 11-11: VehicleMaker names them energyUseTerrain1..16 (1-based): turret makers cost bots nothing before
                    if (f != null && f.FieldType == typeof(int)) c[i] = (int)f.GetValue(it);
                }
            _terrainCost[itemId] = c;
            return c;
        }

        /// <summary>Energy <paramref name="itemId"/> costs on terrain <paramref name="terrain"/>; -1 = it can't be used there.</summary>
        internal static double TerrainEnergy(int itemId, int terrain, double max, double current)
        {
            int[] c = TerrainCosts(itemId);
            int v = terrain >= 0 && terrain < 16 ? c[terrain] : 0;
            if (v == -1) return -1;
            if (v == -2) return max;
            if (v == -3) return current;
            return v > 0 ? v / 1000.0 : 0;
        }

        /// <summary>No vehicle in the arena's list holds this id (a condemned one stays listed until the next arena poll).</summary>
        bool VehicleIdFree(ushort id)
        {
            foreach (Vehicle v in _arena.Vehicles)
                if (v._id == id) return false;
            return true;
        }

        /// <summary>The body leaves the server's vehicle and bot lists at the next arena poll WITHOUT the client destroy packet that
        /// destroy() sends. Clients keep the vehicle, and the player-list row on its id, until the slot's next body arrives under
        /// the same id and replaces it: one SC_Vehicles, as the server does for a player's class change (Player.setDefaultVehicle).</summary>
        void RemoveQuietly(CTFXBot b)
        {
            _bots.Remove(b._id);
            CTFXSlot s = b.Slot;
            if (!b.bCondemned) { b.bCondemned = true; _arena._botsInArena--; }   // what Vehicle.destroy does, minus the packet
            if (s != null) { if (s.BotId == b._id) s.BotId = 0; s.Ghost = b; }
            b.Release();
        }

        /// <summary>The engine wants a live slot's body gone (resetVehicles at every game start destroys every vehicle): it leaves the
        /// server lists quietly and the slot respawns within a second under the same id, so its row stays in every player list.
        /// False while stopping (StopAll clears _on first) or for a retired slot: those go the normal way.</summary>
        internal bool TakeOverRemoval(CTFXBot b)
        {
            CTFXSlot s = b.Slot;
            if (!_on || s == null || s.Retired || GetBot(b._id) != b) return false;
            ElectNewLeader(b);
            RemoveQuietly(b);
            EngineRemovals++;
            QueueSpawn(s, Environment.TickCount + 300 + Rng.Next(700));
            return true;
        }

        /// <summary>The engine is about to tell clients this body is gone (destroy): its row goes first, so no client keeps a row
        /// whose vehicle was destroyed. The slot's next body adds the row again, under the same id.</summary>
        internal void VehicleGone(CTFXBot b)
        {
            CTFXSlot s = b.Slot;
            if (s != null && s.Entry != null && s.Entry._id == b._id) DropEntry(s);
        }

        /// <summary>The slot's player-list row moves to team t on every client (Helpers.Player_SetTeam = SC_ChangeTeam, as a player's switch).</summary>
        void SetRowTeam(CTFXSlot s, Team t)
        {
            if (s == null || s.Entry == null || t == null || s.Entry._team == t) return;
            s.Entry._team = t;
            try { Helpers.Player_SetTeam(RowHolders(s), s.Entry, t); RowTeamChanges++; } catch (Exception ex) { LogErr("rowTeam", ex); }
        }
        internal int RowTeamChanges;               // stats: rows moved to a new team (clan joins)

        void DropEntry(CTFXSlot s)
        {
            Player e = s.Entry;
            s.Entry = null;
            if (e != null) { try { Helpers.Object_PlayerLeave(RowHolders(s), e); } catch (Exception) { } }
            s.Holders.Clear();
        }

        /// <summary>The arena players whose client holds the slot's row. 10-24 join crash: half the joins since batch 8 dropped out of
        /// the zone in the second they entered the arena, 58 of 59 with bots on - batch 8 sent each arrival SC_PlayerLeave for every
        /// row before the row itself, a leave for a player its client never had. Leaves and team changes go to holders only.</summary>
        List<Player> RowHolders(CTFXSlot s)
        {
            List<Player> to = new List<Player>();
            foreach (Player p in _arena.Players) if (s.Holders.Contains(p)) to.Add(p);
            return to;
        }

        /// <summary>Once a second: forget holders who left the arena (their client dropped every row), so they get the rows again when
        /// they come back.</summary>
        void PruneHolders()
        {
            HashSet<Player> here = new HashSet<Player>();
            foreach (Player p in _arena.Players) here.Add(p);
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots)
                    if (s.Holders.Count > 0) s.Holders.RemoveWhere(x => !here.Contains(x));
        }

        void DropGhost(CTFXSlot s)
        {
            Vehicle v = s.Ghost;
            s.Ghost = null;
            if (v != null) { try { Helpers.Object_VehicleDestroy(_arena.Players, v); } catch (Exception) { } }
        }

        /// <summary>The slot's player-list row (names + team uniforms), sent with its first body and kept across respawns. It is a
        /// stand-in Player that is only ever SENT to clients (SC_PlayerEnter / SC_PlayerLeave), never put in a team or arena list,
        /// so it cannot receive packets or count toward the zone population. Its id is the slot's vehicle id, which the engine
        /// already reports as the bot's occupant (SC_Vehicles, bot updates), so the client ties the name and the team (and with it
        /// the team's CFG uniform) to the body.</summary>
        void AnnounceSlot(CTFXSlot slot, CTFXBot b)
        {
            if (b._team == null) return;
            Vehicle bv = new Vehicle(b._type, _arena);
            bv._id = b._id;
            if (slot.Entry != null && slot.Entry._id == b._id)
            {   // same row; newBot sent the body. A row whose team is not the body's (a seat that changed team) is moved first
                SetRowTeam(slot, b._team);
                slot.Entry._baseVehicle = bv;
                return;
            }
            DropEntry(slot);
            Player p = new Player();
            p._id = b._id;
            p._alias = RowName(slot);           // Travis 10-22: "{Jett 45}" (nameWrap=) - sorts under every player in the list
            p._squad = "";
            p._team = b._team;
            p._baseVehicle = bv;
            slot.Entry = p;
            slot.Holders.Clear();
            foreach (Player x in _arena.Players) slot.Holders.Add(x);   // everyone in the arena gets it, players still entering included
            Helpers.Object_Players(_arena.Players, p);
            Helpers.Object_Vehicles(_arena.Players, b);             // the body again, after its row, so clients tie the two
        }

        internal CTFXBot GetBot(ushort id)
        {
            CTFXBot b;
            return (id != 0 && _bots.TryGetValue(id, out b)) ? b : null;
        }

        void DestroyBot(ushort id)
        {
            CTFXBot b;
            if (!_bots.TryGetValue(id, out b)) return;
            _bots.Remove(id);
            try { b.destroy(false); } catch (Exception) { }
            if (b.Slot != null && b.Slot.BotId == id) b.Slot.BotId = 0;
            b.Release();
        }

        void ElectNewLeader(CTFXBot b)
        {
            CTFXSlot s = b.Slot;
            if (s == null) return;
            CTFXBotGroup g = s.Group;
            int si = (int)s.Style;
            if (g.Leader[si] != b._id) return;
            g.Leader[si] = 0;
            foreach (CTFXSlot o in g.Slots)
            {
                if (o == s || o.Style != s.Style || o.BotId == 0) continue;
                CTFXBot ob = GetBot(o.BotId);
                if (ob != null && !ob.IsDead && ob != b) { g.Leader[si] = ob._id; break; }
            }
        }

        internal void RequestRecycle(CTFXBot b)
        {
            if (!_recycle.Contains(b._id)) _recycle.Add(b._id);
        }

        internal void RetreatGoal(CTFXBotGroup g, out int tx, out int ty, CTFXBot who = null)
        {
            if (Mini != null && who != null && Mini.Retreat(who, out tx, out ty)) return;       // MiniTP: stay inside the bot's own area
            if (g.HomeEnd != null && RouteNode(g.HomeEnd + ":patrol", 0, out tx, out ty)) return;      // back to its own flag room
            if (g.CoreOf != null && RandomCoreNode(g.CoreOf, out tx, out ty)) return;                  // back into its hall
            if (g.Attackers) SpawnTile(Areas[AreaIndexFor(g)], out tx, out ty);
            else RandomGoal(g.Home, out tx, out ty);
        }

        // ---- pathfinder throttle: at most 10 requests in flight across all bots (poll thread only)
        internal bool TryBeginPath()
        {
            if (_pathInFlight >= 10) return false;
            _pathInFlight++;
            PathReq++;
            return true;
        }

        internal void EndPath(bool failed)
        {
            if (_pathInFlight > 0) _pathInFlight--;
            if (failed) PathFail++;
        }

        // Console + logs/ctfxbots.log (the server console is not captured anywhere else)
        // File only, so the server console stays readable: everything lands in logs/ctfxbots.log.
        static void Out(string line)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine("logs", "ctfxbots.log"), DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine); }
            catch (Exception) { }
        }

        // Errors and the one-line start notice also reach the console.
        static void Shout(string line)
        {
            Console.WriteLine(line);
            Out(line);
        }

        internal void WarnOnce(string msg)
        {
            if (_warned.Count < 100 && _warned.Add(msg))
                Out("[CTFXBots] " + msg);
        }

        void LogErr(string where, Exception ex)
        {
            int now = Environment.TickCount;
            if (now - _lastErr < 5000) return;
            _lastErr = now;
            Shout("[CTFXBots] " + where + " error: " + ex);
        }

        // ---- hot spots
        internal void AddHotSpot(int x, int y)
        {
            _hsX[_hsHead] = x; _hsY[_hsHead] = y; _hsT[_hsHead] = Environment.TickCount;
            if (Cmdr != null) Cmdr.AddContact(x, y, Environment.TickCount, 3.0);
            _hsHead = (_hsHead + 1) % 20;
            if (_hsCount < 20) _hsCount++;
        }

        /// <summary>Newest hot spot (px), only if younger than 90 s.</summary>
        internal bool GetHotSpot(int now, out int x, out int y)
        {
            x = 0; y = 0;
            if (_hsCount == 0) return false;
            int idx = (_hsHead + 19) % 20;
            if (now - _hsT[idx] > 90000) return false;
            x = _hsX[idx]; y = _hsY[idx];
            return true;
        }

        // ================================================================ poll
        void Poll(int now)
        {
            try
            {
                if (_lastLoop != 0) { int gap = now - _lastLoop; _gapSum += gap; _gapN++; if (gap > _gapMax) _gapMax = gap; }
                _lastLoop = now;
                if (_on && _arena._bGameRunning && !_wasRunning) { OpeningUntil = now + 20000; OpeningWaits = 0; if (!Prof.IsMiniTP) Flags.Reset(now); }   // a game start: staggered exits, flags to the centres
                _wasRunning = _arena._bGameRunning;
                if (_on) { RefreshHumans(now); try { HuntStep(now); } catch (Exception ex) { LogErr("hunt", ex); } Turrets.Refresh(now); Turrets.Fight(now); Shots.Step(now); KbSampleHumans(now); if (_kbWatch.Count > 0) KbWatchStep(now); }   // every arena poll: hits land within one poll of their moment
                if (now - _lastPoll < 40) return;
                _lastPoll = now;
                if (!_arena._bActive)
                {
                    if (_inactiveSince == 0) _inactiveSince = now | 1;
                    else if (now - _inactiveSince > 10000) Detach("arena closed");
                    return;
                }
                _inactiveSince = 0;

                if (now - _lastHumanCheck >= 1000)
                {
                    _lastHumanCheck = now;
                    // Spectators count: someone panning around in spec to watch must start and keep the bots.
                    int n = 0;
                    foreach (Player p in _arena.Players) n++;
                    _humans = n;
                    PruneHolders();
                    if (_cfg.AutoStart)
                    {
                        if (!_on && !_manualOff && n > 0)
                            Start(_cfg.Count, "autostart");
                        else if (_on && n == 0)
                        {
                            if (!_noHumans) { _noHumans = true; _noHumanSince = now; }
                            else if (now - _noHumanSince >= 60000)
                            {
                                StopAll();
                                _on = false;
                                _noHumans = false;
                                Out("[CTFXBots] no humans for 60 s: all bots removed");
                            }
                        }
                        else _noHumans = false;
                    }
                }

                if (_on)
                {
                    ProcessRecycle(now);
                    if (_cfg.Squads) Squads.Step(_groups, now);
                    Cmdr.Step(now);
                    Chat.Step(now);
                    Flags.Step(now);
                    if (Mini != null) { try { Mini.Poll(now); MiniFillStep(now); } catch (Exception ex) { LogErr("mini", ex); } }
                    Bounty.Step(now);
                    Turrets.GuardStep(now);
                    Squads.PostStep(now);
                    AutoBounty(now);
                    Monitor.Sample(now);
                    BrawlStep(now);
                    BaseStep(now);
                    if (_cfg.ClanOn) ClanStep(now);
                    RoomStep(now);
                    if (_cfg.ClanOn && _cfg.MilkOn) MilkStep(now);
                    if (_cfg.ClanOn && _cfg.MilkTest) MilkTestStep(now);
                    if (now - _lastLostCheck >= 2000) { _lastLostCheck = now; ProcessLost(now); }
                    if (now - _lastArea >= 1000) { _lastArea = now; SampleAreas(); }
                    ProcessSpawns(now);
                    if (now - _lastStats >= _cfg.StatsEverySec * 1000) { _lastStats = now; _lastStatsLine = BuildStats(); Out(_lastStatsLine); WriteRound(); }
                    if (_tdTestAt != 0 && now - _tdTestAt >= 0) { _tdTestAt = 0; TdSelfTest(); }
                    HitTestStep(now);
                }
            }
            catch (Exception ex) { LogErr("poll", ex); }
        }

        void ProcessSpawns(int now)
        {
            if (_spawnQ.Count == 0 || now - _lastSpawn < 150) return;
            int made = 0, tried = 0;
            for (int i = 0; i < _spawnQ.Count && made < 3; )
            {
                SpawnReq r = _spawnQ[i];
                if (now - r.Due < 0) { i++; continue; }
                _spawnQ.RemoveAt(i);
                r.Slot.Queued = false;
                if (r.Slot.Retired) continue;
                tried++;
                if (SpawnSlot(r.Slot, now)) made++;
            }
            if (tried > 0) _lastSpawn = now;
        }

        void ProcessRecycle(int now)
        {
            if (_recycle.Count == 0) return;
            List<ushort> ids = new List<ushort>(_recycle);
            _recycle.Clear();
            foreach (ushort id in ids)
            {
                CTFXBot b = GetBot(id);
                if (b == null) continue;
                CTFXSlot s = b.Slot;
                ElectNewLeader(b);
                RemoveQuietly(b);                               // same id and row for the new body
                Recycled++;
                if (s != null) QueueSpawn(s, now + 800 + Rng.Next(700));
            }
        }

        /// <summary>Bots the engine destroyed behind our back (not polled for 4 s and no death event seen) are respawned.</summary>
        void ProcessLost(int now)
        {
            List<ushort> lost = null;
            foreach (KeyValuePair<ushort, CTFXBot> kv in _bots)
                if (!kv.Value.DeathHandled && now - kv.Value.LastPollTick > 4000)
                {
                    if (lost == null) lost = new List<ushort>();
                    lost.Add(kv.Key);
                }
            if (lost == null) return;
            foreach (ushort id in lost)
            {
                CTFXBot b = GetBot(id);
                if (b == null) continue;
                CTFXSlot s = b.Slot;
                ElectNewLeader(b);
                DestroyBot(id);
                if (s != null) QueueSpawn(s, now + 2000);
            }
        }

        // ================================================================ hooks from CTF.cs
        void OnBotDeath(Bot dead, Player killer, int weaponID)
        {
            try
            {
                CTFXBot b = dead as CTFXBot;
                if (b == null || b.Mgr != this || b.DeathHandled) return;
                b.DeathHandled = true;
                int now = Environment.TickCount;
                try { if (Usage != null) Usage.Death(b, killer == null ? b.LastHitBy : null, b.LastHitItem, now - b.LifeStart, killer != null); } catch (Exception) { }
                if (killer == null)
                {
                    if (b.LastHitBy != null) _killsBB++; else _killsOther++;
                }
                else
                {
                    _killsHB++;
                    if (killer._team != b._team)
                    {   // the zone kill reward (Logic_Rewards) with the bot bounty as the victim bounty, then his bounty grows
                        int vb = b.Slot != null ? b.Slot.Bounty : 0;
                        int cash, exp, pts;
                        KillReward(vb, killer.Bounty, out cash, out exp, out pts);
                        int bty0 = killer.Bounty, gain = BountyGain(pts);
                        killer.Cash += cash;
                        killer.Experience += exp;
                        killer.KillPoints += pts;
                        killer.Bounty += gain;
                        killer.syncState();
                        ShareKill(killer._team, b._state.positionX, b._state.positionY, cash, exp, pts, killer, null);
                        // Travis 10-27: "killing a high bounty bot doesn't give the reward?" - it does (the engine's own line under it says
                        // Cash=0 Exp=0 Points=0: that is the zone [Bot] reward, which ctfpl.cfg leaves at 0). All four numbers here, and logged.
                        if (b.Slot != null && b.Slot.Name != null)
                            killer.sendMessage(0, "You killed " + b.Slot.Name + " (bounty " + vb + "): +" + cash + " cash, +" + exp + " exp, +" + pts + " points, bounty "
                                + bty0 + " -> " + killer.Bounty + " (+" + gain + ")");
                        Out("[CTFXReward] " + killer._alias + " killed " + (b.Slot != null ? b.Slot.Name : "?") + " (bounty " + vb + "): +" + cash + " cash +" + exp
                            + " exp +" + pts + " pts, bounty " + bty0 + " -> " + killer.Bounty);
                    }
                }
                if (killer == null && b.LastHitBy != null && b.LastHitBy.Slot != null && b.LastHitBy._team != b._team)
                {
                    int cash, exp, pts;
                    KillReward(b.Slot != null ? b.Slot.Bounty : 0, b.LastHitBy.Slot.Bounty, out cash, out exp, out pts);
                    AddBounty(b.LastHitBy.Slot, BountyGain(pts));
                    ShareKill(b.LastHitBy._team, b._state.positionX, b._state.positionY, cash, exp, pts, null, b.LastHitBy);
                }
                try
                {   // soak telemetry (10-17): where and to whom every bot death happens -> logs/ctfxdeaths.jsonl
                    CTFXBot kb = killer == null ? b.LastHitBy : null;
                    string kk = killer != null ? "human" : b.LastHitByTurret ? "turret" : kb != null ? "bot" : "other";
                    int dFoes = 0, dMates = 0;
                    foreach (CTFXBot o in LiveBots)
                    {
                        if (o == b || o.IsDead || o.InDropship) continue;
                        double ddx = o._state.positionX - b._state.positionX, ddy = o._state.positionY - b._state.positionY;
                        if (ddx * ddx + ddy * ddy > 350.0 * 350.0) continue;
                        if (o._team == b._team) dMates++; else dFoes++;
                    }
                    System.IO.File.AppendAllText(System.IO.Path.Combine("logs", "ctfxdeaths.jsonl"),
                        "[" + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + ",\"" + (b._team != null ? b._team._name : "") + "\",\"" + (b.Slot != null ? b.Slot.Style.ToString() : "") + "\",\""
                        + AreaAt(b._state.positionX, b._state.positionY) + "\",\"" + kk + "\",\"" + (kb != null && kb._team != null ? kb._team._name : "") + "\",\"" + (kb != null && kb.Slot != null ? kb.Slot.Style.ToString() : "")
                        + "\"," + b.ClassId + "," + (Flags != null && Flags.Carrying(b) ? 1 : 0) + "," + (now - b.LifeStart) + "," + dFoes + "," + dMates + "]" + Environment.NewLine);
                }
                catch (Exception) { }
                SeatDeath(b.Slot);
                if (killer == null && b.LastHitBy != null && b.LastHitBy._team != b._team) SeatKill(b.LastHitBy.Slot);
                if (b.Slot != null) { if (b.Slot.Bounty > b.Slot.BountyPeak) b.Slot.BountyPeak = b.Slot.Bounty; b.Slot.Bounty = BountyStart(); }
                AddHotSpot(b._state.positionX, b._state.positionY);
                try { Bounty.NoteKill(b, killer == null ? b.LastHitBy : null, killer); } catch (Exception) { }
                try { Flags.BotDied(b, killer, killer == null ? b.LastHitBy : null, killer == null && b.LastHitByTurret); }
                catch (Exception ex) { LogErr("flagDeath", ex); }
                try { if (Mini != null) Mini.CarrierDied(b, killer, killer == null && !b.LastHitByTurret ? b.LastHitBy : null); }     // 11-11: MiniTP carriers
                catch (Exception ex) { LogErr("flagDeath", ex); }
                b.LastHitBy = null;
                ElectNewLeader(b);
                // the corpse lies where it fell until the slot respawns: SpawnSlot takes it out quietly and reuses its id
                if (_on && b.Slot != null) QueueSpawn(b.Slot, now + 6000 + Rng.Next(4001));
            }
            catch (Exception ex) { LogErr("botDeath", ex); }
        }

        void OnPlayerKilled(Player victim, Bot killer)
        {
            try
            {
                if (killer != null && !(killer is CTFXBot)) return;
                _killsBH++;
                CTFXBot kb = BotKillerOf(victim);
                if (kb != null && kb.Slot != null && victim != null)
                {
                    SeatKill(kb.Slot);
                    int cash, exp, pts;
                    KillReward(victim.Bounty, kb.Slot.Bounty, out cash, out exp, out pts);
                    AddBounty(kb.Slot, BountyGain(pts));
                    if (victim._state != null) ShareKill(kb._team, victim._state.positionX, victim._state.positionY, cash, exp, pts, null, kb);
                }
                Flags.HumanKilled(victim);
                if (Mini != null && kb != null) Mini.HumanKilledBy(victim, kb);      // 11-11 (Travis: the bot that killed me holding a flag did not take it)
                if (victim != null && victim._state != null)
                    AddHotSpot(victim._state.positionX, victim._state.positionY);
            }
            catch (Exception ex) { LogErr("playerKilled", ex); }
        }

        // ================================================================ stats / commands
        // One check after each start that a TD carrier really blocks an enemy warp: the server's own checkBotAntiwarp asked about a
        // stand-in enemy player (never sent anywhere) 100 px from a carrier, and at a point 900 px away.
        int _tdTestAt;
        void TdSelfTest()
        {
            try
            {
                CTFXBot carrier = null;
                int carriers = 0;
                foreach (CTFXBot b in _bots.Values)
                    if (b.TdActive && !b.IsDead) { carriers++; if (carrier == null) carrier = b; }
                ItemInfo.UtilityItem td = CTFXBot.TdItem();
                if (carrier == null) { Out("[CTFXBots] TD self-test: no living carrier (" + (td == null ? "item not found" : "no TD class in play") + ")"); return; }
                Team enemy = null;
                foreach (Team t in _arena.Teams) if (t != carrier._team) { enemy = t; break; }
                Player near = StandIn(carrier, 100, enemy), far = StandIn(carrier, 900, enemy);
                int nearest = int.MaxValue;
                foreach (CTFXBot b in _bots.Values)
                    if (b.TdActive && !b.IsDead && b._team != enemy)
                    {
                        double dx = b._state.positionX - far._state.positionX, dy = b._state.positionY - far._state.positionY;
                        nearest = Math.Min(nearest, (int)Math.Sqrt(dx * dx + dy * dy));
                    }
                Bot hitNear = _arena.checkBotAntiwarp(near), hitFar = _arena.checkBotAntiwarp(far);
                Out("[CTFXBots] TD self-test: " + carriers + " living bots carry the Teleport Disruptor (anti-warp " + (td != null ? td.antiWarpDistance : 0)
                    + " px); an enemy warping 100 px from one is " + (hitNear != null ? "BLOCKED" : "NOT blocked") + ", at 900 px (nearest carrier "
                    + nearest + " px) " + (hitFar != null ? "blocked" : "not blocked"));
            }
            catch (Exception ex) { LogErr("tdSelfTest", ex); }
        }

        static Player StandIn(CTFXBot near, int dx, Team team)
        {
            Player p = new Player();
            p._state = new Helpers.ObjectState();
            p._state.positionX = (short)(near._state.positionX + dx);
            p._state.positionY = near._state.positionY;
            p._team = team;
            return p;
        }

        /// <summary>The slot's player-list callsign ("Kestrel 41"), picked once and kept across respawns; never a name a player has.</summary>
        internal string NameFor(CTFXSlot slot)
        {
            if (slot.Name != null) return slot.Name;
            for (int tries = 0; tries < 50; tries++)
            {
                int k = _nameNext++;
                string n = Mini != null ? CTFXGamerTags.Make(k) : Callsigns[(k * 37) % Callsigns.Length] + " " + (11 + (k * 53) % 89);      // MiniTP: a 2000-era gamer tag
                string renamed;
                if (_cfg != null && _cfg.Renames.TryGetValue(n, out renamed)) n = renamed;   // rename= (CTFXBots.txt); the checks below use the new name
                if (_arena.getPlayerByName(n) != null) continue;
                bool used = false;
                foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot o in g.Slots) if (o != slot && o.Name == n) used = true;
                if (used) continue;
                slot.Name = n;
                return n;
            }
            slot.Name = "Bot " + (_nameNext++);
            return slot.Name;
        }

        /// <summary>The name players see on the slot's player-list row (so in the kill feed too) and in its team chat: the callsign inside
        /// nameWrap= (Travis 10-22: "{Jett 45}" sorts under every player name in the alphabetical list). The callsign alone stays the key
        /// for ?bot, rename=, mod whispers and the personality seed.</summary>
        internal string RowName(CTFXSlot slot)
        {
            string n = NameFor(slot), w = _cfg != null ? _cfg.NameWrap : null;
            return w != null && w.Length == 2 ? w[0] + n + w[1] : n;
        }

        /// <summary>A player finished entering the arena: send them every slot's player-list row (dead bots included), then the bodies
        /// again so the client ties each to its row.</summary>
        void OnPlayerEnter(Player p)
        {
            try
            {
                if (!_on || !BotPlayers || p == null || p._arena != _arena) return;
                List<Vehicle> vs = new List<Vehicle>();
                foreach (CTFXBotGroup g in _groups)
                    foreach (CTFXSlot s in g.Slots)
                    {
                        if (s.Entry == null) continue;
                        // Travis 10-22 (each post listed twice for the player who opened the arena): AnnounceSlot already broadcast the
                        // rows of the bots that spawned while he was still entering, and the old client lists a second SC_PlayerEnter
                        // for the same id as a second row. 10-24: batch 8 sent a leave before every row, and a client that gets a leave
                        // for a row it never had drops out of the zone (the join crash) - so a row now goes only to a client without it.
                        if (!s.Holders.Contains(p)) { Helpers.Object_Players(p, s.Entry); s.Holders.Add(p); }
                        CTFXBot b = GetBot(s.BotId);
                        if (b != null && b._id == s.Entry._id) vs.Add(b);
                    }
                if (vs.Count > 0) Helpers.Object_Vehicles(p, vs);
            }
            catch (Exception ex) { LogErr("playerEnter", ex); }
        }

        /// <summary>Tier mix, the average reaction time, and how many bots of one team are on the same enemy at most (swarming).</summary>
        string HumanStats()
        {
            int r = 0, g = 0, v = 0, a = 0, most = 0;
            Dictionary<object, int> on = new Dictionary<object, int>();
            foreach (CTFXBot b in _bots.Values)
            {
                if (b.Human != null)
                    switch (b.Human.Tier) { case "rookie": r++; break; case "regular": g++; break; case "veteran": v++; break; default: a++; break; }
                object t = b.TargetObj;
                if (t == null || b.IsDead) continue;
                int c;
                on.TryGetValue(t, out c);
                on[t] = ++c;
                if (c > most) most = c;
            }
            return " | human: rookie/regular/veteran/ace " + r + "/" + g + "/" + v + "/" + a + ", reaction avg "
                + (Reacts > 0 ? (ReactMsSum / Reacts).ToString() : "-") + " ms over " + Reacts + ", most bots on one target " + most;
        }

        string AvgEnergyPct()
        {
            double sum = 0; int n = 0;
            foreach (CTFXBot b in _bots.Values) if (!b.IsDead && b.EnergyMax > 0) { sum += b.Energy / b.EnergyMax; n++; }
            return n == 0 ? "-" : ((int)Math.Round(sum * 100 / n)).ToString();
        }

        int ListedCount()
        {
            int n = 0;
            foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots) if (s.Entry != null) n++;
            return n;
        }

        /// <summary>Living bots per class right now (the full class spread).</summary>
        string ClassCensus()
        {
            SortedDictionary<string, int> n = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (CTFXBot b in _bots.Values)
            {
                if (b.IsDead) continue;
                string k = CTFXBot.ShortClass(b.ClassId);
                int c; n.TryGetValue(k, out c); n[k] = c + 1;
            }
            StringBuilder sb = new StringBuilder(" | classes");
            foreach (KeyValuePair<string, int> kv in n) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value);
            return sb.ToString();
        }

        /// <summary>Living bots standing on the outside landing strips A-H right now, and the busiest strip (Travis: 2-5 at most).</summary>
        // ---- density by area: every second the living bots per base end, building core hall and "out" (anywhere else); the
        // stats line shows the last sample and the peak since the previous stats line
        int _lastArea;
        readonly Dictionary<string, int> _areaNow = new Dictionary<string, int>(StringComparer.Ordinal), _areaPeak = new Dictionary<string, int>(StringComparer.Ordinal);

        void SampleAreas()
        {
            if (Routes == null) return;
            _areaNow.Clear();
            foreach (CTFXBot b in _bots.Values)
            {
                if (b.IsDead) continue;
                string a = null;
                foreach (string e in EndIds) if (InsideEnd(e, b)) { a = e; break; }
                if (a == null)
                    foreach (KeyValuePair<string, List<int>> kv in _coreNodes)
                    {
                        foreach (int n in kv.Value)
                        {
                            int dx = Routes.X[n] - b._state.positionX, dy = Routes.Y[n] - b._state.positionY;
                            if (dx * dx + dy * dy <= 320 * 320) { a = kv.Key + "-core"; break; }
                        }
                        if (a != null) break;
                    }
                if (a == null) a = "out";
                int c;
                _areaNow.TryGetValue(a, out c);
                _areaNow[a] = c + 1;
            }
            foreach (KeyValuePair<string, int> kv in _areaNow)
            {
                int p;
                if (!_areaPeak.TryGetValue(kv.Key, out p) || kv.Value > p) _areaPeak[kv.Key] = kv.Value;
            }
        }

        string AreaCensus()
        {
            if (_areaNow.Count == 0 && _areaPeak.Count == 0) return "";
            List<string> keys = new List<string>(_areaPeak.Keys);
            keys.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder(" | areas now/peak");
            foreach (string k in keys)
            {
                int n;
                _areaNow.TryGetValue(k, out n);
                sb.Append(' ').Append(k).Append(' ').Append(n).Append('/').Append(_areaPeak[k]);
            }
            _areaPeak.Clear();
            return sb.ToString();
        }

        string StripCensus()
        {
            int total = 0, worst = 0;
            string worstName = "-";
            foreach (CTFXArea a in Areas)
            {
                double hw = a.W / 32.0, hh = a.H / 32.0;
                int n = 0;
                foreach (CTFXBot b in _bots.Values)
                {
                    if (b.IsDead) continue;
                    double tx = b._state.positionX / 16.0, ty = b._state.positionY / 16.0;
                    if (Math.Abs(tx - a.Cx) <= hw && Math.Abs(ty - a.Cy) <= hh) n++;
                }
                total += n;
                if (n > worst) { worst = n; worstName = a.Name; }
            }
            return " | on strips " + total + " (busiest " + worstName + " " + worst + ")";
        }

        // ---------------------------------------------------------------- route book (CTFXRoutes.cs)
        void LoadRoutes()
        {
            Routes = null; EndIds.Clear(); _spawnNodes.Clear(); _neutralNodes.Clear(); _caveNodes.Clear();
            if (!_cfg.UseRoutes) { Out("[CTFXBots] routes: off (routes=0), bots use the engine pathfinder"); return; }
            string err = null;
            string path = Prof.IsMiniTP
                ? Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("MiniTP", "MiniTPRoutes.txt"))))
                : Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXRoutes.txt"))));
            try { Routes = CTFXRouteBook.Load(path, WalkLine, 96, out err); } catch (Exception ex) { Routes = null; err = ex.Message; }
            if (Routes == null) { Shout("[CTFXBots] routes: " + err + ", falling back to the engine pathfinder"); return; }
            foreach (KeyValuePair<string, int[]> kv in Routes.Routes)
            {
                string name = kv.Key, kind;
                Routes.Kinds.TryGetValue(name, out kind);
                if (name.EndsWith(":patrol", StringComparison.OrdinalIgnoreCase)) EndIds.Add(name.Substring(0, name.Length - 7));
                if (kind == "spawn")
                {
                    string end = name.Substring(0, name.IndexOf(':'));
                    List<int> l;
                    if (!_spawnNodes.TryGetValue(end, out l)) { l = new List<int>(); _spawnNodes[end] = l; }
                    l.Add(kv.Value[0]);
                }
                if (kind == "strip" || kind == "ring") _neutralNodes.AddRange(kv.Value);      // the outside areas: landing strips, plaza ring
                if (kind == "link" && (name.StartsWith("cave-", StringComparison.OrdinalIgnoreCase) || name.EndsWith(">plaza", StringComparison.OrdinalIgnoreCase)))
                {
                    _neutralNodes.AddRange(kv.Value);
                    if (name.StartsWith("cave-", StringComparison.OrdinalIgnoreCase)) _caveNodes.Add(kv.Value[0]);
                }
            }
            EndIds.Sort(StringComparer.Ordinal);
            Out("[CTFXBots] " + Routes.Summary + "; ends " + string.Join(" ", EndIds.ToArray()) + "; no engine pathfinder calls");
            BuildCores();
        }

        /// <summary>The other end of the same building (brown-top -> brown-bottom), null when there is none.</summary>
        internal string OtherEnd(string end)
        {
            if (end == null) return null;
            int dash = end.IndexOf('-');
            if (dash <= 0) return null;
            string b = end.Substring(0, dash + 1);
            foreach (string e in EndIds) if (e != end && e.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Tile of node index of a named route (index -1 = the last node, -2 the one before ...).</summary>
        internal bool RouteNode(string route, int index, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int[] r;
            if (Routes == null || route == null || !Routes.Routes.TryGetValue(route, out r) || r.Length == 0) return false;
            int n = r[index < 0 ? Math.Max(0, r.Length + index) : Math.Min(index, r.Length - 1)];
            tx = Routes.X[n] >> 4; ty = Routes.Y[n] >> 4;
            return true;
        }

        /// <summary>Live bots (any team, out of the ship) within r px of (x, y).</summary>
        internal int CrowdAt(int x, int y, double r)
        {
            int n = 0;
            double r2 = r * r;
            foreach (CTFXBot o in LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y;
                if (dx * dx + dy * dy <= r2) n++;
            }
            return n;
        }

        /// <summary>
        /// Travis 10-15 (J21 screenshot): "few bots are focusing on the objective properly and getting too caught in combat outside of areas
        /// that attack or defend a flagging base set of chokes". An attack position on base end <paramref name="end"/>: a node of its
        /// approach from outside (its cave route: the west corridor, the north plaza and the canyons) or of the hall link's last stretch
        /// under its mouth; never a spot 5+ bots already hold (4 tries, then its patrol).
        /// </summary>
        internal bool StagingGoal(string end, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (end == null) return false;
            string q = end.Split('-')[0];
            for (int i = 0; i < 4; i++)
            {
                // 10-15 night: inside the end (75 %) or the hall mouth under it; no longer the outdoor cave approach
                bool ok = Rng.NextDouble() < 0.75 ? RandomEndNode(end, out tx, out ty) : RouteNode(q + "-core>" + end, -1, out tx, out ty);
                if (!ok) continue;
                if (InStaleBrawl(tx * 16 + 8, ty * 16 + 8)) { StaleStageSkips++; continue; }
                if (CrowdAt(tx * 16 + 8, ty * 16 + 8, 200.0) < 5) { Staged++; return true; }
                StageCrowded++;
            }
            return RandomRouteNode(end + ":patrol", out tx, out ty);
        }
        internal int Staged, StageCrowded, HallPassThrough;

        readonly Dictionary<string, List<int>> _endNodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A random route waypoint inside base end <paramref name="end"/> (InsideEndPx).</summary>
        internal bool RandomEndNode(string end, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (Routes == null || end == null) return false;
            List<int> l;
            if (!_endNodes.TryGetValue(end, out l))
            {
                l = new List<int>();
                for (int n = 0; n < Routes.N; n++) if (!Routes.IsGrid(n) && InsideEndPx(end, Routes.X[n], Routes.Y[n])) l.Add(n);
                _endNodes[end] = l;
            }
            if (l.Count == 0) return false;
            int k = l[Rng.Next(l.Count)];
            tx = Routes.X[k] >> 4; ty = Routes.Y[k] >> 4;
            return true;
        }

        internal bool RandomRouteNode(string route, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int[] r;
            if (Routes == null || route == null || !Routes.Routes.TryGetValue(route, out r) || r.Length == 0) return false;
            int n = r[Rng.Next(r.Length)];
            tx = Routes.X[n] >> 4; ty = Routes.Y[n] >> 4;
            return true;
        }

        /// <summary>One of a base end's spawn spots (its rooms): where a fire team flanks through it.</summary>
        internal bool RandomSpawnNode(string end, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            List<int> l;
            if (Routes == null || end == null || !_spawnNodes.TryGetValue(end, out l) || l.Count == 0) return false;
            int n = l[Rng.Next(l.Count)];
            tx = Routes.X[n] >> 4; ty = Routes.Y[n] >> 4;
            return true;
        }

        /// <summary>A node on the neutral routes (caves, plaza links): where the bounty pack roams.</summary>
        internal bool RandomNeutralNode(out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (Routes == null || _neutralNodes.Count == 0) return false;
            int n = _neutralNodes[Rng.Next(_neutralNodes.Count)];
            tx = Routes.X[n] >> 4; ty = Routes.Y[n] >> 4;
            return true;
        }

        /// <summary>Spawn inside the group's home end (a checked spawn tile), or in a cave for the bounty pack.</summary>
        bool RouteSpawnTile(CTFXBotGroup g, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (Routes == null) return false;
            List<int> l = null;
            if (g.HomeEnd != null) _spawnNodes.TryGetValue(g.HomeEnd, out l);
            else if (g.CoreOf != null) _coreNodes.TryGetValue(g.CoreOf, out l);
            else if (g.Home == ZonePockets) l = _caveNodes;
            if (l == null || l.Count == 0) return false;
            int n = l[Rng.Next(l.Count)];
            tx = Routes.X[n] >> 4; ty = Routes.Y[n] >> 4;
            return true;
        }

        // ---- building cores and boxes (route book): home = <quad>-core groups and the squads' "inside" tests
        readonly Dictionary<string, List<int>> _coreNodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int[]> _quadBox = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);   // px: x0 y0 x1 y1

        /// <summary>
        /// Core nodes of each building: its hub-to-end link routes, plus the strip routes' nodes within 90 tiles of the hub (the hall,
        /// not the landing strip outside). The building box spans its ends' patrol / spawn / raid nodes and those core links.
        /// </summary>
        void BuildCores()
        {
            _coreNodes.Clear(); _quadBox.Clear();
            Dictionary<string, int> hub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int[]> kv in Routes.Routes)
            {
                string name = kv.Key, kind;
                Routes.Kinds.TryGetValue(name, out kind);
                int core = name.IndexOf("-core>", StringComparison.OrdinalIgnoreCase), dash = name.IndexOf('-');
                if (kind == "link" && core > 0 && dash == core)
                {
                    string q = name.Substring(0, core), to = name.Substring(core + 6);
                    if (!to.StartsWith(q + "-", StringComparison.OrdinalIgnoreCase)) continue;     // the plaza link leaves the building
                    if (!hub.ContainsKey(q)) hub[q] = kv.Value[0];
                    Core(q).AddRange(kv.Value);
                    foreach (int n in kv.Value) Box(q, n);
                }
                else if ((kind == "patrol" || kind == "spawn" || kind == "raid") && dash > 0)
                    foreach (int n in kv.Value) Box(name.Substring(0, dash), n);
            }
            foreach (KeyValuePair<string, int[]> kv in Routes.Routes)
            {
                string kind;
                Routes.Kinds.TryGetValue(kv.Key, out kind);
                int at = kv.Key.IndexOf(">", StringComparison.Ordinal);
                if (kind != "strip" || at < 0) continue;
                string target = kv.Key.Substring(at + 1);
                int core = target.IndexOf("-core", StringComparison.OrdinalIgnoreCase);
                if (core <= 0) continue;
                string q = target.Substring(0, core);
                int h;
                if (!hub.TryGetValue(q, out h)) continue;
                foreach (int n in kv.Value)
                {
                    int dx = (Routes.X[n] - Routes.X[h]) >> 4, dy = (Routes.Y[n] - Routes.Y[h]) >> 4;
                    if (dx * dx + dy * dy <= 90 * 90) { Core(q).Add(n); Box(q, n); }
                }
            }
            StringBuilder sb = new StringBuilder("[CTFXBots] building cores:");
            foreach (KeyValuePair<string, List<int>> kv in _coreNodes)
            {
                int[] b = _quadBox[kv.Key];
                sb.Append(' ').Append(kv.Key).Append(" (").Append(kv.Value.Count).Append(" nodes, box ").Append(b[0] >> 4).Append(',').Append(b[1] >> 4)
                  .Append('-').Append(b[2] >> 4).Append(',').Append(b[3] >> 4).Append(')');
            }
            Out(sb.ToString());
        }

        List<int> Core(string q)
        {
            List<int> l;
            if (!_coreNodes.TryGetValue(q, out l)) { l = new List<int>(); _coreNodes[q] = l; }
            return l;
        }

        void Box(string q, int n)
        {
            int[] b;
            int x = Routes.X[n], y = Routes.Y[n];
            if (!_quadBox.TryGetValue(q, out b)) { _quadBox[q] = new int[] { x, y, x, y }; return; }
            if (x < b[0]) b[0] = x; if (y < b[1]) b[1] = y; if (x > b[2]) b[2] = x; if (y > b[3]) b[3] = y;
        }

        internal bool RandomCoreNode(string q, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            List<int> l;
            if (Routes == null || q == null || !_coreNodes.TryGetValue(q, out l) || l.Count == 0) return false;
            int n = l[Rng.Next(l.Count)];
            tx = Routes.X[n] >> 4; ty = Routes.Y[n] >> 4;
            return true;
        }

        /// <summary>The hub of a building's core: where its core links meet.</summary>
        internal bool CoreHub(string q, out int tx, out int ty)
        {
            return RouteNode(q + "-core>" + TopEnd(q), 0, out tx, out ty);
        }

        /// <summary>The end a core group raids: <quad>-top when there is one, else the building's first end.</summary>
        internal string TopEnd(string q)
        {
            string first = null;
            foreach (string e in EndIds)
            {
                if (!e.StartsWith(q + "-", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.EndsWith("-top", StringComparison.OrdinalIgnoreCase)) return e;
                if (first == null) first = e;
            }
            return first;
        }

        /// <summary>Inside a base end: within 20 tiles of one of its spawn spots.</summary>
        internal bool InsideEnd(string end, CTFXBot b)
        {
            List<int> l;
            if (Routes == null || end == null || !_spawnNodes.TryGetValue(end, out l)) return false;
            foreach (int n in l)
            {
                int dx = Routes.X[n] - b._state.positionX, dy = Routes.Y[n] - b._state.positionY;
                if (dx * dx + dy * dy <= 320 * 320) return true;
            }
            return false;
        }

        /// <summary>InsideEnd for a point (px).</summary>
        internal bool InsideEndPx(string end, int x, int y)
        {
            List<int> l;
            if (Routes == null || end == null || !_spawnNodes.TryGetValue(end, out l)) return false;
            foreach (int n in l)
            {
                int dx = Routes.X[n] - x, dy = Routes.Y[n] - y;
                if (dx * dx + dy * dy <= 320 * 320) return true;
            }
            return false;
        }

        /// <summary>CTF.cs ?humans: a pixel position as players name it. Twin Peaks proper is above tile row 1060 (the CTF:X import starts
        /// there), the dropship station east of tile column 990; inside CTF:X a base end ("brown top"), a hall ("brown hall") or "outside".</summary>
        string PlaceName(int x, int y)
        {
            try
            {
                if (y / 16 < 1060) return "Twin Peaks";
                if (x / 16 >= 990) return "dropship";
                string a = AreaAt(x, y);
                if (a == "out") return "outside";
                if (a.EndsWith("-core")) return a.Substring(0, a.Length - 5) + " hall";
                return a.Replace('-', ' ');
            }
            catch (Exception) { return null; }
        }

        /// <summary>The area a point is in, as SampleAreas counts it: a base end, a building's core hall (q-core), or "out".</summary>
        internal string AreaAt(int x, int y)
        {
            foreach (string e in EndIds) if (InsideEndPx(e, x, y)) return e;
            foreach (KeyValuePair<string, List<int>> kv in _coreNodes)
                foreach (int n in kv.Value)
                {
                    int dx = Routes.X[n] - x, dy = Routes.Y[n] - y;
                    if (dx * dx + dy * dy <= 320 * 320) return kv.Key + "-core";
                }
            return "out";
        }

        internal List<int> CoreNodes(string q)
        {
            List<int> l;
            return q != null && _coreNodes.TryGetValue(q, out l) ? l : null;
        }

        internal int[] QuadBox(string q)
        {
            int[] b;
            return q != null && _quadBox.TryGetValue(q, out b) ? b : null;
        }

        internal void Log(string line) { Out(line); }

        /// <summary>Inside a building's box (its ends, core hall and the raid routes between them), 2 tiles of margin.</summary>
        internal bool InQuad(string q, CTFXBot b)
        {
            int[] box;
            if (q == null || !_quadBox.TryGetValue(q, out box)) return false;
            int x = b._state.positionX, y = b._state.positionY;
            return x >= box[0] - 32 && x <= box[2] + 32 && y >= box[1] - 32 && y <= box[3] + 32;
        }

        /// <summary>A stormer's hiding spot: of 4 random spawn spots of the end, the one with the fewest enemies of team within 250 px.</summary>
        internal bool HideTile(string end, Team team, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            List<int> l;
            if (Routes == null || end == null || !_spawnNodes.TryGetValue(end, out l) || l.Count == 0) return false;
            int best = -1, bn = int.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                int n = l[Rng.Next(l.Count)], c = 0;
                foreach (CTFXBot o in _bots.Values)
                {
                    if (o.IsDead || o._team == team) continue;
                    int dx = o._state.positionX - Routes.X[n], dy = o._state.positionY - Routes.Y[n];
                    if (dx * dx + dy * dy <= 250 * 250) c++;
                }
                if (c < bn) { bn = c; best = n; }
            }
            tx = Routes.X[best] >> 4; ty = Routes.Y[best] >> 4;
            return true;
        }

        // ---- bots' anti-warp on bots (Travis 10-21: bots TD-ing other bots caused more annoyance than it solved - escapes, summons and
        // flag hops held all over the map). botAntiwarpBots=flag (default): an enemy BOT's anti-warp holds only a bot carrying a flag;
        // on = every bot (the old rule); off = none. Players' and turrets' anti-warp hold every bot as before, and the bots' anti-warp
        // still holds every human (his warps are the engine's own checks).
        internal int BotTd = 1;                        // 0 off, 1 flag, 2 on (botAntiwarpBots, CTFXBots.txt)
        internal int BotTdSkips;                       // stats: anti-warp checks of a bot's spot where only an enemy bot's anti-warp was ignored

        bool BotTdHolds(CTFXBot who)
        {
            if (who == null || BotTd == 2) return true;                        // a human's spot, or the old rule
            return BotTd == 1 && Flags != null && Flags.Carrying(who);
        }

        /// <summary>
        /// Who anti-warps a warp by team at (x, y): the server's own checks of handlePlayerWarp (enemy bots within 1000 px, enemy
        /// turrets and enemy players within 5000 px whose active gear reaches the spot) asked about a stand-in player that is
        /// never sent anywhere. Null = free to warp. <paramref name="who"/> = the bot standing there (null = a human's spot): an
        /// enemy bot's anti-warp holds it only as botAntiwarpBots says.
        /// </summary>
        internal string AntiWarpedAt(int x, int y, Team team, CTFXBot who = null)
        {
            Player p = new Player();
            p._arena = _arena;
            p._team = team;
            p._state = new Helpers.ObjectState();
            p._state.positionX = (short)x;
            p._state.positionY = (short)y;
            Bot b = _arena.checkBotAntiwarp(p);
            if (b != null)
            {
                if (BotTdHolds(who)) return "a " + b._type.Name.Trim((char)34, ' ');
                BotTdSkips++;
            }
            Computer c = _arena.checkVehAntiWarp(p);
            if (c != null) return "a " + c._type.Name.Trim((char)34, ' ');
            Player q = p.checkAntiWarp();
            if (q != null) return q._alias;
            return null;
        }

        /// <summary>Where the anti-warp covering (x, y) for a player of team stands (an enemy bot, turret or player); false = none.
        /// <paramref name="who"/>: the bot standing there, as for AntiWarpedAt.</summary>
        internal bool AntiWarpSource(int x, int y, Team team, out int sx, out int sy, CTFXBot who = null)
        {
            sx = 0; sy = 0;
            Player p = new Player();
            p._arena = _arena;
            p._team = team;
            p._state = new Helpers.ObjectState();
            p._state.positionX = (short)x;
            p._state.positionY = (short)y;
            Bot b = _arena.checkBotAntiwarp(p);
            if (b != null && BotTdHolds(who)) { sx = b._state.positionX; sy = b._state.positionY; return true; }
            Computer c = _arena.checkVehAntiWarp(p);
            if (c != null) { sx = c._state.positionX; sy = c._state.positionY; return true; }
            Player q = p.checkAntiWarp();
            if (q != null) { sx = q._state.positionX; sy = q._state.positionY; return true; }
            return false;
        }

        /// <summary>A bot body (radius 8) fits at (x, y) px: its centre and four edge points are on open tiles.</summary>
        internal bool BodyFits(int x, int y)
        {
            return !BlockedPx(x, y) && !BlockedPx(x - 8, y) && !BlockedPx(x + 8, y) && !BlockedPx(x, y - 8) && !BlockedPx(x, y + 8);
        }

        /// <summary>warpFx: tell clients the warp item was used (SC_ItemUsed from the user's player-list row) so they draw its effect.</summary>
        internal void WarpFx(CTFXBot user, CTFXBot target, int item, int x, int y)
        {
            if (!_cfg.WarpFx || user == null || user.Slot == null || user.Slot.Entry == null) return;
            try { Helpers.Player_RouteItemUsed(false, _arena.Players, user.Slot.Entry, target._id, (short)item, (short)x, (short)y, user._state.yaw); }
            catch (Exception ex) { LogErr("warpFx", ex); }
        }

        internal void Note(string msg) { WarnOnce(msg); }

        CTFXZone ZoneOfQuad(string quad)
        {
            switch (quad)
            {
                case "brown": return ZoneBrown;
                case "blue": return ZoneTop;
                case "purple": return ZoneBottom;
                case "green": return ZoneLeft;
            }
            return ZoneCenter;
        }

        /// <summary>
        /// A straight walk a bot body fits along: the centre line and both sides (6 px out) on unblocked tiles. Live tile state,
        /// so an open door is walkable and a shut one is not.
        /// </summary>
        internal bool WalkLine(int x0, int y0, int x1, int y1)
        {
            double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0) return !BlockedPx(x0, y0);
            double nx = -dy / len * 6.0, ny = dx / len * 6.0;
            int steps = (int)(len / 8.0) + 1;
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps, x = x0 + dx * t, y = y0 + dy * t;
                if (BlockedPx((int)x, (int)y) || BlockedPx((int)(x + nx), (int)(y + ny)) || BlockedPx((int)(x - nx), (int)(y - ny))) return false;
            }
            return true;
        }

        // ---- off the route network (a landing patch behind a rock, a warp landing, a knockback into a corner): the tile grid to the
        // nearest waypoint (10-12: bots landing on area H's east patch walked into the rock between it and every waypoint)
        Dictionary<int, int> _nodeAt;                       // tile key (x * 4096 + y) -> a route node on or next to that tile
        internal int NetEscapes, NetEscapeFails;
        static readonly int[] GDx = new int[] { 0, 1, 1, 1, 0, -1, -1, -1 }, GDy = new int[] { -1, -1, 0, 1, 1, 1, 0, -1 };

        void BuildNodeTiles()
        {
            _nodeAt = new Dictionary<int, int>();
            if (Routes == null) return;
            for (int n = 0; n < Routes.N; n++)
            {
                int tx = Routes.X[n] >> 4, ty = Routes.Y[n] >> 4;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int k = (tx + dx) * 4096 + (ty + dy);
                        if (!_nodeAt.ContainsKey(k)) _nodeAt[k] = n;
                    }
            }
        }

        /// <summary>
        /// Breadth-first over the tiles (8-way; the body must fit, a diagonal step needs both shoulder tiles) from (px, py) to the nearest
        /// tile next to a route waypoint it can walk straight onto, within 80 tiles. The path comes back string-pulled (each leg a
        /// straight walkable line) as px points ending on the waypoint. False = nothing within reach.
        /// </summary>
        internal bool PathToNetwork(int px, int py, List<int> xs, List<int> ys, out int node)
        {
            node = -1; xs.Clear(); ys.Clear();
            if (Routes == null) return false;
            if (_nodeAt == null) BuildNodeTiles();
            int sx = px >> 4, sy = py >> 4, start = sx * 4096 + sy, found = -1;
            Dictionary<int, int> prev = new Dictionary<int, int>();
            Queue<int> q = new Queue<int>();
            prev[start] = -1; q.Enqueue(start);
            while (q.Count > 0 && prev.Count < 25000)
            {
                int k = q.Dequeue(), x = k / 4096, y = k % 4096, n;
                if (_nodeAt.TryGetValue(k, out n) && WalkLine(x * 16 + 8, y * 16 + 8, Routes.X[n], Routes.Y[n])) { found = k; node = n; break; }
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + GDx[d], ny = y + GDy[d], nk = nx * 4096 + ny;
                    if (Math.Abs(nx - sx) > 80 || Math.Abs(ny - sy) > 80 || prev.ContainsKey(nk)) continue;
                    if (!BodyFits(nx * 16 + 8, ny * 16 + 8)) continue;
                    if (GDx[d] != 0 && GDy[d] != 0 && (!BodyFits(nx * 16 + 8, y * 16 + 8) || !BodyFits(x * 16 + 8, ny * 16 + 8))) continue;
                    prev[nk] = k; q.Enqueue(nk);
                }
            }
            if (found < 0) { NetEscapeFails++; return false; }
            List<int> path = new List<int>();
            for (int k = found; k != -1; k = prev[k]) path.Add(k);
            path.Reverse();
            int cx = px, cy = py, i = 0;
            while (i < path.Count - 1)
            {
                int j = path.Count - 1;
                while (j > i + 1 && !WalkLine(cx, cy, path[j] / 4096 * 16 + 8, path[j] % 4096 * 16 + 8)) j--;
                cx = path[j] / 4096 * 16 + 8; cy = path[j] % 4096 * 16 + 8;
                xs.Add(cx); ys.Add(cy);
                i = j;
            }
            xs.Add(Routes.X[node]); ys.Add(Routes.Y[node]);
            NetEscapes++;
            return true;
        }

        internal bool BlockedPx(int x, int y)
        {
            if (x < 0 || y < 0) return true;
            int tx = x >> 4, ty = y >> 4;
            if (tx >= _arena._levelWidth || ty >= _arena._levelHeight) return true;
            return _arena._tiles[ty * _arena._levelWidth + tx].Blocked;
        }

        void LoadKnockback()
        {
            Knock.Clear();
            string file = "?";
            try
            {
                string name = _arena._server._zoneConfig.level.itmFile;
                file = Path.Combine("assets", name);
                if (!File.Exists(file)) file = name;
                foreach (string line in File.ReadAllLines(file))
                {
                    if (line.Length < 3 || line[0] != '6' || line[1] != ',') continue;     // projectile rows only
                    List<string> v = CSVReader.Parse(line);
                    if (v.Count < 129 || v[0] != "6") continue;
                    KnockInfo k = new KnockInfo();
                    k.Force = CSVReader.GetInt(v[121]);
                    k.Radius = CSVReader.GetInt(v[120]);
                    if (k.Force == 0 || k.Radius == 0) continue;                            // radius 0 = no recoil at all (client)
                    k.IgnoreMs = Math.Max(0, CSVReader.GetInt(v[123]) * 10);
                    k.Absolute = CSVReader.GetBool(v[126]);
                    k.DirPct = CSVReader.GetInt(v[128]);
                    Knock[CSVReader.GetInt(v[2])] = k;
                }
                Out("[CTFXBots] knockback: " + Knock.Count + " projectiles push bots (" + file + " col 121)");
            }
            catch (Exception ex) { Knock.Clear(); Shout("[CTFXBots] knockback off, could not read " + file + ": " + ex.Message); }
        }

        internal CTFXUsage Usage = new CTFXUsage();      // item / build / class usage for the overnight runs (CTFXShop.cs)

        /// <summary>The round file the overnight harness collects: usage + the latest stats line (logs/ctfxround.json).</summary>
        void WriteRound()
        {
            try { File.WriteAllText(Path.Combine("logs", "ctfxround.json"), Usage.Json(_lastStatsLine, "shop " + _cfg.Shop)); }
            catch (Exception ex) { WarnOnce("round file: " + ex.Message); }
        }

        string BuildStats()
        {
            int alive = 0;
            int[] per = new int[11];
            foreach (CTFXBot b in _bots.Values) if (!b.IsDead) { alive++; per[(int)b.Style]++; }
            double avg = PollCount > 0 ? PollTicks * 1000000.0 / Stopwatch.Frequency / PollCount : 0.0;
            PollTicks = 0; PollCount = 0;
            return "[CTFXBots] alive " + alive + "/" + TotalSlots()
                + " | post " + per[0] + " guard " + per[1] + " assault " + per[2] + " roam " + per[3] + " sniper " + per[4] + " drain " + per[5] + " adhd " + per[6] + " stormer " + per[7] + " engineer " + per[8] + " bountier " + per[9]
                + " | kills b>b " + _killsBB + " b>h " + _killsBH + " h>b " + _killsHB
                + " | path req/fail " + PathReq + "/" + PathFail + " | stuck " + StuckEvents + " | recycled " + Recycled
                + " | doors open " + (Doors != null ? Doors.Opens : 0) + " fail " + (Doors != null ? Doors.Fails : 0)
                + " | kb " + Knocks + " | stun " + Stuns + " (from humans " + HumanStuns + ", routed at once " + StunRoutes + ") | dry " + DryEvents
                + " | energy: shots refused " + EnergyRefusals + ", warps refused " + WarpEnergyRefusals + ", shield kept " + (ShieldMilliHp / 1000) + " hp off bots, avg " + AvgEnergyPct() + "%"
                + (Routes != null ? " | route plans/fail " + RoutePlans + "/" + RouteFails + ", onto the network by grid " + NetEscapes + " (no way " + NetEscapeFails + ")" : "")
                + AreaCensus() + StripCensus() + ClassCensus() + (BotPlayers ? " | listed " + ListedCount() : "")
                + " | respawn ids kept/moved " + IdsKept + "/" + IdsMoved + " | engine resets taken over " + EngineRemovals
                + " | ids " + (LowIds ? "low" : "5001+") + ", team warps to bots " + WarpsToBots + ", killer reports mapped " + KillersMapped + " (sent as player kills " + KillsRouted + "), healed by players " + HealsByPlayers + " (" + HealsByPlayersHp + "), spectates " + Spectates + HumanStats() + ", warp energy charged " + WarpEnergyCharged
                + ", bot anti-warp ignored " + BotTdSkips + " (botAntiwarpBots=" + (BotTd == 2 ? "on" : BotTd == 0 ? "off" : "flag") + ")"
                + " | shots " + Shots.Fired + " (direct " + Shots.Direct + ", splash " + Shots.Splashed + ", wall " + Shots.Walls
                + ", floor " + Shots.Floors + ", spent " + Shots.Expired + ", own team " + Shots.TeamHits + ", flying " + Shots.Live
                + (Shots.Dropped > 0 ? ", DROPPED " + Shots.Dropped : "") + ")" + Shots.VolleyTally()
                + (_cfg.Squads ? Squads.Summary(_groups) : "") + Turrets.Stats()
                + Cmdr.Summary() + Cmdr.BrownText() + Cmdr.LinesText() + Cmdr.SiegeText() + SensorsText() + CombosText() + UtilText() + OddsText() + EliteText() + BaseText() + ClanText() + RoomText() + (_cfg.MilkOn ? MilkText() : "") + HuntText() + PacksText() + " | fall-backs " + FallBacks + " (to cover " + CoverRetreats + ", lone " + AloneFalls + ")" + " | answered shooters past engage range " + FarAnswers + " (viewScale " + ViewScale.ToString("0.##") + ", engageScale " + EngageScale.ToString("0.##") + ")" + DodgeText() + RfgText() + " | energy-only cuts " + DrainCuts + SpreadText() + Dropship.Summary() + Chat.Summary() + Flags.Summary() + Bounty.Summary() + BountyText() + HoldText() + " | opening exits staggered " + OpeningWaits
                + Monitor.Window(Environment.TickCount)
                + " | avg poll us " + avg.ToString("F1", CultureInfo.InvariantCulture) + LoopText();
        }

        string KindSummary(CTFXKind kind)
        {
            int teams = 0, total = 0, alive = 0;
            int[] per = new int[11];
            foreach (CTFXBotGroup g in _groups)
            {
                if (g.Kind != kind) continue;
                teams++;
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Retired) continue;
                    total++;
                    CTFXBot b = GetBot(s.BotId);
                    if (b != null && !b.IsDead) { alive++; per[(int)s.Style]++; }
                }
            }
            if (teams == 0) return null;
            return kind + " (" + teams + " team" + (teams == 1 ? "" : "s") + "): alive " + alive + "/" + total
                + " [post " + per[0] + " guard " + per[1] + " assault " + per[2] + " roam " + per[3] + " sniper " + per[4] + " drain " + per[5] + " adhd " + per[6] + " stormer " + per[7] + " engineer " + per[8] + " bountier " + per[9] + "]";
        }

        bool Command(Player player, string payload)
        {
            try
            {
                if (player == null || player._arena != _arena) return false;
                string[] a = (payload ?? "").Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
                if (Mini != null && sub != "status"
                    && player.PermissionLevel < InfServer.Data.PlayerPermission.ArenaMod && player.PermissionLevelLocal < InfServer.Data.PlayerPermission.ArenaMod)
                { player.sendMessage(-1, "Only arena mods can switch the bots."); return true; }
                switch (sub)
                {
                    case "on":
                        {
                            int n = 0;
                            if (a.Length > 1) int.TryParse(a[1], out n);
                            if (_pendingCfg != null && !_pendingCfg.Enabled) { player.sendMessage(0, "CTFXBots: reloaded config has enabled=0, not starting."); return true; }
                            _manualOff = false;
                            if (Mini != null) MiniOff = false;
                            Start(n, "command");
                            player.sendMessage(0, (Mini != null ? "MiniTP bots" : "CTFXBots") + ": on, " + TotalSlots() + " bots queued (spawning gradually).");
                            if (Mini != null) _arena.sendArenaMessage("The bots are back (" + _miniPer + " per side, humans take their seats) - switched on by " + player._alias + ".");
                            return true;
                        }
                    case "off":
                        StopAll();
                        _on = false;
                        _manualOff = true;
                        if (Mini != null)
                        {
                            MiniOff = true;
                            player.sendMessage(0, "MiniTP bots: off, all bots removed - humans only until *bots on.");
                            _arena.sendArenaMessage("All bots were removed by " + player._alias + ": humans only. (*bots on brings them back)");
                            return true;
                        }
                        player.sendMessage(0, "CTFXBots: off, all bots removed (autostart paused until *ctfxbots on).");
                        return true;
                    case "reload":
                        {
                            CTFXConfig c = CTFXConfig.Load();
                            if (c == null) { player.sendMessage(0, "CTFXBots: CTFXBots.txt not found, keeping the current config."); return true; }
                            _pendingCfg = c;
                            player.sendMessage(0, "CTFXBots: config re-read (" + c.Groups.Count + " team lines, " + c.Solos.Count + " solo lines); applies on the next *ctfxbots on.");
                            return true;
                        }
                    case "tune":
                        {
                            if (a.Length > 1 && a[1].ToLowerInvariant() == "reset")
                            {
                                Cmdr.ResetTune(_cfg.PauseScale, _cfg.HerdK, _cfg.DefendRatio);
                                player.sendMessage(0, "CTFXBots: tune reset to CTFXBots.txt: " + Cmdr.Knobs());
                                return true;
                            }
                            player.sendMessage(0, "CTFXBots: " + Cmdr.Knobs());
                            foreach (string l in Cmdr.TuneLog) player.sendMessage(0, "  " + l);
                            return true;
                        }
                    case "skill":
                    case "level":
                    case "difficulty":
                        {   // *bots skill [mix|rookie|regular|veteran|ace|elite|weak]: every bot's skill tier (Travis 11-08: a moderator adjusts it to test behaviours)
                            string want = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                            if (want.Length > 0)
                            {
                                if (!CTFXHuman.SetSkillMode(want)) { player.sendMessage(0, "CTFXBots: skill is mix | rookie | regular | veteran | ace | elite (veterans + aces) | weak (rookies + regulars)"); return true; }
                                foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots)
                                {   // every seat gets its new roll; a living body takes it at once, the timings it caches at spawn follow with its next body
                                    s.Human = CTFXHuman.Roll(Rng, NameFor(s));
                                    if (s.Style == CTFXStyle.Bountier) s.Human.MakeElite(new Random(s.Human.Seed ^ 0x5bd1e995));
                                    CTFXBot lb = GetBot(s.BotId);
                                    if (lb != null && !lb.IsDead) lb.Human = s.Human;
                                }
                                _arena.sendArenaMessage("Bot skill set to " + want + " by " + player._alias + ".");
                            }
                            int[] tiers = new int[4];
                            foreach (CTFXBotGroup g in _groups) foreach (CTFXSlot s in g.Slots)
                            {
                                if (s.Retired || s.Human == null) continue;
                                string t = s.Human.Tier;
                                tiers[t == "rookie" ? 0 : t == "regular" ? 1 : t == "veteran" ? 2 : 3]++;
                            }
                            player.sendMessage(0, "CTFXBots skill: " + CTFXHuman.SkillMode + " | rookie " + tiers[0] + ", regular " + tiers[1] + ", veteran " + tiers[2] + ", ace " + tiers[3] + " | *bots skill mix|rookie|regular|veteran|ace|elite|weak");
                            return true;
                        }
                    case "inv":
                    case "profile":
                        {   // *ctfxbots inv [name]: the named bot (substring), else the nearest living bot
                            string want = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1).ToLowerInvariant() : null;
                            CTFXBot hit = null; double best = double.MaxValue;
                            foreach (CTFXBot b in _bots.Values)
                            {
                                if (b.IsDead || b.Slot == null) continue;
                                if (want != null) { if (b.Slot.Name.ToLowerInvariant().Contains(want)) { hit = b; break; } continue; }
                                double dx = b._state.positionX - player._state.positionX, dy = b._state.positionY - player._state.positionY, d = dx * dx + dy * dy;
                                if (d < best) { best = d; hit = b; }
                            }
                            if (hit == null) { player.sendMessage(0, "CTFXBots: no living bot " + (want != null ? "named like '" + want + "'" : "found") + "."); return true; }
                            foreach (string l in hit.InvReport()) player.sendMessage(0, l);
                            return true;
                        }
                    case "status":
                        {
                            int alive = 0;
                            foreach (CTFXBot b in _bots.Values) if (!b.IsDead) alive++;
                            player.sendMessage(0, "CTFXBots: " + (_on ? "ON" : "off") + " | alive " + alive + "/" + TotalSlots() + " | queued " + _spawnQ.Count + " | humans " + _humans + " | autostart " + (_cfg.AutoStart ? "on" : "off") + (_manualOff ? " (paused)" : ""));
                            foreach (CTFXKind k in new CTFXKind[] { CTFXKind.Titan, CTFXKind.Collective, CTFXKind.Private, CTFXKind.Solo })
                            {
                                string line = KindSummary(k);
                                if (line != null) player.sendMessage(0, "  " + line);
                            }
                            player.sendMessage(0, _lastStatsLine);
                            return true;
                        }
                }
                player.sendMessage(0, "CTFXBots: *ctfxbots on [N] | off | status | skill [tier] | reload | tune [reset]");
                return true;
            }
            catch (Exception ex) { LogErr("command", ex); return true; }
        }
    }
}
/* PARSER GUARD - keep last */
