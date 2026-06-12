using LMS.Migration.Core.Models;

namespace LMS.Migration.Core
{
    /// <summary>
    /// Implements the Player Rating System (spec §7–9 / schema Chain 6):
    /// APG → z-score → 5+(1.5×z) → elite compression → experience cap.
    /// Pure functions, unit-testable.
    /// </summary>
    public static class RatingCalculator
    {
        /// <summary>Step 4 core scale: Base Rating = 5 + (1.5 × z).</summary>
        public static float BaseRating(float zScore) => 5f + (1.5f * zScore);

        /// <summary>Elite compression: ratings above 9 are compressed ×0.35.</summary>
        public static float EliteCompress(float rating) =>
            rating <= 9f ? rating : 9f + ((rating - 9f) * 0.35f);

        /// <summary>
        /// Step 5 experience-based cap.
        /// ≥30 discipline games → 9.99; else 10 − (3 × e^(−0.1215 × (games−1))).
        /// </summary>
        public static float MaxCap(int disciplineGames) =>
            disciplineGames >= 30
                ? 9.99f
                : 10f - (3f * MathF.Exp(-0.1215f * (disciplineGames - 1)));

        /// <summary>Step 6: Final = min(compressed, cap), floored at 0.</summary>
        public static float FinalRating(float zScore, int disciplineGames)
        {
            if (disciplineGames == 0) return 0f;
            var adjusted = EliteCompress(BaseRating(zScore));
            return MathF.Max(0f, MathF.Min(adjusted, MaxCap(disciplineGames)));
        }

        /// <summary>
        /// Reliability (spec §9): (games played × 0.75%) + (unique teams faced
        /// × 0.75%), capped at 95%. Games played = ALL qualifying matches.
        /// </summary>
        public static float Reliability(int gamesPlayed, int uniqueTeamsFaced) =>
            MathF.Min(95f, (gamesPlayed * 0.75f) + (uniqueTeamsFaced * 0.75f));

        /// <summary>Population mean and standard deviation of a set of APGs.</summary>
        public static (float Mean, float Stddev) PopulationBenchmarks(IReadOnlyCollection<float> apgs)
        {
            if (apgs.Count == 0) return (0f, 0f);
            float mean = apgs.Average();
            float variance = apgs.Sum(a => (a - mean) * (a - mean)) / apgs.Count;
            return (mean, MathF.Sqrt(variance));
        }

        public static float ZScore(float apg, float mean, float stddev) =>
            stddev <= 0f ? 0f : (apg - mean) / stddev;
    }

    /// <summary>
    /// Accumulates per-player rating inputs across the migration run, then
    /// produces the launch-baseline player_ratings rows.
    /// Spec: historical rating movement is NOT back-calculated — the rating
    /// computed here becomes each player's baseline (prev = current, change 0).
    /// </summary>
    public class PlayerRatingAccumulator
    {
        private class Accum
        {
            // Most recent up to 100 qualifying games per discipline
            public readonly Queue<float> BattingImpacts = new();
            public readonly Queue<float> BowlingImpacts = new();
            public int GamesPlayed;
            public readonly HashSet<uint> TeamsFaced = new();
            public uint LastFixtureId;
        }

        private const int MaxGames = 100;
        private readonly Dictionary<uint, Accum> _players = new();

        /// <summary>Feed one player_match_stats row (chronological order).</summary>
        public void Add(PlayerMatchStats s, uint opposingTeamId)
        {
            if (s.IsNoResult == 1) return;   // excluded from all rating calculations

            if (!_players.TryGetValue(s.PlayerId, out var a))
            {
                a = new Accum();
                _players[s.PlayerId] = a;
            }

            a.GamesPlayed++;
            a.LastFixtureId = s.FixtureId;
            if (opposingTeamId != 0) a.TeamsFaced.Add(opposingTeamId);

            if (s.BattingRatingImpact.HasValue)
            {
                if (a.BattingImpacts.Count == MaxGames) a.BattingImpacts.Dequeue();
                a.BattingImpacts.Enqueue(s.BattingRatingImpact.Value);
            }
            if (s.BowlingRatingImpact.HasValue)
            {
                if (a.BowlingImpacts.Count == MaxGames) a.BowlingImpacts.Dequeue();
                a.BowlingImpacts.Enqueue(s.BowlingRatingImpact.Value);
            }
        }

