using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using InfServer.Network;
using InfServer.Protocol;


namespace InfServer.Game
{
    // ZoneServer Class
    /// Represents the entire server state
    ///////////////////////////////////////////////////////
    public partial class ZoneServer : Server
    {	// Member variables
        ///////////////////////////////////////////////////
        private Dictionary<ushort, Player> _players;		//The players in our zone, indexed by id
        public ushort _lastPlayerKey;						//The last player key to be allocated

        //Settings
        static public int maxPlayers;

        ///////////////////////////////////////////////////
        // Member Functions
        ///////////////////////////////////////////////////
        /// <summary>
        /// Performs player-related initialization
        /// </summary>
        public bool initPlayers()
        {	//Initialize
            _players = new Dictionary<ushort, Player>();
            _lastPlayerKey = 1;			//We don't want to be giving players an id of 0

            //Load some settings
            maxPlayers = _config["server/maxPlayers"].intValue;
            return true;
        }

        /// <summary>
        /// Allocates a new player slot for use for entering players
        /// </summary>
        public Player newPlayer(Client<Player> c, string alias)
        {
            using (LogAssume.Assume(_logger))
            {
                //Is there any space?
                if (_players.Count == maxPlayers)
                {
                    Log.write(TLog.Warning, "Server full.");
                    return null;
                }

                //We want to continue wrapping around the playerid limits
                //looking for empty spots.
                ushort pk;

                for (pk = _lastPlayerKey; pk <= maxPlayers; ++pk)
                {	//If we've reached the maximum, wrap around
                    if (pk == maxPlayers)
                    {
                        Log.write(TLog.Warning, "Player Key roll-over in newPlayer.");
                        pk = 1;
                        continue;
                    }

                    //Does such a player exist?
                    if (_players.ContainsKey(pk))
                    {
                        var existingPlayer = _players[pk];
                        Log.write(TLog.Warning, "Attempted to use an existing player key in newPlayer: {0}: {1}", pk, existingPlayer);
                        continue;
                    }

                    //We have a space!
                    break;
                }

                _lastPlayerKey = pk;

                //Create our new player object
                Player newPlayer = new Player();

                c._obj = newPlayer;
                newPlayer._client = c;
                newPlayer._client._playerConn = true;

                newPlayer._id = pk;
                newPlayer._magic = _rand.Next();

                newPlayer._alias = alias;
                newPlayer._server = this;

                Log.write(TLog.Normal, "New player: {0} ({1})", newPlayer, newPlayer._client._ipe);

                _players[pk] = newPlayer;

                return newPlayer;
            }
        }

        /// <summary>
        /// Handles the loss of a player
        /// </summary>
        public void lostPlayer(Player player)
        {
            if (player == null)
            {
                Log.write(TLog.Error, "lostPlayer(): Called with null player.");
                return;
            }

            using (LogAssume.Assume(_logger))
            {	//Is it present in our list?
                if (!_players.ContainsKey(player._id))
                {	//Notify and discontinue
                    Log.write(TLog.Error, "Lost player '{0}' who wasn't present in the ZoneServer list.", player);
                    return;
                }

                //He's gone!
                _players.Remove(player._id);
                Log.write(TLog.Normal, "Lost player: {0} ({1})", player, player._client._ipe);

                //Make sure his stats get updated
                if (!IsStandalone && player._bDBLoaded)
                {   // (2026-11-28) a failed stats save must never stop the "player left" note to the database (that
                    // left him "currently in use" there - nobody could log in on that alias until a restart)
                    try { _db.updatePlayer(player); }
                    catch (Exception ex) { Log.write(TLog.Exception, "lostPlayer(): saving stats failed for {0}: {1}", player, ex); }

                    //We've lost him!
                    _db.lostPlayer(player);
                }

                //Set a destroy timer. This prevents lingering clients from not fully disconnecting
                if (player._client != null)
                    player._client._tickDestroy = Environment.TickCount64;
            }
        }

        /// <summary>
        /// Obtains the player associated with a given instance 
        /// </summary>
        public Player getPlayer(Data.PlayerInstance inst)
        {	//Dereference the id
            Player player;

            if (!_players.TryGetValue(inst.id, out player))
                //Doesn't exist!
                return null;

            return (player._magic == inst.magic) ? player : null;
        }

        long _tickGhostSweep;
        /// <summary>Ghost players found so far (shown in the log).</summary>
        public int GhostPlayersRemoved;

        /// <summary>
        /// (2026-11-28, owner: "make sure if people leave there arent ghosts like blocking people from joining") every
        /// 5 s: a player still in the zone's list whose connection is closed, or who has sent nothing for over a minute,
        /// is removed from the zone and the database - he can log straight back in.
        /// </summary>
        public void sweepGhostPlayers()
        {
            long now = Environment.TickCount64;
            if (now - _tickGhostSweep < 5000)
                return;
            _tickGhostSweep = now;
            long silentMs = Math.Max(60000, 3L * Math.Max(0, Client.connectionTimeout));
            foreach (Player p in _players.Values.ToList())
            {
                if (p == null)
                    continue;
                bool gone = p._client == null || p._client._bDestroyed
                    || (p._client._lastPacketRecv > 0 && now - p._client._lastPacketRecv > silentMs);
                if (!gone)
                    continue;
                GhostPlayersRemoved++;
                Log.write(TLog.Warning, "Ghost player removed: {0} (connection {1}).", p,
                    p._client == null ? "missing" : p._client._bDestroyed ? "closed" : "silent " + (now - p._client._lastPacketRecv) / 1000 + " s");
                try { p.forceRemoveGhost(); }
                catch (Exception ex)
                {
                    Log.write(TLog.Exception, "sweepGhostPlayers(): {0}: {1}", p, ex);
                    _players.Remove(p._id);   // last resort: never leave him in the list
                }
            }
        }

        /// <summary>
        /// Obtains the player associated with a given name
        /// </summary>
        public Player getPlayer(string name)
        {	//Attempt to find him
            if (String.IsNullOrWhiteSpace(name))
                return null;

            foreach (Player p in _players.Values.ToList())
            {
                if (p == null)
                {   //Make a note
                    Log.write(TLog.Error, "ZonePlayers(): found null player in _players");
                    continue;
                }

                if (String.Equals(name, p._alias, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }
    }
}
