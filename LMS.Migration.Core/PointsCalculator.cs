using LMS.Migration.Core.Models;

namespace LMS.Migration.Core
{
    /// <summary>
    /// Implements the Match Points &amp; Player Ratings Project specification.
    /// Every step mirrors the calculation chains in the schema document v4.1
    /// (Chains 1–5). All methods are pure and unit-testable.
    /// </summary>
    public static class PointsCalculator
    {
        private const float WinFactor = 1.10f;
        private const float MatchPointsCap = 300f;
        private const float NotOutBonusValue = 15f;

        /// <summary>
        /// Populates all points fields on the stats row.
        /// Pre-conditions: raw inputs (runs, balls, wickets, fielding counts,
        /// MatchRpball, MatchResult, IsNoResult) are already set.
        /// </summary>
        public static void Calculate(PlayerMatchStats s)
        {
            // Edge case (spec §1): no-result / rained-out matches, or
            // Match RPBall = 0, are excluded from ALL points and ratings.
            if (s.IsNoResult == 1 || s.MatchRpball <= 0f)
            {
                s.IsNoResult = 1;
                s.FieldingPoints = 0f;
                s.AllRounderMatchPoints = 0f;
                s.LegendsPoints = 0f;
                // TO CONFIRM with business: do no-result matches still award
                // the 150 participation points? (Legends doc: "per match played")
                // Current behaviour: NO points of any kind.
                s.ParticipationPoints = 0;
                return;
            }

            bool isWin = s.MatchResult == "Win";
            bool isTie = s.MatchResult == "Tie";

            CalculateBatting(s, isWin, isTie);
            CalculateBowling(s, isWin);
            CalculateFielding(s);

            // Chain 4 — All-Rounder: NULL treated as 0 in the sum.
            s.AllRounderMatchPoints =
                (s.BattingMatchPoints ?? 0f) +
                (s.BowlingMatchPoints ?? 0f) +
                s.FieldingPoints;

            // Chain 5 — Legends: RAW points BEFORE opposition strength /
            // league weighting, plus participation. NULL treated as 0.
            s.LegendsPoints =
                (s.BattingMatchPoints ?? 0f) +
                (s.BowlingMatchPoints ?? 0f) +
                s.FieldingPoints +
                s.ParticipationPoints;
        }

        // ── Chain 1 — Batting Points ─────────────────────────────────
        private static void CalculateBatting(PlayerMatchStats s, bool isWin, bool isTie)
        {
            // Step 0 — Did Not Bat
            if (s.BattingBallsFaced == 0)
            {
                s.BattingMatchPoints = null;
                s.BattingRatingImpact = null;
                return;
            }

            // Step 1 — Player RPBall
            float playerRpball = (float)s.BattingRunsScored / s.BattingBallsFaced;
            s.BattingPlayerRpball = playerRpball;

            // Step 2 — Efficiency Ratio
            float efficiency = playerRpball / s.MatchRpball;
            s.BattingEfficiencyRatio = efficiency;

            // Step 3 — Batting Base Points
            float basePoints = s.BattingRunsScored * efficiency * 2.0f;
            s.BattingBasePoints = basePoints;

            // Step 4 — Win Adjustment (×1.10 win only; tie/loss unchanged)
            float afterWin = isWin ? basePoints * WinFactor : basePoints;
            s.BattingAfterWin = afterWin;

            // Step 5 — Not Out Bonus
            float bonus = 0f;
            if (s.BattingIsNotOut == 1)
            {
                if (isWin || isTie)
                    bonus = NotOutBonusValue;
                else if (playerRpball >= 0.90f * s.MatchRpball)   // loss
                    bonus = NotOutBonusValue;
            }
            s.BattingNotOutBonus = bonus;

            // Step 6 — RAW points (audit, before cap)
            float raw = afterWin + bonus;
            s.BattingRawPoints = raw;

            // Step 7 — FINAL: cap at 300
            float final = MathF.Min(MatchPointsCap, raw);
            s.BattingMatchPoints = final;

            // Step 8 — Rating Impact (ratings/prestige rankings only)
            s.BattingRatingImpact = final * s.OppositionStrength * s.LeagueStrengthWeighting;
        }

        // ── Chain 2 — Bowling Points ─────────────────────────────────
        private static void CalculateBowling(PlayerMatchStats s, bool isWin)
        {
            // Step 0 — Did Not Bowl. Applies even if runs were conceded via
            // wides/no-balls before any legal ball was delivered.
            if (s.BowlingBallsBowled == 0)
            {
                s.BowlingMatchPoints = null;
                s.BowlingRatingImpact = null;
                return;
            }

            // Step 1 — Bowler RPBall
            float bowlerRpball = (float)s.BowlingRunsConceded / s.BowlingBallsBowled;
            s.BowlingRpball = bowlerRpball;

            // Steps 2–3 — Improvement & Base Economy Points
            float baseEconomy;
            if (s.BowlingRunsConceded == 0)
            {
                s.BowlingImprovement = null;     // not calculated (division by zero)
                baseEconomy = 200f;              // scoreless spell = perfect economy
            }
            else
            {
                float improvement = (s.MatchRpball / bowlerRpball) - 1f;
                s.BowlingImprovement = improvement;

                baseEconomy = improvement >= 0f
                    ? MathF.Min(200f, 20f + (100f * improvement))
                    : MathF.Max(1f, 20f + (75f * improvement));
            }
            s.BowlingBaseEconomyPts = baseEconomy;

            // Step 4 — Spell Length Scaling (full 20-ball spell = 1.0)
            float scaling = 0.10f + 0.90f * MathF.Sqrt((s.BowlingBallsBowled - 1) / 19f);
            s.BowlingScalingFactor = scaling;

            // Step 5 — Weighted Economy Points
            float weightedEconomy = baseEconomy * scaling;
            s.BowlingWeightedEconomyPts = weightedEconomy;

            // Step 6 — Wicket Points
            float wicketPoints = s.BowlingWickets * 25f;
            s.BowlingWicketPoints = wicketPoints;

            // Step 7 — Bowling Base Points
            float basePoints = weightedEconomy + wicketPoints;
            s.BowlingBasePoints = basePoints;

            // Steps 8–9 — Win Adjustment → RAW points (audit)
            float raw = isWin ? basePoints * WinFactor : basePoints;
            s.BowlingRawPoints = raw;

            // Step 10 — FINAL: cap at 300
            float final = MathF.Min(MatchPointsCap, raw);
            s.BowlingMatchPoints = final;

            // Step 11 — Rating Impact
            s.BowlingRatingImpact = final * s.OppositionStrength * s.LeagueStrengthWeighting;
        }

        // ── Chain 3 — Fielding Points (raw, never opposition-adjusted) ─
        private static void CalculateFielding(PlayerMatchStats s)
        {
            s.FieldingPoints =
                (s.FieldingCatches * 10f) +
                (s.FieldingRunOuts * 10f) +
                (s.FieldingStumpings * 25f) +
                (s.FieldingDoublePlays * 15f);
        }

        // ── Opposition Strength (spec §3) — locked BEFORE the match ──
        /// <param name="oppositionRank">Opposition team's latest published rank (1 = best).</param>
        /// <param name="formScore">Sum of opposition's last 6 results (W=+1, L=-1, T/NR=0) ÷ 6.</param>
        public static float OppositionStrength(int oppositionRank, float formScore)
        {
            float rankingStrength = 1f + 0.25f * (1f - (oppositionRank - 1) / 999f);
            float formAdjustment = 0.075f * formScore;
            return rankingStrength + formAdjustment;
        }
    }
}
