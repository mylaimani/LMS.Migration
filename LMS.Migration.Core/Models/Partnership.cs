namespace LMS.Migration.Core.Models
{
    /// <summary>One row per batting partnership (lms.partnerships).</summary>
    public class Partnership
    {
        public uint FixtureId { get; set; }
        public byte InningsNumber { get; set; }
        public byte PartnershipNumber { get; set; }   // 1st, 2nd, 3rd... partnership

        public uint Batter1Id { get; set; }
        public uint Batter2Id { get; set; }
        public uint BattingTeamId { get; set; }
        public uint BowlingTeamId { get; set; }

        public ushort RunsTogether { get; set; }
        public ushort BallsTogether { get; set; }
        public byte FoursTogether { get; set; }
        public byte SixesTogether { get; set; }

        public byte StartOver { get; set; }
        public byte EndOver { get; set; }

        /// <summary>runs / (balls/5) — 5 balls per over in LMS.</summary>
        public float RunRate =>
            BallsTogether == 0 ? 0f : RunsTogether / (BallsTogether / 5f);

        /// <summary>Derived from StartOver: Powerplay / Middle / Death.</summary>
        public string OverPhase =>
            StartOver <= 6 ? "Powerplay" : StartOver <= 15 ? "Middle" : "Death";

        public DateTime GameDate { get; set; }

        // Context
        public uint LeagueId { get; set; }
        public uint DivisionId { get; set; }
        public uint SeasonId { get; set; }
        public string SeasonName { get; set; } = "";
        public uint VenueId { get; set; }
        public uint RegionId { get; set; }
    }
}
