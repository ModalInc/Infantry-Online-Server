// CTF:X bot engine - PROFILES. The manager runs exactly one profile at a time (one per attach):
//   Default = the CTF:X event on the big map (every constant below is what the engine hard-coded before profiles existed)
//   MiniTP  = the small MiniTP event of the same level (MiniTP/MiniTPBots.txt, MiniTP/*.cs, MiniTPBots\MINITP-BOT-META-SPEC.md)
// The default profile must stay byte-for-byte equivalent to the old behaviour: zone 112 (CTF:X Halloween) runs it live.
//
// Selection (CTFXBotManager.Attach / EventChanged): MiniTP only when the arena's event is MiniTP AND MiniTP/MiniTPBots.txt exists
// with enabled=1. Anything else = Default.

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXProfile
    {
        public readonly string Name;
        public readonly bool IsMiniTP;
        /// <summary>The tile window bots may plan goals in (IsOpenA, the door graph). Inclusive.</summary>
        public readonly int X0, X1, Y0, Y1;
        /// <summary>The two teams' exact names (null = the CTF:X lookup by substring, lowest team id).</summary>
        public readonly string TitanTeam, CollectiveTeam;

        CTFXProfile(string name, bool mini, int x0, int x1, int y0, int y1, string titan, string coll)
        {
            Name = name; IsMiniTP = mini; X0 = x0; X1 = x1; Y0 = y0; Y1 = y1; TitanTeam = titan; CollectiveTeam = coll;
        }

        /// <summary>CTF:X on the big map: x29-980, y1060-2023, teams found by substring (ids 0 / 1, "Titan Militia'" / "Collective'").</summary>
        public static readonly CTFXProfile Default = new CTFXProfile("ctfx", false, 29, 980, 1060, 2023, null, null);

        /// <summary>
        /// MiniTP: one window over everything a MiniTP bot walks: the dropship (x610-694, y498-578), the A7 and D7 bases (x958-1141,
        /// y570-696) and the field (x1050-1251, y675-894); teams 36 / 37 by exact name (the apostrophe twins 0 / 1 are CTF:X's).
        /// </summary>
        public static readonly CTFXProfile MiniTP = new CTFXProfile("minitp", true, 600, 1260, 490, 900, "Titan Militia", "Collective");

        public override string ToString() { return Name; }
    }
}
