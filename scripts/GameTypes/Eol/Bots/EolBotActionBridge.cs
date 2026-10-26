using InfServer.Bots;
using InfServer.Game;
using InfServer.Game.Modules.BotActions;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Thin bridge so Eol bots drive the ZoneServer bot-action module.
    /// </summary>
    public static class EolBotActionBridge
    {
        public static bool Run(Bot bot, BotActionId action, int now = 0, bool ignoreCooldown = true)
        {
            if (bot == null)
                return false;
            SyncCombatAnchor(bot);
            BehaviorResult r = bot.RunAction(action, now, ignoreCooldown);
            return r == BehaviorResult.Continue || r == BehaviorResult.Success;
        }

        /// <summary>
        /// Push HQ / spacecraft / raid PDB into the action context so engage can leash.
        /// Engage also resolves anchors via <see cref="EolObjectives"/> when unset.
        /// </summary>
        public static void SyncCombatAnchor(Bot bot)
        {
            if (bot?.Actions == null)
                return;

            Helpers.ObjectState anchor = null;

            if (bot is AlienHorde alien && alien.vHq != null && !alien.vHq.IsDead)
                anchor = alien.vHq._state;
            else if (bot is BasicDefense def)
            {
                // vHq is private — objectives board supplies HQ; optional explicit set below.
            }
            else if (bot is RoamingAttacker || bot is RoamingCaptain)
            {
                // Prefer raid PDB from objectives (RaiderPdbAnchor).
            }

            // Prefer objectives (HQ / crash / PDB) so we don't need private field accessors.
            if (anchor == null && bot._arena?.BotActionModule != null)
            {
                // Resolved inside EngageTarget via TryGetDefendAnchor / TryGetPatrolAnchor.
            }

            if (anchor != null)
                bot.Actions.SetAnchor(anchor);
        }

        /// <summary>
        /// Seed sticky player, run attack/seek, soft-sync targets so a brief module miss
        /// does not wipe a live lock (LOS flicker / cover).
        /// </summary>
        public static bool RunCombatKeepSticky(
            Bot bot,
            BotActionId action,
            ref Player playerTarget,
            out Bot botTarget,
            int now = 0)
        {
            botTarget = null;
            if (bot == null)
                return false;

            Player prior = playerTarget;
            if (bot.Actions != null && prior != null && !prior.IsDead && !prior.IsSpectator)
                bot.Actions.SuggestPlayerTarget(prior);

            bool ran = Run(bot, action, now);

            SyncCombatTargetsFromActions(bot, ref playerTarget, out botTarget);

            // Soft keep: module blanked the lock but script still had a live hostile.
            if (playerTarget == null && prior != null && !prior.IsDead && !prior.IsSpectator
                && prior._team != bot._team
                && Helpers.isInRange(Script_Eol.EolBotCombatSeekRangeRaw, bot._state, prior._state))
            {
                playerTarget = prior;
                if (bot.Actions != null)
                    bot.Actions.SuggestPlayerTarget(prior);
            }

            FilterDistantBotPatrolInterrupt(bot, ref botTarget);

            // Prefer the player lock over a distant bot interrupt.
            if (playerTarget != null && botTarget != null
                && !IsEnemyBotCloseEnoughToLeavePatrol(bot, botTarget))
                botTarget = null;

            return ran || playerTarget != null || botTarget != null;
        }

        public static void SyncPlayerTargetFromActions(Bot bot, ref Player target)
        {
            if (bot?.Actions == null)
                return;
            Player p = bot.Actions.LastPlayerTarget;
            if (p != null && !p.IsDead)
                target = p;
            // Do not clear an existing sticky when the module has no player this tick —
            // AttackPlayer may fail briefly while LOS is blocked.
        }

        /// <summary>
        /// Sync player and/or enemy-bot targets from the last action-module tick.
        /// </summary>
        public static bool SyncCombatTargetsFromActions(Bot bot, ref Player playerTarget, out Bot botTarget)
        {
            botTarget = null;
            SyncPlayerTargetFromActions(bot, ref playerTarget);
            if (bot?.Actions == null)
                return playerTarget != null;

            Bot bt = bot.Actions.LastBotTarget;
            if (bt != null && !bt.IsDead && !bt.bCondemned)
                botTarget = bt;
            return playerTarget != null || botTarget != null;
        }

        /// <summary>
        /// Cross-faction bots can be visible across the whole seek ring (6000). Only close
        /// enemy bots with clear weapon LOS should cancel HQ / ship patrol — otherwise
        /// everyone stacks on one target through walls.
        /// </summary>
        public static bool IsEnemyBotCloseEnoughToLeavePatrol(Bot self, Bot enemy)
        {
            if (self?._state == null || enemy?._state == null || enemy.IsDead)
                return false;
            if (!Helpers.isInRange(Script_Eol.EolBotCloseAlertRangeRaw, self._state, enemy._state))
                return false;
            return InfServer.Bots.BotCombat.CanDirectEngage(self, enemy._state);
        }

        /// <summary>Drop distant or wall-blocked bot locks so patrol can resume.</summary>
        public static void FilterDistantBotPatrolInterrupt(Bot self, ref Bot botTarget)
        {
            if (botTarget == null)
                return;
            if (!IsEnemyBotCloseEnoughToLeavePatrol(self, botTarget))
                botTarget = null;
        }
    }
}
