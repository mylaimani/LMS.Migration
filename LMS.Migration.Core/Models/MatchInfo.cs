namespace LMS.Migration.Core.Models
{
    /// <summary>Match-level facts extracted from FixtureState JSON.</summary>
    public class MatchInfo
    {
        public uint FixtureId { get; set; }
        public DateTime GameDate { get; set; }
        public byte BallsPerOver { get; set; } = 5;

        /// <summary>Root.MatchResult if present in the JSON (raw value).</summary>
        public string? MatchResultRaw { get; set; }

        /// <summary>True when abandoned / rained out / no result.</summary>
        public bool IsNoResult { get; set; }

        /// <summary>Team id of the winner; 0 = tie or no result.</summary>
        public uint WinningTeamId { get; set; }

        /// <summary>Total runs both innings ÷ total legal balls both innings.</summary>
        public float MatchRpball { get; set; }
    }
}
