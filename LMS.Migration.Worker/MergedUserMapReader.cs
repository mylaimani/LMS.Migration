// By Mani 15 Oct 2026
using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Old user id -> surviving user id, so a player whose accounts were merged in Spawtz is stored
    /// in ClickHouse under one id. Spawtz never rewrites FixtureState after a merge.
    ///
    /// Reads dbo.Rep_MergedUserMap when that table exists (built by migrations/merged_user_map_sql_server.sql).
    /// When it does not exist (16 Oct 2026: live database must stay read-only) the map is built here from
    /// dbo.MergedUsersLog with the same rules as the SQL procedure: latest MergeDate wins when an old id
    /// was merged more than once, chains are followed to the end (A->B, B->C gives A->C), at most 10 hops,
    /// and a chain never returns to its own starting id.
    /// </summary>
    public class MergedUserMapReader
    {
        private readonly string _connectionString;

        public MergedUserMapReader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task<Dictionary<uint, uint>> LoadAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            Dictionary<uint, uint> oldToFinalUserId;
            if (await MapTableExistsAsync(connection))
            {
                oldToFinalUserId = await ReadMapTableAsync(connection);
                Console.WriteLine("Merged player ids read from dbo.Rep_MergedUserMap.");
            }
            else
            {
                Dictionary<uint, uint> directMerge = await ReadDirectMergesAsync(connection);
                oldToFinalUserId = FollowChains(directMerge);
                Console.WriteLine("Merged player ids built in memory from dbo.MergedUsersLog (no Rep_MergedUserMap table).");
            }

            if (oldToFinalUserId.Count == 0)
            {
                throw new InvalidOperationException("No merged player ids found: dbo.MergedUsersLog is empty on this database.");
            }

            return oldToFinalUserId;
        }

        private static async Task<bool> MapTableExistsAsync(SqlConnection connection)
        {
            using var command = new SqlCommand("SELECT OBJECT_ID('dbo.Rep_MergedUserMap', 'U')", connection);
            object? result = await command.ExecuteScalarAsync();
            return result != null && result != DBNull.Value;
        }

        private static async Task<Dictionary<uint, uint>> ReadMapTableAsync(SqlConnection connection)
        {
            var oldToFinalUserId = new Dictionary<uint, uint>();
            using var command = new SqlCommand("SELECT OldUserId, FinalUserId FROM dbo.Rep_MergedUserMap", connection);
            command.CommandTimeout = 300;

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                uint oldUserId = (uint)reader.GetInt32(0);
                uint finalUserId = (uint)reader.GetInt32(1);
                oldToFinalUserId[oldUserId] = finalUserId;
            }
            return oldToFinalUserId;
        }

        /// <summary>One direct merge per old id: the row with the latest MergeDate (then highest Id) wins.</summary>
        private static async Task<Dictionary<uint, uint>> ReadDirectMergesAsync(SqlConnection connection)
        {
            var directMerge = new Dictionary<uint, uint>();
            using var command = new SqlCommand(@"
                SELECT OldUserId, NewUserId
                FROM dbo.MergedUsersLog
                WHERE OldUserId <> NewUserId
                ORDER BY OldUserId, MergeDate, Id", connection);
            command.CommandTimeout = 300;

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                uint oldUserId = (uint)reader.GetInt32(0);
                uint newUserId = (uint)reader.GetInt32(1);
                directMerge[oldUserId] = newUserId;   // ordered ascending, so the last row for an old id wins
            }
            return directMerge;
        }

        /// <summary>A->B, B->C gives A->C. At most 10 hops; a chain never returns to its starting id.</summary>
        private static Dictionary<uint, uint> FollowChains(Dictionary<uint, uint> directMerge)
        {
            var oldToFinalUserId = new Dictionary<uint, uint>(directMerge.Count);
            foreach (KeyValuePair<uint, uint> merge in directMerge)
            {
                uint startUserId = merge.Key;
                uint currentUserId = merge.Value;
                int hops = 1;
                while (hops < 10 && directMerge.TryGetValue(currentUserId, out uint nextUserId) && nextUserId != startUserId)
                {
                    currentUserId = nextUserId;
                    hops++;
                }
                oldToFinalUserId[startUserId] = currentUserId;
            }
            return oldToFinalUserId;
        }
    }
}
