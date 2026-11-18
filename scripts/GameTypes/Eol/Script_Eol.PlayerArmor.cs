using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner) human and pirate bots wear what a real player of their class would: "Suit super charger for
    /// the energy, then either armor-hard / soft / composite piece as the main armor, then one boots/greaves.... then one
    /// gauntlets.. then one field such as a PF generator or BDF generator" - picked per bot, class-legal by the item's
    /// own skill logic; officers sometimes wear an artificed piece ("the captains can sometimes have artificed pieces"),
    /// sergeants and up sometimes a reformulated one; a bot running a cloak / stealth wears no body armour. This
    /// REPLACES the old bot armour (no +35 %, no extra ignore / percent, no pirate armour table): the class's own
    /// armour + the items, nothing more - and the bot runs a player's energy (engine Bot.PlayerEnergy), so energy
    /// damage drains it and its fields and cloak drop at empty.
    /// </summary>
    public partial class Script_Eol
    {
        static readonly string[] BodyArmourCategories = { "Armor-Hard", "Armor-Soft", "Armor-Composite" };
        const string BootsCategory = "Armor-Boots/Greaves", GauntletsCategory = "Armor-Gauntlets", FieldsCategory = "Armor-Fields";
        /// <summary>Not for bots: the "Unnamed Blast" event pieces, and HAZMAT Armor / Boots - Hazmat (not in the shop).</summary>
        static readonly HashSet<int> ArmourNeverForBots = new HashSet<int> { 1714, 1715, 1716, 1526, 1691 };
        /// <summary>Conscripts: their own set (class skill 30).</summary>
        static readonly int[] ConscriptBody = { 192, 195 }, ConscriptBoots = { 190, 191 }, ConscriptGauntlets = { 193, 194 }, ConscriptField = { 200 };
        /// <summary>Pirate Roamer vehicles and the player class whose armour + energy they use instead of their own table.</summary>
        static readonly Dictionary<int, int> RoamerArmourClass = new Dictionary<int, int>
        {
            { 142, ClsCaptain }, { 144, ClsMarine }, { 145, ClsMarine }, { 146, ClsMarine }, { 147, ClsMarine }, { 157, ClsMarine },
            // (2026-11-18, owner: "feels much better, except the pirate bots, please apply same logic to them") the pirate
            // base bots too - Defenders: Marine, base Captains: Captain, Engineers: Chief Engineer (they had only their
            // vehicle's flat 6 ignore + 10 %, Engineers 1 + 0 %)
            { 140, ClsMarine }, { 141, ClsMarine }, { 143, ClsMarine }, { 156, ClsMarine }, { 158, ClsMarine },
            { 162, ClsCaptain }, { 415, ClsCaptain }, { 437, ClsCaptain },
            { 463, ClsChiefEngineer }, { 464, ClsChiefEngineer }, { 465, ClsChiefEngineer },
        };

        Dictionary<int, List<int>> _classSkillIds;

        List<int> ClassSkillIds(int cls)
        {
            if (_classSkillIds == null)
                _classSkillIds = new Dictionary<int, List<int>>();
            List<int> ids;
            if (_classSkillIds.TryGetValue(cls, out ids))
                return ids;
            ids = new List<int>();
            List<SkillInfo> skills = _arena._server._assets.getSkillInfos;
            if (skills != null)
                foreach (SkillInfo s in skills)
                    if (s != null && s.DefaultVehicleId == cls)
                        ids.Add(s.SkillId);
            _classSkillIds[cls] = ids;
            return ids;
        }

        bool IsOfficerClass(int cls) { return cls == ClsCaptain || cls == ClsColonel || cls == ClsLieutenant; }
        bool IsNcoOrOfficerClass(int cls) { return IsOfficerClass(cls) || cls == ClsSergeant || cls == ClsSquadLeader; }

        /// <summary>Utility items of one category this class may wear. Exp-gated pieces (@) only for the right ranks, at a chance.</summary>
        List<int> ClassArmourChoices(int cls, string category)
        {
            List<int> ok = new List<int>();
            VehInfo veh = _arena._server._assets.getVehicleByID(cls);
            List<int> skills = ClassSkillIds(cls);
            if (veh == null)
                return ok;   // (a class with no class skill - Partisan, Combat Tech - still wears what its logic allows)
            Random r = _arena._rand;
            bool artificedRoll = IsOfficerClass(cls) && r.NextDouble() < 0.35;
            bool reformRoll = IsNcoOrOfficerClass(cls) && r.NextDouble() < 0.35;
            foreach (ItemInfo it in _arena._server._assets.getItems)
            {
                ItemInfo.UtilityItem u = it as ItemInfo.UtilityItem;
                if (u == null || ArmourNeverForBots.Contains(u.id) || !string.Equals((u.category ?? "").Trim('"', ' '), category, StringComparison.OrdinalIgnoreCase))
                    continue;
                string logic = u.skillLogic ?? "";
                if (logic.Contains("@700000") && !artificedRoll) continue;   // artificed: officers, sometimes
                if (logic.Contains("@350000") && !reformRoll) continue;      // reformulated: sergeants and up, sometimes
                if (!ClassMayUse(logic, veh.ClassId, skills))
                    continue;
                ok.Add(u.id);
            }
            // an officer who rolled artificed wears one if the class can
            if (artificedRoll && category == "Armor-Hard")
            {
                List<int> art = ok.FindAll(id => (Itm(id)?.skillLogic ?? "").Contains("@700000"));
                if (art.Count > 0) return art;
            }
            return ok;
        }

        int PickOne(List<int> l) { return l == null || l.Count == 0 ? 0 : l[_arena._rand.Next(l.Count)]; }
        int PickOne(int[] a) { return a == null || a.Length == 0 ? 0 : a[_arena._rand.Next(a.Length)]; }

        static bool BotHasUtility(Bots.Bot bot, int itemId)
        {
            if (bot.Loadout == null) return false;
            for (int s = 0; s < BotLoadout.SlotCount; s++)
                if (bot.Loadout.GetItemId(s) == itemId && bot.Loadout.GetSlotKind(s) == BotLoadoutSlotKind.Utility)
                    return true;
            return false;
        }

        static bool BotRunsStealth(Bots.Bot bot)
        {
            if (bot.Loadout == null) return false;
            foreach (ItemInfo.UtilityItem u in bot.Loadout.GetActiveUtilities())
                if (u.cloakDistance > 0 || u.stealthDistance > 0)
                    return true;
            return false;
        }

        /// <summary>
        /// Player armour + energy for a human / pirate bot of class <paramref name="cls"/>. <paramref name="useClassTable"/>:
        /// the bot's vehicle isn't the class (pirate Roamer, board rider) - take the class's armour + energy instead.
        /// Call after its cloak (if any) is set.
        /// </summary>
        public void ApplyPlayerArmor(Bots.Bot bot, int cls, bool useClassTable)
        {
            if (bot == null || bot.Loadout == null)
                return;
            try
            {
                // no bot armour on top: the class + its items only
                bot.ExtraArmorIgnore = 0;
                bot.ExtraArmorPercent = 0;
                bot.ExtraArmorPercentBypassEnergy = 0;
                bot.DamageTakenPercent = bot is AlienHorde ? AlienDamageTakenPercent : BotDamageTakenPercent;   // (2026-11-18: no bonus - BotDamageTakenPercent)
                if (useClassTable)
                {
                    VehInfo cv = _arena._server._assets.getVehicleByID(cls);
                    if (cv != null) bot.ArmorSource = cv;
                }

                if (!BotHasUtility(bot, ItmSuperCharger))
                    SetUtility(bot, ItmSuperCharger);
                bool stealth = BotRunsStealth(bot);
                int body, boots, gauntlets, field;
                if (cls == ClsColonel)
                {   // (2026-11-17, owner: "there should be no colonel armor") the SuperCharger only
                    body = boots = gauntlets = field = 0;
                }
                else if (cls == ClsConscript)
                {
                    body = PickOne(ConscriptBody); boots = PickOne(ConscriptBoots); gauntlets = PickOne(ConscriptGauntlets); field = PickOne(ConscriptField);
                }
                else
                {
                    List<int> bodies = new List<int>();
                    foreach (string c in BodyArmourCategories) bodies.AddRange(ClassArmourChoices(cls, c));
                    if (IsOfficerClass(cls))
                    {   // (keep the artificed pick if it rolled one)
                        List<int> art = bodies.FindAll(id => (Itm(id)?.skillLogic ?? "").Contains("@700000"));
                        if (art.Count > 0) bodies = art;
                    }
                    body = PickOne(bodies);
                    boots = PickOne(ClassArmourChoices(cls, BootsCategory));
                    gauntlets = PickOne(ClassArmourChoices(cls, GauntletsCategory));
                    field = PickOne(ClassArmourChoices(cls, FieldsCategory));
                }
                if (stealth) body = 0;   // (owner) a cloak / stealth user wears no body armour
                // (2026-11-18, owner: "the infils energy is really slow regen rate ... the same with black ops") and no
                // field: cloak + field drained more than they recharge (BlackOps -27.5 -18 vs +42.5 a second), so energy
                // sat near zero and cloak / fields kept switching off - the cloak is their energy cost, like a player's
                if (stealth) field = 0;
                foreach (int id in new[] { body, boots, gauntlets, field })
                    if (id != 0 && Itm(id) is ItemInfo.UtilityItem && !BotHasUtility(bot, id))
                        SetUtility(bot, id);
                bot.PlayerEnergy = true;
                bot.RefreshUtilityEffects();
                if (_playerArmorLogged < 12)
                {
                    _playerArmorLogged++;
                    Log.write(TLog.Normal, "[Eol] player armour: class {0} -> body {1}, boots {2}, gauntlets {3}, field {4}{5}",
                        cls, Itm(body)?.name ?? "-", Itm(boots)?.name ?? "-", Itm(gauntlets)?.name ?? "-", Itm(field)?.name ?? "-", stealth ? " (stealth: no body armour)" : "");
                    // (2026-11-18) what is really worn and counted against damage
                    List<string> worn = new List<string>();
                    foreach (ItemInfo.UtilityItem u in bot.Loadout.GetActiveUtilities()) worn.Add(u.name);
                    int ki, kp, ei, ep;
                    bot.GetWornArmor(0, out ki, out kp); bot.GetWornArmor(1, out ei, out ep);
                    Log.write(TLog.Normal, "[Eol]   worn + counted: {0} | kinetic {1} ignore {2:0.#} %, explosive {3} ignore {4:0.#} %",
                        string.Join(", ", worn), ki / 1000, kp / 10.0, ei / 1000, ep / 10.0);
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] player armour for class {0}: {1}", cls, ex.Message);
            }
        }
        static int _playerArmorLogged;

        /// <summary>A pirate Roamer that hasn't had its armour yet: the player armour of its class (from PrepareNewBots).</summary>
        void PreparePirateRoamerArmor(Bots.Bot b)
        {
            int cls;
            if (b == null || b.PlayerEnergy || b._type == null || b is AlienHorde || !RoamerArmourClass.TryGetValue(b._type.Id, out cls))
                return;   // (2026-11-18: any team now - the pirate base teams too)
            ApplyPlayerArmor(b, cls, useClassTable: true);
        }
    }
}
