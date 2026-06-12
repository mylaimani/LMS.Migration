using LMS.Migration.Core.Models;

namespace LMS.Migration.Core
{
    /// <summary>One row per player + team + league + division + season (lms.league_rankings).</summary>
    public class LeagueRankingEntry
    {
        public uint PlayerId { get; set; }
        public uint TeamId { get; set; }
        public uint LeagueId { get; set; }
        public uint DivisionId { get; set; }
        public uint SeasonId { get; set; }
        public string SeasonName { get; set; } = "";
        public float BattingTotalPoints { get; set; }
        public float BowlingTotalPoints { get; set; }
        public float FieldingTotalPoints { get; set; }
        public float AllRounderTotalPoints { get; set; }
        public uint BattingRank { get; set; }
        public uint BowlingRank { get; set; }
        public uint AllRounderRank { get; set; }
        public ushort GamesPlayed { get; set; }
        public uint LastFixtureId { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Live League Rankings (spec §2 / schema Chain 7, league part).
    /// RAW match points only — no opposition strength, no divisors, no caps.
    /// Aggregates by Player + Team + League (+ Division + Season); a player
    /// in multiple teams gets separate entries, points are NOT combined.
    /// </summary>
    public class LeagueRankingAccumulator
    {
        private readonly Dictionary<(uint Player, uint Team, uint League, uint Division, uint Season), LeagueRankingEntry> _entries = new();

        /// <summary>Feed one player_match_stats row. NULL points count as 0.</summary>
        public void Add(PlayerMatchStats s)
        {
            if (s.IsNoResult == 1) return;

            var key = (s.PlayerId, s.TeamId, s.LeagueId, s.DivisionId, s.SeasonId);
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new LeagueRankingEntry
                {
                    PlayerId = s.PlayerId,
                    TeamId = s.TeamId,
                    LeagueId = s.LeagueId,
                    DivisionId = s.DivisionId,
                    SeasonId = s.SeasonId,
                    SeasonName = s.SeasonName
                };
                _entries[key] = e;
            }

            e.BattingTotalPoints += s.BattingMatchPoints ?? 0f;
            e.BowlingTotalPoints += s.BowlingMatchPoints ?? 0f;
            e.FieldingTotalPoints += s.FieldingPoints;
            e.AllRounderTotalPoints = e.BattingTotalPoints + e.BowlingTotalPoints + e.FieldingTotalPoints;
            e.GamesPlayed++;
            e.LastFixtureId = s.FixtureId;
        }

        /// <summary>
        /// Assigns batting/bowling/all-rounder ranks within each
        /// league + division + season group and returns all rows.
        /// </summary>
        public List<LeagueRankingEntry> BuildRankings(DateTime now)
        {
            foreach (var group in _entries.Values.GroupBy(e => (e.LeagueId, e.DivisionId, e.SeasonId)))
            {
                Rank(group, e => e.BattingTotalPoints, (e, r) => e.BattingRank = r);
                Rank(group, e => e.BowlingTotalPoints, (e, r) => e.BowlingRank = r);
                Rank(group, e => e.AllRounderTotalPoints, (e, r) => e.AllRounderRank = r);
            }

            var rows = _entries.Values.ToList();
            foreach (var e in rows) e.LastUpdated = now;
            return rows;
        }

        private static void Rank(
            IEnumerable<LeagueRankingEntry> group,
            Func<LeagueRankingEntry, float> score,
            Action<LeagueRankingEntry, uint> assign)
        {
            // Standard competition ranking (RANK(): equal scores share a rank,
            // next rank skips). Ties broken for ordering stability by player id.
            uint position = 0, rank = 0;
            float? prev = null;
            foreach (var e in group.OrderByDescending(score).ThenBy(e => e.PlayerId))
            {
                position++;
                if (prev == null || score(e) < prev.Value) rank = position;
                prev = score(e);
                assign(e, rank);
            }
        }
    }
}
