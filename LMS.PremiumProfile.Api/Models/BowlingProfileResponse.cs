namespace LMS.PremiumProfile.Api.Models;

// ── Top-level response ──────────────────────────────────────────────────────
public class BowlingProfileResponse
{
    public uint      PlayerId  { get; set; }
    public uint?     SeasonId  { get; set; }
    public uint?     LeagueId  { get; set; }
    public int?      Year      { get; set; }
    public DateOnly? FromDate  { get; set; }
    public DateOnly? ToDate    { get; set; }

    /// <summary>Powerplay / Middle / Death bowling stats.</summary>
    public List<BowlingPhaseStatRow>   PhaseStats       { get; set; } = [];

    /// <summary>Economy + wicket trend by over.</summary>
    public BowlingPattern              BowlingPattern   { get; set; } = new();

    /// <summary>Batters this bowler concedes most runs against (min 10 balls).</summary>
    public List<H2HBatterRow>          FavouriteBatters { get; set; } = [];

    /// <summary>Batters this bowler dismisses most (min 10 balls).</summary>
    public List<H2HBatterRow>          NemesisBatters   { get; set; } = [];
}

// ── Phase stats ──────────────────────────────────────────────────────────────
public class BowlingPhaseStatRow
{
    /// <summary>Powerplay | Middle | Death</summary>
    public string Phase        { get; set; } = "";
    public ulong  RunsConceded { get; set; }
    public ulong  Balls        { get; set; }   // legal balls bowled
    public ulong  Wickets      { get; set; }
    public ulong  Dots         { get; set; }
    public ulong  Sixes        { get; set; }
    public ulong  Fours        { get; set; }
    public ulong  Threes       { get; set; }
    public ulong  Twos         { get; set; }
    public ulong  Ones         { get; set; }
    public ulong  Wides        { get; set; }
    public ulong  NoBalls      { get; set; }

    // computed
    /// <summary>Runs conceded per over (LMS = 5 legal balls per over).</summary>
    public double Economy      => Balls > 0 ? Math.Round((double)RunsConceded / Balls * 5, 2) : 0;
    public double StrikeRate   => Wickets > 0 ? Math.Round((double)Balls / Wickets, 2) : 0;
    public double DotPct       => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
    public double Average      => Wickets > 0 ? Math.Round((double)RunsConceded / Wickets, 2) : 0;
}

// ── Bowling pattern ──────────────────────────────────────────────────────────
public class BowlingPattern
{
    /// <summary>Aggregate bowling figures across all overs.</summary>
    public BowlingFigures Totals { get; set; } = new();

    /// <summary>Economy rate and wickets by over (1-indexed, capped at over 20).</summary>
    public List<BowlingOverTrendRow> OverTrend { get; set; } = [];
}

public class BowlingFigures
{
    public ulong  RunsConceded { get; set; }
    public ulong  Balls        { get; set; }   // legal balls only
    public ulong  Wickets      { get; set; }
    public ulong  Dots         { get; set; }
    public ulong  Sixes        { get; set; }
    public ulong  Fours        { get; set; }
    public ulong  Threes       { get; set; }
    public ulong  Twos         { get; set; }
    public ulong  Ones         { get; set; }
    public ulong  NoBalls      { get; set; }
    public ulong  Wides        { get; set; }

    public double Economy      => Balls > 0 ? Math.Round((double)RunsConceded / Balls * 5, 2) : 0;
    public double StrikeRate   => Wickets > 0 ? Math.Round((double)Balls / Wickets, 2) : 0;
    public double DotPct       => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
    public double Average      => Wickets > 0 ? Math.Round((double)RunsConceded / Wickets, 2) : 0;
}

public class BowlingOverTrendRow
{
    /// <summary>1-indexed over number.</summary>
    public int    Over          { get; set; }
    public ulong  RunsConceded  { get; set; }
    public ulong  LegalBalls    { get; set; }
    public ulong  Wickets       { get; set; }
    public double Economy       => LegalBalls > 0 ? Math.Round((double)RunsConceded / LegalBalls * 5, 2) : 0;
}

// ── H2H batter rows ──────────────────────────────────────────────────────────
public class H2HBatterRow
{
    public uint   StrikerId  { get; set; }
    public ulong  Balls      { get; set; }
    public ulong  Runs       { get; set; }
    public ulong  Wickets    { get; set; }
    public ulong  Sixes      { get; set; }
    public ulong  Boundaries { get; set; }
    public ulong  Dots       { get; set; }

    public double Economy    => Balls > 0 ? Math.Round((double)Runs / Balls * 5, 2) : 0;
    public double DotPct     => Balls > 0 ? Math.Round((double)Dots / Balls * 100, 1) : 0;
}
