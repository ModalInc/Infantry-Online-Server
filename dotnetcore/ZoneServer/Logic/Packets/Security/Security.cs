using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Protocol;
using InfServer.Game;

namespace InfServer.Logic
{	// Logic_Security Class
	/// Deals with elements of the server's security
    /// 
	///////////////////////////////////////////////////////
    
	class Logic_Security
	{
        public static uint reliable;
        public static uint reliableChecksum;

        /// <summary>
		/// Triggered when the client is attempting to enter the game and sends his security reply
		/// </summary>
		static public void Handle_CS_Environment(CS_Environment pkt, Player player)
		{	//Does he have a target ready to receive the information?
			Player target = player.getVar("envReq") as Player;
            List<string> badPrograms = new List<string>();
            badPrograms.Add("cheat engine 6.2");
            badPrograms.Add("ollydbg");
            badPrograms.Add("cheat engine");
            badPrograms.Add("cheatengine");
            badPrograms.Add("ollydbg.exe");
            badPrograms.Add("wireshark");
            badPrograms.Add("wireshark.exe");
            badPrograms.Add("speederxp");
            badPrograms.Add("L. Spiro");
            badPrograms.Add("spiro");
            badPrograms.Add("mhs");
            badPrograms.Add("hex editor");
            if (target == null)
            {//It was a request by the server
                //Check the processes
                foreach (string element in pkt.processes)
                    if (badPrograms.Contains(Logic_Text.RemoveIllegalCharacters(element).ToLower()) && player._permissionStatic != Data.PlayerPermission.Level5)
                    {//They have a cheat running or debugger, kick them out and inform mods
                        if (!player._server.IsStandalone)
                        {
                            CS_ChatQuery<Data.Database> pktquery = new CS_ChatQuery<Data.Database>();
                            pktquery.queryType = CS_ChatQuery<Data.Database>.QueryType.alert;
                            pktquery.sender = player._alias;
                            pktquery.payload = String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}) Reason=Using a hack or cheat engine. Program={3}", 
                                player._server.Name, player._arena._name, player._alias, Logic_Text.RemoveIllegalCharacters(element).ToLower());
                            //Send it!
                            player._server._db.send(pktquery);
                        }
                        else
                        {
                            foreach (Player ppl in player._arena.Players.ToList())
                                if (ppl.PermissionLevelLocal >= Data.PlayerPermission.Level1)
                                    ppl.sendMessage(-1, String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}) Reason=Using a hack or cheat engine. Program={3}", 
                                        player._server.Name, player._arena._name, player._alias, Logic_Text.RemoveIllegalCharacters(element).ToLower()));
                        }
                        Log.write(TLog.Security, String.Format("Player Kicked: (Zone={0}, Arena={1}, Player={2}) Reason=Using a hack or cheat engine. Program={3}", 
                                player._server.Name, player._arena._name, player._alias, Logic_Text.RemoveIllegalCharacters(element).ToLower()));
                        player.disconnect();
                        return;
                    }

                //Check the windows
                 foreach (string element in pkt.windows)
                    if (badPrograms.Contains(Logic_Text.RemoveIllegalCharacters(element).ToLower()) && player._permissionStatic != Data.PlayerPermission.Level5)
                    {//They have a cheat running or debugger, kick them out and inform mods

                        if (!player._server.IsStandalone)
                        {
                            CS_ChatQuery<Data.Database> pktquery = new CS_ChatQuery<Data.Database>();
                            pktquery.queryType = CS_ChatQuery<Data.Database>.QueryType.alert;
                            pktquery.sender = player._alias;
                            pktquery.payload = String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}) Reason=Using a hack or cheat engine. Program={3}", player._server.Name, player._arena._name, player._alias, Logic_Text.RemoveIllegalCharacters(element).ToLower());
                            //Send it!
                            player._server._db.send(pktquery);
                        }
                        else
                        {
                            foreach (Player ppl in player._arena.Players.ToList())
                                if (ppl.PermissionLevelLocal >= Data.PlayerPermission.Level1)
                                    ppl.sendMessage(-1, String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}) Reason=Using a hack or cheat engine.", player._server.Name, player._arena._name, player._alias));
                        }

                        player.disconnect();  
                    }

                return;
            }
            player.setVar("envReq", null);

			//Display to him the results
			target.sendMessage(0, "&Processes:");

            foreach (string element in pkt.processes)            
               target.sendMessage(0, "*" + Logic_Text.RemoveIllegalCharacters(element));
            

			target.sendMessage(0, "&Windows:");
            foreach (string element in pkt.windows)            
                target.sendMessage(0, "*" + Logic_Text.RemoveIllegalCharacters(element));
            
            // TODO: Write these out to a file so that later we can compare them.
            if (!String.IsNullOrWhiteSpace(player.getVar("envReqParams") as String))
            {
                var filename = ""; // read envReqParams
                // Write out to a file.
            }
		}

        // (2026-11-26) owner-checked checksum (see ZoneServer.ChecksumOwnerAliases)
        const int OwnerCsWindowMs = 600000;    // a report counts toward the majority for 10 minutes
        const int OwnerCsMinAgree = 3;         // the players' majority needs at least 3 players on one value
        static readonly object _ownerCsLock = new object();
        static readonly Dictionary<string, KeyValuePair<uint, int>> _ownerCsReports =
            new Dictionary<string, KeyValuePair<uint, int>>(StringComparer.OrdinalIgnoreCase);   // alias -> (checksum, tick)
        static uint _ownerCsValue = 0;         // the owner's last checksum (0 = owner not seen since start)
        static string _ownerCsBy = "";

        static void OwnerChecksumCheck(uint checksum, Player player)
        {
            ZoneServer server = player._server;
            int now = System.Environment.TickCount;
            bool isOwner = server.ChecksumOwnerAliases.Contains(player._alias);
            uint reference;
            string refBy;
            int majCount, total;
            uint majority;

            lock (_ownerCsLock)
            {
                if (isOwner)
                {
                    if (_ownerCsValue != checksum)
                        Log.write(TLog.Security, String.Format("[Checksum] owner {0} reports {1} (was {2} by {3})",
                            player._alias, checksum, _ownerCsValue, _ownerCsBy));
                    _ownerCsValue = checksum;
                    _ownerCsBy = player._alias;
                }
                else
                    _ownerCsReports[player._alias] = new KeyValuePair<uint, int>(checksum, now);

                // the players' majority over the last 10 minutes (owners left out)
                foreach (string old in _ownerCsReports.Where(r => now - r.Value.Value > OwnerCsWindowMs).Select(r => r.Key).ToList())
                    _ownerCsReports.Remove(old);
                total = _ownerCsReports.Count;
                var top = _ownerCsReports.GroupBy(r => r.Value.Key).OrderByDescending(g => g.Count()).FirstOrDefault();
                majority = top == null ? 0 : top.Key;
                majCount = top == null ? 0 : top.Count();
                bool clearMajority = majCount >= OwnerCsMinAgree && majCount * 2 > total;

                if (_ownerCsValue != 0 && !(clearMajority && majority != _ownerCsValue))
                { reference = _ownerCsValue; refBy = _ownerCsBy; }
                else if (clearMajority)
                { reference = majority; refBy = String.Format("majority {0}/{1}", majCount, total); }
                else
                { reference = 0; refBy = ""; }
            }

            if (isOwner)
            {   // the owner is never kicked; tell him when the players overrule his files
                if (reference != 0 && reference != checksum)
                {
                    Log.write(TLog.Security, String.Format("[Checksum] owner {0} has {1} but the players' majority has {2} ({3}/{4}) - majority used",
                        player._alias, checksum, majority, majCount, total));
                    object last = player.getVar("csAlertTick");
                    if (last == null || now - (int)last > OwnerCsWindowMs)
                    {
                        player.setVar("csAlertTick", now);
                        player.sendMessage(-1, String.Format("&Checksum: your game files differ from most players (yours {0}, {1} of {2} players have {3}) - their value is used, nobody is kicked for differing from you.",
                            checksum, majCount, total, majority));
                    }
                }
                return;
            }

            if (reference == 0 || checksum == reference || player._permissionStatic >= Data.PlayerPermission.Level3)
            {
                player.setVar("csStrikes", null);
                return;
            }

            // mismatch: first one is a warning, the second one in a row a kick
            int strikes = (player.getVar("csStrikes") as int? ?? 0) + 1;
            player.setVar("csStrikes", strikes);
            Log.write(TLog.Security, String.Format("[Checksum] {0} (arena {1}) has {2}, reference {3} ({4}) - strike {5}/2",
                player._alias, player._arena._name, checksum, reference, refBy, strikes));
            if (strikes < 2)
            {
                foreach (Player ppl in player._arena.Players.ToList())
                    if (ppl.PermissionLevelLocal >= Data.PlayerPermission.Level1)
                        ppl.sendMessage(-1, String.Format("&Checksum mismatch (strike 1/2): {0} has {1}, reference {2} ({3})",
                            player._alias, checksum, reference, refBy));
                return;
            }

            string alert = String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}, Checksum={3}, Reliable={4} [{5}]) Reason=Client checksum mismatch (2 in a row).",
                server.Name, player._arena._name, player._alias, checksum, reference, refBy);
            player.sendMessage(-1, "&Your game files differ from this zone's. Please restart Infantry so it downloads the current files, then rejoin.");
            if (!server.IsStandalone)
            {
                CS_ChatQuery<Data.Database> pktquery = new CS_ChatQuery<Data.Database>();
                pktquery.queryType = CS_ChatQuery<Data.Database>.QueryType.alert;
                pktquery.sender = player._alias;
                pktquery.payload = alert;
                server._db.send(pktquery);
            }
            else
            {
                foreach (Player ppl in player._arena.Players.ToList())
                    if (ppl.PermissionLevelLocal >= Data.PlayerPermission.Level1)
                        ppl.sendMessage(-1, alert);
            }
            player.setVar("csStrikes", null);
            player.disconnect();
        }

        /// <summary>
		/// Triggered when the client has responsed to a security request
		/// </summary>
        static public void Handle_CS_Security(CS_SecurityCheck pkt, Player player)
        {
            //Don't do in private arenas until i figure out if it can fuck things up
            if (player != null && player._arena.IsPrivate)
                return;

            Player reliablePlayer = player.getVar("reliable") as Player;
            Player target = player.getVar("secReq") as Player;
        //    if (player._alias == "kon")
          //      return;
            if (target == null)
            {//Server is checking assets
                // (2026-11-26) zone with an owner-checked checksum (Eol): owner reference + majority + 2 strikes
                if (player._server.ChecksumOwnerAliases != null)
                {
                    if (reliablePlayer != null)
                        player.setVar("reliable", null);   // a Level 5 no longer sets the reference here
                    OwnerChecksumCheck(pkt.Unk3, player);
                    return;
                }
                if (reliablePlayer != null && reliablePlayer == player)
                {//Lets use the mods asset checksum
                    Log.write(TLog.Security, "Reliable client checksum " + pkt.Unk3 + " set by " + player._alias
                        + " (was " + player._server._reliableChecksum + " set by " + player._server._reliableChecksumBy + ")");
                    player._server._reliableChecksum = pkt.Unk3;
                    player._server._reliableChecksumBy = player._alias;
                    player.setVar("reliable", null);
                    return;
                }
                //Not a mod
                if (pkt.Unk3 != player._server._reliableChecksum && player._server._reliableChecksum != 0 && player._permissionStatic < Data.PlayerPermission.Level3)
                {//Mismatch
                    Log.write(TLog.Security, "Checksum mismatch: " + pkt.Unk3 + " vs " + player._server._reliableChecksum);

                    // (2026-11-25) zone with the kick switched off (Eol): log who and against whose value, tell staff, keep him
                    if (player._server.ChecksumAlertOnly)
                    {
                        Log.write(TLog.Security, String.Format("[Checksum] {0} (static level {1}, arena {2}) has {3}, reliable {4} set by {5} - not kicked (alert only)",
                            player._alias, (int)player._permissionStatic, player._arena._name, pkt.Unk3, player._server._reliableChecksum, player._server._reliableChecksumBy));
                        // staff in the arena hear about each player at most once every 10 minutes
                        int now = System.Environment.TickCount;
                        object last = player.getVar("csAlertTick");
                        if (last == null || now - (int)last > 600000)
                        {
                            player.setVar("csAlertTick", now);
                            foreach (Player ppl in player._arena.Players.ToList())
                                if (ppl.PermissionLevelLocal >= Data.PlayerPermission.Level1)
                                    ppl.sendMessage(-1, String.Format("&Checksum mismatch (not kicked): {0} has {1}, reliable {2} set by {3}",
                                        player._alias, pkt.Unk3, player._server._reliableChecksum, player._server._reliableChecksumBy));
                        }
                        return;
                    }

                    if (!player._server.IsStandalone)
                    {
                        CS_ChatQuery<Data.Database> pktquery = new CS_ChatQuery<Data.Database>();
                        pktquery.queryType = CS_ChatQuery<Data.Database>.QueryType.alert;
                        pktquery.sender = player._alias;
                        pktquery.payload = String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}, Checksum={3}, Reliable={4}) Reason=Client checksum mismatch.", player._server.Name, player._arena._name, player._alias, pkt.Unk3, player._server._reliableChecksum);
                        //Send it!
                        player._server._db.send(pktquery);
                        player.disconnect();
                    }
                    else
                    {
                        foreach (Player ppl in player._arena.Players.ToList())
                            if (ppl.PermissionLevelLocal >= Data.PlayerPermission.Level1)
                                ppl.sendMessage(-1, String.Format("&ALERT - Player Kicked: (Zone={0}, Arena={1}, Player={2}, Checksum={3}, Reliable={4}) Reason=Client checksum mismatch.", player._server.Name, player._arena._name, player._alias, pkt.Unk3, player._server._reliableChecksum));
                        player.disconnect();
                    }
                }
                return;
            }
            if (target == player)
            {
                reliable = pkt.Unk3;
                player._server._reliableChecksum = pkt.Unk3;
            }
            player.setVar("secReq", null);
            
            
            //Check pkt.Unk3 against reliable source here
            if (pkt.Unk3 != reliable && reliable != 0)
            {//Message any mod that used this command
                target.sendMessage(0, "@**Mismatch for player: " + player._alias.ToString());
                target.sendMessage(0, "@**Expected: " + reliable + "       Received: " + pkt.Unk3);
                //player.disconnect();
                //Write and inform mods here
            }           
            else
                target.sendMessage(0, "Player " + player._alias.ToString() + " :: " + pkt.Unk3); //In-Memory Asset checksum
        }

		/// <summary>
		/// Registers all handlers
		/// </summary>
		[Logic.RegistryFunc]
		static public void Register()
		{
			CS_Environment.Handlers += Handle_CS_Environment;
            CS_SecurityCheck.Handlers += Handle_CS_Security;
		}
	}
}
