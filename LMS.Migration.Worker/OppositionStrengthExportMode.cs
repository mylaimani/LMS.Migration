// By Mani
using System.Data;
using ClickHouse.Client.ADO;
using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Export mode:  .\LMS.Migration.Worker.exe export-opposition pairs [fromYear] [toYear]
    ///               .\LMS.Migration.Worker.exe export-opposition results [fromYear] [toYear]
    ///
    /// Copies ball-by-ball opposition data from ClickHouse into Abser's SQL Server tables so his rating
    /// rebuild can join to them directly (no API calls):
    ///   pairs   -> dbo.Rep_PlayerOppositionBalls     balls per batsman v bowler per fixture (option B)
    ///   results -> dbo.Rep_PlayerOppositionStrength  finished rows from player_opposition_strength (option A)
    ///
    /// Works one calendar year at a time and deletes that year's SQL rows before loading it, so a run that
    /// stops part-way can simply be started again. Years default to 2015 .. current year.
    /// Needs LMS_CH_CONN (ClickHouse source) and LMS_RATING_SQL_CONN (SQL Server destination).
    /// </summary>
    public static class OppositionStrengthExportMode
    {
        private const int FirstYearWithBallByBall = 2015;
        private const int BulkCopyBatchSize = 50_000;

        // LMS over-counting rule, same as BallRules.CountsAsBall / BallEvent.CountsAsBall.
        private const string CountsAsBall = "(is_legal_ball = 1 OR extras_wide >= 3 OR extras_no_ball >= 3)";

        public static async Task RunAsync(string[] args, string clickHouseConnectionString, string clickHouseDatabase)
        {
            string? sqlConnectionString = Environment.GetEnvironmentVariable("LMS_RATING_SQL_CONN");
            if (string.IsNullOrEmpty(sqlConnectionString))
            {
                Console.WriteLine("Environment variable LMS_RATING_SQL_CONN is not set (SQL Server destination).");
                return;
            }

            string whatToExport = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (whatToExport != "pairs" && whatToExport != "results")
            {
                Console.WriteLine("Usage: LMS.Migration.Worker.exe export-opposition pairs|results [fromYear] [toYear]");
                return;
            }

            int fromYear = args.Length > 2 && int.TryParse(args[2], out int parsedFrom) ? parsedFrom : FirstYearWithBallByBall;
            int toYear = args.Length > 3 && int.TryParse(args[3], out int parsedTo) ? parsedTo : DateTime.UtcNow.Year;

            long totalRows = 0;
            for (int year = fromYear; year <= toYear; year++)
            {
                long rowsForYear = whatToExport == "pairs"
                    ? await ExportPairsForYearAsync(year, clickHouseConnectionString, clickHouseDatabase, sqlConnectionString)
                    : await ExportResultsForYearAsync(year, clickHouseConnectionString, clickHouseDatabase, sqlConnectionString);
                totalRows += rowsForYear;
                Console.WriteLine($"[OK] {year}: {rowsForYear:N0} rows");
            }

            Console.WriteLine($"Done. {totalRows:N0} rows exported ({whatToExport}).");
        }

        // ── pairs ────────────────────────────────────────────────────────────────

        private static async Task<long> ExportPairsForYearAsync(int year, string clickHouseConnectionString, string clickHouseDatabase, string sqlConnectionString)
        {
            DataTable pairsTable = CreatePairsTable();
            await FillPairsTableAsync(pairsTable, year, clickHouseConnectionString, clickHouseDatabase);

            await DeleteYearAsync("dbo.Rep_PlayerOppositionBalls", "GameDate", year, sqlConnectionString);
            await BulkCopyAsync(pairsTable, "dbo.Rep_PlayerOppositionBalls", sqlConnectionString);
            return pairsTable.Rows.Count;
        }

        private static DataTable CreatePairsTable()
        {
            var table = new DataTable();
            table.Columns.Add("FixtureId", typeof(int));
            table.Columns.Add("GameDate", typeof(DateTime));
            table.Columns.Add("BatsmanId", typeof(int));
            table.Columns.Add("BowlerId", typeof(int));
            table.Columns.Add("BattingTeamId", typeof(int));
            table.Columns.Add("BowlingTeamId", typeof(int));
            table.Columns.Add("BallsCounted", typeof(short));
            table.Columns.Add("DeliveriesCounted", typeof(short));   // 15 Oct 2026 - every delivery, re-bowled ones included
            return table;
        }

        private static async Task FillPairsTableAsync(DataTable pairsTable, int year, string clickHouseConnectionString, string clickHouseDatabase)
        {
            string sql = $@"
                SELECT
                    fixture_id, any(game_date) AS fixture_game_date,
                    striker_id, bowler_id,
                    any(batting_team_id) AS batting_team_id,
                    any(bowling_team_id) AS bowling_team_id,
                    sum(toUInt64({CountsAsBall})) AS balls_counted,
                    count() AS deliveries_counted
                FROM {clickHouseDatabase}.ball_events
                WHERE toYear(game_date) = {year}
                  AND striker_id != 0 AND bowler_id != 0
                GROUP BY fixture_id, striker_id, bowler_id";
            // 15 Oct 2026: no HAVING balls_counted > 0 any more - a pair whose only delivery was a re-bowled
            // wide/no-ball is written with BallsCounted 0, DeliveriesCounted 1 (uplift now weights by deliveries).

            using var connection = new ClickHouseConnection(clickHouseConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pairsTable.Rows.Add(
                    Convert.ToInt32(reader.GetValue(0)),
                    ReadDate(reader.GetValue(1)),
                    Convert.ToInt32(reader.GetValue(2)),
                    Convert.ToInt32(reader.GetValue(3)),
                    Convert.ToInt32(reader.GetValue(4)),
                    Convert.ToInt32(reader.GetValue(5)),
                    Convert.ToInt16(reader.GetValue(6)),
                    Convert.ToInt16(reader.GetValue(7)));
            }
        }

        // ── results ──────────────────────────────────────────────────────────────

        private static async Task<long> ExportResultsForYearAsync(int year, string clickHouseConnectionString, string clickHouseDatabase, string sqlConnectionString)
        {
            DataTable resultsTable = CreateResultsTable();
            await FillResultsTableAsync(resultsTable, year, clickHouseConnectionString, clickHouseDatabase);

            await DeleteYearAsync("dbo.Rep_PlayerOppositionStrength", "GameDate", year, sqlConnectionString);
            await BulkCopyAsync(resultsTable, "dbo.Rep_PlayerOppositionStrength", sqlConnectionString);
            return resultsTable.Rows.Count;
        }

        private static DataTable CreateResultsTable()
        {
            var table = new DataTable();
            table.Columns.Add("FixtureId", typeof(int));
            table.Columns.Add("UserId", typeof(int));
            table.Columns.Add("Role", typeof(string));
            table.Columns.Add("GameDate", typeof(DateTime));
            table.Columns.Add("TeamId", typeof(int));
            table.Columns.Add("BallsCounted", typeof(short));
            table.Columns.Add("OpponentsCounted", typeof(byte));
            table.Columns.Add("OpponentsWithoutRating", typeof(byte));
            table.Columns.Add("EffectiveOppositionRating", typeof(decimal));
            table.Columns.Add("UpliftPercent", typeof(decimal));
            table.Columns.Add("EffectiveOppositionRatingRaw", typeof(decimal));
            table.Columns.Add("UpliftPercentRaw", typeof(decimal));
            table.Columns.Add("RatingSource", typeof(string));
            table.Columns.Add("FormulaVersion", typeof(short));
            table.Columns.Add("CalculatedAt", typeof(DateTime));
            return table;
        }

        private static async Task FillResultsTableAsync(DataTable resultsTable, int year, string clickHouseConnectionString, string clickHouseDatabase)
        {
            // FINAL keeps only the latest row per player/fixture/role (ReplacingMergeTree).
            string sql = $@"
                SELECT fixture_id, player_id, role, game_date, team_id, balls_counted, opponents_counted,
                       opponents_without_rating, effective_opposition_rating, uplift_percent,
                       rating_source, formula_version, calculated_at
                FROM {clickHouseDatabase}.player_opposition_strength FINAL
                WHERE toYear(game_date) = {year}";

            using var connection = new ClickHouseConnection(clickHouseConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                resultsTable.Rows.Add(
                    Convert.ToInt32(reader.GetValue(0)),
                    Convert.ToInt32(reader.GetValue(1)),
                    Convert.ToString(reader.GetValue(2)),
                    ReadDate(reader.GetValue(3)),
                    Convert.ToInt32(reader.GetValue(4)),
                    Convert.ToInt16(reader.GetValue(5)),
                    Convert.ToByte(reader.GetValue(6)),
                    Convert.ToByte(reader.GetValue(7)),
                    Math.Round(Convert.ToDecimal(reader.GetValue(8)), 2, MidpointRounding.AwayFromZero),
                    Math.Round(Convert.ToDecimal(reader.GetValue(9)), 2, MidpointRounding.AwayFromZero),
                    Convert.ToDecimal(reader.GetValue(8)),   // ClickHouse test table holds 2 dp only
                    Convert.ToDecimal(reader.GetValue(9)),
                    Convert.ToString(reader.GetValue(10)),
                    Convert.ToInt16(reader.GetValue(11)),
                    Convert.ToDateTime(reader.GetValue(12)));
            }
        }

        // ── shared ───────────────────────────────────────────────────────────────

        private static async Task DeleteYearAsync(string tableName, string dateColumn, int year, string sqlConnectionString)
        {
            using var connection = new SqlConnection(sqlConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(
                $"DELETE FROM {tableName} WHERE {dateColumn} >= @YearStart AND {dateColumn} < @NextYearStart", connection);
            command.CommandTimeout = 600;
            command.Parameters.AddWithValue("@YearStart", new DateTime(year, 1, 1));
            command.Parameters.AddWithValue("@NextYearStart", new DateTime(year + 1, 1, 1));
            await command.ExecuteNonQueryAsync();
        }

        private static async Task BulkCopyAsync(DataTable table, string destinationTable, string sqlConnectionString)
        {
            if (table.Rows.Count == 0)
            {
                return;
            }

            using var connection = new SqlConnection(sqlConnectionString);
            await connection.OpenAsync();
            using var bulkCopy = new SqlBulkCopy(connection)
            {
                DestinationTableName = destinationTable,
                BatchSize = BulkCopyBatchSize,
                BulkCopyTimeout = 600
            };
            foreach (DataColumn column in table.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }
            await bulkCopy.WriteToServerAsync(table);
        }

        /// <summary>ClickHouse Date can arrive as DateTime or DateOnly depending on the client settings.</summary>
        private static DateTime ReadDate(object value)
        {
            if (value is DateOnly dateOnly)
            {
                return dateOnly.ToDateTime(TimeOnly.MinValue);
            }
            return Convert.ToDateTime(value);
        }
    }
}
// End of Mani
