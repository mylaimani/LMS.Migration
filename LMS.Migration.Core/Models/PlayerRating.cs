namespace LMS.Migration.Core.Models
{
    /// <summary>Current rating state per player (lms.player_ratings).</summary>
    public class PlayerRating
    {
        public uint PlayerId { get; set; }

        public ushort BattingGamesUsed { get; set; }
        public float BattingTotalAdjustedPts { get; set; }
        public float BattingApg { get; set; }
        public float BattingZScore { get; set; }
        public float BattingBaseRating { get; set; }
        public float BattingRating { get; set; }
        public float BattingMaxCap { get; set; }
        public float BattingPrevRating { get; set; }
        public float BattingRatingChange { get; set; }

        public ushort BowlingGamesUsed { get; set; }
        public float BowlingTotalAdjustedPts { get; set; }
        public float BowlingApg { get; set; }
        public float BowlingZScore { get; set; }
        public float BowlingBaseRating { get; set; }
        public float BowlingRating { get; set; }
        public float BowlingMaxCap { get; set; }
        public float BowlingPrevRating { get; set; }
        public float BowlingRatingChange { get; set; }

        public float AllRounderRating { get; set; }      // 50/50 model

        public ushort GamesPlayed { get; set; }          // ALL qualifying matches (any discipline)
        public ushort UniqueTeamsFaced { get; set; }
        public float ReliabilityPct { get; set; }

        public float PopulationMeanBatting { get; set; }
        public float PopulationStddevBatting { get; set; }
        public float PopulationMeanBowling { get; set; }
        public float PopulationStddevBowling { get; set; }

        public uint LastUpdatedFixtureId { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
