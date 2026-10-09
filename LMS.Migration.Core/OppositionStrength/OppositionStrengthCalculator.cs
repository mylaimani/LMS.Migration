// By Mani
// Category 1 of LMS Adjusted Match Points: "Players Actually Faced" (ball-by-ball opposition uplift).
// Pure calculation only. No database or ClickHouse calls in this file.

namespace LMS.Migration.Core.OppositionStrength
{
    /// <summary>One opponent's rating as it was BEFORE the fixture being processed.</summary>
    public class OpponentRating
    {
        public uint PlayerId { get; set; }

        /// <summary>BowlRatingLevel when the opponent bowled to our batsman,
        /// BatRatingLevel when the opponent batted against our bowler. Scale 0-10.</summary>
        public decimal RatingLevel { get; set; }

        /// <summary>Reliability as stored in Rep_PlayerRatingHistory: 0-100 (e.g. 50.25).</summary>
        public decimal ReliabilityPercent { get; set; }
    }

    /// <summary>How many balls our player faced from (or bowled to) one opponent in this fixture.</summary>
    public class BallsAgainstOpponent
    {
        public uint OpponentId { get; set; }
        public int BallsCounted { get; set; }
    }

    /// <summary>The two numbers Wayne asked for, plus counts so testers can check them.</summary>
    public class OppositionStrengthResult
    {
        public decimal EffectiveOppositionRating { get; set; }
        public decimal UpliftPercent { get; set; }
        public int BallsCounted { get; set; }
        public int OpponentsCounted { get; set; }
        public int OpponentsWithoutRating { get; set; }
    }

    public static class OppositionStrengthCalculator
    {
        public const decimal NeutralRating = 5.0m;
        public const decimal UpliftPerRatingPoint = 10.0m;
        public const decimal PositiveUpliftMultiplier = 2.5m;            // Wayne 05/10/2026 (was 2.0)
        public const decimal FullReliabilityThresholdPercent = 50m;      // Wayne 05/10/2026: 50%+ = full rating
        public const int FormulaVersion = 2;

        /// <summary>
        /// Wayne 05/10/2026:
        ///   Adjusted Reliability = MIN(Reliability / 50%, 1)
        ///   Effective Rating     = 5 + (Rating - 5) x Adjusted Reliability
        /// 25% reliability uses half the difference from 5.0, 40% uses 80%, 50% and above use the full rating.
        /// </summary>
        public static decimal CalculateEffectiveRating(decimal ratingLevel, decimal reliabilityPercent)
        {
            decimal adjustedReliability = reliabilityPercent / FullReliabilityThresholdPercent;

            if (adjustedReliability < 0m)
            {
                adjustedReliability = 0m;
            }
            if (adjustedReliability > 1m)
            {
                adjustedReliability = 1m;
            }

            decimal effectiveRating = NeutralRating + (ratingLevel - NeutralRating) * adjustedReliability;
            return effectiveRating;
        }

        /// <summary>Wayne's rule: above 5.0 the uplift is multiplied by 2.5, below 5.0 it is not.</summary>
        public static decimal CalculateUpliftPercent(decimal effectiveOppositionRating)
        {
            decimal rawUpliftPercent = (effectiveOppositionRating - NeutralRating) * UpliftPerRatingPoint;

            if (effectiveOppositionRating >= NeutralRating)
            {
                return rawUpliftPercent * PositiveUpliftMultiplier;
            }
            else
            {
                return rawUpliftPercent;
            }
        }

        /// <summary>Calculates one player's batting OR bowling opposition strength for one fixture.</summary>
        public static OppositionStrengthResult CalculateForPlayer(
            List<BallsAgainstOpponent> ballsAgainstEachOpponent,
            Dictionary<uint, OpponentRating> opponentRatingsById)
        {
            var result = new OppositionStrengthResult();
            decimal totalWeightedScore = 0m;

            foreach (BallsAgainstOpponent ballsAgainstOpponent in ballsAgainstEachOpponent)
            {
                if (ballsAgainstOpponent.BallsCounted <= 0)
                {
                    continue;
                }

                decimal effectiveRating = GetEffectiveRatingForOpponent(
                    ballsAgainstOpponent.OpponentId, opponentRatingsById, result);

                totalWeightedScore += effectiveRating * ballsAgainstOpponent.BallsCounted;
                result.BallsCounted += ballsAgainstOpponent.BallsCounted;
                result.OpponentsCounted += 1;
            }

            if (result.BallsCounted == 0)
            {
                result.EffectiveOppositionRating = NeutralRating;
                result.UpliftPercent = 0m;
                return result;
            }

            decimal effectiveOppositionRating = totalWeightedScore / result.BallsCounted;
            result.UpliftPercent = Math.Round(CalculateUpliftPercent(effectiveOppositionRating), 2, MidpointRounding.AwayFromZero);
            result.EffectiveOppositionRating = Math.Round(effectiveOppositionRating, 2, MidpointRounding.AwayFromZero);
            return result;
        }

        /// <summary>An opponent with no rating row counts as neutral (5.0 effective), so has no effect.</summary>
        private static decimal GetEffectiveRatingForOpponent(
            uint opponentId,
            Dictionary<uint, OpponentRating> opponentRatingsById,
            OppositionStrengthResult result)
        {
            if (opponentRatingsById.TryGetValue(opponentId, out OpponentRating? opponentRating))
            {
                return CalculateEffectiveRating(opponentRating.RatingLevel, opponentRating.ReliabilityPercent);
            }

            result.OpponentsWithoutRating += 1;
            return NeutralRating;
        }
    }
}
// End of Mani
