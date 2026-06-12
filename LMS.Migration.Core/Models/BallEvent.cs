namespace LMS.Migration.Core.Models
{
    /// <summary>One row per ball bowled (lms.ball_events).</summary>
    public class BallEvent
    {
        // Identity
        public uint FixtureId { get; set; }
        public byte InningsNumber { get; set; }
        public byte OverNumber { get; set; }
        public byte BallSequence { get; set; }
        public DateTime BallTimestamp { get; set; }

        // Players
        public uint BowlerId { get; set; }
        public uint StrikerId { get; set; }
        public uint NonStrikerId { get; set; }
        public uint FielderId { get; set; }
        public uint KeeperId { get; set; }
        public byte BattingPosition { get; set; }
        public uint BattingTeamId { get; set; }
        public uint BowlingTeamId { get; set; }

        // Runs / extras
        public byte RunsOffBat { get; set; }
        public byte ExtrasWide { get; set; }
        public byte ExtrasNoBall { get; set; }
        public byte ExtrasLegBye { get; set; }
        public byte ExtrasBye { get; set; }

        // Wicket
        public bool IsWicket { get; set; }
        public string WicketType { get; set; } = "";

        // LMS-specific
        public byte HomeRuns { get; set; }
        public byte Steal { get; set; }
        public byte DoublePlay { get; set; }
        public byte BallsPerOver { get; set; } = 5;
        public byte PitchCondition { get; set; }

        // Running state (computed during parse)
        public ushort ScoreAtBall { get; set; }
        public byte WicketsAtBall { get; set; }
        public DateTime GameDate { get; set; }

        // Context — one SQL Server lookup per fixture
        // (no Competition table: League + Division + Season)
        public uint LeagueId { get; set; }
        public uint DivisionId { get; set; }
        public uint SeasonId { get; set; }
        public string SeasonName { get; set; } = "";
        public uint VenueId { get; set; }
        public uint RegionId { get; set; }
        public byte CountryId { get; set; }

        // LMS Pulse — computed by the win-predictor model after each ball.
        // TODO: wire up the Pulse model; 0 until implemented.
        public float PulseAfterPct { get; set; }
        public float PulseChangePct { get; set; }

        // Convenience flags used by parser/extractor logic only.
        // NOT inserted into ClickHouse — the table derives its own
        // MATERIALIZED columns (is_legal_ball, is_dot_ball, is_boundary,
        // is_six, over_phase) at insert time.
        public bool IsLegalBall => ExtrasWide == 0 && ExtrasNoBall == 0;
        public bool IsBoundary => RunsOffBat == 4;
        public bool IsSix => RunsOffBat == 6;
    }
}
