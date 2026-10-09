// By Mani
// Turns one fixture's batsman-v-bowler ball counts into player_opposition_strength rows
// (one Batting row per batsman, one Bowling row per bowler). Pure: no database calls.

namespace LMS.Migration.Core.OppositionStrength
{
    /// <summary>Balls one batsman faced from one bowler in one fixture (LMS over-counting rule already applied).</summary>
    public class BatsmanBowlerBalls
    {
        public uint FixtureId { get; set; }
        public DateTime GameDate { get; set; }
        public uint BatsmanId { get; set; }
        public uint BowlerId { get; set; }
        public uint BattingTeamId { get; set; }
        public uint BowlingTeamId { get; set; }
        public int BallsCounted { get; set; }
    }

    /// <summary>A player's batting and bowling rating plus reliability, as known before the fixture.</summary>
    public class PlayerRatingSnapshot
    {
        public uint PlayerId { get; set; }
        public decimal BatRatingLevel { get; set; }
        public decimal BowlRatingLevel { get; set; }
        public decimal ReliabilityPercent { get; set; }
    }

    /// <summary>One row of LMSClickHouseDB.player_opposition_strength.</summary>
    public class PlayerOppositionStrengthRow
    {
        public uint FixtureId { get; set; }
        public DateTime GameDate { get; set; }
        public uint PlayerId { get; set; }
        public string Role { get; set; } = "";
        public uint TeamId { get; set; }
        public int BallsCounted { get; set; }
        public int OpponentsCounted { get; set; }
        public int OpponentsWithoutRating { get; set; }
        public decimal EffectiveOppositionRating { get; set; }
        public decimal UpliftPercent { get; set; }
        public string RatingSource { get; set; } = "";
        public int FormulaVersion { get; set; }
        public DateTime CalculatedAt { get; set; }
    }

    public static class OppositionStrengthBuilder
    {
        public const string BattingRole = "Batting";
        public const string BowlingRole = "Bowling";

        /// <summary>Builds every Batting and Bowling row for one fixture.</summary>
        public static List<PlayerOppositionStrengthRow> BuildForFixture(
            List<BatsmanBowlerBalls> ballsPerPair,
            Dictionary<uint, PlayerRatingSnapshot> ratingsByPlayerId,
            string ratingSource,
            DateTime calculatedAt)
        {
            var rows = new List<PlayerOppositionStrengthRow>();

            // A batsman's opposition is the bowlers he faced, so use their BOWLING rating.
            Dictionary<uint, OpponentRating> bowlersAsOpponents = BuildOpponentRatingsUsingBowlingRating(ratingsByPlayerId);
            AddBattingRows(ballsPerPair, bowlersAsOpponents, ratingSource, calculatedAt, rows);

            // A bowler's opposition is the batsmen he bowled to, so use their BATTING rating.
            Dictionary<uint, OpponentRating> batsmenAsOpponents = BuildOpponentRatingsUsingBattingRating(ratingsByPlayerId);
            AddBowlingRows(ballsPerPair, batsmenAsOpponents, ratingSource, calculatedAt, rows);

            return rows;
        }

        private static void AddBattingRows(
            List<BatsmanBowlerBalls> ballsPerPair,
            Dictionary<uint, OpponentRating> bowlersAsOpponents,
            string ratingSource,
            DateTime calculatedAt,
            List<PlayerOppositionStrengthRow> rows)
        {
            var pairsByBatsman = new Dictionary<uint, List<BatsmanBowlerBalls>>();
            foreach (BatsmanBowlerBalls pair in ballsPerPair)
            {
                if (!pairsByBatsman.ContainsKey(pair.BatsmanId))
                {
                    pairsByBatsman[pair.BatsmanId] = new List<BatsmanBowlerBalls>();
                }
                pairsByBatsman[pair.BatsmanId].Add(pair);
            }

            foreach (var (batsmanId, pairsForBatsman) in pairsByBatsman)
            {
                var ballsAgainstEachBowler = new List<BallsAgainstOpponent>();
                foreach (BatsmanBowlerBalls pair in pairsForBatsman)
                {
                    ballsAgainstEachBowler.Add(new BallsAgainstOpponent { OpponentId = pair.BowlerId, BallsCounted = pair.BallsCounted });
                }

                OppositionStrengthResult result = OppositionStrengthCalculator.CalculateForPlayer(ballsAgainstEachBowler, bowlersAsOpponents);
                BatsmanBowlerBalls firstPair = pairsForBatsman[0];
                rows.Add(CreateRow(firstPair, batsmanId, BattingRole, firstPair.BattingTeamId, result, ratingSource, calculatedAt));
            }
        }

