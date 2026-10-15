// By Mani 15 Oct 2026
using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Loads dbo.Rep_MergedUserMap (old user id -> surviving user id), built by
    /// migrations/merged_user_map_sql_server.sql from dbo.MergedUsersLog.
    /// The worker applies it to every player id it parses, so a player whose
    /// accounts were merged in Spawtz is stored in ClickHouse under one id.
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
            var oldToFinalUserId = new Dictionary<uint, uint>();

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand("SELECT OldUserId, FinalUserId FROM dbo.Rep_MergedUserMap", connection);
            command.CommandTimeout = 300;

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                uint oldUserId = (uint)reader.GetInt32(0);
                uint finalUserId = (uint)reader.GetInt32(1);
                oldToFinalUserId[oldUserId] = finalUserId;
            }

            if (oldToFinalUserId.Count == 0)
            {
                throw new InvalidOperationException(
                    "dbo.Rep_MergedUserMap is empty. Run migrations/merged_user_map_sql_server.sql on the FixtureState database first.");
            }

            return oldToFinalUserId;
        }
    }
}
