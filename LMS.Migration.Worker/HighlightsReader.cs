using LMS.Migration.Core.Models;
using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Reads the Highlights table (one row per generated clip — sixes, fours,
    /// wickets) in keyset-paginated batches.
    /// Columns: Id, FixtureId, BatsmenId, BowlerId, KeeperId, FielderId,
    ///          Innings, Result, DateTime, ClipUrl, Ball, Over.
    /// </summary>
    public class HighlightsReader
    {
        private const int BatchSize = 5000;
        private readonly string _connectionString;

        public HighlightsReader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async IAsyncEnumerable<ClipRecord> ReadAllAsync()
        {
            long lastId = 0;

            while (true)
            {
                var batch = new List<ClipRecord>(BatchSize);

                using (var conn = new SqlConnection(_connectionString))
                {
                    await conn.OpenAsync();

                    using var cmd = new SqlCommand(@"
                        SELECT TOP (@batch)
                            Id, FixtureId, BatsmenId, BowlerId, KeeperId,
                            FielderId, Innings, Result, [DateTime], ClipUrl,
                            Ball, [Over]
                        FROM Highlights
                        WHERE Id > @after
                        ORDER BY Id", conn);
                    cmd.Parameters.AddWithValue("@batch", BatchSize);
                    cmd.Parameters.AddWithValue("@after", lastId);
                    cmd.CommandTimeout = 300;

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        lastId = Convert.ToInt64(reader["Id"]);

                        var result = reader["Result"]?.ToString()?.Trim() ?? "";
                        var (clipType, wicketType) = ClassifyResult(result);
                        batch.Add(new ClipRecord
                        {
                            ClipId = (ulong)lastId,
                            FixtureId = ToUInt(reader["FixtureId"]),
                            StrikerId = ToUInt(reader["BatsmenId"]),
                            BowlerId = ToUInt(reader["BowlerId"]),
                            KeeperId = ToUInt(reader["KeeperId"]),
                            FielderId = ToUInt(reader["FielderId"]),
                            InningsNumber = (byte)Math.Min(ToUInt(reader["Innings"]), byte.MaxValue),
                            // ClipGenerated IOver is 0-indexed (IOver=0 = Over 1)
                            // TODO: verify Highlights.Over uses the same convention
                            OverNumber = (byte)Math.Min(ToUInt(reader["Over"]) + 1, byte.MaxValue),
                            BallSequence = (byte)Math.Min(ToUInt(reader["Ball"]), byte.MaxValue),
                            BallTimestamp = reader["DateTime"] is DateTime dt ? dt : DateTime.UnixEpoch,
                            ClipUrl = reader["ClipUrl"]?.ToString() ?? "",
                            ClipType = clipType,
                            WicketType = wicketType,
                            IsSix = (byte)(clipType == "six" ? 1 : 0)
                        });
                    }
                }

                if (batch.Count == 0) yield break;
                foreach (var clip in batch) yield return clip;
            }
        }

        private static uint ToUInt(object? value) =>
            value == null || value == DBNull.Value ? 0u : Convert.ToUInt32(value);

        // Wicket tokens are UPPERCASE (C=caught, B=bowled, LBW, ST=stumped,
        // RO=run out, HW=hit wicket, DP=double play). Lowercase tokens are
        // extras (w=wide, nb=no-ball, b=byes, lb=leg byes). Examples:
        //   "4"=four, "4b"=four byes, "w_4"=wide+four, "nb_6"=six off no-ball,
        //   "RO_1"=run out with a run, "[C,Steal]"=caught + steal, "ST_w"=stumped off wide
        private static readonly string[] WicketTokens = { "C", "B", "LBW", "ST", "RO", "HW", "DP" };

        private static (string ClipType, string WicketType) ClassifyResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result) || result.Equals("null", StringComparison.OrdinalIgnoreCase))
                return ("other", "");

            var tokens = result.Split(new[] { '_', ',', '[', ']', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Wicket beats runs ("w_4_RO" is a wicket clip even with a four in it)
            bool isWicket = tokens.Any(t => WicketTokens.Contains(t));   // case-sensitive
            if (isWicket)
                return ("wicket", result);          // keep the raw code for detail

            if (tokens.Any(t => t == "6")) return ("six", "");
            if (tokens.Any(t => t == "4" || t == "4b" || t == "4lb")) return ("four", "");

            return ("other", "");                   // 0/1/2 runs, lone nb/w, etc.
        }
    }
}
