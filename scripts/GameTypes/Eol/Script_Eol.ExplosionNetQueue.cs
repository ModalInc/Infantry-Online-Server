using System;
using System.Collections.Generic;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    public partial class Script_Eol
    {
        struct ExplosionNetEvent
        {
            public short itemID;
            public short posX;
            public short posY;
            public short posZ;
            public byte yaw;
            public ushort creator;
        }

        // Paces arena-wide explosion reliables (alien abort, SCU waves) so clients are not flooded.
        readonly Queue<ExplosionNetEvent> _explosionNetQueue = new Queue<ExplosionNetEvent>(64);
        int _tickLastExplosionNetDrain;

        const int ExplosionNetMinIntervalMs = 20;
        const int ExplosionNetOpsPerDrain = 3;

        /// <summary>
        /// Queue an arena-wide explosion instead of blasting <c>sendReliable</c> to every player immediately.
        /// </summary>
        public void EnqueueExplosionRoute(short itemID, short posX, short posY, short posZ, byte yaw, ushort creator)
        {
            _explosionNetQueue.Enqueue(new ExplosionNetEvent
            {
                itemID = itemID,
                posX = posX,
                posY = posY,
                posZ = posZ,
                yaw = yaw,
                creator = creator,
            });
        }

        void DrainExplosionNetQueue(int now)
        {
            if (_arena == null)
                return;
            if (_explosionNetQueue.Count == 0)
                return;

            if (_tickLastExplosionNetDrain != 0 && now - _tickLastExplosionNetDrain < ExplosionNetMinIntervalMs)
                return;

            _tickLastExplosionNetDrain = now;
            int budget = ExplosionNetOpsPerDrain;

            while (budget-- > 0 && _explosionNetQueue.Count > 0)
            {
                ExplosionNetEvent ev = _explosionNetQueue.Dequeue();
                Helpers.Player_RouteExplosion(_arena.Players, ev.itemID, ev.posX, ev.posY, ev.posZ, ev.yaw, ev.creator);
            }
        }

        void ClearExplosionNetQueue()
        {
            _explosionNetQueue.Clear();
            _tickLastExplosionNetDrain = 0;
        }
    }
}
