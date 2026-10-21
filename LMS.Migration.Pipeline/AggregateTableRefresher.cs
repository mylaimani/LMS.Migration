using System.Data.Common;
using System.Text;
using ClickHouse.Client.ADO;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// By Mani 21 Oct 2026
    /// Keeps the five SummingMergeTree tables (h2h_stats, player_batting_phase,
    /// player_bowling_phase, team_phase, league_avg) correct when ONE fixture is
    /// deleted and written again.
    ///
    /// Why: their materialized views only ever ADD on insert into ball_events. Deleting
    /// the fixture's balls does not take its numbers back out, so a re-saved match was
    /// counted twice until rebuild_mvs.sql rebuilt everything. The counters are UInt64,
    /// so negative "retract" rows are not possible either.
    ///
    /// How: for each table, remember which key rows the fixture touched BEFORE the
    /// delete (CaptureKeysAsync), then AFTER the new balls are in, delete those key rows
    /// (plus any new keys) and insert them again summed from ball_events. The query per
    /// table is the materialized view's own SELECT from clickhouse_schema.sql, limited
    /// to the affected keys.
    /// </summary>
    public class AggregateTableRefresher
    {
        private sealed class AggregateTable
        {
            public string Name = "";
            public string[] KeyColumns = Array.Empty<string>();
            public string AggregateSelect = "";
        }

        private static readonly AggregateTable[] Tables =
        {
            new AggregateTable
            {
                Name = "h2h_stats",
                KeyColumns = new[] { "bowler_id", "striker_id" },
                AggregateSelect = "sum(is_legal_ball), sum(runs_off_bat), sum(is_wicket), sum(is_six), sum(is_boundary), sum(is_dot_ball)"
            },
            new AggregateTable
            {
                Name = "player_batting_phase",
                KeyColumns = new[] { "striker_id", "season_id", "league_id", "division_id", "venue_id", "over_phase" },
                AggregateSelect = "sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket), sum(is_boundary), sum(is_six), sum(is_dot_ball)"
            },
            new AggregateTable
            {
                Name = "player_bowling_phase",
                KeyColumns = new[] { "bowler_id", "season_id", "league_id", "division_id", "venue_id", "over_phase" },
                AggregateSelect = "sum(runs_off_bat + extras_wide + extras_no_ball), sum(is_legal_ball), sum(is_wicket), sum(is_dot_ball), "
                                + "countIf(extras_wide > 0), countIf(extras_no_ball > 0), "
                                + "countIf(runs_off_bat = 6 AND is_legal_ball = 1), countIf(runs_off_bat = 4 AND is_legal_ball = 1), "
                                + "countIf(runs_off_bat = 3 AND is_legal_ball = 1), countIf(runs_off_bat = 2 AND is_legal_ball = 1), "
                                + "countIf(runs_off_bat = 1 AND is_legal_ball = 1)"
            },
            new AggregateTable
            {
                Name = "team_phase",
                KeyColumns = new[] { "batting_team_id", "season_id", "league_id", "venue_id", "over_phase" },
                AggregateSelect = "sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket), sum(is_boundary)"
            },
            new AggregateTable
            {
                Name = "league_avg",
                KeyColumns = new[] { "league_id", "division_id", "region_id", "country_id", "season_id", "over_phase" },
                AggregateSelect = "sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket), sum(is_boundary), sum(is_six)"
            }
        };

        private readonly string _connectionString;
        private readonly string _database;

        public AggregateTableRefresher(string connectionString, string databaseName = "LMSClickHouseDB")
        {
            _connectionString = connectionString;
            _database = databaseName;
        }

        /// <summary>Key rows each table currently holds for this fixture. Call BEFORE deleting the fixture.</summary>
        public async Task<Dictionary<string, HashSet<string>>> CaptureKeysAsync(uint fixtureId)
        {
            var keysPerTable = new Dictionary<string, HashSet<string>>();
            using var connection = new ClickHouseConnection(_connectionString);
            await connection.OpenAsync();

            foreach (AggregateTable table in Tables)
                keysPerTable[table.Name] = await ReadKeyTuplesAsync(connection, table, fixtureId);

            return keysPerTable;
        }

        /// <summary>
        /// Call AFTER the fixture's new rows are in ball_events. Rewrites every affected key
        /// row in the five tables from ball_events, so each key is counted exactly once.
        /// </summary>
        public async Task RefreshAsync(uint fixtureId, Dictionary<string, HashSet<string>> keysBeforeDelete)
        {
            using var connection = new ClickHouseConnection(_connectionString);
            await connection.OpenAsync();

            foreach (AggregateTable table in Tables)
            {
                HashSet<string> affectedKeys = await ReadKeyTuplesAsync(connection, table, fixtureId);
                if (keysBeforeDelete.TryGetValue(table.Name, out HashSet<string>? oldKeys))
                    affectedKeys.UnionWith(oldKeys);
                if (affectedKeys.Count == 0)
                    continue;

                string keyList = string.Join(", ", table.KeyColumns);
                string tupleList = string.Join(", ", affectedKeys);
                await ExecuteAsync(connection,
                    $"DELETE FROM {_database}.{table.Name} WHERE ({keyList}) IN ({tupleList})");
                await ExecuteAsync(connection,
                    $"INSERT INTO {_database}.{table.Name} SELECT {keyList}, {table.AggregateSelect} " +
                    $"FROM {_database}.ball_events WHERE ({keyList}) IN ({tupleList}) GROUP BY {keyList}");
            }
        }

        private async Task<HashSet<string>> ReadKeyTuplesAsync(ClickHouseConnection connection, AggregateTable table, uint fixtureId)
        {
            var tuples = new HashSet<string>();
            string keyList = string.Join(", ", table.KeyColumns);

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT {keyList} FROM {_database}.ball_events WHERE fixture_id = {fixtureId}";

            using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tuples.Add(FormatTuple(reader, table.KeyColumns.Length));

            return tuples;
        }

        private static string FormatTuple(DbDataReader reader, int columnCount)
        {
            var tuple = new StringBuilder("(");
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                if (columnIndex > 0) tuple.Append(", ");
                object value = reader.GetValue(columnIndex);
                if (value is string text)
                    tuple.Append('\'').Append(text.Replace("'", "\\'")).Append('\'');
                else
                    tuple.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            return tuple.Append(')').ToString();
        }

        private static async Task ExecuteAsync(ClickHouseConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
