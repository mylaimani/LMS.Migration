namespace LMS.PremiumProfile.Api.Models;

// ── Top-level response ──────────────────────────────────────────────────────
public class BattingProfileResponse
{
    public uint   PlayerId  { get; set; }
    public uint?  SeasonId  { get; set; }
    public uint?  LeagueId  { get; set; }

    /// <summary>Powerplay / Middle / Death batting stats.</summary>
    public List<PhaseStatRow>   PhaseStats       { get; set; } = [];

    /// <summary>Run distribution + over-by-over trend.</summary>
    public ScoringPattern       ScoringPattern   { get; set; } = new();

    /// <summary>Bowlers this batter scores fastest against (min 10 balls).</summary>
    public List<H2HBowlerRow>   FavouriteBowlers { get; set; } = [];

    /// <summary>Bowlers who have dismissed this batter most (min 10 balls).</summary>
    public List<H2HBowlerRow>   NemesisBowlers   { get; set; } = [];

    /// <summary>Best batting partners (sorted by total runs together).</summary>
    public List<PartnershipRow> Partnerships     { get; set; } = [];
}

// ── Phase stats ─────────────────────────────────────────────────────────────
public class PhaseStatRow
{
    /// <summary>Powerplay | Middle | Death</summary>
    public string Phase       { get; set; } = "";
    public ulong  Runs        { get; set; }
    public ulong  Balls       { get; set; }   // legal balls faced
    public ulong  Dismissals  { get; set; }
    public ulong  Boundaries  { get; set; }
    public ulong  Sixes       { get; set; }
    public ulong  Dots        { get; set; }

    // computed
    public double Average     => Dismissals > 0 ? Math.Round((double)Runs / Dismissals, 2) : 0;
    public double StrikeRate  => Balls > 0 ? Math.Round((double)Runs / Balls * 100, 2) : 0;
    public double BoundaryPct => Balls > 0 ? Math.Round((double)Boundaries / Balls * 100, 1) : 0;
    public double SixPct      => Balls > 0 ? Math.Round((double)Sixes / Balls * 100, 1) : 0;
    public double DotPct      => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
}

// ── Scoring pattern ─────────────────────────────────────────────────────────
public class ScoringPattern
{
    public RunDistribution  Distribution { get; set; } = new();

    /// <summary>Run rate per over (over 1 → last over).</summary>
    public List<OverTrendRow> OverTrend { get; set; } = [];
}

public class RunDistribution
{
    public ulong Dots      { get; set; }
    public ulong Ones      { get; set; }
    public ulong Twos      { get; set; }
    public ulong Threes    { get; set; }
    public ulong Fours     { get; set; }
    public ulong Sixes     { get; set; }
    public ulong HomeRuns  { get; set; }
    public ulong Steals    { get; set; }
    public ulong TotalRuns { get; set; }
    public ulong TotalBalls { get; set; }
    public double OverallStrikeRate => TotalBalls > 0
        ? Math.Round((double)TotalRuns / TotalBalls * 100, 2) : 0;
}

public class OverTrendRow
{
    /// <summary>1-indexed over number.</summary>
    public int    Over        { get; set; }
    public ulong  Runs        { get; set; }
    public ulong  LegalBalls  { get; set; }
    public double RunsPerBall => LegalBalls > 0 ? Math.Round((double)Runs / LegalBalls, 3) : 0;
    public double RunRate     => Math.Round(RunsPerBall * 5, 2);  // per over (LMS = 5 balls)
}

// ── H2H bowler rows ─────────────────────────────────────────────────────────
public class H2HBowlerRow
{
    public uint   BowlerId   { get; set; }
    public ulong  Balls      { get; set; }
    public ulong  Runs       { get; set; }
    public ulong  Wickets    { get; set; }
    public ulong  Sixes      { get; set; }
    public ulong  Boundaries { get; set; }
    public ulong  Dots       { get; set; }

    public double StrikeRate  => Balls > 0 ? Math.Round((double)Runs / Balls * 100, 2) : 0;
    public double DotPct      => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
}

// ── Partnership rows ─────────────────────────────────────────────────────────
public class PartnershipRow
{
    public uint   PartnerId         { get; set; }
    public long   PartnershipCount  { get; set; }
    public ulong  TotalRuns         { get; set; }
    public ulong  TotalBalls        { get; set; }
    public ulong  TotalFours        { get; set; }
    public ulong  TotalSixes        { get; set; }

    public double AvgRunsTogether  => PartnershipCount > 0
        ? Math.Round((double)TotalRuns / PartnershipCount, 1) : 0;
    public double RunRate          => TotalBalls > 0
        ? Math.Round((double)TotalRuns / TotalBalls * 5, 2) : 0;  // per over
}
