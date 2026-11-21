using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using InfServer.Bots;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "if the alien queen can move around ... it should stay close to the spawn point and move around
    /// there and shoot the pink weapon and also the other one with spit and the darts on the side" / "she can also regain
    /// HP slowly") the Alien Queen as a moving bot (veh 836, the Predator's body with the Queen's 3000 HP and armour):
    ///   - wanders round the spot she came up at (within QueenWanderRaw), turning to fight anything in range;
    ///   - never strays past QueenLeashRaw: she walks back (still shooting) until she's inside QueenBackRaw;
    ///   - guns: BioBurst (the pink ring of ElectroBolts) as her main, AS&D (Hot Spit + a Wasp Drone out each side) as
    ///     the misc gun between BioBurst shots, Stomp up close;
    ///   - heals QueenRegenHp every QueenRegenMs, up to full.
    /// Her death ends the swarm exactly as the old standing Queen did (Script_Eol.AlienSwarm.cs).
    /// </summary>
    public partial class AlienHorde : Bot
    {
        const int QueenLeashRaw = 360;
        const int QueenBackRaw = 160;
        const int QueenWanderRaw = 220;
        const int QueenRegenMs = 2000;
        const int QueenRegenHp = 10;

        bool _queenReturning;
        Helpers.ObjectState _queenWander;
        int _queenWanderNext, _queenRegenNext;

        /// <summary>The Queen's whole poll (instead of the bug brain).</summary>
        bool PollQueen(int now)
        {
            if (IsDead || bCondemned)
                return base.poll();

            // slow regeneration
            if (now - _queenRegenNext >= 0)
            {
                _queenRegenNext = now + QueenRegenMs;
                int max = _type != null ? _type.Hitpoints : 3000;
                if (_state.health > 0 && _state.health < max)
                    _state.health = (short)Math.Min(max, _state.health + QueenRegenHp);
            }

            Helpers.ObjectState home = _baseScript.SwarmHomeOf(this) ?? new Helpers.ObjectState { positionX = _state.positionX, positionY = _state.positionY };
            double dHome = Helpers.distanceTo(_state, home);
            if (dHome > QueenLeashRaw) _queenReturning = true;
            else if (dHome < QueenBackRaw) _queenReturning = false;

            if (_queenReturning)
            {   // too far from her spot: back, shooting on the way
                if (_target != null && !_target.IsDead)
                    EolBotPhysicsUtil.TryFireOnlyWhileUnstick(this, _target);
                _queenWander = null;
                EolBotBrain.GoTo(this, home, PathPriority.Combat);
                return base.poll();
            }

            if (fireAtEnemy(now))
                return base.poll();

            // nothing to fight: stroll round her spot
            if (_queenWander == null || now - _queenWanderNext >= 0 || Helpers.distanceTo(_state, _queenWander) < 40)
            {
                _queenWander = PickQueenWander(home);
                _queenWanderNext = now + 4000 + _queenRng.Next(3000);
            }
            if (_queenWander != null)
                EolBotBrain.GoTo(this, _queenWander, PathPriority.Idle);
            return base.poll();
        }

        static readonly Random _queenRng = new Random();

        Helpers.ObjectState PickQueenWander(Helpers.ObjectState home)
        {
            for (int i = 0; i < 16; i++)
            {
                double a = _queenRng.NextDouble() * Math.PI * 2;
                int r = 60 + _queenRng.Next(QueenWanderRaw - 60);
                short x = (short)(home.positionX + Math.Cos(a) * r), y = (short)(home.positionY + Math.Sin(a) * r);
                if (x < 32 || y < 32 || !EolHqPatrol.IsOpenForBot(this, x, y))
                    continue;
                return new Helpers.ObjectState { positionX = x, positionY = y };
            }
            return null;
        }
    }
}
