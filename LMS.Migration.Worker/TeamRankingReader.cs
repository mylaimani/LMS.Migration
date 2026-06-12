using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Reads team world ranking snapshots:
    ///   StatisticsLMSRankingDate          (Id, DateTime, Completed)  — snapshot calendar (~twice weekly)
    ///   StatisticsLMSWorldTeamRanking     (DateId, TeamId, Position, Points)
    /// Used for the opposition strength formula:
    ///   Ranking Strength = 1 + 0.25 × (1 − (Rank − 1) ÷ 999).
    /// </summary>
    public class TeamRankingReader
    {
        private readonly string _connectionString;

        public TeamRankingReader(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>All completed snapshot dates, oldest first.</summary>
        public async Task<List<(int DateId, DateTime Date)>> LoadSnapshotDatesAsync()
        {
            const string query = @"
                SELECT Id, [DateTime]
                FROM StatisticsLMSRankingDate
                WHERE Completed = 1
                ORDER BY [DateTime]";

            var list = new List<(int, DateTime)>();

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(query, conn);
            cmd.CommandTimeout = 300;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add((reader.GetInt32(0), reader.GetDateTime(1)));

            return list;
        }

        /// <summary>TeamId → Position for one snapshot (~14k rows).</summary>
        public async Task<Dictionary<uint, int>> LoadSnapshotAsync(int dateId)
        {
            const string query = @"
                SELECT TeamId, Position
                FROM StatisticsLMSWorldTeamRanking
                WHERE DateId = @DateId";

            var map = new Dictionary<uint, int>();

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(query, conn);
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("@DateId", dateId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                map[Convert.ToUInt32(reader["TeamId"])] = Convert.ToInt32(reader["Position"]);

            return map;
        }
    }

    /// <summary>
    /// Provides each match with the team rankings from the latest published
    /// snapshot BEFORE that match (spec §3.3: "latest published rankings
    /// snapshot available at the time"). Fixtures must be processed in
    /// chronological order; snapshots are loaded just-in-time, one at a time
    /// (~14k rows in memory).
    /// </summary>
    public class HistoricalTeamRankingProvider
    {
        private readonly TeamRankingReader _reader;
        private List<(int DateId, DateTime Date)> _snapshots = new();
        private int _index = -1;                      // currently loaded snapshot
        private Dictionary<uint, int> _current = new();

        public HistoricalTeamRankingProvider(TeamRankingReader reader)
        {
            _reader = reader;
        }

        public async Task InitAsync()
        {
            _snapshots = await _reader.LoadSnapshotDatesAsync();
        }

        /// <summary>
        /// Ensures the loaded snapshot is the latest one dated on/before
        /// <paramref name="matchDate"/>. Skips intermediate snapshots when
        /// jumping forward in time (loads only the one that applies).
        /// </summary>
        public async Task AdvanceToAsync(DateTime matchDate)
        {
            int target = _index;
            while (target + 1 < _snapshots.Count && _snapshots[target + 1].Date <= matchDate)
                target++;

            if (target != _index && target >= 0)
            {
                _current = await _reader.LoadSnapshotAsync(_snapshots[target].DateId);
                _index = target;
            }
        }

        /// <summary>
        /// World rank of the team in the applicable snapshot.
        /// Unranked / pre-first-snapshot / beyond 1000 → 1000
        /// (ranking strength floor of 1.000).
        /// </summary>
        public int GetRank(uint teamId) =>
            _current.TryGetValue(teamId, out var pos) ? Math.Min(pos, 1000) : 1000;

        public int SnapshotCount => _snapshots.Count;
    }
}