        /// <summary>
        /// Initial calibration (spec §8): compute APG for all players, derive
        /// population benchmarks, then apply the rating model to everyone.
        /// </summary>
        public List<PlayerRating> BuildRatings(DateTime now)
        {
            // Pass 1 — APG per player per discipline
            var battingApgs = new List<float>();
            var bowlingApgs = new List<float>();
            foreach (var a in _players.Values)
            {
                if (a.BattingImpacts.Count > 0) battingApgs.Add(a.BattingImpacts.Average());
                if (a.BowlingImpacts.Count > 0) bowlingApgs.Add(a.BowlingImpacts.Average());
            }

            var (batMean, batStd) = RatingCalculator.PopulationBenchmarks(battingApgs);
            var (bowlMean, bowlStd) = RatingCalculator.PopulationBenchmarks(bowlingApgs);

            // Pass 2 — rating per player
            var result = new List<PlayerRating>(_players.Count);
            foreach (var (playerId, a) in _players)
            {
                int batGames = a.BattingImpacts.Count;
                int bowlGames = a.BowlingImpacts.Count;

                float batTotal = a.BattingImpacts.Sum();
                float bowlTotal = a.BowlingImpacts.Sum();
                float batApg = batGames > 0 ? batTotal / batGames : 0f;
                float bowlApg = bowlGames > 0 ? bowlTotal / bowlGames : 0f;

                float batZ = batGames > 0 ? RatingCalculator.ZScore(batApg, batMean, batStd) : 0f;
                float bowlZ = bowlGames > 0 ? RatingCalculator.ZScore(bowlApg, bowlMean, bowlStd) : 0f;

                float batRating = RatingCalculator.FinalRating(batZ, batGames);
                float bowlRating = RatingCalculator.FinalRating(bowlZ, bowlGames);

                result.Add(new PlayerRating
                {
                    PlayerId = playerId,
                    BattingGamesUsed = (ushort)batGames,
                    BattingTotalAdjustedPts = batTotal,
                    BattingApg = batApg,
                    BattingZScore = batZ,
                    BattingBaseRating = batGames > 0 ? RatingCalculator.BaseRating(batZ) : 0f,
                    BattingRating = batRating,
                    BattingMaxCap = batGames > 0 ? RatingCalculator.MaxCap(batGames) : 0f,
                    BattingPrevRating = batRating,      // launch baseline
                    BattingRatingChange = 0f,

                    BowlingGamesUsed = (ushort)bowlGames,
                    BowlingTotalAdjustedPts = bowlTotal,
                    BowlingApg = bowlApg,
                    BowlingZScore = bowlZ,
                    BowlingBaseRating = bowlGames > 0 ? RatingCalculator.BaseRating(bowlZ) : 0f,
                    BowlingRating = bowlRating,
                    BowlingMaxCap = bowlGames > 0 ? RatingCalculator.MaxCap(bowlGames) : 0f,
                    BowlingPrevRating = bowlRating,     // launch baseline
                    BowlingRatingChange = 0f,

                    AllRounderRating = (0.5f * batRating) + (0.5f * bowlRating),

                    GamesPlayed = (ushort)Math.Min(a.GamesPlayed, ushort.MaxValue),
                    UniqueTeamsFaced = (ushort)Math.Min(a.TeamsFaced.Count, ushort.MaxValue),
                    ReliabilityPct = RatingCalculator.Reliability(a.GamesPlayed, a.TeamsFaced.Count),

                    PopulationMeanBatting = batMean,
                    PopulationStddevBatting = batStd,
                    PopulationMeanBowling = bowlMean,
                    PopulationStddevBowling = bowlStd,

                    LastUpdatedFixtureId = a.LastFixtureId,
                    LastUpdated = now
                });
            }

            return result;
        }
    }
}
