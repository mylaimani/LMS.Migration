using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    public class FixtureMetadata
    {
        public uint FixtureId { get; set; }
        public uint LeagueId { get; set; }
        public string LeagueName { get; set; } = "";
        public uint DivisionId { get; set; }
        public uint SeasonId { get; set; }
        public string SeasonName { get; set; } = "";
        public uint VenueId { get; set; }
        public string VenueName { get; set; } = "";
        public uint RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public byte CountryId { get; set; }
        public string CountryName { get; set; } = "";
        public DateTime FixtureDate { get; set; }
        public bool RainedOut { get; set; }
    }

    /// <summary>
    /// Fixture context lookup (no Competition table — LMS uses League +
    /// Division + Season): fixture → FixtureLeagueDivisionRoundSeason →
    /// league/division/season; Fixture.VenueId → Venue → region → country;
    /// FixtureLMSExtraInformation → RainedOut.
    ///
    /// For bulk migration use LoadAllAsync() once (~273k rows) instead of
    /// 176k per-fixture queries.
    /// </summary>
    public class FixtureMetadataReader
    {
        private const string BaseQuery = @"
            SELECT
                f.Id                AS FixtureId,
                f.[DateTime]        AS FixtureDate,
                l.Id                AS LeagueId,
                l.Name              AS LeagueName,
                d.Id                AS DivisionId,
                s.Id                AS SeasonId,
                s.Name              AS SeasonName,
                v.Id                AS VenueId,
                v.Name              AS VenueName,
                r.Id                AS RegionId,
                r.Value             AS RegionName,
                c.Id                AS CountryId,
                c.Value             AS CountryName,
                ISNULL(x.RainedOut, 0) AS RainedOut
            FROM Fixture f
            LEFT JOIN FixtureLeagueDivisionRoundSeason fldrs ON fldrs.FixtureId = f.Id
            LEFT JOIN League l     ON l.Id = fldrs.LeagueId
            LEFT JOIN Division d   ON d.Id = fldrs.DivisionId
            LEFT JOIN Season s     ON s.Id = fldrs.SeasonId
            LEFT JOIN Venue v      ON v.Id = f.VenueId
            LEFT JOIN libRegion r  ON r.Id = v.RegionId
            LEFT JOIN libCountry c ON c.Id = r.CountryId
            LEFT JOIN FixtureLMSExtraInformation x ON x.FixtureId = f.Id";

        private readonly string _connectionString;

        public FixtureMetadataReader(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>All fixtures in one query — use for the bulk migration.</summary>
        public async Task<Dictionary<uint, FixtureMetadata>> LoadAllAsync()
        {
            var map = new Dictionary<uint, FixtureMetadata>();

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(BaseQuery, conn);
            cmd.CommandTimeout = 600;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var m = Map(reader);
                map[m.FixtureId] = m;
            }

            return map;
        }

        /// <summary>Single fixture — use in the future live (per-match) mode.</summary>
        public async Task<FixtureMetadata?> GetAsync(uint fixtureId)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(BaseQuery + " WHERE f.Id = @FixtureId", conn);
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("@FixtureId", (int)fixtureId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return Map(reader);
        }

        private static FixtureMetadata Map(SqlDataReader reader) => new()
        {
            FixtureId = ToUInt(reader["FixtureId"]),
            LeagueId = ToUInt(reader["LeagueId"]),
            LeagueName = reader["LeagueName"]?.ToString() ?? "",
            DivisionId = ToUInt(reader["DivisionId"]),
            SeasonId = ToUInt(reader["SeasonId"]),
            SeasonName = reader["SeasonName"]?.ToString() ?? "",
            VenueId = ToUInt(reader["VenueId"]),
            VenueName = reader["VenueName"]?.ToString() ?? "",
            RegionId = ToUInt(reader["RegionId"]),
            RegionName = reader["RegionName"]?.ToString() ?? "",
            CountryId = (byte)Math.Min(ToUInt(reader["CountryId"]), byte.MaxValue),
            CountryName = reader["CountryName"]?.ToString() ?? "",
            FixtureDate = reader["FixtureDate"] is DateTime dt ? dt : DateTime.UnixEpoch,
            RainedOut = reader["RainedOut"] != DBNull.Value && Convert.ToBoolean(reader["RainedOut"])
        };

        private static uint ToUInt(object? value) =>
            value == null || value == DBNull.Value ? 0u : Convert.ToUInt32(value);
    }
}
