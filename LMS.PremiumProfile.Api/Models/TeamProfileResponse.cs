namespace LMS.PremiumProfile.Api.Models;

/// <summary>
/// Response for GET /api/team/{teamId}
/// Covers the premium Insights tab: phase analysis, league benchmark, partnerships, and all-time club greats.
/// </summary>
public class TeamProfileResponse
{
    public uint      TeamId   { get; set; }
    public uint?     SeasonId { get; set; }
    public uint?     LeagueId { get; set; }
    public int?      Year     { get; set; }
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate   { get; set; }

    /// <summary>Team batting stats per phase (Powerplay / Middle / Death).</summary>
    public List<TeamBattingPhaseRow>  BattingPhase    { get; set; } = [];

    /// <summary>Team bowling stats per phase.</summary>
    public List<TeamBowlingPhaseRow>  BowlingPhase    { get; set; } = [];

    /// <summary>Team batting run rate and bowling economy vs the league average, per phase.</summary>
    public List<PhaseBenchmarkRow>    Benchmark       { get; set; } = [];

    /// <summary>Top 10 batting partnerships within the team, ordered by total runs.</summary>
    public List<TeamPartnershipRow>   TopPartnerships { get; set; } = [];

    /// <summary>All-time record holders for this team (no date/season filter applied).</summary>
    public TeamClubGreats             ClubGreats      { get; set; } = new();
}

// ── Batting phase ─────────────────────────────────────────────────────────────
public class TeamBattingPhaseRow
{
    public string Phase      { get; set; } = "";
    public ulong  Runs       { get; set; }
    public ulong  Balls      { get; set; }
    public ulong  Wickets    { get; set; }
    public ulong  Boundaries { get; set; }
    public ulong  Sixes      { get; set; }
    public ulong  Dots       { get; set; }

    /// <summary>Runs per 5-ball over (LMS format).</summary>
    public double RunRate     => Balls > 0 ? Math.Round((double)Runs / Balls * 5, 2) : 0;
    public double StrikeRate  => Balls > 0 ? Math.Round((double)Runs / Balls * 100, 2) : 0;
    public double BoundaryPct => Balls > 0 ? Math.Round((double)Boundaries / Balls * 100, 1) : 0;
    public double SixPct      => Balls > 0 ? Math.Round((double)Sixes / Balls * 100, 1) : 0;
    public double DotPct      => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
}

// ── Bowling phase ─────────────────────────────────────────────────────────────
public class TeamBowlingPhaseRow
{
    public string Phase        { get; set; } = "";
    public ulong  RunsConceded { get; set; }
    public ulong  Balls        { get; set; }
    public ulong  Wickets      { get; set; }
    public ulong  Dots         { get; set; }
    public ulong  Wides        { get; set; }
    public ulong  NoBalls      { get; set; }

    /// <summary>Runs conceded per 5-ball over (LMS format).</summary>
    public double Economy    => Balls > 0 ? Math.Round((double)RunsConceded / Balls * 5, 2) : 0;
    public double StrikeRate => Wickets > 0 ? Math.Round((double)Balls / Wickets, 2) : 0;
    public double DotPct     => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
}

// ── Phase benchmark ───────────────────────────────────────────────────────────
/// <summary>
/// Compares this team's batting run rate and bowling economy (LMS: runs per 5 balls)
/// against the league-wide average for the same phase.
///
/// BattingEdge > 0 → team bats above the league average in this phase.
/// BowlingEdge > 0 → team bowls below the league average economy (better bowling).
/// </summary>
public class PhaseBenchmarkRow
{
    public string Phase              { get; set; } = "";

    public double TeamBattingRunRate { get; set; }
    public double LeagueAvgRunRate   { get; set; }
    public double BattingEdge        => Math.Round(TeamBattingRunRate - LeagueAvgRunRate, 2);

    public double TeamBowlingEconomy { get; set; }
    public double LeagueAvgEconomy   { get; set; }
    public double BowlingEdge        => Math.Round(LeagueAvgEconomy - TeamBowlingEconomy, 2);
}

// ── Partnerships ──────────────────────────────────────────────────────────────
public class TeamPartnershipRow
{
    public uint  Batter1Id        { get; set; }
    public uint  Batter2Id        { get; set; }
    public long  PartnershipCount { get; set; }
    public ulong TotalRuns        { get; set; }
    public ulong TotalBalls       { get; set; }
    public ulong TotalFours       { get; set; }
    public ulong TotalSixes       { get; set; }

    public double AvgRunsTogether => PartnershipCount > 0
        ? Math.Round((double)TotalRuns / PartnershipCount, 1) : 0;
    public double RunRate         => TotalBalls > 0
        ? Math.Round((double)TotalRuns / TotalBalls * 5, 2) : 0;
}

// ── Club Greats ───────────────────────────────────────────────────────────────
/// <summary>All-time record holders across six categories for this team.</summary>
public class TeamClubGreats
{
    public ClubGreatEntry? MostRuns        { get; set; }
    public ClubGreatEntry? MostWickets     { get; set; }
    public ClubGreatEntry? MostAppearances { get; set; }
    public ClubGreatEntry? MostSixes       { get; set; }
    public ClubGreatEntry? MostCatches     { get; set; }
    public ClubGreatEntry? MostHomeRuns    { get; set; }
}

public class ClubGreatEntry
{
    public uint  PlayerId { get; set; }
    /// <summary>Stat value (runs / wickets / appearances / sixes / catches / home runs).</summary>
    public ulong Value    { get; set; }
    /// <summary>Distinct fixtures this player appeared in for this team (in the relevant role).</summary>
    public ulong Games    { get; set; }
}
