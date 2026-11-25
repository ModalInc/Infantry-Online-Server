using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "i want to create a board medic on that ghost reaper team, he is on a hover board, and has
    /// medikit and also summon kit") the Ghost Reapers' 5th: a Medic on a Hoverboard. Medikit = the squad medic heal
    /// pulses (Script_Eol.Tactics.cs PollMedics; H calls bring him too). Summon kit = out of a fight, once a minute, he
    /// pulls any Ghost Reaper bot that is far away and not fighting to his side. Medic weapons, armour and energy.
    /// </summary>
    public partial class Script_Eol
    {
        const int VehHoverboard = 120;
        const int BoardMedicSummonMs = 60000;       // summon kit cooldown
        const int BoardMedicSummonBeyond = 1200;    // pulls mates further away than this
        int _tickBoardMedic;
        readonly Dictionary<Bots.Bot, int> _boardMedicNextSummon = new Dictionary<Bots.Bot, int>();

        public bool IsBoardMedic(Bots.Bot b)
        {
            return b != null && b._type != null && b._type.Id == VehHoverboard && IsVehicleSquadTeam(b._team);
        }

        /// <summary>Medic guns (sidearm, main, misc) + medic armour and energy for the board medic.</summary>
        void ApplyBoardMedicLoadout(Bots.Bot bot)
        {
            int side, main, misc;
            PickClassWeapons(ClsMedic, bot._team, out side, out main, out misc);
            EquipCloseFar(bot, side, main);
            if (misc != 0)
            {
                ItemInfo mi = Itm(misc);
                WeaponController wm = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                if (mi != null && wm.equip(mi)) { wm.Owner = bot; EolBotBrain.SetMiscWeapon(bot, wm); }
            }
            bot.ArmorSource = _arena._server._assets.getVehicleByID(ClsMedic);
            ApplyPlayerArmor(bot, ClsMedic, useClassTable: true);
            _boardMedicNextSummon[bot] = Environment.TickCount + 20000;
        }

        /// <summary>The summon kit: far-off, idle Ghost Reaper bots warp to the board medic (once a minute, out of combat).</summary>
        void PollBoardMedicSummons(int now)
        {
            if (_arena == null || botTeam8 == null || now - _tickBoardMedic < 1000)
                return;
            _tickBoardMedic = now;
            foreach (Bots.Bot m in _arena.ActiveBots)
            {
                if (!IsBoardMedic(m) || m.IsDead || m.bCondemned || IsDeploying(m))
                    continue;
                RoamingAttacker rm = m as RoamingAttacker;
                if (rm != null && rm.InLiveCombat)
                    continue;
                if (!InPlayArea(m._state.positionX, m._state.positionY, 0))
                    continue;
                int due;
                if (_boardMedicNextSummon.TryGetValue(m, out due) && now - due < 0)
                    continue;
                int pulled = 0;
                foreach (Bots.Bot b in _arena.ActiveBots)
                {
                    if (b == null || b == m || b.IsDead || b.bCondemned || b._team != m._team || IsDeploying(b))
                        continue;
                    RoamingAttacker rb = b as RoamingAttacker;
                    if (rb != null && rb.InLiveCombat)
                        continue;
                    if (Helpers.distanceTo(b._state, m._state) <= BoardMedicSummonBeyond)
                        continue;
                    // (2026-11-26) no summon OUT of an enemy teleport disruptor (owner: "its only for summons the TD" - teleporting
                    // to someone inside one is fine)
                    if (_arena.isTeleportDisruptedAt(b._state.positionX, b._state.positionY, b._team))
                        continue;
                    Helpers.ObjectState at = OpenSpotNear(m._state.positionX, m._state.positionY, 160);
                    if (at == null)
                        continue;
                    WarpBot(b, at);
                    b.DamageImmuneUntilTick = NonZeroTick(now + 1500);
                    pulled++;
                }
                _boardMedicNextSummon[m] = now + (pulled > 0 ? BoardMedicSummonMs : 10000);
                if (pulled > 0)
                    SquadSay(m, Pick3("regroup on me", "summoned you in", "on me, reapers"));
            }
            if (_boardMedicNextSummon.Count > 16)
            {
                List<Bots.Bot> gone = new List<Bots.Bot>();
                foreach (Bots.Bot b in _boardMedicNextSummon.Keys) if (b == null || b.bCondemned) gone.Add(b);
                foreach (Bots.Bot b in gone) _boardMedicNextSummon.Remove(b);
            }
        }
    }
}
