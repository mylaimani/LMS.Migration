using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Streams FixtureState rows in keyset-paginated batches (fresh
    /// connection per batch) so a multi-hour run never depends on one
    /// long-lived connection. Transient SQL errors are retried.
    /// Order: fixtureid ASC — required for form guide / opposition strength.
    /// </summary>
    public class SqlServerReader
    {
        private const int BatchSize = 200;
        private const int MaxAttempts = 4;

        private readonly string _connectionString;

        public SqlServerReader(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <param name="startAfter">Resume point: only fixtures with id &gt; this are read.</param>
        public async IAsyncEnumerable<(uint FixtureId, string FixtureStateJson)> ReadAllFixturesAsync(uint startAfter = 0)
        {
            uint last = startAfter;

            while (true)
            {
                var batch = await ReadBatchWithRetryAsync(last);
                if (batch.Count == 0) yield break;

                last = batch[^1].FixtureId;   // advance past every row read, even empty ones

                foreach (var (id, json) in batch)
                {
                    if (!string.IsNullOrEmpty(json))
                        yield return (id, json);
                }
            }
        }

        private async Task<List<(uint FixtureId, string? Json)>> ReadBatchWithRetryAsync(uint after)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var batch = new List<(uint, string?)>(BatchSize);

                    using var conn = new SqlConnection(_connectionString);
                    await conn.OpenAsync();

                    using var cmd = new SqlCommand(@"
                        SELECT TOP (@batch) fixtureid, state
                        FROM FixtureState
                        WHERE fixtureid > @after
                        ORDER BY fixtureid", conn);
                    cmd.Parameters.AddWithValue("@batch", BatchSize);
                    cmd.Parameters.AddWithValue("@after", (long)after);
                    cmd.CommandTimeout = 300;

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (!uint.TryParse(reader[0]?.ToString(), out var id)) continue;
                        batch.Add((id, reader.IsDBNull(1) ? null : reader.GetString(1)));
                    }

                    return batch;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    Console.WriteLine($"[RETRY {attempt}/{MaxAttempts - 1}] reading batch after fixture {after}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                }
            }
        }
    }
}
