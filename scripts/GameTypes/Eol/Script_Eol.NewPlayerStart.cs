using System;

using InfServer.Game;
using InfServer.Logic;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-26, owner: "players who are new, join the zone with 0 cash 0 xp and then they need to ?wipecharacter yes to
    /// get the 20k cash starting. can we please make it so if its the first time entering it. this should NOT wipe existing
    /// players") the engine only gives the start kit when the database has NO row for the player in this zone; these players
    /// already have one that is completely empty (0 cash, 0 xp, no kills or deaths, no items), so they were loaded as
    /// returning players with nothing. On entering the arena such a blank character gets the zone's first-time setup - the
    /// same events ?wipecharacter yes runs (setcash=20000, Conscript, ...). Anyone with ANY history is left alone.
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Nothing at all on this character: no cash, never earned xp, never killed or died, and owns no items
        /// (a player who spent his start cash has items, so he is never touched).</summary>
        static bool IsBlankCharacter(InfServer.Game.Player p)
        {
            return p != null
                && p.Cash == 0 && p.Experience == 0 && p.ExperienceTotal == 0
                && p.Kills == 0 && p.Deaths == 0 && p.vehicleKills == 0 && p.vehicleDeaths == 0
                && (p._inventory == null || p._inventory.Count == 0);
        }

        void GiveStarterIfBlank(InfServer.Game.Player player)
        {
            if (player == null || player._server == null || player.getVar("eolStarterChecked") != null)
                return;
            player.setVar("eolStarterChecked", true);   // (once per session)
            if (!IsBlankCharacter(player))
                return;
            Logic_Assets.RunEvent(player, player._server._zoneConfig.EventInfo.firstTimeSkillSetup);
            Logic_Assets.RunEvent(player, player._server._zoneConfig.EventInfo.firstTimeInvSetup);
            player.setDefaultSkill();
            player.syncInventory();
            player.syncState();
            Log.write(TLog.Normal, "[Eol] blank character {0}: first-time setup given (cash now {1})", player._alias, player.Cash);
            player.sendMessage(0, String.Format("&Welcome to Eol! You start with ${0:N0} to gear up.", player.Cash));
        }
    }
}
