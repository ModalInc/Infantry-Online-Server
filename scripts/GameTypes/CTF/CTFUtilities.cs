using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Microsoft.CSharp;

using InfServer.Game;
using InfServer.Scripting;
using InfServer.Protocol;
using InfServer.Logic;

using Assets;

namespace CTFGameType
{
    public class PlayerData
    {
        public string alias;
        public string team;
        public string teamType;
        public string className;
        public bool isOffense;
        public string weapon;
    }

    public class CustomPhrase
    {
        public string in_game_alias { get; set; }  // Changed from player_alias to match the working function
        public string custom_phrase { get; set; }  // Changed from phrase to match the working function
        public DateTime expires_at { get; set; }
        public bool is_active { get; set; }
    }

    public class PhraseExplosionManager
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static Dictionary<string, string> playerPhrases = new Dictionary<string, string>();
        private static DateTime lastCacheUpdate = DateTime.MinValue;
        private static readonly TimeSpan cacheExpiryTime = TimeSpan.FromMinutes(5); // Cache for 5 minutes
        
        // Your Supabase configuration
        private const string SUPABASE_URL = "https://nkinpmqnbcjaftqduujf.supabase.co"; // Replace with your Supabase URL
        
        // Keys are never committed: scripts/GameTypes/CTF/SupabaseKeys.txt (git-ignored, "anon=" / "service=" lines)
        // or the SUPABASE_ANON_KEY / SUPABASE_SERVICE_ROLE_KEY environment variables.
        private static readonly string SUPABASE_ANON_KEY = SupabaseKey("anon", "SUPABASE_ANON_KEY");
        private static readonly string SUPABASE_SERVICE_ROLE_KEY = SupabaseKey("service", "SUPABASE_SERVICE_ROLE_KEY");
        private const string PHRASES_API_ENDPOINT = SUPABASE_URL + "/rest/v1/rpc/get_simple_player_phrases";

