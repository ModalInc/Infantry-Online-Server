using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using InfServer.Game;

namespace InfServer.Game.Modules
{
    public class ClassLimit
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; } // Optional, for reference
        public int PerArenaLimit { get; set; }
        public int PerTeamLimit { get; set; }
        public bool Dynamic { get; set; } = false; // New: Whether this class uses dynamic limits
        public int Percent { get; set; } = 0; // New: Percentage of total players allowed for this class
    }

    public class ClassModule
    {
        private Dictionary<int, ClassLimit> _classLimits;
        private int _lastDynamicRecalculation = 0; // Track when we last recalculated dynamic limits
        private const int DYNAMIC_RECALCULATION_INTERVAL = 60000; // 60 seconds in milliseconds

        public ClassModule(string jsonPath)
        {
            try
            {
                // Try the original path first
                if (File.Exists(jsonPath))
                {
                    var json = File.ReadAllText(jsonPath);
                    var limits = JsonConvert.DeserializeObject<List<ClassLimit>>(json);
                    _classLimits = limits?.ToDictionary(l => l.ClassId, l => l) ?? new Dictionary<int, ClassLimit>();
                    Log.write(TLog.Normal, "ClassModule loaded {0} limits from {1}", _classLimits.Count, jsonPath);
                    return;
                }

                // Try alternative case variations for the Modules directory
                var directory = Path.GetDirectoryName(jsonPath);
                var fileName = Path.GetFileName(jsonPath);

                if (directory != null)
                {
                    // Try with "modules" (lowercase m)
                    var alternativePath = Path.Combine(directory.Replace("Modules", "modules"), fileName);
                    if (File.Exists(alternativePath))
                    {
                        var json = File.ReadAllText(alternativePath);
                        var limits = JsonConvert.DeserializeObject<List<ClassLimit>>(json);
                        _classLimits = limits?.ToDictionary(l => l.ClassId, l => l) ?? new Dictionary<int, ClassLimit>();
                        Log.write(TLog.Normal, "ClassModule loaded {0} limits from {1}", _classLimits.Count, alternativePath);
                        return;
                    }

                    // Try with "Modules" (capital M)
                    alternativePath = Path.Combine(directory.Replace("modules", "Modules"), fileName);
                    if (File.Exists(alternativePath))
                    {
                        var json = File.ReadAllText(alternativePath);
                        var limits = JsonConvert.DeserializeObject<List<ClassLimit>>(json);
                        _classLimits = limits?.ToDictionary(l => l.ClassId, l => l) ?? new Dictionary<int, ClassLimit>();
                        Log.write(TLog.Normal, "ClassModule loaded {0} limits from {1}", _classLimits.Count, alternativePath);
                        return;
                    }
                }

                // If no file found, create an empty dictionary
                _classLimits = new Dictionary<int, ClassLimit>();
                Log.write(TLog.Warning, "Class limits file not found at {0}. Using default (no limits).", jsonPath);
            }
            catch (Exception ex)
            {
                // If there's any error reading or parsing the file, create an empty dictionary
                _classLimits = new Dictionary<int, ClassLimit>();
                Log.write(TLog.Warning, "Error reading class limits file {0}: {1}. Using default (no limits).", jsonPath, ex.Message);
            }
        }

        /// <summary>
        /// Checks if it's time to recalculate dynamic limits and performs the recalculation if needed
        /// </summary>
        public void CheckDynamicRecalculation(Arena arena)
        {
            int now = Environment.TickCount;

            // Check if it's time for a recalculation (every minute)
            if (now - _lastDynamicRecalculation >= DYNAMIC_RECALCULATION_INTERVAL)
            {
                RecalculateDynamicLimits(arena);
                _lastDynamicRecalculation = now;
            }
        }

        /// <summary>
        /// Recalculates dynamic limits based on current player count
        /// </summary>
        private void RecalculateDynamicLimits(Arena arena)
        {
            int totalPlayers = arena.PlayerCount;

            // If no players, skip recalculation
            if (totalPlayers == 0)
                return;

            bool hasChanges = false;
            var dynamicClasses = _classLimits.Values.Where(l => l.Dynamic).ToList();

            foreach (var limit in dynamicClasses)
            {
                // Calculate new limits based on percentage
                int newTeamLimit = Math.Max(1, (int)Math.Round((double)totalPlayers * limit.Percent / 100.0));
                int newArenaLimit = newTeamLimit * 2; // Arena limit is double the team limit

                // Check if limits have changed
                if (limit.PerTeamLimit != newTeamLimit || limit.PerArenaLimit != newArenaLimit)
                {
                    int oldTeamLimit = limit.PerTeamLimit;
                    int oldArenaLimit = limit.PerArenaLimit;

                    limit.PerTeamLimit = newTeamLimit;
                    limit.PerArenaLimit = newArenaLimit;

                    hasChanges = true;

                    Console.WriteLine($"Dynamic recalculation for {limit.ClassName ?? $"Class {limit.ClassId}"}: " +
                                    $"Team limit {oldTeamLimit}->{newTeamLimit}, Arena limit {oldArenaLimit}->{newArenaLimit} " +
                                    $"(Total players: {totalPlayers}, Percent: {limit.Percent}%)");
                }
            }

            // Save changes if any were made
            if (hasChanges)
            {
                SaveLimits();
            }
        }

        /// <summary>
        /// Sets a class to use dynamic limits
        /// </summary>
        public void SetDynamicLimits(Player player, Arena arena, int classId, bool dynamic, int percent)
        {
            if (!HasClass(classId))
            {
                // Create new limit entry
                _classLimits[classId] = new ClassLimit
                {
                    ClassId = classId,
                    ClassName = GetSkillName(player, classId),
                    PerArenaLimit = 0,
                    PerTeamLimit = 0,
                    Dynamic = dynamic,
                    Percent = percent
                };
            }
            else
            {
                // Update existing entry
                _classLimits[classId].Dynamic = dynamic;
                _classLimits[classId].Percent = percent;
            }

            // If turning off dynamic, set manual limits to current calculated values
            if (!dynamic)
            {
                int totalPlayers = arena.PlayerCount;
                int calculatedTeamLimit = Math.Max(1, (int)Math.Round((double)totalPlayers * percent / 100.0));
                int calculatedArenaLimit = calculatedTeamLimit * 2;

                _classLimits[classId].PerTeamLimit = calculatedTeamLimit;
                _classLimits[classId].PerArenaLimit = calculatedArenaLimit;
            }

            SaveLimits();
            string skillName = GetSkillName(player, classId);
            string status = dynamic ? "enabled" : "disabled";
            player.sendMessage(0, $"$Dynamic limits for {skillName} {status} at {percent}%.");
        }

        public static int GetPlayerCurrentClassId(Player player)
        {
            if (player._occupiedVehicle != null)
                return player._occupiedVehicle._type.ClassId;
            if (player._baseVehicle != null)
                return player._baseVehicle._type.ClassId;
            return 0; // Or -1 if you want to indicate "no class"
        }

        /// <summary>
        /// Gets the player's current skill ID for class limit purposes
        /// </summary>
        public static int GetPlayerCurrentSkillId(Player player)
        {
            try
            {
                // Get the first skill the player has (assuming they have at least one)
                if (player?._skills != null && player._skills.Count > 0)
                {
                    // Return the first skill ID (assuming the primary skill is the first one)
                    return player._skills.Keys.First();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting player skill ID: {ex.Message}");
            }
            return 0; // Default fallback
        }

        public bool CanChangeClass(Arena arena, Player player, int classId)
        {
            // Check for dynamic recalculation first
            CheckDynamicRecalculation(arena);

            if (!HasClass(classId))
                return true; // No limit set for this class

            var limit = _classLimits[classId];

            // Arena cap overrides team cap
            if (limit.PerArenaLimit >= 0)
            {
                int currentArenaCount = arena.PlayersIngame.Count(p => GetPlayerCurrentSkillId(p) == classId);
                if (currentArenaCount >= limit.PerArenaLimit)
                    return false;
            }

            // If arena cap allows, check team cap
            if (limit.PerTeamLimit >= 0 && player._team != null)
            {
                int currentTeamCount = player._team.ActivePlayers.Count(p => GetPlayerCurrentSkillId(p) == classId);
                if (currentTeamCount >= limit.PerTeamLimit)
                    return false;
            }

            return true;
        }

        public bool CanUnspecToClass(Arena arena, int classId, Team team)
        {
            // Check for dynamic recalculation first
            CheckDynamicRecalculation(arena);

            if (!HasClass(classId))
                return true; // No limit set for this class

            var limit = _classLimits[classId];

            // Arena cap overrides team cap
            if (limit.PerArenaLimit >= 0)
            {
                int currentArenaCount = arena.PlayersIngame.Count(p => GetPlayerCurrentSkillId(p) == classId);
                if (currentArenaCount >= limit.PerArenaLimit)
                    return false;
            }

            // If arena cap allows, check team cap
            if (limit.PerTeamLimit >= 0 && team != null)
            {
                int currentTeamCount = team.ActivePlayers.Count(p => GetPlayerCurrentSkillId(p) == classId);
                if (currentTeamCount >= limit.PerTeamLimit)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if we can add a bot of the specified class to the arena/team, counting both players and bots
        /// </summary>
        public bool CanAddBotOfClass(Arena arena, int classId, Team team)
        {
            // Check for dynamic recalculation first
            CheckDynamicRecalculation(arena);

            if (!HasClass(classId))
                return true; // No limit set for this class

            var limit = _classLimits[classId];

            // Arena cap check - count both players and bots
            if (limit.PerArenaLimit >= 0)
            {
                int playerArenaCount = arena.PlayersIngame.Count(p => GetPlayerCurrentSkillId(p) == classId);
                int botArenaCount = arena.ActiveBots.Count(b => 
                    b != null && !b.bCondemned && b._type.ClassId == classId);
                
                if (playerArenaCount + botArenaCount >= limit.PerArenaLimit)
                    return false;
            }

            // Team cap check - count both players and bots on the team
            if (limit.PerTeamLimit >= 0 && team != null)
            {
                int playerTeamCount = team.ActivePlayers.Count(p => GetPlayerCurrentSkillId(p) == classId);
                int botTeamCount = arena.ActiveBots.Count(b => 
                    b != null && !b.bCondemned && b._team == team && b._type.ClassId == classId);
                
                if (playerTeamCount + botTeamCount >= limit.PerTeamLimit)
                    return false;
            }

            return true;
        }
        
        /// <summary>
        /// Checks if two players of the same class can be swapped between teams
        /// </summary>
        public bool CanSwapPlayersOfClass(Arena arena, int classId, Team team1, Team team2, Player player1, Player player2)
        {
            // Check for dynamic recalculation first
            CheckDynamicRecalculation(arena);

            if (!HasClass(classId))
                return true; // No limit set for this class

            var limit = _classLimits[classId];

            // Arena cap check - swap doesn't change arena count
            if (limit.PerArenaLimit >= 0)
            {
                int currentArenaCount = arena.PlayersIngame.Count(p => GetPlayerCurrentSkillId(p) == classId);
                if (currentArenaCount > limit.PerArenaLimit)
                    return false; // Already over limit (shouldn't happen)
            }

            // Team cap check - need to account for the swap
            if (limit.PerTeamLimit >= 0)
            {
                if (team1 != null)
                {
                    // Count players on team1 excluding player1 (who is leaving)
                    int team1Count = team1.ActivePlayers.Count(p => 
                        p != player1 && GetPlayerCurrentSkillId(p) == classId);
                    // After swap, team1 gets player2, so count becomes team1Count + 1
                    if (team1Count + 1 > limit.PerTeamLimit)
                        return false;
                }
                
                if (team2 != null)
                {
                    // Count players on team2 excluding player2 (who is leaving)
                    int team2Count = team2.ActivePlayers.Count(p => 
                        p != player2 && GetPlayerCurrentSkillId(p) == classId);
                    // After swap, team2 gets player1, so count becomes team2Count + 1
                    if (team2Count + 1 > limit.PerTeamLimit)
                        return false;
                }
            }

            return true;
        }

        public bool HasClass(int classId)
        {
            return _classLimits.ContainsKey(classId);
        }

        public string GetSkillName(Player player, int classId)
        {
            try
            {
                // Try to get the skill name from the player's skill list
                if (player?._skills != null && player._skills.ContainsKey(classId))
                {
                    return player._skills[classId].skill.Name;
                }

                // Fallback: try to get from the server's assets
                if (player?._server?._assets != null)
                {
                    var skillInfo = player._server._assets.getSkillByID(classId);
                    if (skillInfo != null)
                    {
                        return skillInfo.Name;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting skill name: {ex.Message}");
            }

            return $"Class {classId}";
        }

        public void SetArenaCapacity(Player player, Arena arena, int classId, int newCapacity)
        {
            if (newCapacity < 0)
            {
                player.sendMessage(0, "!Arena capacity cannot be negative.");
                return;
            }

            if (!HasClass(classId))
            {
                // Create new limit entry with auto-calculated team cap
                int autoTeamCap = newCapacity > 0 ? newCapacity / 2 : 0; // Arena cap / 2, rounded down
                _classLimits[classId] = new ClassLimit
                {
                    ClassId = classId,
                    ClassName = GetSkillName(player, classId),
                    PerArenaLimit = newCapacity,
                    PerTeamLimit = autoTeamCap,
                    Dynamic = false,
                    Percent = 0
                };
            }
            else
            {
                // Update existing entry - only change arena cap, leave team cap unchanged
                _classLimits[classId].PerArenaLimit = newCapacity;
                // Turn off dynamic limits when manual limits are set
                if (_classLimits[classId].Dynamic)
                {
                    _classLimits[classId].Dynamic = false;
                    _classLimits[classId].Percent = 0;
                    string skillName = GetSkillName(player, classId);
                    player.sendMessage(0, $"$Dynamic limits for {skillName} disabled (manual limits set).");
                }
            }

            // Check if new capacity is lower than current count and warn admin
            if (newCapacity >= 0)
            {
                int currentCount = arena.PlayersIngame.Count(p => GetPlayerCurrentSkillId(p) == classId);
                if (currentCount > newCapacity)
                {
                    string skillName = GetSkillName(player, classId);
                    player.sendMessage(0, $"!Warning: {skillName} arena capacity set to {newCapacity}, but {currentCount} players are currently using this class.");
                }
            }

            SaveLimits();
            string skillName2 = GetSkillName(player, classId);
            player.sendMessage(0, $"$Arena capacity for {skillName2} set to {newCapacity}.");
        }

        public void SetTeamCapacity(Player player, Arena arena, int classId, int newCapacity)
        {
            if (newCapacity < 0)
            {
                player.sendMessage(0, "!Team capacity cannot be negative.");
                return;
            }

            if (!HasClass(classId))
            {
                // Create new limit entry with auto-calculated arena cap
                int autoArenaCap = newCapacity * 2; // Team cap * 2
                _classLimits[classId] = new ClassLimit
                {
                    ClassId = classId,
                    ClassName = GetSkillName(player, classId),
                    PerArenaLimit = autoArenaCap,
                    PerTeamLimit = newCapacity
                };
            }
            else
            {
                // Update existing entry - only change team cap, leave arena cap unchanged
                _classLimits[classId].PerTeamLimit = newCapacity;
            }

            // Check if new capacity is lower than current count and warn admin
            if (newCapacity >= 0)
            {
                int currentTeamCount = 0;
                if (player._team != null)
                {
                    currentTeamCount = player._team.ActivePlayers.Count(p => GetPlayerCurrentSkillId(p) == classId);
                }

                if (currentTeamCount > newCapacity)
                {
                    string skillName = GetSkillName(player, classId);
                    player.sendMessage(0, $"!Warning: {skillName} team capacity set to {newCapacity}, but {currentTeamCount} players are currently using this class in your team.");
                }
            }

            SaveLimits();
            string skillName2 = GetSkillName(player, classId);
            player.sendMessage(0, $"$Team capacity for {skillName2} set to {newCapacity}.");
        }

        private void SaveLimits()
        {
            try
            {
                var limits = _classLimits.Values.ToList();
                var json = JsonConvert.SerializeObject(limits, Formatting.Indented);

                // Prefer lowercase modules/ for Linux case-sensitive paths
                string jsonPath = "modules/class_limits.json";
                if (File.Exists(jsonPath))
                {
                    File.WriteAllText(jsonPath, json);
                    return;
                }

                // Legacy capitalized folder (Windows-only case-insensitive installs)
                if (File.Exists("Modules/class_limits.json"))
                {
                    File.WriteAllText("Modules/class_limits.json", json);
                    return;
                }

                Directory.CreateDirectory("modules");
                File.WriteAllText(jsonPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving class limits: {ex.Message}");
            }
        }

        public void GetDebugInfo(Player player, Arena arena)
        {
            // Check for dynamic recalculation first
            CheckDynamicRecalculation(arena);

            if (_classLimits.Count == 0)
            {
                player.sendMessage(0, "No class limits configured.");
                return;
            }

            player.sendMessage(0, "%=== Class Limits Info ===");
            player.sendMessage(0, "$Class Name (ID) | Arena Count/Limit | Team Count/Limit | Dynamic");
            player.sendMessage(0, "%----------------|------------------|------------------|---------");

            foreach (var limit in _classLimits.Values.OrderBy(l => l.ClassName ?? l.ClassId.ToString()))
            {
                int currentArenaCount = arena.PlayersIngame.Count(p => GetPlayerCurrentSkillId(p) == limit.ClassId);
                int currentTeamCount = 0;

                // Calculate current team count for this class
                if (player._team != null)
                {
                    currentTeamCount = player._team.ActivePlayers.Count(p => GetPlayerCurrentSkillId(p) == limit.ClassId);
                }

                string className = limit.ClassName ?? $"Class {limit.ClassId}";
                string arenaInfo = limit.PerArenaLimit >= 0 ? $"{currentArenaCount}/{limit.PerArenaLimit}" : $"{currentArenaCount}/No limit";
                string teamInfo = limit.PerTeamLimit >= 0 ? $"{currentTeamCount}/{limit.PerTeamLimit}" : $"{currentTeamCount}/No limit";
                string dynamicInfo = limit.Dynamic ? $"Yes ({limit.Percent}%)" : "No";

                player.sendMessage(0, $"{className}({limit.ClassId}) | {arenaInfo} | {teamInfo} | {dynamicInfo}");
            }

            player.sendMessage(0, "%=== End Class Limits Info ===");
        }

        public static void GetDebugInfoNotAvailable(Player player)
        {
            player.sendMessage(0, "!Class limits module is not available in this arena.");
        }

        public static void ArenaCapCommand(Player player, Arena arena, string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                player.sendMessage(0, "!Usage: *arenacap <class>:<amount> or *arenacap <classid>:<amount>");
                player.sendMessage(0, "!Example: *arenacap Sniper:5 or *arenacap 13:5");
                return;
            }

            var parts = payload.Split(':');
            if (parts.Length != 2)
            {
                player.sendMessage(0, "!Invalid format. Use: <class>:<amount> or <classid>:<amount>");
                return;
            }

            string classIdentifier = parts[0].Trim();
            string amountStr = parts[1].Trim();

            if (!int.TryParse(amountStr, out int amount))
            {
                player.sendMessage(0, "!Invalid capacity, please provide a number.");
                return;
            }

            int classId = -1;

            // Try to parse as class ID first
            if (int.TryParse(classIdentifier, out int parsedId))
            {
                classId = parsedId;
            }
            else
            {
                // Try to find by name using server assets
                if (player?._server?._assets != null)
                {
                    var skill = player._server._assets.getSkillByName(classIdentifier);
                    if (skill != null)
                    {
                        classId = skill.SkillId;
                    }
                }
            }

            if (classId == -1)
            {
                player.sendMessage(0, $"!Class '{classIdentifier}' not found.");
                return;
            }

            if (arena.ClassesModule != null)
            {
                arena.ClassesModule.SetArenaCapacity(player, arena, classId, amount);
            }
            else
            {
                player.sendMessage(0, "!Class limits module is not available in this arena.");
            }
        }

        public static void TeamCapCommand(Player player, Arena arena, string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                player.sendMessage(0, "!Usage: *teamcap <class>:<amount> or *teamcap <classid>:<amount>");
                player.sendMessage(0, "!Example: *teamcap Sniper:3 or *teamcap 13:3");
                return;
            }

            var parts = payload.Split(':');
            if (parts.Length != 2)
            {
                player.sendMessage(0, "!Invalid format. Use: <class>:<amount> or <classid>:<amount>");
                return;
            }

            string classIdentifier = parts[0].Trim();
            string amountStr = parts[1].Trim();

            if (!int.TryParse(amountStr, out int amount))
            {
                player.sendMessage(0, "!Invalid capacity, please provide a number.");
                return;
            }

            int classId = -1;

            // Try to parse as class ID first
            if (int.TryParse(classIdentifier, out int parsedId))
            {
                classId = parsedId;
            }
            else
            {
                // Try to find by name using server assets
                if (player?._server?._assets != null)
                {
                    var skill = player._server._assets.getSkillByName(classIdentifier);
                    if (skill != null)
                    {
                        classId = skill.SkillId;
                    }
                }
            }

            if (classId == -1)
            {
                player.sendMessage(0, $"!Class '{classIdentifier}' not found.");
                return;
            }

            if (arena.ClassesModule != null)
            {
                arena.ClassesModule.SetTeamCapacity(player, arena, classId, amount);
            }
            else
            {
                player.sendMessage(0, "!Class limits module is not available in this arena.");
            }
        }

        public static void ClassLimitsCommand(Player player, Arena arena, string payload)
        {
            if (arena.ClassesModule != null)
            {
                arena.ClassesModule.GetDebugInfo(player, arena);
            }
            else
            {
                GetDebugInfoNotAvailable(player);
            }
        }

        public static void DynamicLimitsCommand(Player player, Arena arena, string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                player.sendMessage(0, "!Usage: *dynamiclimits <class>:<on/off>[:<percent>] or *dynamiclimits <classid>:<on/off>[:<percent>]");
                player.sendMessage(0, "!Example: *dynamiclimits Sniper:on:10 or *dynamiclimits 13:off");
                return;
            }

            var parts = payload.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                player.sendMessage(0, "!Invalid format. Use: <class>:<on/off>[:<percent>] or <classid>:<on/off>[:<percent>]");
                return;
            }

            string classIdentifier = parts[0].Trim();
            string dynamicStr = parts[1].Trim().ToLower();
            string percentStr = parts.Length == 3 ? parts[2].Trim() : "0";

            bool dynamic = false;
            if (dynamicStr == "on" || dynamicStr == "true" || dynamicStr == "1")
            {
                dynamic = true;
            }
            else if (dynamicStr == "off" || dynamicStr == "false" || dynamicStr == "0")
            {
                dynamic = false;
            }
            else
            {
                player.sendMessage(0, "!Invalid dynamic setting. Use 'on' or 'off'.");
                return;
            }

            int percent = 0;
            if (dynamic)
            {
                // When turning dynamic on, percent is required
                if (parts.Length < 3)
                {
                    player.sendMessage(0, "!When turning dynamic on, percent is required. Use: <class>:on:<percent>");
                    return;
                }

                if (!int.TryParse(percentStr, out percent) || percent < 1 || percent > 100)
                {
                    player.sendMessage(0, "!Invalid percentage, please provide a number between 1 and 100.");
                    return;
                }
            }
            else
            {
                // When turning dynamic off, percent is optional and defaults to 0
                if (parts.Length == 3)
                {
                    if (!int.TryParse(percentStr, out percent) || percent < 0 || percent > 100)
                    {
                        player.sendMessage(0, "!Invalid percentage, please provide a number between 0 and 100.");
                        return;
                    }
                }
            }

            int classId = -1;

            // Try to parse as class ID first
            if (int.TryParse(classIdentifier, out int parsedId))
            {
                classId = parsedId;
            }
            else
            {
                // Try to find by name using server assets
                if (player?._server?._assets != null)
                {
                    var skill = player._server._assets.getSkillByName(classIdentifier);
                    if (skill != null)
                    {
                        classId = skill.SkillId;
                    }
                }
            }

            if (classId == -1)
            {
                player.sendMessage(0, $"!Class '{classIdentifier}' not found.");
                return;
            }

            if (arena.ClassesModule != null)
            {
                arena.ClassesModule.SetDynamicLimits(player, arena, classId, dynamic, percent);
            }
            else
            {
                player.sendMessage(0, "!Class limits module is not available in this arena.");
            }
        }

        public static bool CanPlayerUnspecToCurrentClass(Player player, Arena arena, Team team)
        {
            if (arena.ClassesModule == null)
                return true; // No module, no restrictions

            int playerSkillId = GetPlayerCurrentSkillId(player);
            return arena.ClassesModule.CanUnspecToClass(arena, playerSkillId, team);
        }

        public static string GetUnspecBlockedMessage(Player player, string className)
        {
            return GetClassBlockedMessage(player, className, "unspec");
        }

        public static string GetClassChangeBlockedMessage(Player player, string className)
        {
            return GetClassBlockedMessage(player, className, "change class");
        }

        /// <summary>
        /// Returns false and sends a message when the player cannot unspec/join with their current class.
        /// </summary>
        public static bool CheckUnspecClassLimit(Player player, Arena arena, Team team)
        {
            if (arena?.ClassesModule == null || player == null)
                return true;

            if (CanPlayerUnspecToCurrentClass(player, arena, team))
                return true;

            int playerSkillId = GetPlayerCurrentSkillId(player);
            string skillname = "Unknown";

            if (player._skills != null && player._skills.TryGetValue(playerSkillId, out var sk))
                skillname = sk.skill.Name;
            else if (player._server?._assets != null)
            {
                var skillInfo = player._server._assets.getSkillByID(playerSkillId);
                if (skillInfo != null)
                    skillname = skillInfo.Name;
            }

            player.sendMessage(0, GetUnspecBlockedMessage(player, skillname));
            return false;
        }

        private static string GetClassBlockedMessage(Player player, string className, string action)
        {
            try
            {
                return $"!Cannot {action}: {className} is at capacity limit.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"!Error getting class blocked message: {ex.Message}");
                return $"!Cannot {action}: {className} is at capacity limit.";
            }
        }
    }
}
