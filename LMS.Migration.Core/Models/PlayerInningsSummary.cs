namespace LMS.Migration.Core.Models
{
    /// <summary>
    /// Authoritative per-player match summary taken from the FixtureState
    /// JSON aggregates (Innings[].Batsmen / Bowlers) — more reliable than
    /// re-deriving from individual balls (handles retirements, run-out of
    /// non-striker, etc.).
    /// </summary>
    public class PlayerInningsSummary
    {
        public uint PlayerId { get; set; }
        public uint TeamId { get; set; }

        // Batting (from Innings[].Batsmen[])
        public bool Batted { get; set; }
        public ushort RunsScored { get; set; }
        public ushort BallsFaced { get; set; }
        public byte BattingOrder { get; set; }
        /// <summary>True when OutEvent is null (never dismissed).</summary>
        public bool IsNotOut { get; set; }

        // Bowling (from Innings[].Bowlers[]; balls = Over × ballsPerOver + Ball)
        public bool Bowled { get; set; }
        public ushort BallsBowled { get; set; }
        public ushort RunsConceded { get; set; }
        public byte Wickets { get; set; }
    }
}