        internal static string SupabaseKey(string fileKey, string envVar)
        {
            string env = Environment.GetEnvironmentVariable(envVar);
            if (!String.IsNullOrWhiteSpace(env))
                return env.Trim();
            try
            {
                string path = System.IO.Path.Combine("scripts", System.IO.Path.Combine("GameTypes",
                    System.IO.Path.Combine("CTF", "SupabaseKeys.txt")));
                if (System.IO.File.Exists(path))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#"))
                            continue;
                        int eq = line.IndexOf('=');
                        if (eq > 0 && String.Equals(line.Substring(0, eq).Trim(), fileKey, StringComparison.OrdinalIgnoreCase))
                            return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("CTF: could not read SupabaseKeys.txt: {0}", ex.Message));
            }
            return "";
        }
        
        public static async Task<string> GetPlayerPhrase(string playerAlias)
        {
            try
            {
                // Check if cache needs refreshing
                if (DateTime.Now - lastCacheUpdate > cacheExpiryTime)
                {
                    await RefreshPhrasesCache();
                }
                
                // Return custom phrase if found, otherwise return null
                if (playerPhrases.ContainsKey(playerAlias.ToLower()))
                {
                    return playerPhrases[playerAlias.ToLower()];
                }
                
                return null; // No phrase found
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error getting player phrase for {0}: {1}", playerAlias, ex.Message));
                return null; // Return null on error
            }
        }
        private static async Task RefreshPhrasesCache()
        {
            try
            {
                // Create a new HttpClient for each request to avoid connection issues
                using (var handler = new HttpClientHandler())
                {
                    // Configure SSL settings for Supabase - match the TestConnection method
                    handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
                    handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                    
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                        
                        // Set headers exactly like the working JavaScript version
                        client.DefaultRequestHeaders.Add("User-Agent", "CTF-Game/1.0");
                        client.DefaultRequestHeaders.Add("apikey", SUPABASE_SERVICE_ROLE_KEY);
                        client.DefaultRequestHeaders.Add("Authorization", String.Format("Bearer {0}", SUPABASE_SERVICE_ROLE_KEY));
                        
                        // Use empty JSON object as request body (matching JavaScript)
                        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                        
                        Console.WriteLine(String.Format("Attempting to connect to: {0}", PHRASES_API_ENDPOINT));
                        
                        var response = await client.PostAsync(PHRASES_API_ENDPOINT, content);
                        
                        Console.WriteLine(String.Format("Response status: {0}", response.StatusCode));
                        
                        if (response.IsSuccessStatusCode)
                        {
                            string jsonResponse = await response.Content.ReadAsStringAsync();
                            Console.WriteLine(String.Format("Response length: {0} characters", jsonResponse.Length));
                            
                            // Add some debug output to see what we're getting
                            if (jsonResponse.Length < 500) // Only log short responses
                            {
                                Console.WriteLine(String.Format("Response content: {0}", jsonResponse));
                            }
                            
                            var phrases = ParsePhrasesJson(jsonResponse);
                            
                            // Update cache
                            playerPhrases.Clear();
                            foreach (var phrase in phrases)
                            {
                                // Since we're using get_simple_player_phrases, all returned phrases are active
                                if (!String.IsNullOrEmpty(phrase.in_game_alias) && !String.IsNullOrEmpty(phrase.custom_phrase))
                                {
                                    playerPhrases[phrase.in_game_alias.ToLower()] = phrase.custom_phrase;
                                }
                            }
                            
                            lastCacheUpdate = DateTime.Now;
                            Console.WriteLine(String.Format("Phrases cache refreshed: {0} active phrases loaded", playerPhrases.Count));
                            
                            // Log the loaded phrases for debugging
                            foreach (var kvp in playerPhrases)
                            {
                                Console.WriteLine(String.Format("Loaded phrase: {0} -> {1}", kvp.Key, kvp.Value));
                            }
                        }
                        else
                        {
                            string errorContent = await response.Content.ReadAsStringAsync();
                            Console.WriteLine(String.Format("HTTP Error {0}: {1}", response.StatusCode, errorContent));
                        }
                    }
                }
            }
            catch (HttpRequestException httpEx)
            {
                Console.WriteLine(String.Format("HTTP Request Error: {0}", httpEx.Message));
                if (httpEx.InnerException != null)
                {
                    Console.WriteLine(String.Format("Inner exception: {0}", httpEx.InnerException.Message));
                }
            }
            catch (TaskCanceledException tcEx)
            {
                if (tcEx.CancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("Request was cancelled (likely due to timeout)");
                }
                else
                {
                    Console.WriteLine(String.Format("Request timed out: {0}", tcEx.Message));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error refreshing phrases cache: {0}", ex.Message));
                if (ex.InnerException != null)
                {
                    Console.WriteLine(String.Format("Inner exception: {0}", ex.InnerException.Message));
                }
            }
        }
        
        private static List<CustomPhrase> ParsePhrasesJson(string json)
        {
            var phrases = new List<CustomPhrase>();
            
            try
            {
                // Simple JSON parsing without external dependencies
                json = json.Trim();
                if (json.StartsWith("[") && json.EndsWith("]"))
                {
                    json = json.Substring(1, json.Length - 2); // Remove array brackets
                    
                    var objects = SplitJsonObjects(json);
                    foreach (var obj in objects)
                    {
                        var phrase = ParseSinglePhrase(obj);
                        if (phrase != null)
                            phrases.Add(phrase);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error parsing phrases JSON: {0}", ex.Message));
            }
            
            return phrases;
        }
        
        private static List<string> SplitJsonObjects(string json)
        {
            var objects = new List<string>();
            int depth = 0;
            int start = 0;
            
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{')
                    depth++;
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        objects.Add(json.Substring(start, i - start + 1));
                        start = i + 1;
                        // Skip comma and whitespace
                        while (start < json.Length && (json[start] == ',' || char.IsWhiteSpace(json[start])))
                            start++;
                        i = start - 1;
                    }
                }
            }
            
            return objects;
        }
        
        private static CustomPhrase ParseSinglePhrase(string json)
        {
            try
            {
                var phrase = new CustomPhrase();
                
                // Extract fields using simple string parsing - updated field names
                phrase.in_game_alias = ExtractJsonValue(json, "in_game_alias");
                phrase.custom_phrase = ExtractJsonValue(json, "custom_phrase");
                
                // For the simple function, we assume active phrases
                phrase.is_active = true;
                phrase.expires_at = DateTime.MaxValue; // No expiration from simple function
                
                return phrase;
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error parsing single phrase: {0}", ex.Message));
                return null;
            }
        }
        
        private static string ExtractJsonValue(string json, string key)
        {
            string pattern = String.Format("\"{0}\"\\s*:\\s*", key);
            var match = Regex.Match(json, pattern);
            if (!match.Success) return "";
            
            int start = match.Index + match.Length;
            if (start >= json.Length) return "";
            
            // Handle string values (quoted)
            if (json[start] == '"')
            {
                start++; // Skip opening quote
                int end = json.IndexOf('"', start);
                if (end == -1) return "";
                return json.Substring(start, end - start);
            }
            
            // Handle other values (boolean, null, etc.)
            int valueEnd = json.IndexOfAny(new char[] { ',', '}' }, start);
            if (valueEnd == -1) valueEnd = json.Length;
            
            return json.Substring(start, valueEnd - start).Trim();
        }
        
        // Method to test basic connectivity to Supabase
        public static async Task<bool> TestConnection(string alias)
        {
            try
            {
                //Console.WriteLine("Testing connection to Supabase...");
                
                using (var handler = new HttpClientHandler())
                {
                    // Configure SSL settings properly
                    handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
                    handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                    
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(30); // Increased timeout
                        
                        // Use the same headers as the working RefreshPhrasesCache method
                        client.DefaultRequestHeaders.Add("User-Agent", "CTF-Game/1.0");
                        client.DefaultRequestHeaders.Add("apikey", SUPABASE_SERVICE_ROLE_KEY);
                        client.DefaultRequestHeaders.Add("Authorization", String.Format("Bearer {0}", SUPABASE_SERVICE_ROLE_KEY));
                        
                        // Test the actual RPC endpoint that we use, not the base URL
                        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                        
                        //Console.WriteLine(String.Format("Testing RPC endpoint: {0}", PHRASES_API_ENDPOINT));
                        
                        var response = await client.PostAsync(PHRASES_API_ENDPOINT, content);
                        
                        //Console.WriteLine(String.Format("RPC test - Status: {0}", response.StatusCode));
                        
                        if (response.IsSuccessStatusCode)
                        {
                            string responseContent = await response.Content.ReadAsStringAsync();
                            //Console.WriteLine(String.Format("RPC test successful! Response length: {0} characters", responseContent.Length));
                            //responseContent should contain alias and the associated custom_phrase
                            string extractedAlias = ExtractJsonValue(responseContent, "in_game_alias");
                            string custom_phrase = ExtractJsonValue(responseContent, "custom_phrase");
                            //Console.WriteLine(String.Format("Player alias: {0}", extractedAlias));
                            //Console.WriteLine(String.Format("Player custom_phrase: {0}", custom_phrase));
                            return true;
                        }
                        else
                        {
                            string errorContent = await response.Content.ReadAsStringAsync();
                            Console.WriteLine(String.Format("RPC test failed - HTTP {0}: {1}", response.StatusCode, errorContent));
                            return false;
                        }
                    }
                }
            }
            catch (HttpRequestException httpEx)
            {
                Console.WriteLine(String.Format("HTTP Request Error during test: {0}", httpEx.Message));
                if (httpEx.InnerException != null)
                {
                    Console.WriteLine(String.Format("Inner exception: {0}", httpEx.InnerException.Message));
                }
                return false;
            }
            catch (TaskCanceledException tcEx)
            {
                if (tcEx.CancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("Connection test was cancelled");
                }
                else
                {
                    Console.WriteLine(String.Format("Connection test timed out: {0}", tcEx.Message));
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Connection test failed: {0}", ex.Message));
                if (ex.InnerException != null)
                {
                    Console.WriteLine(String.Format("Inner exception: {0}", ex.InnerException.Message));
                }
                return false;
            }
        }
        
        // Method to manually refresh cache (call this from admin commands if needed)
        public static async Task ForceRefreshPhrases()
        {
            await RefreshPhrasesCache();
        }
        
        // Method to get all cached phrases (for debugging)
        public static Dictionary<string, string> GetCachedPhrases()
        {
            return new Dictionary<string, string>(playerPhrases);
        }
    }

    // Extension method to create custom explosion text
    public static class ExplosionHelper
    {
        public static async Task CreateCustomExplosion(Arena arena, Player killerPlayer, short posX, short posY, short posZ)
        {
            try
            {
                // Get the killer's custom phrase
                string customPhrase = await PhraseExplosionManager.GetPlayerPhrase(killerPlayer._alias);
                
                // Create the explosion with custom text at victim's location
                CreateTextExplosion(arena, customPhrase, posX, posY, posZ, killerPlayer);
                
                // Optional: Log the explosion for debugging
                // Console.WriteLine(String.Format("{0} triggered explosion with phrase: {1}", killerPlayer._alias, customPhrase));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error creating custom explosion: {0}", ex.Message));
                // Fallback to default explosion
                CreateTextExplosion(arena, "BLOOP!", posX, posY, posZ, killerPlayer);
            }
        }
        
        private static void CreateTextExplosion(Arena arena, string text, short x, short y, short z, Player from)
        {
            // Create letter explosions similar to Albert's EMP grenade
            // Each letter is spawned as a separate projectile at the explosion location
            int xOffset = 10; // Space between letters
            
            for (int i = 0; i < text.Length; i++)
            {
                char letter = text[i];
                if (letter == ' ') continue; // Skip spaces
                
                // Get the letter projectile from assets
                ItemInfo.Projectile letterWep;
                
                // Special case for N since we know its exact ID
                if (letter == 'N' || letter == 'n')
                {
                    letterWep = AssetManager.Manager.getItemByID(1341) as ItemInfo.Projectile;
                }
                // letter m is 1356
                else if (letter == 'M' || letter == 'm')
                {
                    letterWep = AssetManager.Manager.getItemByID(1356) as ItemInfo.Projectile;
                }
                // Handle numbers by getting their spelled out names
                else if (char.IsDigit(letter))
                {
                    string[] numberNames = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
                    int digit = letter - '0';
                    letterWep = AssetManager.Manager.getItemByName(numberNames[digit]) as ItemInfo.Projectile;
                }
                else
                {
                    letterWep = AssetManager.Manager.getItemByName(letter.ToString()) as ItemInfo.Projectile;
                }
                
                if (letterWep != null)
                {
                    short newPosX = (short)(x + (i * xOffset));
                    
                    // Create the explosion projectile using the same method as Albert's EMP
                    SC_Projectile letterExplosion = new SC_Projectile
                    {
                        projectileID = (short)letterWep.id,
                        playerID = (ushort)from._id,
                        posX = newPosX,
                        posY = y,
                        posZ = z,
                        yaw = from._state.yaw
                    };
                    
                    // Send the explosion to all players
                    foreach (Player p in arena.Players)
                    {
                        p._client.sendReliable(letterExplosion);
                    }
                }
                else
                {
                    // Debug logging for missing letters
                    Console.WriteLine(String.Format("Could not find weapon for letter: {0}", letter));
                }
            }
        }
    }

    public class WebIntegration
    {
        private static readonly HttpClient httpClient = new HttpClient();
        //private const string API_ENDPOINT = "http://localhost:3000/api/game-data";
        private const string API_ENDPOINT = "https://freeinf.org/api/game-data";
        
        public static async Task SendGameDataToWebsite(Arena arena, string baseUsed)
        {
            try
            {
                // Determine game type based on arena name
                string gameType = DetermineGameType(arena._name);
                
                // Get all players in the arena
                var players = arena.Players.ToList();
                var gameDataPlayers = new List<PlayerData>();
                
                // Determine which team is offense/defense for OvD games
                bool titanIsOffense = false;
                //if (gameType == "OvD")
                //{
                    titanIsOffense = DetermineOffenseTeam(players, arena);
                //}
                
                // Process each player
                foreach (Player player in players)
                {
                    string actualTeamName = player._team._name;
                    string teamType = DeterminePlayerTeamType(player);
                    string className = player._baseVehicle._type.Name;
                    bool isOffense = DetermineIsOffense(teamType, titanIsOffense, gameType);
                    string weapon = GetSpecialWeapon(player);
                    
                    gameDataPlayers.Add(new PlayerData
                    {
                        alias = player._alias,
                        team = actualTeamName,
                        teamType = teamType,
                        className = className,
                        isOffense = isOffense,
                        weapon = weapon
                    });
                }
                
                // Manually construct JSON string (no external libraries needed)
                string jsonData = BuildJsonString(arena._name, gameType, baseUsed, gameDataPlayers);
                
                // Send to API
                var content = new StringContent(jsonData, System.Text.Encoding.UTF8, "application/json");
                
                var response = await httpClient.PostAsync(API_ENDPOINT, content);
                
                if (response.IsSuccessStatusCode)
                {
                    //Console.WriteLine("Game data sent successfully to website");
                }
                else
                {
                    //Console.WriteLine(String.Format("Failed to send game data: {0}", response.StatusCode));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error sending game data: {0}", ex.Message));
            }
        }
        
        private static string DetermineGameType(string arenaName)
        {
            if (arenaName.Contains("OvD"))
                return "OvD";
            else if (arenaName.Contains("Arena 1"))
                return "Pub";
            else if (arenaName.Contains("Mix"))
                return "Mix";
            else if (arenaName.Contains("Duel"))
                return "Dueling";
            else if (arenaName.Contains("CTF"))
                return "CTF";
            else
                return "Unknown";
        }
        
        private static string DeterminePlayerTeamType(Player player)
        {
            if (player._team._name.Contains(" T"))
                return "Titan";
            else if (player._team._name.Contains(" C"))
                return "Collective";
            else
                return "Unknown";
        }
        
        private static bool DetermineOffenseTeam(List<Player> players, Arena arena)
        {
            // First, try to find the team with a Squad Leader - that team is offense
            foreach (Player player in players)
            {
                // Get the player's primary skill name instead of vehicle type
                string primarySkill = "";
                if (player._skills.Count > 0)
                {
                    primarySkill = player._skills.First().Value.skill.Name;
                }
                
                if (primarySkill == "Squad Leader")
                {
                    return player._team._name.Contains(" T"); // Return true if Titan has Squad Leader
                }
            }
            
            // If no Squad Leader found, use a consistent fallback based on team balance
            // Count players on each team type (excluding Dueler players)
            int titanCount = 0;
            int collectiveCount = 0;
            
            foreach (Player player in players)
            {
                // Skip Dueler players from team counting
                string primarySkill = "";
                if (player._skills.Count > 0)
                {
                    primarySkill = player._skills.First().Value.skill.Name;
                }
                if (primarySkill == "Dueler") continue;
                
                if (player._team._name.Contains(" T"))
                    titanCount++;
                else if (player._team._name.Contains(" C"))
                    collectiveCount++;
            }
            
            // Make the larger team defense (more common in OvD), smaller team offense
            // If equal, default to Titan = offense
            if (titanCount > collectiveCount)
                return false; // Titan = defense (larger team)
            else
                return true;  // Titan = offense (smaller or equal team)
        }
        
        private static bool DetermineIsOffense(string teamType, bool titanIsOffense, string gameType)
        {
            // For OvD games, use the Squad Leader logic
            if (gameType == "OvD")
            {
                if (teamType == "Titan")
                    return titanIsOffense;
                else
                    return !titanIsOffense;
            }
            
            // For CTF games, assume teams alternate offense/defense
            if (gameType == "CTF")
            {
                if (teamType == "Titan")
                    return titanIsOffense;
                else
                    return !titanIsOffense;
            }
            
            // For other game types (Pub, Mix, Duel, Unknown), still try to separate teams
            // Don't default everyone to offense - use the same logic
            if (teamType == "Titan")
                return titanIsOffense;
            else
                return !titanIsOffense;
        }
        
        private static string GetSpecialWeapon(Player player)
        {
            // Check for special weapons based on player's current equipment
            // You may need to adapt this based on your specific weapon detection system
            
            if (player._baseVehicle != null && player._baseVehicle._type != null)
            {
                string vehicleType = player._baseVehicle._type.Name;
                if (vehicleType.Contains("CAW"))
                    return "CAW";
                else if (vehicleType.Contains("SG"))
                    return "SG";
            }
            
            // Alternative: Check player's inventory or equipped items
            // You may need to implement this based on your specific system
            
            return null; // Return null if no special weapon
        }
        
        private static string BuildJsonString(string arenaName, string gameType, string baseUsed, List<PlayerData> players)
        {
            var json = new System.Text.StringBuilder();
            json.Append("{");
            
            // Add basic properties
            json.AppendFormat("\"arenaName\":\"{0}\",", EscapeJsonString(arenaName));
            json.AppendFormat("\"gameType\":\"{0}\",", EscapeJsonString(gameType));
            json.AppendFormat("\"baseUsed\":\"{0}\",", EscapeJsonString(baseUsed));
            
            // Add players array
            json.Append("\"players\":[");
            
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                json.Append("{");
                json.AppendFormat("\"alias\":\"{0}\",", EscapeJsonString(player.alias));
                json.AppendFormat("\"team\":\"{0}\",", EscapeJsonString(player.team));
                json.AppendFormat("\"teamType\":\"{0}\",", EscapeJsonString(player.teamType));
                json.AppendFormat("\"class\":\"{0}\",", EscapeJsonString(player.className));
                json.AppendFormat("\"isOffense\":{0},", player.isOffense.ToString().ToLower());
                
                if (string.IsNullOrEmpty(player.weapon))
                {
                    json.Append("\"weapon\":null");
                }
                else
                {
                    json.AppendFormat("\"weapon\":\"{0}\"", WebIntegration.EscapeJsonString(player.weapon));
                }
                
                json.Append("}");
                
                if (i < players.Count - 1)
                {
                    json.Append(",");
                }
            }
            
            json.Append("]");
            json.Append("}");
            
            return json.ToString();
        }
        
        public static string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";
                
            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }
    }

    // Simple test program to send sample game data to your website
    // Run this to test the integration before implementing with real game data

    public class TestGameData
    {
        private static readonly HttpClient httpClient = new HttpClient();
        //private const string API_ENDPOINT = "http://localhost:3000/api/game-data";
        private const string API_ENDPOINT = "https://freeinf.org/api/game-data";
        
        public static async Task Main(string[] args)
        {
            Console.WriteLine("Sending test game data to website...");
            
            // Create sample game data similar to what your actual game would send
            var testGameData = new
            {
                arenaName = "OvD Arena",
                gameType = "OvD",
                baseUsed = "Test Base Alpha",
                players = new[]
                {
                    // Offense Team (Titan)
                    new { alias = "Herthbul", team = "Titan", @class = "Squad Leader", isOffense = true, weapon = (string)null },
                    new { alias = "Dinobot", team = "Titan", @class = "Heavy Weapons", isOffense = true, weapon = (string)null },
                    new { alias = "jay", team = "Titan", @class = "Infantry", isOffense = true, weapon = (string)null },
                    new { alias = "iron", team = "Titan", @class = "Infantry", isOffense = true, weapon = (string)null },
                    new { alias = "Angelus", team = "Titan", @class = "Infantry", isOffense = true, weapon = (string)null },
                    
                    // Defense Team (Collective)
                    new { alias = "Dilatory", team = "Collective", @class = "Field Medic", isOffense = false, weapon = (string)null },
                    new { alias = "albert", team = "Collective", @class = "Combat Engineer", isOffense = false, weapon = (string)null },
                    new { alias = "Axidus", team = "Collective", @class = "Heavy Weapons", isOffense = false, weapon = (string)null },
                    new { alias = "Greed", team = "Collective", @class = "Infantry", isOffense = false, weapon = "CAW" },
                    new { alias = "Silly Wanker", team = "Collective", @class = "Infantry", isOffense = false, weapon = "SG" }
                }
            };
            
            try
            {
                // Convert to JSON using our manual JSON builder
                string jsonData = BuildJsonString(testGameData);
                Console.WriteLine("Sending data:");
                Console.WriteLine(jsonData);
                
                // Send to API
                var content = new StringContent(jsonData, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(API_ENDPOINT, content);
                
                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("✅ Success! Server response: {0}", responseContent));
                    Console.WriteLine("Check your website at http://localhost:3000 to see the updated player list!");
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("❌ Failed to send data. Status: {0}", response.StatusCode));
                    Console.WriteLine(String.Format("Error: {0}", errorContent));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("❌ Exception occurred: {0}", ex.Message));
            }
            
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        private static string BuildJsonString(object testGameData)
        {
            var json = new System.Text.StringBuilder();
            json.Append("{");
            json.AppendFormat("\"arenaName\":\"{0}\",", ((TestGameDataObject)testGameData).arenaName);
            json.AppendFormat("\"gameType\":\"{0}\",", ((TestGameDataObject)testGameData).gameType);
            json.AppendFormat("\"baseUsed\":\"{0}\",", ((TestGameDataObject)testGameData).baseUsed);
            json.Append("\"players\":[");

            var players = ((TestGameDataObject)testGameData).players;
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i];
                json.Append("{");
                json.AppendFormat("\"alias\":\"{0}\",", player.alias);
                json.AppendFormat("\"team\":\"{0}\",", player.team);
                json.AppendFormat("\"class\":\"{0}\",", player.@class);
                json.AppendFormat("\"isOffense\":{0},", player.isOffense.ToString().ToLower());
                
                if (player.weapon == null)
                {
                    json.Append("\"weapon\":null");
                }
                else
                {
                    json.AppendFormat("\"weapon\":\"{0}\"", WebIntegration.EscapeJsonString(player.weapon));
                }
                
                json.Append("}");
                
                if (i < players.Length - 1)
                {
                    json.Append(",");
                }
            }
            
            json.Append("]");
            json.Append("}");
            
            return json.ToString();
        }
    }

    public class TestGameDataObject
    {
        public string arenaName { get; set; }
        public string gameType { get; set; }
        public string baseUsed { get; set; }
        public TestPlayerObject[] players { get; set; }
    }

    public class TestPlayerObject
    {
        public string alias { get; set; }
        public string team { get; set; }
        public string @class { get; set; }
        public bool isOffense { get; set; }
        public string weapon { get; set; }
    }

    // =============================================================================
    // DUELING SYSTEM CLASSES
    // =============================================================================

    public enum DuelType
    {
        Unranked,
        RankedBo3,
        RankedBo5
    }

    public enum DuelStatus
    {
        Challenged,
        InProgress,
        Completed,
        Abandoned
    }

    public class DuelMatch
    {
        public int MatchId { get; set; }
        public DuelType MatchType { get; set; }
        public string Player1Name { get; set; }
        public string Player2Name { get; set; }
        public string Player1Id { get; set; }  // Player 1 ID (null if unregistered)
        public string Player2Id { get; set; }  // Player 2 ID (null if unregistered)
        public string WinnerName { get; set; }
        public string WinnerId { get; set; }   // Winner ID (null if unregistered)
        public int Player1RoundsWon { get; set; }
        public int Player2RoundsWon { get; set; }
        public int TotalRounds { get; set; }
        public DuelStatus Status { get; set; }
        public string ArenaName { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public List<DuelRound> Rounds { get; set; }

        public DuelMatch()
        {
            Rounds = new List<DuelRound>();
            // Initialize IDs as null (will be populated if players are registered)
            Player1Id = null;
            Player2Id = null;
            WinnerId = null;
        }
    }

    public class DuelRound
    {
        public int RoundNumber { get; set; }
        public string WinnerName { get; set; }
        public string LoserName { get; set; }
        public int WinnerHpLeft { get; set; }
        public int LoserHpLeft { get; set; }
        public int DurationSeconds { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public List<DuelKill> Kills { get; set; }

        public DuelRound()
        {
            Kills = new List<DuelKill>();
        }
    }

    public class DuelKill
    {
        public string KillerName { get; set; }
        public string VictimName { get; set; }
        public string WeaponUsed { get; set; }
        public int DamageDealt { get; set; }
        public int VictimHpBefore { get; set; }
        public int VictimHpAfter { get; set; }
        public int ShotsFired { get; set; }
        public int ShotsHit { get; set; }
        public bool IsDoubleHit { get; set; }
        public bool IsTripleHit { get; set; }
        public DateTime KillTimestamp { get; set; }
    }

    public class DuelChallenge
    {
        public string ChallengerName { get; set; }
        public string ChallengeName { get; set; }
        public DuelType DuelType { get; set; }
        public DateTime ChallengeTime { get; set; }
        public bool IsActive { get { return DateTime.Now.Subtract(ChallengeTime).TotalMinutes < 2; } } // 2 minute timeout
    }

    public class TileWaitingPlayer
    {
        public string PlayerName { get; set; }
        public DuelType DuelType { get; set; }
        public DateTime WaitStartTime { get; set; }
        public bool IsActive { get { return DateTime.Now.Subtract(WaitStartTime).TotalSeconds < 30; } } // 30 second timeout
    }

    public class DuelSideAssignment
    {
        public string Player1Name { get; set; }
        public string Player2Name { get; set; }
        public string Player1Side { get; set; } // "player1" or "player2"
        public string Player2Side { get; set; } // "player1" or "player2"
        public int RoundNumber { get; set; }
    }

    public class PlayerShotStats
    {
        public string PlayerName { get; set; }
        public int ShotsFired { get; set; }
        public int ShotsHit { get; set; }
        public DateTime LastReset { get; set; }
        public int MatchId { get; set; }

        public PlayerShotStats()
        {
            ShotsFired = 0;
            ShotsHit = 0;
            LastReset = DateTime.Now;
        }

        public double Accuracy
        {
            get
            {
                if (ShotsFired == 0) return 0.0;
                return (double)ShotsHit / ShotsFired * 100.0;
            }
        }
    }

    public static class DuelingSystem
    {
        private static readonly HttpClient httpClient = new HttpClient();
        
        // API Configuration - Change USE_LOCAL_API to switch between local and production
        private const bool USE_LOCAL_API = true; // Set to false for production
        private const string LOCAL_DUELING_API_ENDPOINT = "http://localhost:3000/api/dueling/stats";
        private const string PRODUCTION_DUELING_API_ENDPOINT = "https://freeinf.org/api/dueling/stats";
        private static string DUELING_API_ENDPOINT
        {
            get
            {
                return USE_LOCAL_API ? LOCAL_DUELING_API_ENDPOINT : PRODUCTION_DUELING_API_ENDPOINT;
            }
        }

        // Thread-safe collections and locks
        private static readonly ConcurrentDictionary<string, DuelMatch> activeDuels = new ConcurrentDictionary<string, DuelMatch>();
        private static readonly List<DuelChallenge> activeChallenges = new List<DuelChallenge>();
        private static readonly ConcurrentDictionary<string, DateTime> lastDamageTime = new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentDictionary<string, List<DateTime>> recentDamageHits = new ConcurrentDictionary<string, List<DateTime>>();
        
        // Locks for critical sections
        private static readonly object challengeLock = new object();
        private static readonly object duelLock = new object();

        // Dueling tiles coordinates (to be updated based on your map)
        private static readonly Dictionary<string, Tuple<short, short>> DUELING_TILES = new Dictionary<string, Tuple<short, short>>
        {
            { "ranked_bo3", new Tuple<short, short>(775, 517) },   // Bo3 tile location
            { "ranked_bo5", new Tuple<short, short>(784, 517) }    // Bo5 tile location
        };

        // Dueling spawn positions (scaled to 16x16 pixels)
        private static readonly Dictionary<string, Tuple<short, short, byte>> DUEL_SPAWN_POSITIONS = new Dictionary<string, Tuple<short, short, byte>>
        {
            { "player1", new Tuple<short, short, byte>(763, 534, 64) },   // Player 1 facing right (yaw 64)
            { "player2", new Tuple<short, short, byte>(795, 534, 192) }   // Player 2 facing left (yaw 192)
        };

        // Track players waiting on tiles
        private static readonly ConcurrentDictionary<string, TileWaitingPlayer> playersOnTiles = new ConcurrentDictionary<string, TileWaitingPlayer>();

        // Track current side assignments for active duels (for side swapping)
        private static readonly ConcurrentDictionary<string, DuelSideAssignment> duelSideAssignments = new ConcurrentDictionary<string, DuelSideAssignment>();

        // Track shot statistics for dueling players
        private static readonly ConcurrentDictionary<string, PlayerShotStats> playerShotStats = new ConcurrentDictionary<string, PlayerShotStats>();

        public static async Task HandleDuelCommand(Player player, string command, string payload)
        {
            try
            {
                string[] parts = payload.Split(' ');
                string subCommand = parts.Length > 0 ? parts[0].ToLower() : "help";

                switch (subCommand)
                {
                    case "challenge":
                        if (parts.Length < 2)
                        {
                            player.sendMessage(-1, "Usage: ?duel challenge <player> [type]");
                            return;
                        }
                        
                        // Parse player name and duel type more intelligently to handle spaces in names
                        string targetName;
                        string duelTypeStr = "unranked";
                        
                        // Check if the last part is a valid duel type
                        string lastPart = parts[parts.Length - 1].ToLower();
                        if (lastPart == "unranked" || lastPart == "bo3" || lastPart == "bo5" || 
                            lastPart == "ranked_bo3" || lastPart == "ranked_bo5")
                        {
                            // Last part is a duel type, so player name is everything in between
                            duelTypeStr = lastPart;
                            if (parts.Length == 2)
                            {
                                // Only "challenge" and duel type, no player name
                                player.sendMessage(-1, "Usage: ?duel challenge <player> [type]");
                                player.sendMessage(-1, "Example: ?duel challenge \"Jeff Bezos\" bo3");
                                return;
                            }
                            // Join all parts except first (challenge) and last (duel type)
                            targetName = String.Join(" ", parts, 1, parts.Length - 2);
                        }
                        else
                        {
                            // No duel type specified, player name is everything after "challenge"
                            targetName = String.Join(" ", parts, 1, parts.Length - 1);
                        }
                        
                        // Trim any quotes that players might use
                        targetName = targetName.Trim('"', '\'');
                        
                        if (String.IsNullOrEmpty(targetName))
                        {
                            player.sendMessage(-1, "Usage: ?duel challenge <player> [type]");
                            player.sendMessage(-1, "Example: ?duel challenge \"Jeff Bezos\" bo3");
                            return;
                        }
                        
                        DuelType duelType = ParseDuelType(duelTypeStr);
                        await ChallengeToDuel(player, targetName, duelType);
                        break;

                    case "test":
                        // Test command for simulating duels against fake players
                        if (parts.Length < 2)
                        {
                            player.sendMessage(-1, "Usage: ?duel test <fake_player_name> [type]");
                            player.sendMessage(-1, "Example: ?duel test TestBot bo3");
                            return;
                        }
                        
                        string fakePlayerName = parts.Length > 2 ? String.Join(" ", parts, 1, parts.Length - 2) : String.Join(" ", parts, 1, parts.Length - 1);
                        string testDuelTypeStr = parts.Length > 2 && (parts[parts.Length - 1].ToLower() == "bo3" || parts[parts.Length - 1].ToLower() == "bo5" || parts[parts.Length - 1].ToLower() == "unranked") ? parts[parts.Length - 1] : "bo3";
                        
                        // If only one word after "test", it's the player name and default to bo3
                        if (parts.Length == 2)
                        {
                            fakePlayerName = parts[1];
                            testDuelTypeStr = "bo3";
                        }
                        
                        DuelType testDuelType = ParseDuelType(testDuelTypeStr);
                        await SimulateDuelMatch(player, fakePlayerName, testDuelType);
                        break;

                    case "accept":
                        await AcceptDuelChallenge(player);
                        break;

                    case "decline":
                        DeclineDuelChallenge(player);
                        break;

                    case "forfeit":
                        await ForfeitDuel(player);
                        break;

                    case "stats":
                        // Handle stats command with potential spaces in player names
                        string statsTarget;
                        if (parts.Length > 1)
                        {
                            // Join all parts after "stats" to handle spaces in names
                            statsTarget = String.Join(" ", parts, 1, parts.Length - 1).Trim('"', '\'');
                        }
                        else
                        {
                            statsTarget = player._alias;
                        }
                        await ShowPlayerDuelStats(player, statsTarget);
                        break;

                    case "help":
                    default:
                        ShowDuelHelp(player);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in HandleDuelCommand: {0}", ex.Message));
                player.sendMessage(-1, "An error occurred processing your duel command.");
            }
        }

        private static void ShowDuelHelp(Player player)
        {
            player.sendMessage(-1, "!== DUELING SYSTEM ==");
            player.sendMessage(-1, "@?duel challenge <player> [type] - Challenge a player to duel");
            player.sendMessage(-1, "?duel accept - Accept a duel challenge");
            player.sendMessage(-1, "?duel decline - Decline a duel challenge");
            player.sendMessage(-1, "!?duel forfeit - Forfeit current duel");
            player.sendMessage(-1, "@?duel stats [player] - View dueling statistics");
            player.sendMessage(-1, "~?duel test <fake_player> [type] - Simulate duel for testing");
            player.sendMessage(-1, "");
            player.sendMessage(-1, "!Duel Types: unranked, bo3 (ranked), bo5 (ranked)");
            player.sendMessage(-1, "@RANKED TILES: Step on Bo3 (775,517) or Bo5 (784,517) tiles!");
            player.sendMessage(-1, "!Auto-match when 2 players step on same tile type!");
            player.sendMessage(-1, "@Players swap sides after each round in tile duels!");
            player.sendMessage(-1, "");
            player.sendMessage(-1, "Examples:");
            player.sendMessage(-1, "?duel challenge Axidus bo3");
            player.sendMessage(-1, "?duel challenge Jeff Bezos bo5");
            player.sendMessage(-1, "?duel stats Jeff Bezos");
            player.sendMessage(-1, "~?duel test TestBot bo3 - Test with fake player");
        }

        private static DuelType ParseDuelType(string type)
        {
            string lowerType = type.ToLower();
            if (lowerType == "bo3" || lowerType == "ranked_bo3")
                return DuelType.RankedBo3;
            else if (lowerType == "bo5" || lowerType == "ranked_bo5")
                return DuelType.RankedBo5;
            else
                return DuelType.Unranked;
        }

        private static async Task ChallengeToDuel(Player challenger, string targetName, DuelType duelType)
        {
            // Find target player
            Player target = challenger._arena.Players.FirstOrDefault(p => 
                p._alias.Equals(targetName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                challenger.sendMessage(-1, String.Format("Player '{0}' not found in arena.", targetName));
                return;
            }

            if (target == challenger)
            {
                challenger.sendMessage(-1, "You cannot challenge yourself to a duel.");
                return;
            }

            // Thread-safe challenge processing
            lock (challengeLock)
            {
                // Check if players are already in a duel
                if (IsPlayerInDuel(challenger._alias) || IsPlayerInDuel(target._alias))
                {
                    challenger.sendMessage(-1, "One of the players is already in a duel.");
                    return;
                }

                // Remove old challenges
                RemoveExpiredChallenges();

                // Check if there's already an active challenge between these players
                var existingChallenge = activeChallenges.FirstOrDefault(c => 
                    c.IsActive && (
                        (c.ChallengerName == challenger._alias && c.ChallengeName == target._alias) ||
                        (c.ChallengerName == target._alias && c.ChallengeName == challenger._alias)
                    ));

                if (existingChallenge != null)
                {
                    challenger.sendMessage(-1, "There's already an active challenge between you and this player.");
                    return;
                }

                // Create new challenge
                var challenge = new DuelChallenge
                {
                    ChallengerName = challenger._alias,
                    ChallengeName = target._alias,
                    DuelType = duelType,
                    ChallengeTime = DateTime.Now
                };

                activeChallenges.Add(challenge);

                // Notify both players with colors
                string duelTypeStr = GetDuelTypeString(duelType);

                challenger.sendMessage(-1, String.Format("@Challenge sent to {0} for {1} duel!", target._alias, duelTypeStr));
                target.sendMessage(-1, String.Format("!{0} challenges you to a {1} duel!", challenger._alias, duelTypeStr));
                target.sendMessage(-1, "Type ?duel accept to accept or ?duel decline to decline");
                target.sendMessage(-1, "Challenge expires in 2 minutes");
            }
        }

        private static async Task AcceptDuelChallenge(Player player)
        {
            DuelChallenge challengeToAccept = null;
            Player challenger = null;

            // Thread-safe challenge acceptance
            lock (challengeLock)
            {
                challengeToAccept = activeChallenges.FirstOrDefault(c => 
                    c.ChallengeName == player._alias && c.IsActive);

                if (challengeToAccept == null)
                {
                    player.sendMessage(-1, "No active challenge found.");
                    return;
                }

                challenger = player._arena.Players.FirstOrDefault(p => 
                    p._alias.Equals(challengeToAccept.ChallengerName, StringComparison.OrdinalIgnoreCase));

                if (challenger == null)
                {
                    player.sendMessage(-1, "Challenger is no longer in the arena.");
                    // Mark challenge as processed by creating a new list without it
                    var newChallenges = activeChallenges.Where(c => c != challengeToAccept).ToList();
                    activeChallenges.Clear();
                    foreach (var c in newChallenges) activeChallenges.Add(c);
                    return;
                }

                // Check if either player is now in a duel (double-check for race conditions)
                if (IsPlayerInDuel(challenger._alias) || IsPlayerInDuel(player._alias))
                {
                    player.sendMessage(-1, "One of the players is already in a duel.");
                    return;
                }
            }

            // Start the duel outside the lock (async operation)
            await StartDuel(challenger, player, challengeToAccept.DuelType);

            // Remove the challenge after duel starts
            lock (challengeLock)
            {
                var newChallenges = activeChallenges.Where(c => c != challengeToAccept).ToList();
                activeChallenges.Clear();
                foreach (var c in newChallenges) activeChallenges.Add(c);
            }
        }

        private static void DeclineDuelChallenge(Player player)
        {
            DuelChallenge challengeToDecline = null;
            Player challenger = null;

            // Thread-safe challenge decline
            lock (challengeLock)
            {
                challengeToDecline = activeChallenges.FirstOrDefault(c => 
                    c.ChallengeName == player._alias && c.IsActive);

                if (challengeToDecline == null)
                {
                    player.sendMessage(-1, "No active challenge found.");
                    return;
                }

                // Find the challenger to notify them
                challenger = player._arena.Players.FirstOrDefault(p => 
                    p._alias.Equals(challengeToDecline.ChallengerName, StringComparison.OrdinalIgnoreCase));

                // Remove the challenge
                var newChallenges = activeChallenges.Where(c => c != challengeToDecline).ToList();
                activeChallenges.Clear();
                foreach (var c in newChallenges) activeChallenges.Add(c);
            }

            player.sendMessage(-1, "Challenge declined.");
            
            if (challenger != null)
            {
                challenger.sendMessage(-1, String.Format("{0} declined your duel challenge.", player._alias));
            }
        }

        private static async Task StartDuel(Player player1, Player player2, DuelType duelType)
        {
            string matchKey = String.Format("{0}_{1}", player1._alias, player2._alias);

            // Thread-safe duel creation
            lock (duelLock)
            {
                // Double-check that neither player is in a duel
                if (IsPlayerInDuel(player1._alias) || IsPlayerInDuel(player2._alias))
                {
                    player1.sendMessage(-1, "One of the players is already in a duel.");
                    player2.sendMessage(-1, "One of the players is already in a duel.");
                    return;
                }

                var duelMatch = new DuelMatch
                {
                    MatchType = duelType,
                    Player1Name = player1._alias,
                    Player2Name = player2._alias,
                    Status = DuelStatus.InProgress,
                    ArenaName = player1._arena._name,
                    StartedAt = DateTime.Now
                };

                // Initialize player IDs (will be resolved by the API)
                PopulatePlayerIds(duelMatch);

                activeDuels.TryAdd(matchKey, duelMatch);

                // Announce duel start
                string duelTypeStr = GetDuelTypeString(duelType);

                // Send message to all players in arena with colors
                foreach (Player p in player1._arena.Players)
                {
                    p.sendMessage(-1, String.Format("!DUEL STARTED: {0} vs {1}! ({2})", player1._alias, player2._alias, duelTypeStr));
                }

                // Reset shot stats for both players
                ResetPlayerShotStats(player1);
                ResetPlayerShotStats(player2);

                // Reset player states and start countdown with positioning
                RestorePlayerFromDuelingDeath(player1);
                RestorePlayerFromDuelingDeath(player2);
                
                // Start countdown and positioning sequence immediately (async)
                Task.Run(async () => {
                    try
                    {
                        await StartDuelCountdown(player1, player2, player1._arena);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(String.Format("Error during duel start countdown: {0}", ex.Message));
                    }
                });
                
                // Start first round
                StartDuelRound(duelMatch, 1);
            }
        }

        private static async Task StartTileBasedDuel(Player player1, Player player2, DuelType duelType)
        {
            string matchKey = String.Format("{0}_{1}", player1._alias, player2._alias);

            // Thread-safe duel creation
            lock (duelLock)
            {
                // Double-check that neither player is in a duel
                if (IsPlayerInDuel(player1._alias) || IsPlayerInDuel(player2._alias))
                {
                    player1.sendMessage(-1, "One of the players is already in a duel.");
                    player2.sendMessage(-1, "One of the players is already in a duel.");
                    return;
                }

                var duelMatch = new DuelMatch
                {
                    MatchType = duelType,
                    Player1Name = player1._alias,
                    Player2Name = player2._alias,
                    Status = DuelStatus.InProgress,
                    ArenaName = player1._arena._name,
                    StartedAt = DateTime.Now
                };

                // Initialize player IDs (will be resolved by the API)
                PopulatePlayerIds(duelMatch);

                activeDuels.TryAdd(matchKey, duelMatch);

                // Set up initial side assignments
                var sideAssignment = new DuelSideAssignment
                {
                    Player1Name = player1._alias,
                    Player2Name = player2._alias,
                    Player1Side = "player1", // player1 starts on left side
                    Player2Side = "player2", // player2 starts on right side
                    RoundNumber = 1
                };
                duelSideAssignments.TryAdd(matchKey, sideAssignment);

                // Announce duel start
                string duelTypeStr = GetDuelTypeString(duelType);

                // Send message to all players in arena with colors
                foreach (Player p in player1._arena.Players)
                {
                    p.sendMessage(-1, String.Format("!TILE DUEL STARTED: {0} vs {1}! ({2})", player1._alias, player2._alias, duelTypeStr));
                }

                // Reset shot stats for both players
                ResetPlayerShotStats(player1);
                ResetPlayerShotStats(player2);

                // Reset player states and start countdown with positioning
                RestorePlayerFromDuelingDeath(player1);
                RestorePlayerFromDuelingDeath(player2);
                
                // Start countdown and positioning sequence (async)
                Task.Run(async () => {
                    try
                    {
                        await StartDuelCountdown(player1, player2, player1._arena);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(String.Format("Error during match start countdown: {0}", ex.Message));
                    }
                });
                
                // Start first round
                StartDuelRound(duelMatch, 1);
            }
        }

        private static void StartDuelRound(DuelMatch match, int roundNumber)
        {
            var round = new DuelRound
            {
                RoundNumber = roundNumber,
                StartedAt = DateTime.Now.AddSeconds(4) // Will be updated when countdown finishes
            };

            match.Rounds.Add(round);
            Console.WriteLine(String.Format("Created round {0} for duel: {1} vs {2}", roundNumber, match.Player1Name, match.Player2Name));
        }

        private static void WarpPlayersToStartingPositions(Player player1, Player player2, DuelSideAssignment sideAssignment)
        {
            try
            {
                // Get spawn positions based on side assignments
                var player1Spawn = DUEL_SPAWN_POSITIONS[sideAssignment.Player1Side];
                var player2Spawn = DUEL_SPAWN_POSITIONS[sideAssignment.Player2Side];

                // Warp player 1 with exact positioning using Helpers.ObjectState
                WarpPlayerToExactPosition(player1, player1Spawn.Item1, player1Spawn.Item2, player1Spawn.Item3);
                
                // Warp player 2 with exact positioning using Helpers.ObjectState
                WarpPlayerToExactPosition(player2, player2Spawn.Item1, player2Spawn.Item2, player2Spawn.Item3);

                Console.WriteLine(String.Format("Warped {0} to side {1} at ({2},{3}) facing {4}", 
                    player1._alias, sideAssignment.Player1Side, player1Spawn.Item1, player1Spawn.Item2, player1Spawn.Item3));
                Console.WriteLine(String.Format("Warped {0} to side {1} at ({2},{3}) facing {4}", 
                    player2._alias, sideAssignment.Player2Side, player2Spawn.Item1, player2Spawn.Item2, player2Spawn.Item3));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error warping players: {0}", ex.Message));
            }
        }

        private static void WarpPlayerToExactPosition(Player p, short posX, short posY, byte yaw)
        {
            try
            {
                // Use the same comprehensive approach as CTF.cs for proper yaw direction
                Helpers.ObjectState newState = new Helpers.ObjectState
                {
                    positionX = (short)(posX * 16),
                    positionY = (short)(posY * 16),
                    positionZ = (short)0,
                    yaw = yaw,
                    velocityX = 0,
                    velocityY = 0,
                    energy = 600,  // Full energy for dueling
                    health = 60,   // Full health for dueling
                    direction = (Helpers.ObjectState.Direction)yaw
                };

                // Reset warp and state first (like CTF.cs example)
                p.resetWarp();
                p.resetState(false, false, false);  // Use same reset pattern as CTF.cs

                // IMPROVED: Ensure health/energy are properly set during warp
                // First warp call (like CTF.cs does) with proper HP/energy values
                p.warp(Helpers.ResetFlags.ResetAll, newState, 60, 600, yaw);
                
                // Get the player's vehicle (base vehicle or occupied vehicle)
                Vehicle vehicle = p._occupiedVehicle ?? p._baseVehicle;
                if (vehicle != null)
                {
                    // Directly update vehicle state (like CTF.cs does)
                    vehicle._state.positionX = (short)(posX * 16);
                    vehicle._state.positionY = (short)(posY * 16);
                    vehicle._state.positionZ = 0;
                    vehicle._state.velocityX = 0;
                    vehicle._state.velocityY = 0;
                    vehicle._state.yaw = yaw;
                    vehicle._state.direction = (Helpers.ObjectState.Direction)yaw;
                    vehicle._state.energy = 600;
                    vehicle._state.health = 60;
                    vehicle._tickDead = 0;
                    
                    // Update vehicle state
                    vehicle.update(false);
                    
                    // Send state update to client (like CTF.cs does)
                    SC_PlayerUpdate stateUpdate = new SC_PlayerUpdate
                    {
                        tickUpdate = Environment.TickCount,
                        player = p,
                        vehicle = vehicle,
                        itemID = 0,
                        bBot = false,
                        activeEquip = null
                    };
                    
                    stateUpdate.vehicle._state = vehicle._state;
                    p._client.sendReliable(stateUpdate);
                }
                
                // Second warp call for redundancy (like CTF.cs does)
                p.warp(Helpers.ResetFlags.ResetAll, newState, 60, 600, yaw);
                
                // Final state assignments
                p._state.health = 60;
                p._state.energy = 600;
                p._state.yaw = yaw;
                p._deathTime = 0;
                
                // Force state synchronization
                p.syncState();
                
                Console.WriteLine(String.Format("Warped {0} to ({1}, {2}) with yaw {3}, health 60, energy 600 (vehicle: {4})", 
                    p._alias, posX, posY, yaw, vehicle != null ? "yes" : "no"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error warping player {0} to exact position: {1}", p._alias, ex.Message));
            }
        }

        private static void SwapSidesAndWarp(DuelMatch match, Player player1, Player player2, int newRoundNumber)
        {
            string matchKey = String.Format("{0}_{1}", match.Player1Name, match.Player2Name);
            
            DuelSideAssignment sideAssignment;
            if (duelSideAssignments.TryGetValue(matchKey, out sideAssignment))
            {
                // Swap the sides
                string tempSide = sideAssignment.Player1Side;
                sideAssignment.Player1Side = sideAssignment.Player2Side;
                sideAssignment.Player2Side = tempSide;
                sideAssignment.RoundNumber = newRoundNumber;

                // Announce side swap
                foreach (Player p in player1._arena.Players)
                {
                    p.sendMessage(-1, String.Format("@Round {0}: Players switching sides!", newRoundNumber));
                }

                // Warp players to their new positions
                WarpPlayersToStartingPositions(player1, player2, sideAssignment);
            }
        }

        public static async Task HandlePlayerDeath(Player victim, Player killer, Arena arena)
        {
            try
            {
                // Check if either player is in a duel (more comprehensive check)
                bool victimInDuel = IsPlayerInDuel(victim._alias);
                bool killerInDuel = IsPlayerInDuel(killer._alias);
                
                Console.WriteLine(String.Format("Death event: {0} killed {1} - Victim in duel: {2}, Killer in duel: {3}", 
                    killer._alias, victim._alias, victimInDuel, killerInDuel));

                // If either player is in a duel, handle it as a dueling death
                if (victimInDuel || killerInDuel)
                {
                    // CRITICAL: Manually track kills and deaths since we're bypassing normal death handling
                    // This fixes issue #3 - normal kill/death tracking
                    try
                    {
                        // Increment killer's kills
                        killer.Kills++;
                        // Increment victim's deaths
                        victim.Deaths++;
                        
                        Console.WriteLine(String.Format("DUEL STATS: {0} kills: {1}, {2} deaths: {3}", 
                            killer._alias, killer.Kills, victim._alias, victim.Deaths));
                    }
                    catch (Exception statsEx)
                    {
                        Console.WriteLine(String.Format("Error updating kill/death stats: {0}", statsEx.Message));
                    }

                    // Check if this death is part of a duel
                    string matchKey1 = String.Format("{0}_{1}", victim._alias, killer._alias);
                    string matchKey2 = String.Format("{0}_{1}", killer._alias, victim._alias);

                    DuelMatch duel = null;
                    if (activeDuels.TryGetValue(matchKey1, out duel) || activeDuels.TryGetValue(matchKey2, out duel))
                    {
                        if (duel.Status == DuelStatus.InProgress)
                        {
                            // CRITICAL: Capture HP values BEFORE any state changes
                            // This fixes issue #1 - HP tracking for UI display
                            int killerHpAtDeath = Math.Max(0, Math.Min(60, (int)killer._state.health));
                            int victimHpAtDeath = 0; // Victim is dead
                            
                            // Also capture energy for proper restoration later
                            int killerEnergyAtDeath = Math.Max(0, Math.Min(600, (int)killer._state.energy));
                            
                            Console.WriteLine(String.Format("Active duel death: {0} killed {1} - Killer HP: {2}, Energy: {3}, Victim HP: {4}", 
                                killer._alias, victim._alias, killerHpAtDeath, killerEnergyAtDeath, victimHpAtDeath));

                            // Get current round
                            var currentRound = duel.Rounds.LastOrDefault();
                            if (currentRound != null)
                            {
                                // CRITICAL: Capture shot statistics BEFORE completing the round
                                // This fixes the accuracy issue by getting stats at the exact moment of death
                                int killerShotsFired = GetShotsFired(killer);
                                int killerShotsHit = GetShotsHit(killer);
                                
                                // Validate shot statistics to prevent impossible values
                                killerShotsHit = Math.Min(killerShotsHit, killerShotsFired);
                                
                                Console.WriteLine(String.Format("SHOT STATS AT DEATH: {0} fired {1}, hit {2} (accuracy: {3:F1}%)", 
                                    killer._alias, killerShotsFired, killerShotsHit, 
                                    killerShotsFired > 0 ? (double)killerShotsHit / killerShotsFired * 100 : 0));
                                
                                // Record the kill with proper HP tracking
                                var kill = new DuelKill
                                {
                                    KillerName = killer._alias,
                                    VictimName = victim._alias,
                                    WeaponUsed = GetPlayerWeapon(killer),
                                    DamageDealt = 60, // Assuming full damage for death
                                    VictimHpBefore = victimHpAtDeath,
                                    VictimHpAfter = 0,
                                    ShotsFired = killerShotsFired,
                                    ShotsHit = killerShotsHit,
                                    IsDoubleHit = CheckForDoubleHit(killer._alias),
                                    IsTripleHit = CheckForTripleHit(killer._alias),
                                    KillTimestamp = DateTime.Now
                                };

                                currentRound.Kills.Add(kill);

                                // Complete the round with the captured HP values
                                // This fixes issue #1 - proper HP values for JSON/UI
                                await CompleteRound(duel, currentRound, killer._alias, victim._alias, 
                                    killerHpAtDeath, victimHpAtDeath, arena, killerEnergyAtDeath);
                            }
                        }
                        else
                        {
                            Console.WriteLine(String.Format("Duel found but not in progress (status: {0}) - applying restoration for {1}", 
                                duel.Status, victim._alias));
                            // Restore victim to prevent A1 warp
                            RestorePlayerFromDuelingDeath(victim, 60, 600);
                        }
                    }
                    else
                    {
                        Console.WriteLine(String.Format("Player {0} marked as in duel but no active duel found - applying restoration", 
                            victim._alias));
                        // Fallback: if player is marked as in duel but no active duel found, still restore them
                        RestorePlayerFromDuelingDeath(victim, 60, 600);
                    }
                }
                else
                {
                    Console.WriteLine(String.Format("Neither player in duel - allowing normal death handling for {0}", victim._alias));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in HandlePlayerDeath: {0}", ex.Message));
                
                // Emergency fallback: if there's any error and the victim might be a dueler, reset them
                try
                {
                    if (IsPlayerInDuel(victim._alias))
                    {
                        Console.WriteLine(String.Format("Emergency fallback reset for dueling player {0}", victim._alias));
                        RestorePlayerFromDuelingDeath(victim, 60, 600);
                    }
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine(String.Format("Emergency fallback failed: {0}", fallbackEx.Message));
                }
            }
        }

        private static void RestorePlayerFromDuelingDeath(Player player, int targetHealth = 60, int targetEnergy = 600)
        {
            try
            {
                // CRITICAL: Enhanced restoration to fix issue #2 - A1 warping with low energy
                // This method now properly restores health and energy to prevent weird warping
                
                // First, immediately set health and energy to prevent death state
                player._state.health = (short)targetHealth;
                player._state.energy = (short)targetEnergy;
                
                // Clear death timer immediately - this is crucial to prevent A1 warp
                player._deathTime = 0;
                
                // Get the player's vehicle and update its state too
                Vehicle vehicle = player._occupiedVehicle ?? player._baseVehicle;
                if (vehicle != null)
                {
                    vehicle._state.health = (short)targetHealth;
                    vehicle._state.energy = (short)targetEnergy;
                    vehicle._tickDead = 0; // Clear death tick
                }
                
                // Reset warp to clear any pending warp operations
                player.resetWarp();
                
                // Force the player to be marked as alive with proper state
                player.resetState(false, false, false);  // Don't reset position/inventory, just state flags
                
                // Immediately sync the state to client to prevent visual death
                player.syncState();
                
                Console.WriteLine(String.Format("Successfully restored dueling player {0} from death (Health: {1}, Energy: {2})", 
                    player._alias, targetHealth, targetEnergy));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error restoring dueling player {0} from death: {1}", player._alias, ex.Message));
            }
        }

        private static async Task StartDuelCountdown(Player player1, Player player2, Arena arena)
        {
            try
            {
                // Declare position variables outside the if/else blocks so they can be used for center calculation
                Tuple<short, short, byte> player1Pos;
                Tuple<short, short, byte> player2Pos;
                
                // Check for side assignments to determine correct positions
                string matchKey = String.Format("{0}_{1}", player1._alias, player2._alias);
                DuelSideAssignment sideAssignment;
                
                if (duelSideAssignments.TryGetValue(matchKey, out sideAssignment))
                {
                    // Use side assignments to determine positions
                    string player1Side = (sideAssignment.Player1Name == player1._alias) ? sideAssignment.Player1Side : sideAssignment.Player2Side;
                    string player2Side = (sideAssignment.Player1Name == player1._alias) ? sideAssignment.Player2Side : sideAssignment.Player1Side;
                    
                    player1Pos = DUEL_SPAWN_POSITIONS[player1Side];
                    player2Pos = DUEL_SPAWN_POSITIONS[player2Side];
                    
                    WarpPlayerToExactPosition(player1, player1Pos.Item1, player1Pos.Item2, player1Pos.Item3);
                    WarpPlayerToExactPosition(player2, player2Pos.Item1, player2Pos.Item2, player2Pos.Item3);
                    
                    Console.WriteLine(String.Format("Countdown warp with side assignments: {0} to {1}, {2} to {3}", 
                        player1._alias, player1Side, player2._alias, player2Side));
                }
                else
                {
                    // Fallback to default positions if no side assignments
                    player1Pos = DUEL_SPAWN_POSITIONS["player1"];
                    player2Pos = DUEL_SPAWN_POSITIONS["player2"];
                    
                    WarpPlayerToExactPosition(player1, player1Pos.Item1, player1Pos.Item2, player1Pos.Item3);
                    WarpPlayerToExactPosition(player2, player2Pos.Item1, player2Pos.Item2, player2Pos.Item3);
                    
                    Console.WriteLine(String.Format("Countdown warp with default positions: {0} to player1, {1} to player2", 
                        player1._alias, player2._alias));
                }
                
                // Calculate center position for countdown text (between the two players)
                short centerX = (short)((player1Pos.Item1 + player2Pos.Item1) / 2);
                short centerY = (short)((player1Pos.Item2 + player2Pos.Item2) / 2);
                
                // Countdown sequence: 3, 2, 1, Go!
                string[] countdownTexts = { "3", "2", "1", "Go!" };
                
                for (int i = 0; i < countdownTexts.Length; i++)
                {
                    // Create countdown text explosion
                    CreateDuelCountdownExplosion(arena, countdownTexts[i], centerX, centerY, 16, player1);
                    
                    // Send countdown message to both players
                    if (countdownTexts[i] == "Go!")
                    {
                        player1.sendMessage(-1, String.Format("~{0}", countdownTexts[i]));
                        player2.sendMessage(-1, String.Format("~{0}", countdownTexts[i]));
                        
                        // Update the round start time when "Go!" is called
                        UpdateRoundStartTime(player1, player2);
                    }
                    else
                    {
                        player1.sendMessage(-1, String.Format("${0}", countdownTexts[i]));
                        player2.sendMessage(-1, String.Format("${0}", countdownTexts[i]));
                    }
                    
                    // Wait 1 second between countdown numbers
                    if (i < countdownTexts.Length - 1)
                    {
                        await Task.Delay(1000);
                    }
                }
                
                Console.WriteLine(String.Format("Completed duel countdown for {0} vs {1}", player1._alias, player2._alias));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error during duel countdown: {0}", ex.Message));
            }
        }

        private static void UpdateRoundStartTime(Player player1, Player player2)
        {
            try
            {
                // Find the active duel for these players
                string matchKey1 = String.Format("{0}_{1}", player1._alias, player2._alias);
                string matchKey2 = String.Format("{0}_{1}", player2._alias, player1._alias);

                DuelMatch duel = null;
                if (activeDuels.TryGetValue(matchKey1, out duel) || activeDuels.TryGetValue(matchKey2, out duel))
                {
                    if (duel.Status == DuelStatus.InProgress)
                    {
                        // Get the current round and update its start time
                        var currentRound = duel.Rounds.LastOrDefault();
                        if (currentRound != null)
                        {
                            currentRound.StartedAt = DateTime.Now;
                            Console.WriteLine(String.Format("Updated round {0} start time for duel: {1} vs {2}", 
                                currentRound.RoundNumber, duel.Player1Name, duel.Player2Name));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error updating round start time: {0}", ex.Message));
            }
        }

        private static void CreateDuelCountdownExplosion(Arena arena, string text, short x, short y, short z, Player from)
        {
            try
            {
                // Use the same approach as PhraseExplosionManager for reliable explosions
                ItemInfo.Projectile countdownProjectile = null;
                
                // Try different projectile names for numbers
                if (text == "3")
                {
                    // Try multiple possible names for the number 3
                    countdownProjectile = AssetManager.Manager.getItemByName("three") as ItemInfo.Projectile;
                    if (countdownProjectile == null)
                        countdownProjectile = AssetManager.Manager.getItemByName("3") as ItemInfo.Projectile;
                    if (countdownProjectile == null)
                        countdownProjectile = AssetManager.Manager.getItemByName("Three") as ItemInfo.Projectile;
                }
                else if (text == "2")
                {
                    countdownProjectile = AssetManager.Manager.getItemByName("two") as ItemInfo.Projectile;
                    if (countdownProjectile == null)
                        countdownProjectile = AssetManager.Manager.getItemByName("2") as ItemInfo.Projectile;
                    if (countdownProjectile == null)
                        countdownProjectile = AssetManager.Manager.getItemByName("Two") as ItemInfo.Projectile;
                }
                else if (text == "1")
                {
                    countdownProjectile = AssetManager.Manager.getItemByName("one") as ItemInfo.Projectile;
                    if (countdownProjectile == null)
                        countdownProjectile = AssetManager.Manager.getItemByName("1") as ItemInfo.Projectile;
                    if (countdownProjectile == null)
                        countdownProjectile = AssetManager.Manager.getItemByName("One") as ItemInfo.Projectile;
                }
                else if (text == "Go!")
                {
                    // For "Go!" create letter explosions for G and O
                    CreateLetterExplosion(arena, 'G', (short)(x - 20), y, z, from);
                    CreateLetterExplosion(arena, 'o', (short)(x + 20), y, z, from);
                    return;
                }
                
                if (countdownProjectile != null)
                {
                    // Create the explosion projectile using SC_Projectile
                    SC_Projectile countdownExplosion = new SC_Projectile
                    {
                        projectileID = (short)countdownProjectile.id,
                        playerID = (ushort)from._id,
                        posX = x,
                        posY = y,
                        posZ = z,
                        yaw = from._state.yaw
                    };
                    
                    // Send the explosion to all players in the arena
                    foreach (Player p in arena.Players)
                    {
                        if (p._client != null)
                        {
                            p._client.sendReliable(countdownExplosion);
                        }
                    }
                    
                    Console.WriteLine(String.Format("Created countdown explosion: '{0}' (ID: {1}) at ({2}, {3})", text, countdownProjectile.id, x, y));
                }
                else
                {
                    Console.WriteLine(String.Format("Could not find projectile for countdown text: {0} - trying fallback explosion", text));
                    
                    // Fallback: try to use a generic explosion projectile
                    var fallbackProjectile = AssetManager.Manager.getItemByName("Explosion") as ItemInfo.Projectile;
                    if (fallbackProjectile == null)
                        fallbackProjectile = AssetManager.Manager.getItemByName("explosion") as ItemInfo.Projectile;
                    if (fallbackProjectile == null)
                        fallbackProjectile = AssetManager.Manager.getItemByName("boom") as ItemInfo.Projectile;
                        
                    if (fallbackProjectile != null)
                    {
                        SC_Projectile fallbackExplosion = new SC_Projectile
                        {
                            projectileID = (short)fallbackProjectile.id,
                            playerID = (ushort)from._id,
                            posX = x,
                            posY = y,
                            posZ = z,
                            yaw = from._state.yaw
                        };
                        
                        foreach (Player p in arena.Players)
                        {
                            if (p._client != null)
                            {
                                p._client.sendReliable(fallbackExplosion);
                            }
                        }
                        
                        Console.WriteLine(String.Format("Used fallback explosion for countdown: '{0}'", text));
                    }
                    else
                    {
                        Console.WriteLine(String.Format("No explosion projectiles found for countdown: {0}", text));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error creating countdown explosion: {0}", ex.Message));
            }
        }

        private static void CreateLetterExplosion(Arena arena, char letter, short x, short y, short z, Player from)
        {
            try
            {
                ItemInfo.Projectile letterWep = null;
                
                // Get letter projectile
                if (letter == 'G' || letter == 'g')
                {
                    letterWep = AssetManager.Manager.getItemByName("G") as ItemInfo.Projectile;
                }
                else if (letter == 'O' || letter == 'o')
                {
                    letterWep = AssetManager.Manager.getItemByName("O") as ItemInfo.Projectile;
                }
                
                if (letterWep != null)
                {
                    SC_Projectile letterExplosion = new SC_Projectile
                    {
                        projectileID = (short)letterWep.id,
                        playerID = (ushort)from._id,
                        posX = x,
                        posY = y,
                        posZ = z,
                        yaw = from._state.yaw
                    };
                    
                    // Send the explosion to all players
                    foreach (Player p in arena.Players)
                    {
                        p._client.sendReliable(letterExplosion);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error creating letter explosion for '{0}': {1}", letter, ex.Message));
            }
        }

        private static async Task CompleteRound(DuelMatch match, DuelRound round, 
            string winnerName, string loserName, int winnerHp, int loserHp, Arena arena, int winnerEnergy = 600)
        {
            round.WinnerName = winnerName;
            round.LoserName = loserName;
            round.WinnerHpLeft = winnerHp;
            round.LoserHpLeft = loserHp;
            round.CompletedAt = DateTime.Now;
            round.DurationSeconds = (int)(round.CompletedAt - round.StartedAt).TotalSeconds;

            // Update match scores
            if (winnerName == match.Player1Name)
            {
                match.Player1RoundsWon++;
                Console.WriteLine(String.Format("SCORING: {0} wins round {1}, Player1RoundsWon now: {2}", winnerName, round.RoundNumber, match.Player1RoundsWon));
            }
            else
            {
                match.Player2RoundsWon++;
                Console.WriteLine(String.Format("SCORING: {0} wins round {1}, Player2RoundsWon now: {2}", winnerName, round.RoundNumber, match.Player2RoundsWon));
            }

            // Announce round result with colors and current score
            string currentScore = String.Format("({0}-{1})", match.Player1RoundsWon, match.Player2RoundsWon);
            Console.WriteLine(String.Format("SCORING: Current match score: {0} vs {1} = {2}", match.Player1Name, match.Player2Name, currentScore));
            
            foreach (Player p in arena.Players)
            {
                p.sendMessage(-1, String.Format("@Round {0}: {1} defeats {2} ({3}HP left)", 
                    round.RoundNumber, winnerName, loserName, winnerHp));
                p.sendMessage(-1, String.Format("Current Score: {0} {1}", currentScore, GetDuelTypeString(match.MatchType)));
            }

            // Check if match is complete
            int roundsToWin = GetRoundsToWin(match.MatchType);
            if (match.Player1RoundsWon >= roundsToWin || match.Player2RoundsWon >= roundsToWin)
            {
                match.WinnerName = match.Player1RoundsWon >= roundsToWin ? match.Player1Name : match.Player2Name;
                await CompleteDuel(match, arena);
            }
            else
            {
                // Get players for next round
                Player player1 = arena.Players.FirstOrDefault(p => p._alias == match.Player1Name);
                Player player2 = arena.Players.FirstOrDefault(p => p._alias == match.Player2Name);

                if (player1 != null && player2 != null)
                {
                    // CRITICAL: Reset shot stats for both players BEFORE next round
                    // This fixes the accuracy tracking issue
                    ResetPlayerShotStats(player1);
                    ResetPlayerShotStats(player2);
                    Console.WriteLine(String.Format("Reset shot stats for both players before round {0}", match.Rounds.Count + 1));
                    
                    // IMPROVED: Restore both players with proper health/energy before warping
                    // This fixes issue #2 - prevents A1 warping and ensures full energy
                    RestorePlayerFromDuelingDeath(player1, 60, 600);
                    RestorePlayerFromDuelingDeath(player2, 60, 600);
                    
                    // Wait a moment for state restoration to complete
                    await Task.Delay(100);

                    // Check if this is a tile-based duel (has side assignments)
                    string matchKey = String.Format("{0}_{1}", match.Player1Name, match.Player2Name);
                    if (duelSideAssignments.ContainsKey(matchKey))
                    {
                        // Swap sides for next round and warp
                        int nextRoundNumber = match.Rounds.Count + 1;
                        SwapSidesAndWarp(match, player1, player2, nextRoundNumber);
                        Console.WriteLine(String.Format("Swapped sides for round {0}", nextRoundNumber));
                    }
                    else
                    {
                        // For non-tile duels, warp to default positions
                        var player1Pos = DUEL_SPAWN_POSITIONS["player1"];
                        var player2Pos = DUEL_SPAWN_POSITIONS["player2"];
                        WarpPlayerToExactPosition(player1, player1Pos.Item1, player1Pos.Item2, player1Pos.Item3);
                        WarpPlayerToExactPosition(player2, player2Pos.Item1, player2Pos.Item2, player2Pos.Item3);
                        Console.WriteLine(String.Format("Warped both players to default positions for next round"));
                    }
                    
                    // Start countdown for next round after a brief delay
                    Task.Run(async () => {
                        try
                        {
                            await Task.Delay(1000); // Longer delay to ensure proper state restoration
                            await StartDuelCountdown(player1, player2, arena);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(String.Format("Error during next round countdown: {0}", ex.Message));
                        }
                    });
                }

                // Start next round
                StartDuelRound(match, match.Rounds.Count + 1);
            }
        }

        private static int GetRoundsToWin(DuelType duelType)
        {
            if (duelType == DuelType.RankedBo3)
                return 2; // Best of 3
            else if (duelType == DuelType.RankedBo5)
                return 3; // Best of 5 (first to 3)
            else
                return 1; // Unranked (single round)
        }

        private static async Task CompleteDuel(DuelMatch match, Arena arena)
        {
            // Thread-safe duel completion
            lock (duelLock)
            {
                match.Status = DuelStatus.Completed;
                match.CompletedAt = DateTime.Now;
                
                // CRITICAL: Set total rounds played properly
                match.TotalRounds = match.Rounds != null ? match.Rounds.Count : 0;
                
                Console.WriteLine(String.Format("MATCH COMPLETION: {0} vs {1} - Final Scores: {2}-{3}, Total Rounds: {4}", 
                    match.Player1Name, match.Player2Name, match.Player1RoundsWon, match.Player2RoundsWon, match.TotalRounds));

                // Set winner ID based on winner name
                if (match.WinnerName == match.Player1Name)
                    match.WinnerId = match.Player1Id;
                else if (match.WinnerName == match.Player2Name)
                    match.WinnerId = match.Player2Id;

                // Announce match result with special victory formatting
                string loserName = match.WinnerName == match.Player1Name ? match.Player2Name : match.Player1Name;
                string finalScore = String.Format("({0}-{1})", match.Player1RoundsWon, match.Player2RoundsWon);
                
                foreach (Player p in arena.Players)
                {
                    p.sendMessage(-1, String.Format("● {0} WINS the {1} duel against {2}! {3}", 
                        match.WinnerName, GetDuelTypeString(match.MatchType), loserName, finalScore));
                    p.sendMessage(-1, String.Format("!{0} has drawn first blood.", match.WinnerName));
                }

                Console.WriteLine(String.Format("Duel completed, keeping in activeDuels temporarily to prevent A1 warp: {0} vs {1}", 
                    match.Player1Name, match.Player2Name));
            }

            // Reset both players to proper dueling positions BEFORE removing from activeDuels
            Player player1 = arena.Players.FirstOrDefault(p => p._alias == match.Player1Name);
            Player player2 = arena.Players.FirstOrDefault(p => p._alias == match.Player2Name);
            
            if (player1 != null && player2 != null)
            {
                // IMPROVED: Restore both players with full health/energy to prevent A1 warping
                RestorePlayerFromDuelingDeath(player1, 60, 600);
                RestorePlayerFromDuelingDeath(player2, 60, 600);
                
                // Wait for state restoration to complete
                await Task.Delay(150);
                
                // Warp both players to neutral dueling mat positions (no side assignments after match)
                var player1Pos = DUEL_SPAWN_POSITIONS["player1"];
                var player2Pos = DUEL_SPAWN_POSITIONS["player2"];
                WarpPlayerToExactPosition(player1, player1Pos.Item1, player1Pos.Item2, player1Pos.Item3);
                WarpPlayerToExactPosition(player2, player2Pos.Item1, player2Pos.Item2, player2Pos.Item3);
                
                Console.WriteLine(String.Format("Reset and warped both players after duel completion: {0} vs {1}", 
                    match.Player1Name, match.Player2Name));
            }

            // Send match data to website first
            await SendDuelMatchToWebsite(match);

            // DELAY cleanup to prevent timing issues - use Task.Run to delay cleanup
            Task.Run(async () => {
                try
                {
                    // Wait 3 seconds to ensure all state changes are processed
                    await Task.Delay(3000);
                    
                    lock (duelLock)
                    {
                        Console.WriteLine(String.Format("Delayed cleanup: removing duel {0} vs {1} from activeDuels", 
                            match.Player1Name, match.Player2Name));
                        
                        // Remove from active duels and clean up side assignments
                        string matchKey = String.Format("{0}_{1}", match.Player1Name, match.Player2Name);
                        DuelMatch removedMatch;
                        activeDuels.TryRemove(matchKey, out removedMatch);
                        
                        // Clean up side assignments for tile-based duels
                        DuelSideAssignment removedSideAssignment;
                        duelSideAssignments.TryRemove(matchKey, out removedSideAssignment);

                        // Clean up shot stats for both players
                        PlayerShotStats removedStats1, removedStats2;
                        playerShotStats.TryRemove(match.Player1Name, out removedStats1);
                        playerShotStats.TryRemove(match.Player2Name, out removedStats2);
                        
                        Console.WriteLine(String.Format("Cleanup completed for duel: {0} vs {1}", 
                            match.Player1Name, match.Player2Name));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(String.Format("Error during delayed duel cleanup: {0}", ex.Message));
                }
            });
        }

        private static async Task ForfeitDuel(Player player)
        {
            string playerName = player._alias;
            DuelMatch duel = null;

            // Find the player's active duel
            foreach (var kvp in activeDuels)
            {
                if ((kvp.Value.Player1Name == playerName || kvp.Value.Player2Name == playerName) && 
                    kvp.Value.Status == DuelStatus.InProgress)
                {
                    duel = kvp.Value;
                    break;
                }
            }

            if (duel == null)
            {
                player.sendMessage(-1, "You are not currently in a duel.");
                return;
            }

            string opponentName = duel.Player1Name == playerName ? duel.Player2Name : duel.Player1Name;
            
            duel.WinnerName = opponentName;
            duel.Status = DuelStatus.Abandoned;
            duel.CompletedAt = DateTime.Now;

            foreach (Player p in player._arena.Players)
            {
                p.sendMessage(-1, String.Format("{0} forfeits the duel. {1} wins by forfeit!", playerName, opponentName));
            }

            // Send match data to website (as abandoned)
            await SendDuelMatchToWebsite(duel);

            // Remove from active duels
            string matchKey = String.Format("{0}_{1}", duel.Player1Name, duel.Player2Name);
            DuelMatch removedMatch;
            activeDuels.TryRemove(matchKey, out removedMatch);
        }

        public static async Task HandleTileStep(Player player, short x, short y)
        {
            try
            {
                // Check if player stepped on a ranked dueling tile (with radius detection)
                foreach (var tile in DUELING_TILES)
                {
                    short tileX = tile.Value.Item1;
                    short tileY = tile.Value.Item2;
                    
                    // Check if player is within 1 tile radius (forming a 3x3 grid around the center)
                    if (Math.Abs(x - tileX) <= 1 && Math.Abs(y - tileY) <= 1)
                    {
                        DuelType duelType = tile.Key == "ranked_bo3" ? DuelType.RankedBo3 : DuelType.RankedBo5;
                        await HandleRankedTileStep(player, duelType);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in HandleTileStep: {0}", ex.Message));
            }
        }

        private static async Task HandleRankedTileStep(Player player, DuelType duelType)
        {
            // Check if player is already in a duel
            if (IsPlayerInDuel(player._alias))
            {
                player.sendMessage(-1, "You are already in a duel.");
                return;
            }

            string tileKey = duelType == DuelType.RankedBo3 ? "ranked_bo3" : "ranked_bo5";
            string duelTypeStr = GetDuelTypeString(duelType);

            // Remove expired waiting players
            CleanupExpiredTileWaiters();

            // Check if there's already someone waiting on this tile type
            var waitingPlayer = playersOnTiles.Values.FirstOrDefault(w => 
                w.DuelType == duelType && w.IsActive && w.PlayerName != player._alias);

            if (waitingPlayer != null)
            {
                // Found a match! Start the duel immediately
                Player opponent = player._arena.Players.FirstOrDefault(p => 
                    p._alias.Equals(waitingPlayer.PlayerName, StringComparison.OrdinalIgnoreCase));

                if (opponent != null && !IsPlayerInDuel(opponent._alias))
                {
                    // Remove both players from waiting
                    TileWaitingPlayer removedPlayer;
                    playersOnTiles.TryRemove(waitingPlayer.PlayerName, out removedPlayer);
                    playersOnTiles.TryRemove(player._alias, out removedPlayer);

                    // Announce the match
                    foreach (Player p in player._arena.Players)
                    {
                        p.sendMessage(-1, String.Format("!AUTO-MATCH: {0} vs {1} ({2})!", 
                            player._alias, opponent._alias, duelTypeStr));
                    }

                    // Start the duel with proper warping
                    await StartTileBasedDuel(player, opponent, duelType);
                }
                else
                {
                    // Opponent left, remove them and add current player to waiting
                    TileWaitingPlayer removedPlayer;
                    playersOnTiles.TryRemove(waitingPlayer.PlayerName, out removedPlayer);
                    AddPlayerToTileWaiting(player, duelType, duelTypeStr);
                }
            }
            else
            {
                // No one waiting, add this player to waiting list
                AddPlayerToTileWaiting(player, duelType, duelTypeStr);
            }
        }

        private static void AddPlayerToTileWaiting(Player player, DuelType duelType, string duelTypeStr)
        {
            var waitingPlayer = new TileWaitingPlayer
            {
                PlayerName = player._alias,
                DuelType = duelType,
                WaitStartTime = DateTime.Now
            };

            playersOnTiles.AddOrUpdate(player._alias, waitingPlayer, (key, existing) => waitingPlayer);

            player.sendMessage(-1, String.Format("@Waiting for opponent for {0}...", duelTypeStr));
            player.sendMessage(-1, "Step off the tile to cancel or wait for another player to join.");
        }

        private static void CleanupExpiredTileWaiters()
        {
            var expiredPlayers = playersOnTiles.Where(kvp => !kvp.Value.IsActive).Select(kvp => kvp.Key).ToList();
            foreach (var playerName in expiredPlayers)
            {
                TileWaitingPlayer removedPlayer;
                playersOnTiles.TryRemove(playerName, out removedPlayer);
            }
        }

        private static bool IsPlayerInDuel(string playerName)
        {
            foreach (var duel in activeDuels.Values)
            {
                if ((duel.Player1Name == playerName || duel.Player2Name == playerName) && 
                    duel.Status == DuelStatus.InProgress)
                {
                    return true;
                }
            }
            return false;
        }

        public static void HandleTileLeave(Player player, short x, short y)
        {
            try
            {
                // Check if player was waiting on a tile and is now leaving
                TileWaitingPlayer waitingPlayer;
                if (playersOnTiles.TryGetValue(player._alias, out waitingPlayer))
                {
                    // Check if they're moving away from their tile
                    bool stillOnTile = false;
                    foreach (var tile in DUELING_TILES)
                    {
                        if (waitingPlayer.DuelType == (tile.Key == "ranked_bo3" ? DuelType.RankedBo3 : DuelType.RankedBo5))
                        {
                            short tileX = tile.Value.Item1;
                            short tileY = tile.Value.Item2;
                            
                            // Check if still within radius
                            if (Math.Abs(x - tileX) <= 1 && Math.Abs(y - tileY) <= 1)
                            {
                                stillOnTile = true;
                                break;
                            }
                        }
                    }

                    if (!stillOnTile)
                    {
                        // Player left the tile, remove them from waiting
                        TileWaitingPlayer removedPlayer;
                        playersOnTiles.TryRemove(player._alias, out removedPlayer);
                        player.sendMessage(-1, "!Cancelled waiting for ranked duel.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in HandleTileLeave: {0}", ex.Message));
            }
        }

        private static bool IsOnRankedTile(Player player)
        {
            // Check if player is currently on any ranked tile
            short x = (short)(player._state.positionX / 16);
            short y = (short)(player._state.positionY / 16);

            foreach (var tile in DUELING_TILES)
            {
                short tileX = tile.Value.Item1;
                short tileY = tile.Value.Item2;
                
                if (Math.Abs(x - tileX) <= 1 && Math.Abs(y - tileY) <= 1)
                {
                    return true;
                }
            }
            return false;
        }

        private static string GetDuelTypeString(DuelType duelType)
        {
            if (duelType == DuelType.RankedBo3)
                return "Ranked Bo3";
            else if (duelType == DuelType.RankedBo5)
                return "Ranked Bo5";
            else
                return "Unranked";
        }

        private static string GetPlayerWeapon(Player player)
        {
            // Dueling is always done with assault rifles
            return "Assault Rifle";
        }

        private static int GetShotsFired(Player player)
        {
            // Get shots fired from our custom tracking system
            try
            {
                PlayerShotStats stats;
                if (playerShotStats.TryGetValue(player._alias, out stats))
                {
                    return stats.ShotsFired;
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error getting shots fired for {0}: {1}", player._alias, ex.Message));
                return 0;
            }
        }

        private static int GetShotsHit(Player player)
        {
            // Get shots hit from our custom tracking system
            try
            {
                PlayerShotStats stats;
                if (playerShotStats.TryGetValue(player._alias, out stats))
                {
                    return stats.ShotsHit;
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error getting shots hit for {0}: {1}", player._alias, ex.Message));
                return 0;
            }
        }

        private static int GetPlayerHealth(Player player)
        {
            // Return actual player health, capped at 60 for dueling
            return Math.Min((int)player._state.health, 60);
        }

        private static bool CheckForDoubleHit(string playerName)
        {
            List<DateTime> hits;
            if (!recentDamageHits.TryGetValue(playerName, out hits))
                return false;

            var now = DateTime.Now;
            
            // Get recent hits (within 2 seconds)
            var recentHits = hits.Where(hit => now.Subtract(hit).TotalSeconds <= 2).ToList();
            
            // Check for rapid successive hits (within 500ms)
            if (recentHits.Count >= 2)
            {
                var lastTwoHits = recentHits.Skip(Math.Max(0, recentHits.Count - 2)).ToList();
                return lastTwoHits.All(hit => now.Subtract(hit).TotalMilliseconds <= 500);
            }
            return false;
        }

        private static bool CheckForTripleHit(string playerName)
        {
            List<DateTime> hits;
            if (!recentDamageHits.TryGetValue(playerName, out hits))
                return false;

            var now = DateTime.Now;
            
            // Get recent hits (within 2 seconds)
            var recentHits = hits.Where(hit => now.Subtract(hit).TotalSeconds <= 2).ToList();
            
            // Check for rapid successive hits (within 750ms)
            if (recentHits.Count >= 3)
            {
                var lastThreeHits = recentHits.Skip(Math.Max(0, recentHits.Count - 3)).ToList();
                return lastThreeHits.All(hit => now.Subtract(hit).TotalMilliseconds <= 750);
            }
            return false;
        }

        public static void TrackDamageHit(string playerName)
        {
            // Thread-safe damage hit tracking
            recentDamageHits.AddOrUpdate(
                playerName,
                new List<DateTime> { DateTime.Now },
                (key, existingList) => 
                {
                    existingList.Add(DateTime.Now);
                    return existingList;
                }
            );
        }

        public static void TrackShotFired(Player player)
        {
            // Track shots fired for dueling players
            try
            {
                if (IsPlayerInDuel(player._alias))
                {
                    PlayerShotStats stats;
                    if (!playerShotStats.TryGetValue(player._alias, out stats))
                    {
                        stats = new PlayerShotStats { PlayerName = player._alias };
                        playerShotStats[player._alias] = stats;
                    }
                    stats.ShotsFired++;
                    
                    // Debug output every 5 shots to track accumulation
                    if (stats.ShotsFired % 5 == 0)
                    {
                        Console.WriteLine(String.Format("SHOT TRACKING: {0} fired {1} shots, hit {2} (accuracy: {3:F1}%)", 
                            player._alias, stats.ShotsFired, stats.ShotsHit, stats.Accuracy));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error tracking shot fired for {0}: {1}", player._alias, ex.Message));
            }
        }

        public static void TrackShotHit(Player player)
        {
            // Track shots hit for dueling players
            try
            {
                if (IsPlayerInDuel(player._alias))
                {
                    PlayerShotStats stats;
                    if (!playerShotStats.TryGetValue(player._alias, out stats))
                    {
                        stats = new PlayerShotStats { PlayerName = player._alias };
                        playerShotStats[player._alias] = stats;
                    }
                    stats.ShotsHit++;
                    
                    // Validate that hits don't exceed shots fired
                    if (stats.ShotsHit > stats.ShotsFired)
                    {
                        Console.WriteLine(String.Format("WARNING: Shot hit count ({0}) exceeds shots fired ({1}) for {2} - correcting", 
                            stats.ShotsHit, stats.ShotsFired, player._alias));
                        stats.ShotsHit = stats.ShotsFired;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error tracking shot hit for {0}: {1}", player._alias, ex.Message));
            }
        }

        public static void ResetPlayerShotStats(Player player)
        {
            // Reset shot stats for a player (called at start of duel)
            try
            {
                var stats = new PlayerShotStats { PlayerName = player._alias };
                playerShotStats[player._alias] = stats;
                Console.WriteLine(String.Format("Reset shot stats for {0} - ShotsFired: {1}, ShotsHit: {2}", 
                    player._alias, stats.ShotsFired, stats.ShotsHit));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error resetting shot stats for {0}: {1}", player._alias, ex.Message));
            }
        }

        private static void RemoveExpiredChallenges()
        {
            // Note: This should be called within challengeLock
            var validChallenges = activeChallenges.Where(c => c.IsActive).ToList();
            
            activeChallenges.Clear();
            foreach (var challenge in validChallenges)
            {
                activeChallenges.Add(challenge);
            }
        }

        private static void PopulatePlayerIds(DuelMatch match)
        {
            try
            {
                // For now, just set IDs to null - they will be resolved by the API
                // This avoids async/await compatibility issues with older C# versions
                match.Player1Id = null;
                match.Player2Id = null;
                match.WinnerId = null;

                Console.WriteLine(String.Format("Player ID resolution: {0}(will be resolved by API) vs {1}(will be resolved by API)", 
                    match.Player1Name ?? "unknown",
                    match.Player2Name ?? "unknown"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in PopulatePlayerIds: {0}", ex.Message));
            }
        }



        private static async Task ShowPlayerDuelStats(Player requestingPlayer, string targetName)
        {
            // TODO: Fetch stats from website API
            requestingPlayer.sendMessage(-1, String.Format("Fetching duel stats for {0}...", targetName));
            requestingPlayer.sendMessage(-1, "Visit https://freeinf.org/dueling for detailed statistics!");
        }

        private static async Task SendDuelMatchToWebsite(DuelMatch match)
        {
            try
            {
                string jsonString = BuildMatchJsonString(match);
                Console.WriteLine(String.Format("=== SENDING DUEL MATCH TO WEBSITE ==="));
                Console.WriteLine(String.Format("Endpoint: {0} ({1})", 
                    USE_LOCAL_API ? "LOCAL" : "PRODUCTION", DUELING_API_ENDPOINT));
                Console.WriteLine(String.Format("Match: {0} vs {1}", match.Player1Name, match.Player2Name));
                Console.WriteLine(String.Format("Scores: {0}-{1}, Total Rounds: {2}", 
                    match.Player1RoundsWon, match.Player2RoundsWon, match.TotalRounds));
                Console.WriteLine(String.Format("Status: {0}, Winner: {1}", match.Status, match.WinnerName));
                Console.WriteLine(String.Format("Rounds Count: {0}", match.Rounds.Count));
                
                if (match.Rounds != null && match.Rounds.Count > 0)
                {
                    for (int i = 0; i < match.Rounds.Count; i++)
                    {
                        var round = match.Rounds[i];
                        Console.WriteLine(String.Format("  Round {0}: {1} beats {2} ({3}HP vs {4}HP)", 
                            round.RoundNumber, round.WinnerName, round.LoserName, 
                            round.WinnerHpLeft, round.LoserHpLeft));
                    }
                }
                
                Console.WriteLine(String.Format("JSON Length: {0}", jsonString.Length));
                Console.WriteLine(String.Format("Full JSON: {0}", jsonString));
                Console.WriteLine(String.Format("=== END DEBUG INFO ==="));

                var content = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(DUELING_API_ENDPOINT, content);

                if (response.IsSuccessStatusCode)
                {
                    string responseText = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("✅ SUCCESS: Duel match data sent for {0} vs {1}: {2}", 
                        match.Player1Name, match.Player2Name, responseText));
                }
                else
                {
                    string errorText = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("❌ FAILED: HTTP {0} - {1}", response.StatusCode, errorText));
                    Console.WriteLine(String.Format("Request JSON was: {0}", jsonString));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("❌ EXCEPTION: Error sending duel match data: {0}", ex.Message));
            }
        }

        private static string BuildMatchJsonString(DuelMatch match)
        {
            // Manual JSON serialization for older C# versions
            try
            {
                if (match == null) return "{}";
                
                var sb = new System.Text.StringBuilder();
                sb.Append("{");
                
                // Add match type
                string matchType = match.MatchType.ToString().ToLower().Replace("ranked", "ranked_");
                sb.AppendFormat("\"matchType\":\"{0}\",", EscapeJsonString(matchType));
                
                // Add player names and IDs (IDs will be null for unregistered players)
                sb.AppendFormat("\"player1Name\":\"{0}\",", EscapeJsonString(match.Player1Name ?? ""));
                sb.AppendFormat("\"player2Name\":\"{0}\",", EscapeJsonString(match.Player2Name ?? ""));
                sb.AppendFormat("\"player1_id\":{0},", match.Player1Id == null ? "null" : String.Format("\"{0}\"", EscapeJsonString(match.Player1Id)));
                sb.AppendFormat("\"player2_id\":{0},", match.Player2Id == null ? "null" : String.Format("\"{0}\"", EscapeJsonString(match.Player2Id)));
                sb.AppendFormat("\"winnerName\":\"{0}\",", EscapeJsonString(match.WinnerName ?? ""));
                sb.AppendFormat("\"winner_id\":{0},", match.WinnerId == null ? "null" : String.Format("\"{0}\"", EscapeJsonString(match.WinnerId)));
                sb.AppendFormat("\"arenaName\":\"{0}\",", EscapeJsonString(match.ArenaName ?? ""));
                
                // Add match scores - CRITICAL for dashboard display (using snake_case to match frontend expectations)
                sb.AppendFormat("\"player1_rounds_won\":{0},", match.Player1RoundsWon);
                sb.AppendFormat("\"player2_rounds_won\":{0},", match.Player2RoundsWon);
                sb.AppendFormat("\"total_rounds\":{0},", match.TotalRounds);
                sb.AppendFormat("\"status\":\"{0}\",", match.Status.ToString().ToLower());
                sb.AppendFormat("\"startedAt\":\"{0}\",", match.StartedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                sb.AppendFormat("\"completedAt\":{0},", match.CompletedAt.HasValue ? String.Format("\"{0}\"", match.CompletedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")) : "null");
                
                Console.WriteLine(String.Format("JSON BUILDING: Match {0} vs {1} - Scores: {2}-{3}, Total Rounds: {4}", 
                    match.Player1Name, match.Player2Name, match.Player1RoundsWon, match.Player2RoundsWon, match.TotalRounds));
                
                // Add rounds array (API processes "rounds", frontend displays "rounds_data")
                sb.Append("\"rounds\":[");
                
                // Track overall match stats for both players
                int player1ShotsFired = 0, player1ShotsHit = 0, player1DoubleHits = 0, player1TripleHits = 0;
                int player2ShotsFired = 0, player2ShotsHit = 0, player2DoubleHits = 0, player2TripleHits = 0;
                
                // Store rounds for both API processing and frontend display
                var roundsJson = new System.Text.StringBuilder();
                
                if (match.Rounds != null && match.Rounds.Count > 0)
                {
                    for (int i = 0; i < match.Rounds.Count; i++)
                    {
                        if (i > 0) 
                        {
                            sb.Append(",");
                            roundsJson.Append(",");
                        }
                        
                        var round = match.Rounds[i];
                        
                        // Build round JSON with correct field names for API
                        var roundJsonStr = String.Format("{{\"round_number\":{0},\"winnerName\":\"{1}\",\"loserName\":\"{2}\",\"winnerHpLeft\":{3},\"loserHpLeft\":{4},\"durationSeconds\":{5},\"kills\":[", 
                            round.RoundNumber, 
                            EscapeJsonString(round.WinnerName ?? ""), 
                            EscapeJsonString(round.LoserName ?? ""), 
                            Math.Max(0, round.WinnerHpLeft), 
                            Math.Max(0, round.LoserHpLeft), 
                            round.DurationSeconds);
                        
                        sb.Append(roundJsonStr);
                        roundsJson.Append(roundJsonStr);
                        
                        Console.WriteLine(String.Format("JSON ROUND {0}: Winner {1} HP: {2}, Loser {3} HP: {4}", 
                            round.RoundNumber, round.WinnerName, round.WinnerHpLeft, round.LoserName, round.LoserHpLeft));
                        
                        // Add kills array
                        if (round.Kills != null && round.Kills.Count > 0)
                        {
                            for (int j = 0; j < round.Kills.Count; j++)
                            {
                                if (j > 0) 
                                {
                                    sb.Append(",");
                                    roundsJson.Append(",");
                                }
                                
                                var kill = round.Kills[j];
                                var killJsonStr = String.Format("{{\"killerName\":\"{0}\",\"victimName\":\"{1}\",\"weaponUsed\":\"{2}\",\"damageDealt\":{3},\"victimHpBefore\":{4},\"victimHpAfter\":{5},\"shotsFired\":{6},\"shotsHit\":{7},\"isDoubleHit\":{8},\"isTripleHit\":{9}}}",
                                    EscapeJsonString(kill.KillerName ?? ""),
                                    EscapeJsonString(kill.VictimName ?? ""),
                                    EscapeJsonString(kill.WeaponUsed ?? ""),
                                    kill.DamageDealt,
                                    kill.VictimHpBefore,
                                    kill.VictimHpAfter,
                                    kill.ShotsFired,
                                    kill.ShotsHit,
                                    kill.IsDoubleHit.ToString().ToLower(),
                                    kill.IsTripleHit.ToString().ToLower());
                                
                                sb.Append(killJsonStr);
                                roundsJson.Append(killJsonStr);
                                
                                // Accumulate stats for overall match totals
                                if (kill.KillerName == match.Player1Name)
                                {
                                    player1ShotsFired += kill.ShotsFired;
                                    player1ShotsHit += kill.ShotsHit;
                                    if (kill.IsDoubleHit) player1DoubleHits++;
                                    if (kill.IsTripleHit) player1TripleHits++;
                                }
                                else if (kill.KillerName == match.Player2Name)
                                {
                                    player2ShotsFired += kill.ShotsFired;
                                    player2ShotsHit += kill.ShotsHit;
                                    if (kill.IsDoubleHit) player2DoubleHits++;
                                    if (kill.IsTripleHit) player2TripleHits++;
                                }
                            }
                        }
                        sb.Append("]}");
                        roundsJson.Append("]}");
                    }
                }
                
                sb.Append("],");
                
                // Add rounds_data for frontend display (same as rounds)
                sb.Append("\"rounds_data\":[");
                sb.Append(roundsJson.ToString());
                sb.Append("],");
                
                // Add overall match statistics
                sb.Append("\"match_stats\":{");
                
                double player1Accuracy = player1ShotsFired > 0 ? (double)player1ShotsHit / player1ShotsFired : 0.0;
                double player2Accuracy = player2ShotsFired > 0 ? (double)player2ShotsHit / player2ShotsFired : 0.0;
                
                sb.AppendFormat("\"player1_accuracy\":{0},", player1Accuracy.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendFormat("\"player1_shots_fired\":{0},", player1ShotsFired);
                sb.AppendFormat("\"player1_shots_hit\":{0},", player1ShotsHit);
                sb.AppendFormat("\"player1_double_hits\":{0},", player1DoubleHits);
                sb.AppendFormat("\"player1_triple_hits\":{0},", player1TripleHits);
                sb.AppendFormat("\"player2_accuracy\":{0},", player2Accuracy.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendFormat("\"player2_shots_fired\":{0},", player2ShotsFired);
                sb.AppendFormat("\"player2_shots_hit\":{0},", player2ShotsHit);
                sb.AppendFormat("\"player2_double_hits\":{0},", player2DoubleHits);
                sb.AppendFormat("\"player2_triple_hits\":{0}", player2TripleHits);
                
                sb.Append("}}");
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error building match JSON: {0}", ex.Message));
                return "{}";
            }
        }

        private static string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";

            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }

        private static async Task SimulateDuelMatch(Player realPlayer, string fakePlayerName, DuelType duelType)
        {
            try
            {
                realPlayer.sendMessage(-1, String.Format("🧪 SIMULATING DUEL: {0} vs {1} ({2})", realPlayer._alias, fakePlayerName, GetDuelTypeString(duelType)));
                realPlayer.sendMessage(-1, "This is a test match with randomized stats!");
                
                // Create a simulated duel match
                var simulatedMatch = new DuelMatch
                {
                    MatchType = duelType,
                    Player1Name = realPlayer._alias,
                    Player2Name = fakePlayerName,
                    Status = DuelStatus.Completed,
                    ArenaName = realPlayer._arena._name,
                    StartedAt = DateTime.Now.AddMinutes(-5), // Simulate it started 5 minutes ago
                    CompletedAt = DateTime.Now
                };

                // Populate player IDs (real player gets null, fake player gets null)
                simulatedMatch.Player1Id = null;
                simulatedMatch.Player2Id = null;

                // Determine how many rounds to simulate based on duel type
                int roundsToWin = GetRoundsToWin(duelType);
                int maxPossibleRounds = duelType == DuelType.RankedBo5 ? 5 : (duelType == DuelType.RankedBo3 ? 3 : 1);
                
                // Real player always wins, but make it somewhat competitive
                Random rand = new Random();
                int realPlayerWins = roundsToWin;
                int fakePlayerWins = rand.Next(0, roundsToWin); // Fake player can win 0 to (roundsToWin-1) rounds
                
                simulatedMatch.Player1RoundsWon = realPlayerWins;
                simulatedMatch.Player2RoundsWon = fakePlayerWins;
                simulatedMatch.TotalRounds = realPlayerWins + fakePlayerWins;
                simulatedMatch.WinnerName = realPlayer._alias;
                simulatedMatch.WinnerId = null;

                realPlayer.sendMessage(-1, String.Format("Simulating {0} rounds...", simulatedMatch.TotalRounds));

                // Generate rounds with randomized stats
                for (int roundNum = 1; roundNum <= simulatedMatch.TotalRounds; roundNum++)
                {
                    var round = new DuelRound
                    {
                        RoundNumber = roundNum,
                        StartedAt = DateTime.Now.AddMinutes(-5).AddSeconds(roundNum * 30),
                        CompletedAt = DateTime.Now.AddMinutes(-5).AddSeconds(roundNum * 30 + rand.Next(5, 45)),
                    };
                    
                    round.DurationSeconds = (int)(round.CompletedAt - round.StartedAt).TotalSeconds;

                    // Determine round winner (real player wins more often)
                    bool realPlayerWinsRound;
                    if (roundNum <= realPlayerWins)
                    {
                        // Real player needs to win this round
                        realPlayerWinsRound = true;
                    }
                    else
                    {
                        // Fake player wins this round
                        realPlayerWinsRound = false;
                    }

                    if (realPlayerWinsRound)
                    {
                        round.WinnerName = realPlayer._alias;
                        round.LoserName = fakePlayerName;
                        round.WinnerHpLeft = rand.Next(10, 60); // Real player survives with some HP
                        round.LoserHpLeft = 0;
                    }
                    else
                    {
                        round.WinnerName = fakePlayerName;
                        round.LoserName = realPlayer._alias;
                        round.WinnerHpLeft = rand.Next(5, 45); // Fake player survives with some HP
                        round.LoserHpLeft = 0;
                    }

                    // Generate randomized kill data for this round - BOTH players fire shots during the round
                    
                    // First, generate shot stats for the loser (who fired shots but didn't get the kill)
                    var loserShotsFired = rand.Next(6, 20);
                    var loserAccuracy = rand.NextDouble() * 0.4 + 0.25; // 25% to 65% accuracy for loser
                    var loserShotsHit = Math.Min(loserShotsFired, (int)(loserShotsFired * loserAccuracy));
                    
                    // Create a "miss" record for the loser (represents their shots that didn't result in a kill)
                    var loserShots = new DuelKill
                    {
                        KillerName = round.LoserName,
                        VictimName = round.WinnerName,
                        WeaponUsed = "Assault Rifle",
                        DamageDealt = loserShotsHit * rand.Next(8, 15), // Damage dealt but not fatal
                        VictimHpBefore = round.WinnerHpLeft + (loserShotsHit * rand.Next(8, 15)),
                        VictimHpAfter = round.WinnerHpLeft,
                        ShotsFired = loserShotsFired,
                        ShotsHit = loserShotsHit,
                        IsDoubleHit = rand.Next(0, 100) < 10, // 10% chance for loser
                        IsTripleHit = rand.Next(0, 100) < 3,  // 3% chance for loser
                        KillTimestamp = round.CompletedAt.AddSeconds(-rand.Next(1, 10)) // Slightly before the final kill
                    };
                    
                    round.Kills.Add(loserShots);

                    // Now generate the final kill shot for the winner
                    var kill = new DuelKill
                    {
                        KillerName = round.WinnerName,
                        VictimName = round.LoserName,
                        WeaponUsed = "Assault Rifle",
                        DamageDealt = 60, // Death blow
                        VictimHpBefore = rand.Next(20, 60),
                        VictimHpAfter = 0,
                        ShotsFired = rand.Next(8, 25),
                        ShotsHit = 0, // Will be calculated below
                        IsDoubleHit = rand.Next(0, 100) < 15, // 15% chance
                        IsTripleHit = rand.Next(0, 100) < 5,  // 5% chance
                        KillTimestamp = round.CompletedAt
                    };

                    // Calculate realistic shots hit based on accuracy for the winner
                    double winnerAccuracy = rand.NextDouble() * 0.4 + 0.3; // 30% to 70% accuracy
                    kill.ShotsHit = Math.Min(kill.ShotsFired, (int)(kill.ShotsFired * winnerAccuracy));
                    
                    // Ensure at least 1 hit for the kill
                    if (kill.ShotsHit == 0) kill.ShotsHit = 1;

                    round.Kills.Add(kill);
                    simulatedMatch.Rounds.Add(round);

                    // Announce round result with both players' accuracy
                    realPlayer.sendMessage(-1, String.Format("Round {0}: {1} defeats {2} ({3}HP left)", 
                        roundNum, round.WinnerName, round.LoserName, round.WinnerHpLeft));
                    realPlayer.sendMessage(-1, String.Format("  {0}: {1:F1}% accuracy ({2}/{3}), {4}: {5:F1}% accuracy ({6}/{7})", 
                        round.WinnerName, 
                        kill.ShotsFired > 0 ? (double)kill.ShotsHit / kill.ShotsFired * 100 : 0,
                        kill.ShotsHit, kill.ShotsFired,
                        round.LoserName,
                        loserShotsFired > 0 ? (double)loserShotsHit / loserShotsFired * 100 : 0,
                        loserShotsHit, loserShotsFired));
                }

                // Announce final result
                realPlayer.sendMessage(-1, String.Format("🏆 SIMULATION COMPLETE: {0} wins {1}-{2}!", 
                    simulatedMatch.WinnerName, simulatedMatch.Player1RoundsWon, simulatedMatch.Player2RoundsWon));

                // Send the simulated match to the website API
                realPlayer.sendMessage(-1, "📡 Sending test data to API...");
                await SendDuelMatchToWebsite(simulatedMatch);
                realPlayer.sendMessage(-1, "✅ Test data sent! Check the dueling dashboard to see the results.");
                
                Console.WriteLine(String.Format("SIMULATION: Generated test duel {0} vs {1} with {2} rounds", 
                    realPlayer._alias, fakePlayerName, simulatedMatch.TotalRounds));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in SimulateDuelMatch: {0}", ex.Message));
                realPlayer.sendMessage(-1, "Error occurred during duel simulation.");
            }
        }
    }

    public class InGameRegistration
    {
        private static readonly HttpClient httpClient = new HttpClient();
        //private const string REGISTRATION_API_ENDPOINT = "http://localhost:3000/api/in-game-register"; // Change to your actual domain in production
        private const string REGISTRATION_API_ENDPOINT = "https://freeinf.org/api/in-game-register";
        
        /// <summary>
        /// Handles the ?register command for in-game registration
        /// Usage: ?register email@example.com
        /// </summary>
        public static async Task HandleRegisterCommand(Player player, string command, string payload)
        {
            try
            {
                // Parse the email from the command
                string email = payload.Trim();
                
                if (string.IsNullOrEmpty(email))
                {
                    player.sendMessage(-1, "Usage: ?register your-email@example.com");
                    player.sendMessage(-1, "Example: ?register john.doe@gmail.com");
                    return;
                }
                
                // Validate email format
                if (!IsValidEmail(email))
                {
                    player.sendMessage(-1, "Invalid email format. Please use a valid email address.");
                    player.sendMessage(-1, "Example: ?register john.doe@gmail.com");
                    return;
                }
                
                // Get player's current alias
                string alias = player._alias;
                
                if (string.IsNullOrEmpty(alias))
                {
                    player.sendMessage(-1, "Error: Could not determine your in-game alias.");
                    return;
                }
                
                // Validate alias (basic checks)
                // if (alias.Length < 2)
                // {
                //     player.sendMessage(-1, "Your alias is too short for registration. Please use a longer alias.");
                //     return;
                // }
                
                if (alias.Length > 20)
                {
                    player.sendMessage(-1, "Your alias is too long for registration. Please use a shorter alias.");
                    return;
                }
                
                // Send registration request to web API
                player.sendMessage(-1, "Processing registration request...");
                
                bool success = await SendRegistrationRequest(alias, email);
                
                if (success)
                {
                    player.sendMessage(-1, "=== REGISTRATION INITIATED ===");
                    player.sendMessage(-1, String.Format("Alias: {0}", alias));
                    player.sendMessage(-1, String.Format("Email: {0}", email));
                    player.sendMessage(-1, "");
                    player.sendMessage(-1, "Check your email to complete registration!");
                    player.sendMessage(-1, "You'll receive a verification link to set your password.");
                    player.sendMessage(-1, "");
                    player.sendMessage(-1, "After completing registration, you can:");
                    player.sendMessage(-1, "• Purchase donation perks");
                    player.sendMessage(-1, "• Access your dashboard");
                    player.sendMessage(-1, "• View your donation history");
                    
                    // Log the registration attempt
                    Console.WriteLine(String.Format("Player {0} initiated registration with email {1}", alias, email));
                }
                else
                {
                    player.sendMessage(-1, "Registration failed. Please try again later.");
                    player.sendMessage(-1, "If the problem persists, contact an administrator.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in HandleRegisterCommand: {0}", ex.Message));
                player.sendMessage(-1, "An error occurred during registration. Please try again later.");
            }
        }
        
        /// <summary>
        /// Sends registration request to the web API
        /// </summary>
        private static async Task<bool> SendRegistrationRequest(string alias, string email)
        {
            try
            {
                // Create JSON payload
                string jsonData = String.Format("{{\"alias\":\"{0}\",\"email\":\"{1}\"}}", 
                    EscapeJsonString(alias), 
                    EscapeJsonString(email));
                
                var content = new StringContent(jsonData, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(REGISTRATION_API_ENDPOINT, content);
                
                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("Registration API response: {0}", responseContent));
                    return true;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("Registration API error ({0}): {1}", response.StatusCode, errorContent));
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error sending registration request: {0}", ex.Message));
                return false;
            }
        }
        
        /// <summary>
        /// Validates email format using regex
        /// </summary>
        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrEmpty(email))
                return false;
                
            try
            {
                // Basic email validation regex
                string pattern = @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$";
                return Regex.IsMatch(email, pattern);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Escapes special characters for JSON
        /// </summary>
        private static string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";
                
            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }
        
        /// <summary>
        /// Handles the ?checkregistration command to check registration status
        /// </summary>
        public static async Task HandleCheckRegistrationCommand(Player player, string command, string payload)
        {
            try
            {
                string alias = player._alias;
                
                player.sendMessage(0, "=== REGISTRATION STATUS ===");
                player.sendMessage(0, String.Format("Current Alias: {0}", alias));
                player.sendMessage(0, "");
                player.sendMessage(0, "To register for the donation system:");
                player.sendMessage(0, "1. Use: ?register your-email@example.com");
                player.sendMessage(0, "2. Check your email for verification link");
                player.sendMessage(0, "3. Set your password to complete registration");
                player.sendMessage(0, "4. Access your dashboard at the website");
                // player.sendMessage(0, "");
                // player.sendMessage(0, "Benefits of registering:");
                // player.sendMessage(0, "• Purchase exclusive donation perks");
                // player.sendMessage(0, "• Custom kill messages");
                // player.sendMessage(0, "• Special weapons and abilities");
                // player.sendMessage(0, "• Support server development");
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in HandleCheckRegistrationCommand: {0}", ex.Message));
                player.sendMessage(-1, "An error occurred. Please try again later.");
            }
        }
    }

    public class PlayerStatsIntegration
    {
        private static readonly HttpClient httpClient = new HttpClient();
        // WAS: a hard-coded "http://localhost:3000/api/player-stats".
        //
        // That single line killed the whole stats/ELO pipeline. Nothing listens on :3000 on the live
        // Linux zone, so every end-of-game POST failed silently - the newest row in player_stats was
        // 2026-03-07 and the site showed "No recent games found". Every OTHER endpoint in this file
        // and in CTF.cs had already been switched to production; this one was missed.
        //
        // Resolved at class load instead of hard-coded, because local bot testing must NOT write into
        // the real player_stats table:
        //
        //   scripts/GameTypes/CTF/StatsEndpoint.txt present -> use the base URL it contains
        //   absent                                          -> https://freeinf.org  (production)
        //
        // The live zone has no such file, so it gets production by default - a dev box has to opt
        // OUT deliberately, which is the safe direction for this to fail in.
        private static readonly string STATS_API_BASE = ResolveStatsBase();
        private static readonly string STATS_API_ENDPOINT = STATS_API_BASE + "/api/player-stats";

        private static string ResolveStatsBase()
        {
            try
            {
                string path = System.IO.Path.Combine("scripts", System.IO.Path.Combine("GameTypes",
                    System.IO.Path.Combine("CTF", "StatsEndpoint.txt")));
                if (System.IO.File.Exists(path))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#"))
                            continue;
                        line = line.TrimEnd('/');
                        Console.WriteLine(String.Format("CTF stats endpoint overridden to {0} (StatsEndpoint.txt)", line));
                        return line;
                    }
                }
            }
            catch { /* unreadable override = fall through to production */ }
            return "https://freeinf.org";
        }
        
        public static async Task SendPlayerStatsToWebsite(List<PlayerStatData> playerStats, string gameId = null)
        {
            try
            {
                if (playerStats == null || playerStats.Count == 0)
                {
                    Console.WriteLine("No player stats to send");
                    return;
                }

                // Console.WriteLine(String.Format("Attempting to send stats for {0} players to {1}", playerStats.Count, STATS_API_ENDPOINT));

                // Create the payload
                var payload = new
                {
                    gameId = gameId,
                    gameDate = DateTime.UtcNow.ToString("O"), // ISO 8601 format
                    schemaVersion = StatsSchema.SchemaVersion,
                    scriptVersion = StatsSchema.ScriptVersion,
                    players = playerStats.Select(p => new
                    {
playerName = p.PlayerName,
                        team = p.Team,
                        gameMode = p.GameMode,
                        arenaName = p.ArenaName,
                        baseUsed = p.BaseUsed,
                        side = p.Side,
                        result = p.Result,
                        mainClass = p.MainClass,
                        kills = p.Kills,
                        deaths = p.Deaths,
                        captures = p.Captures,
                        carrierKills = p.CarrierKills,
                        carryTimeSeconds = p.CarryTimeSeconds,
                        classSwaps = p.ClassSwaps,
                        turretDamage = p.TurretDamage,
                        ebHits = p.EBHits,
                        accuracy = p.Accuracy,
                        avgResourceUnusedPerDeath = p.AvgResourceUnusedPerDeath,
                        avgExplosiveUnusedPerDeath = p.AvgExplosiveUnusedPerDeath,
                        gameLengthMinutes = p.GameLengthMinutes,
                        // schema 2
                        classPlayTimes = p.ClassPlayTimes,
                        weapons = p.Weapons,
                        playSeconds = p.PlaySeconds,
                        timesSummoned = p.TimesSummoned,
                        summonsPerformed = p.SummonsPerformed,
                        minedTso = p.MinedTso,
                        minedTox = p.MinedTox,
                        isCaptain = p.IsCaptain
                    }).ToList()
};

                // Manually construct JSON string (no external libraries needed)
                string jsonData = BuildPlayerStatsJsonString(payload);
                // Console.WriteLine(String.Format("JSON payload length: {0} characters", jsonData.Length));
                // Console.WriteLine(String.Format("JSON payload preview: {0}...", jsonData.Substring(0, Math.Min(200, jsonData.Length))));
                
                // Send to API
                var content = new StringContent(jsonData, System.Text.Encoding.UTF8, "application/json");
                
                // Console.WriteLine("Sending HTTP POST request...");
                var response = await httpClient.PostAsync(STATS_API_ENDPOINT, content);
                
                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("✓ Player stats sent successfully for {0} players. Response: {1}", playerStats.Count, responseContent));
                }
                else
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("✗ Failed to send player stats: {0} ({1}) - {2}", (int)response.StatusCode, response.StatusCode, responseContent));
                }
            }
            catch (System.Net.Http.HttpRequestException httpEx)
            {
                Console.WriteLine(String.Format("✗ HTTP Error sending player stats: {0}", httpEx.Message));
                if (httpEx.InnerException != null)
                {
                    Console.WriteLine(String.Format("  Inner Exception: {0}", httpEx.InnerException.Message));
                }
            }
            catch (System.Threading.Tasks.TaskCanceledException tcEx)
            {
                Console.WriteLine(String.Format("✗ Timeout error sending player stats: {0}", tcEx.Message));
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("✗ Unexpected error sending player stats: {0}", ex.Message));
                Console.WriteLine(String.Format("  Exception Type: {0}", ex.GetType().Name));
                if (ex.InnerException != null)
                {
                    Console.WriteLine(String.Format("  Inner Exception: {0}", ex.InnerException.Message));
                }
            }
        }
        
        private static string BuildPlayerStatsJsonString(object payload)
        {
            var json = new System.Text.StringBuilder();
            json.Append("{");
            
            // Add gameId if available
            var payloadType = payload.GetType();
            var gameIdProp = payloadType.GetProperty("gameId");
            var gameId = gameIdProp.GetValue(payload);
            if (gameId != null)
            {
                json.AppendFormat("\"gameId\":\"{0}\",", EscapeJsonString(gameId.ToString()));
            }
            
            // Add gameDate
            var gameDateProp = payloadType.GetProperty("gameDate");
            var gameDate = gameDateProp.GetValue(payload);
            if (gameDate != null)
            {
                json.AppendFormat("\"gameDate\":\"{0}\",", EscapeJsonString(gameDate.ToString()));
            }
            
            // Version stamps (schema 2). Read reflectively like the rest so an older caller
            // without them still serialises.
            var schemaProp = payloadType.GetProperty("schemaVersion");
            if (schemaProp != null && schemaProp.GetValue(payload) != null)
                json.AppendFormat("\"schemaVersion\":{0},", Convert.ToString(schemaProp.GetValue(payload), System.Globalization.CultureInfo.InvariantCulture));
            var scriptProp = payloadType.GetProperty("scriptVersion");
            if (scriptProp != null && scriptProp.GetValue(payload) != null)
                json.AppendFormat("\"scriptVersion\":\"{0}\",", EscapeJsonString(scriptProp.GetValue(payload).ToString()));

            // Add players array
            var playersProp = payloadType.GetProperty("players");
var players = playersProp.GetValue(payload) as System.Collections.IEnumerable;
            
            json.Append("\"players\":[");
            
            if (players != null)
            {
                bool isFirst = true;
                foreach (var player in players)
                {
                    if (!isFirst) json.Append(",");
                    isFirst = false;
                    
                    json.Append("{");
                    
                    // Add all player properties
                    var playerType = player.GetType();
                    var properties = playerType.GetProperties();
                    
                    bool isFirstProp = true;
                    foreach (var prop in properties)
                    {
                        if (!isFirstProp) json.Append(",");
                        isFirstProp = false;
                        
                        json.AppendFormat("\"{0}\":", prop.Name);
                        AppendJsonValue(json, prop.GetValue(player));
                    }

                    json.Append("}");
                }
            }
            
            json.Append("]}");
            return json.ToString();
        }

        /// <summary>
        /// Writes one value as JSON. Before schema 2 every non-primitive fell through to
        /// ToString() in quotes, which would have turned a Dictionary into the literal text
        /// "System.Collections.Generic.Dictionary`2[...]". Numbers use the invariant culture so
        /// a host with a comma decimal separator can never emit 0,5 and break the parser.
        /// </summary>
        private static void AppendJsonValue(System.Text.StringBuilder json, object value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }
            if (value is string)
            {
                json.Append('"').Append(EscapeJsonString((string)value)).Append('"');
                return;
            }
            if (value is bool)
            {
                json.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is int || value is long || value is short || value is ushort || value is byte
                || value is double || value is float || value is decimal)
            {
                json.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            var dict = value as System.Collections.IDictionary;
            if (dict != null)
            {
                json.Append("{");
                bool first = true;
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (!first) json.Append(",");
                    first = false;
                    json.Append('"').Append(EscapeJsonString(Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture))).Append("\":");
                    AppendJsonValue(json, entry.Value);
                }
                json.Append("}");
                return;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                json.Append("[");
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) json.Append(",");
                    first = false;
                    AppendJsonValue(json, item);
                }
                json.Append("]");
                return;
            }
            json.Append('"').Append(EscapeJsonString(value.ToString())).Append('"');
        }

        private static string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";
            
            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }
        
        // Test method to verify the integration works
        public static async Task TestPlayerStatsIntegration()
        {
            Console.WriteLine("Testing Player Stats Integration...");
            
            var testStats = new List<PlayerStatData>
            {
                new PlayerStatData
                {
                    PlayerName = "TestPlayer",
                    Team = "0",
                    GameMode = "CTF",
                    ArenaName = "TestArena",
                    BaseUsed = "TestBase",
                    Side = "Titan",
                    Result = "Win",
                    MainClass = "Warrior",
                    Kills = 5,
                    Deaths = 2,
                    Captures = 1,
                    CarrierKills = 0,
                    CarryTimeSeconds = 30,
                    ClassSwaps = 1,
                    TurretDamage = 100,
                    EBHits = 3,
                    Accuracy = 75.5,
                    AvgResourceUnusedPerDeath = 2.5,
                    AvgExplosiveUnusedPerDeath = 1.2,
                    GameLengthMinutes = 10.5
                }
            };
            
            await SendPlayerStatsToWebsite(testStats, "TEST_GAME_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        }
    }

    // New data class for player stats
    public class PlayerStatData
    {
        public string PlayerName { get; set; }
        public string Team { get; set; }
        public string GameMode { get; set; }
        public string ArenaName { get; set; }
        public string BaseUsed { get; set; }
        public string Side { get; set; }
        public string Result { get; set; }
        public string MainClass { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Captures { get; set; }
        public int CarrierKills { get; set; }
        public int CarryTimeSeconds { get; set; }
        public int ClassSwaps { get; set; }
        public int TurretDamage { get; set; }
        public int EBHits { get; set; }
        public double Accuracy { get; set; }
        public double AvgResourceUnusedPerDeath { get; set; }
        public double AvgExplosiveUnusedPerDeath { get; set; }
        public double GameLengthMinutes { get; set; }

        // ---- schema 2 (Tier 1) -------------------------------------------------------------
        // The spread behind MainClass: class name -> whole seconds actually played as it.
        public Dictionary<string, int> ClassPlayTimes { get; set; }
        // weapon name -> { "fired": n, "landed": n }. Accuracy above stays "best single weapon"
        // for continuity with old rows; the site can derive anything else from this.
        public Dictionary<string, Dictionary<string, int>> Weapons { get; set; }
        // Seconds this player spent in the game (sum of the class spread), so a 3-minute
        // fill-in is never weighed like a full 20-minute game.
        public int PlaySeconds { get; set; }
        public int TimesSummoned { get; set; }     // summons RECEIVED
        public int SummonsPerformed { get; set; }  // summons PERFORMED (the SL stat)
        public int MinedTso { get; set; }
        public int MinedTox { get; set; }
        public bool IsCaptain { get; set; }
    }

    /// <summary>
    /// Stamped on every payload so the site can tell which script produced a row. Bump
    /// SchemaVersion whenever the payload gains or changes a field; ScriptVersion is the
    /// deploy date of the CTF scripts and is purely informational.
    /// </summary>
    public static class StatsSchema
    {
        public const int SchemaVersion = 2;
        public const string ScriptVersion = "ctf-2026.09.11";
    }

    public class ImprovedWebIntegration
{
        private static readonly HttpClient httpClient = new HttpClient();
        private const string API_ENDPOINT = "https://freeinf.org/api/game-data";
        
        // Track all players who participated in the game, even if they leave
        private static Dictionary<string, PlayerGameData> gameParticipants = new Dictionary<string, PlayerGameData>();
        public class PlayerGameData
        {
            public string Alias { get; set; }
            public string Team { get; set; }
            public string TeamType { get; set; }
            public string MostPlayedClass { get; set; }
            public bool IsOffense { get; set; }
            public string Weapon { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastActive { get; set; }
            public bool IsActive { get; set; } // Currently in game
            public int TotalPlayTimeMs { get; set; }
            public Dictionary<string, int> ClassPlayTimes { get; set; }
            
            public PlayerGameData()
            {
                ClassPlayTimes = new Dictionary<string, int>();
                FirstSeen = DateTime.Now;
                LastActive = DateTime.Now;
                IsActive = true;
            }
        }
        
        /// <summary>
        /// Get the most played class for a player using playerClassPlayTimes
        /// </summary>
        private static string GetMostPlayedClass(Player player, Dictionary<Player, Dictionary<string, int>> playerClassPlayTimes, 
                                               Dictionary<Player, int> playerLastClassSwitch)
        {
            try
            {
                if (!playerClassPlayTimes.ContainsKey(player))
                {
                    // Fallback to current class if no play time data
                    return player._skills.Count > 0 ? player._skills.First().Value.skill.Name : player._baseVehicle._type.Name;
                }
                
                // Get current time to calculate current class time
                int currentTick = Environment.TickCount;
                var playTimes = new Dictionary<string, int>(playerClassPlayTimes[player]);
                
                // Add time for current class if player has one
                string currentSkill = player._skills.Count > 0 ? player._skills.First().Value.skill.Name : null;
                if (currentSkill != null && playerLastClassSwitch.ContainsKey(player))
                {
                    int sessionTime = Math.Max(0, currentTick - playerLastClassSwitch[player]);
                    if (!playTimes.ContainsKey(currentSkill))
                        playTimes[currentSkill] = 0;
                    playTimes[currentSkill] += sessionTime;
                }
                
                // Return the class with the most play time
                if (playTimes.Count > 0)
                {
                    var mostPlayed = playTimes.OrderByDescending(x => x.Value).First();
                    return mostPlayed.Key;
                }
                
                // Final fallback
                return player._baseVehicle._type.Name;
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("[GameStats] Error getting most played class for {0}: {1}", player._alias, ex.Message));
                return player._baseVehicle._type.Name;
            }
        }
        
        /// <summary>
        /// Send comprehensive game data to website at game end, including all participants
        /// </summary>
        public static async Task SendGameEndDataToWebsite(Arena arena, string baseUsed, Dictionary<Player, Dictionary<string, int>> playerClassPlayTimes,
                                                         Dictionary<Player, int> playerLastClassSwitch, Team winningTeam = null)
        {
            try
            {
                // Update ALL participants with final game state and fresh most played class calculation
                // Determine game type and offense team
                string gameType = DetermineGameType(arena._name);
                bool titanIsOffense = DetermineOffenseTeam(arena.Players.ToList(), arena);
                
                // Clear existing participants to rebuild with fresh data
                gameParticipants.Clear();
                
                // Process all players with their final playtime data
                foreach (Player player in arena.Players.ToList())
                {
                    if (player._team.IsSpec || (player._baseVehicle != null && player._baseVehicle._type.Name.Contains("Spectator")))
                        continue;
                    
                    // Skip Dueler players entirely from stats
                    string primarySkill = "";
                    if (player._skills.Count > 0)
                    {
                        primarySkill = player._skills.First().Value.skill.Name;
                    }
                    if (primarySkill == "Dueler") continue;
                        
                    string playerKey = player._alias.ToLower();
                    
                    var playerData = new PlayerGameData();
                    playerData.Alias = player._alias;
                    playerData.Team = player._team._name;
                    playerData.TeamType = DeterminePlayerTeamType(player);
                    playerData.IsOffense = DetermineIsOffense(playerData.TeamType, titanIsOffense, gameType);
                    playerData.Weapon = GetSpecialWeapon(player);
                    playerData.LastActive = DateTime.Now;
                    playerData.IsActive = true;
                    
                    // Calculate fresh most played class using final playerClassPlayTimes
                    playerData.MostPlayedClass = GetMostPlayedClass(player, playerClassPlayTimes, playerLastClassSwitch);
                    
                    // Update class play times from the tracking dictionary
                    if (playerClassPlayTimes.ContainsKey(player))
                    {
                        playerData.ClassPlayTimes = new Dictionary<string, int>(playerClassPlayTimes[player]);
                        playerData.TotalPlayTimeMs = playerClassPlayTimes[player].Values.Sum();
                    }
                    
                    gameParticipants[playerKey] = playerData;
                }
                
                // Filter participants who actually played (had some play time)
                var validParticipants = gameParticipants.Values
                    .Where(p => p.TotalPlayTimeMs > 5000) // At least 5 seconds of play time
                    .ToList();
                
                if (validParticipants.Count == 0)
                {
                    Console.WriteLine("[GameStats] No valid participants found, skipping stats upload");
                    return;
                }
                
                // Convert to PlayerData format for the API
                var gameDataPlayers = validParticipants.Select(p => new PlayerData
                {
                    alias = p.Alias,
                    team = p.Team,
                    teamType = p.TeamType,
                    className = p.MostPlayedClass, // Use most played class instead of setup class!
                    isOffense = p.IsOffense,
                    weapon = p.Weapon
                }).ToList();
                
                // Build enhanced JSON with additional metadata
                string jsonData = BuildEnhancedJsonString(arena._name, gameType, baseUsed, gameDataPlayers, 
                                                        validParticipants, winningTeam != null ? winningTeam._name : null);
                
                // Send to API
                var content = new StringContent(jsonData, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(API_ENDPOINT, content);
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine(string.Format("[GameStats] Successfully sent game end data for {0} participants", validParticipants.Count));
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(string.Format("[GameStats] Failed to send game data: {0} - {1}", response.StatusCode, errorContent));
                }
                
                // Clear participants for next game
                gameParticipants.Clear();
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("[GameStats] Error sending game end data: {0}", ex.Message));
            }
        }
        
        /// <summary>
        /// Enhanced JSON builder with additional metadata
        /// </summary>
        private static string BuildEnhancedJsonString(string arenaName, string gameType, string baseUsed, 
                                                    List<PlayerData> players, List<PlayerGameData> participantData, 
                                                    string winningTeam = null)
        {
            var json = new System.Text.StringBuilder();
            json.Append("{");
            
            // Basic game information
            json.AppendFormat("\"arenaName\":\"{0}\",", EscapeJsonString(arenaName));
            json.AppendFormat("\"gameType\":\"{0}\",", EscapeJsonString(gameType));
            json.AppendFormat("\"baseUsed\":\"{0}\",", EscapeJsonString(baseUsed));
            json.AppendFormat("\"gameEndTime\":\"{0}\",", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            
            if (!string.IsNullOrEmpty(winningTeam))
            {
                json.AppendFormat("\"winningTeam\":\"{0}\",", EscapeJsonString(winningTeam));
            }
            
            // Enhanced player data
            json.Append("\"players\":[");
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var participantInfo = participantData.FirstOrDefault(p => p.Alias.Equals(player.alias, StringComparison.OrdinalIgnoreCase));
                
                json.Append("{");
                json.AppendFormat("\"alias\":\"{0}\",", EscapeJsonString(player.alias));
                json.AppendFormat("\"team\":\"{0}\",", EscapeJsonString(player.team));
                json.AppendFormat("\"teamType\":\"{0}\",", EscapeJsonString(player.teamType));
                json.AppendFormat("\"class\":\"{0}\",", EscapeJsonString(player.className));
                json.AppendFormat("\"isOffense\":{0},", player.isOffense.ToString().ToLower());
                
                // Add enhanced statistics
                if (participantInfo != null)
                {
                    json.AppendFormat("\"totalPlayTimeMs\":{0},", participantInfo.TotalPlayTimeMs);
                    json.AppendFormat("\"playTimeSeconds\":{0:F1},", participantInfo.TotalPlayTimeMs / 1000.0);
                    
                    // Add class breakdown
                    if (participantInfo.ClassPlayTimes.Count > 0)
                    {
                        json.Append("\"classBreakdown\":{");
                        var classEntries = participantInfo.ClassPlayTimes.ToList();
                        for (int j = 0; j < classEntries.Count; j++)
                        {
                            json.AppendFormat("\"{0}\":{1:F1}", EscapeJsonString(classEntries[j].Key), 
                                            classEntries[j].Value / 1000.0);
                            if (j < classEntries.Count - 1) json.Append(",");
                        }
                        json.Append("},");
                    }
                }
                
                // Add weapon info
                if (string.IsNullOrEmpty(player.weapon))
                {
                    json.Append("\"weapon\":null");
                }
                else
                {
                    json.AppendFormat("\"weapon\":\"{0}\"", EscapeJsonString(player.weapon));
                }
                
                json.Append("}");
                if (i < players.Count - 1) json.Append(",");
            }
            json.Append("]");
            
            json.Append("}");
            return json.ToString();
        }
        
        // Helper methods (reused from original WebIntegration)
        private static string DetermineGameType(string arenaName)
        {
            if (arenaName.Contains("OvD"))
                return "OvD";
            else if (arenaName.Contains("Arena 1"))
                return "Pub";
            else if (arenaName.Contains("Mix"))
                return "Mix";
            else if (arenaName.Contains("Duel"))
                return "Dueling";
            else if (arenaName.Contains("CTF"))
                return "CTF";
            else
                return "Unknown";
        }
        
        private static string DeterminePlayerTeamType(Player player)
        {
            if (player._team._name.Contains(" T"))
                return "Titan";
            else if (player._team._name.Contains(" C"))
                return "Collective";
            else
                return "Unknown";
        }
        
        private static bool DetermineOffenseTeam(List<Player> players, Arena arena)
        {
            // First, try to find the team with a Squad Leader - that team is offense
            foreach (Player player in players)
            {
                string className = player._baseVehicle._type.Name;
                if (className == "Squad Leader")
                {
                    return player._team._name.Contains(" T"); // Return true if Titan has Squad Leader
                }
            }
            
            // If no Squad Leader found, use team balance logic
            int titanCount = 0;
            int collectiveCount = 0;
            
            foreach (Player player in players)
            {
                if (player._team._name.Contains(" T"))
                    titanCount++;
                else if (player._team._name.Contains(" C"))
                    collectiveCount++;
            }
            
            // Make the larger team defense, smaller team offense
            if (titanCount > collectiveCount)
                return false; // Titan = defense
            else
                return true;  // Titan = offense
        }
        
        private static bool DetermineIsOffense(string teamType, bool titanIsOffense, string gameType)
        {
            if (teamType == "Titan")
                return titanIsOffense;
            else
                return !titanIsOffense;
        }
        
        private static string GetSpecialWeapon(Player player)
        {
            if (player._baseVehicle != null && player._baseVehicle._type != null)
            {
                string vehicleType = player._baseVehicle._type.Name;
                if (vehicleType.Contains("CAW"))
                    return "CAW";
                else if (vehicleType.Contains("SG"))
                    return "SG";
            }
            return null;
        }
        
        private static string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";
                
            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }
    }
} 