namespace LMS.PremiumProfile.Api.Models;

/// <summary>
/// Ball-by-ball LMS Pulse (win probability) for a single fixture.
/// Sourced from lms.ball_events columns: pulse_after_pct, pulse_change_pct.
///
/// NOTE: pulse values are stored as Float32 and are currently 0 until the
/// win-predictor model is integrated into the migration worker.
/// </summary>
public class PulseResponse
{
    public uint FixtureId { get; set; }
    public List<PulseBallRow> Balls { get; set; } = [];
}

public class PulseBallRow
{
    public byte   InningsNumber { get; set; }
    public int    OverNumber    { get; set; }
    public int    BallInOver    { get; set; }
    public uint   BattingTeamId { get; set; }
    public uint   StrikerId     { get; set; }
    public uint   BowlerId      { get; set; }
    public int    RunsOffBat    { get; set; }
    public bool   IsWicket      { get; set; }
    public bool   IsSix         { get; set; }
    public bool   IsBoundary    { get; set; }
    public bool   IsHomeRun     { get; set; }
    /// <summary>Win probability (0–100) for the batting team after this ball.</summary>
    public float  PulseAfterPct  { get; set; }
    /// <summary>Change in win probability caused by this ball (+/-).</summary>
    public float  PulseChangePct { get; set; }
}