        private static void AddBowlingRows(
            List<BatsmanBowlerBalls> ballsPerPair,
            Dictionary<uint, OpponentRating> batsmenAsOpponents,
            string ratingSource,
            DateTime calculatedAt,
            List<PlayerOppositionStrengthRow> rows)
        {
            var pairsByBowler = new Dictionary<uint, List<BatsmanBowlerBalls>>();
            foreach (BatsmanBowlerBalls pair in ballsPerPair)
            {
                if (!pairsByBowler.ContainsKey(pair.BowlerId))
                {
                    pairsByBowler[pair.BowlerId] = new List<BatsmanBowlerBalls>();
                }
                pairsByBowler[pair.BowlerId].Add(pair);
            }

            foreach (var (bowlerId, pairsForBowler) in pairsByBowler)
            {
                var ballsAgainstEachBatsman = new List<BallsAgainstOpponent>();
                foreach (BatsmanBowlerBalls pair in pairsForBowler)
                {
                    ballsAgainstEachBatsman.Add(new BallsAgainstOpponent { OpponentId = pair.BatsmanId, BallsCounted = pair.BallsCounted });
                }

                OppositionStrengthResult result = OppositionStrengthCalculator.CalculateForPlayer(ballsAgainstEachBatsman, batsmenAsOpponents);
                BatsmanBowlerBalls firstPair = pairsForBowler[0];
                rows.Add(CreateRow(firstPair, bowlerId, BowlingRole, firstPair.BowlingTeamId, result, ratingSource, calculatedAt));
            }
        }

        private static PlayerOppositionStrengthRow CreateRow(
            BatsmanBowlerBalls fixtureDetails,
            uint playerId,
            string role,
            uint teamId,
            OppositionStrengthResult result,
            string ratingSource,
            DateTime calculatedAt)
        {
            return new PlayerOppositionStrengthRow
            {
                FixtureId = fixtureDetails.FixtureId,
                GameDate = fixtureDetails.GameDate,
                PlayerId = playerId,
                Role = role,
                TeamId = teamId,
                BallsCounted = result.BallsCounted,
                OpponentsCounted = result.OpponentsCounted,
                OpponentsWithoutRating = result.OpponentsWithoutRating,
                EffectiveOppositionRating = result.EffectiveOppositionRating,
                UpliftPercent = result.UpliftPercent,
                RatingSource = ratingSource,
                FormulaVersion = OppositionStrengthCalculator.FormulaVersion,
                CalculatedAt = calculatedAt
            };
        }

        private static Dictionary<uint, OpponentRating> BuildOpponentRatingsUsingBowlingRating(
            Dictionary<uint, PlayerRatingSnapshot> ratingsByPlayerId)
        {
            var opponentRatings = new Dictionary<uint, OpponentRating>();
            foreach (var (playerId, snapshot) in ratingsByPlayerId)
            {
                opponentRatings[playerId] = new OpponentRating
                {
                    PlayerId = playerId,
                    RatingLevel = snapshot.BowlRatingLevel,
                    ReliabilityPercent = snapshot.ReliabilityPercent
                };
            }
            return opponentRatings;
        }

        private static Dictionary<uint, OpponentRating> BuildOpponentRatingsUsingBattingRating(
            Dictionary<uint, PlayerRatingSnapshot> ratingsByPlayerId)
        {
            var opponentRatings = new Dictionary<uint, OpponentRating>();
            foreach (var (playerId, snapshot) in ratingsByPlayerId)
            {
                opponentRatings[playerId] = new OpponentRating
                {
                    PlayerId = playerId,
                    RatingLevel = snapshot.BatRatingLevel,
                    ReliabilityPercent = snapshot.ReliabilityPercent
                };
            }
            return opponentRatings;
        }
    }
}
// End of Mani
