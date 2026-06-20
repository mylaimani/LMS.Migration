namespace LMS.PremiumProfile.Api.Models;

/// <summary>
/// Career head-to-head stats for a specific bowler vs batter pair.
/// Sourced from lms.h2h_stats MV (fast path, career only) or
/// lms.ball_events (filtered path).
/// </summary>
public class H2HResponse
{
    public uint  BowlerId { get; set; }
    public uint  BatterId { get; set; }
    public uint? SeasonId { get; set; }
    public uint? LeagueId { get; set; }

    public H2HStats Stats { get; set; } = new();
}

public class H2HStats
{
    public ulong Balls      { get; set; }
    public ulong Runs       { get; set; }
    public ulong Wickets    { get; set; }
    public ulong Sixes      { get; set; }
    public ulong Boundaries { get; set; }
    public ulong Dots       { get; set; }

    /// <summary>Runs per 5-ball over (LMS format).</summary>
    public double RunRate    => Balls > 0 ? Math.Round((double)Runs / Balls * 5, 2) : 0;
    public double StrikeRate => Balls > 0 ? Math.Round((double)Runs / Balls * 100, 2) : 0;
    public double DotPct     => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
}
