namespace LMS.PremiumProfile.Api.Models;

/// <summary>
/// Highlight video clips for a single fixture, sourced from lms.clips.
/// lms.clips is populated by the migration worker's "clips" mode
/// (from the SQL Server Highlights table).
/// </summary>
public class ClipsResponse
{
    public uint FixtureId { get; set; }
    public List<ClipRow> Clips { get; set; } = [];
}

public class ClipRow
{
    public ulong    ClipId        { get; set; }
    public byte     InningsNumber { get; set; }
    public byte     OverNumber    { get; set; }
    public byte     BallSequence  { get; set; }
    /// <summary>UTC timestamp of the ball in the live feed.</summary>
    public DateTime BallTimestamp { get; set; }
    /// <summary>URL of the highlight video clip.</summary>
    public string   ClipUrl       { get; set; } = "";
    /// <summary>Event type: "six", "four", "wicket".</summary>
    public string   ClipType      { get; set; } = "";
    public uint     BowlerId      { get; set; }
    public uint     StrikerId     { get; set; }
    public uint     NonStrikerId  { get; set; }
    public uint     KeeperId      { get; set; }
    public uint     FielderId     { get; set; }
    public string   WicketType    { get; set; } = "";
    public bool     IsSix         { get; set; }
    public ushort   DurationSecs  { get; set; }
}
