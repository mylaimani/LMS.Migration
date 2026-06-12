namespace LMS.Migration.Core.Models
{
    /// <summary>One row per highlight video clip (lms.clips), sourced from
    /// the SQL Server Highlights table.</summary>
    public class ClipRecord
    {
        public ulong ClipId { get; set; }
        public uint FixtureId { get; set; }
        public byte InningsNumber { get; set; }
        public byte OverNumber { get; set; }
        public byte BallSequence { get; set; }
        public DateTime BallTimestamp { get; set; } = DateTime.UnixEpoch;
        public string ClipUrl { get; set; } = "";
        public string ClipType { get; set; } = "";       // six / four / wicket
        public uint BowlerId { get; set; }
        public uint StrikerId { get; set; }
        public uint NonStrikerId { get; set; }
        public uint KeeperId { get; set; }
        public uint FielderId { get; set; }
        public string WicketType { get; set; } = "";
        public byte IsSix { get; set; }
        public ushort DurationSecs { get; set; }
        public uint LeagueId { get; set; }
        public uint SeasonId { get; set; }
        public DateTime GameDate { get; set; } = DateTime.UnixEpoch;
    }
}
