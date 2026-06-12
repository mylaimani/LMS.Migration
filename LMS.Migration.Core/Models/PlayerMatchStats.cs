namespace LMS.Migration.Core.Models
{
    /// <summary>
    /// One row per player per completed match — the Points Engine
    /// (lms.player_match_stats). Drives ratings, rankings, legends.
    /// Nullable fields implement the spec NULL rules:
    ///   batting_* points are NULL when the player did not bat,
    ///   bowling_* points are NULL when the player did not bowl.
    /// </summary>
    public class PlayerMatchStats
    {
        public uint FixtureId { get; set; }
        public uint PlayerId { get; set; }
        public uint TeamId { get; set; }

        /// <summary>Win / Loss / Tie / NoResult — relative to this player's team.</summary>
        public string MatchResult { get; set; } = "NoResult";

        /// <summary>1 = abandoned / rained out / no result → excluded from all points and ratings.</summary>
        public byte IsNoResult { get; set; }

        /// <summary>Total runs both innings ÷ total legal balls both innings.</summary>
        public float MatchRpball { get; set; }

        // ── Batting (Chain 1) ─────────────────────────────────────────
        public ushort BattingBallsFaced { get; set; }
        public ushort BattingRunsScored { get; set; }
        public byte BattingIsNotOut { get; set; }
        public float? BattingPlayerRpball { get; set; }
        public float? BattingEfficiencyRatio { get; set; }
        public float? BattingBasePoints { get; set; }
        public float? BattingAfterWin { get; set; }
        public float? BattingNotOutBonus { get; set; }
        public float? BattingRawPoints { get; set; }          // audit — before 300 cap
        public float? BattingMatchPoints { get; set; }        // min(300, raw); NULL if balls_faced = 0
        public float? BattingRatingImpact { get; set; }       // points × opp strength × league weighting

        // ── Bowling (Chain 2) ─────────────────────────────────────────
        public ushort BowlingBallsBowled { get; set; }
        public ushort BowlingRunsConceded { get; set; }
        public byte BowlingWickets { get; set; }
        public float? BowlingRpball { get; set; }
        public float? BowlingImprovement { get; set; }        // NULL if runs_conceded = 0
        public float? BowlingBaseEconomyPts { get; set; }
        public float? BowlingScalingFactor { get; set; }
        public float? BowlingWeightedEconomyPts { get; set; }
        public float? BowlingWicketPoints { get; set; }
        public float? BowlingBasePoints { get; set; }
        public float? BowlingRawPoints { get; set; }          // audit — before 300 cap
        public float? BowlingMatchPoints { get; set; }        // min(300, raw); NULL if balls_bowled = 0
        public float? BowlingRatingImpact { get; set; }

        // ── Fielding (Chain 3) — raw, never opposition-adjusted ──────
        public byte FieldingCatches { get; set; }
        public byte FieldingRunOuts { get; set; }
        public byte FieldingStumpings { get; set; }
        public byte FieldingDoublePlays { get; set; }
        public float FieldingPoints { get; set; }

        // ── Combined (Chains 4 & 5) — NULL treated as 0 in sums ──────
        public float AllRounderMatchPoints { get; set; }
        public float OppositionStrength { get; set; } = 1.0f;        // locked pre-match
        public float LeagueStrengthWeighting { get; set; } = 1.0f;   // 1.00 standard, >1 blue ribbon
        public ushort ParticipationPoints { get; set; }              // 150 if confirmed email
        public float LegendsPoints { get; set; }                     // RAW bat+bowl+field+participation

        // ── Context (SQL Server lookup) ──────────────────────────────
        public uint LeagueId { get; set; }
        public uint DivisionId { get; set; }
        public uint SeasonId { get; set; }
        public string SeasonName { get; set; } = "";
        public uint VenueId { get; set; }
        public uint RegionId { get; set; }
        public byte CountryId { get; set; }
        public DateTime GameDate { get; set; }
    }
}
