using LMS.Migration.Core.Models;

namespace LMS.Migration.Core
{
    /// <summary>
    /// Builds one PlayerMatchStats row per player per match, then runs the
    /// PointsCalculator on each row.
    /// Batting/bowling inputs come from the FixtureState innings aggregates
    /// (authoritative — handles retirements and run-out of non-striker);
    /// fielding credits are counted from the wicket balls.
    /// </summary>
    public class PlayerStatsExtractor
    {
        /// <param name="hasConfirmedEmail">
        /// Lookup for the 150 participation points (Legends). If null, 0 is
        /// awarded. TODO: wire to SQL Server User.EmailAddressConfirmed
        /// before the production Legends run (Phase 2).
        /// </param>
        /// <param name="oppositionStrengthLookup">
        /// (playerTeamId, opposingTeamId) → opposition strength, locked
        /// pre-match. If null, 1.0 is used.
        /// </param>
        public List<PlayerMatchStats> Build(
            List<BallEvent> balls,
            List<PlayerInningsSummary> summaries,
            MatchInfo match,
            Func<uint, bool>? hasConfirmedEmail = null,
            Func<uint, uint, float>? oppositionStrengthLookup = null,
            float leagueStrengthWeighting = 1.0f)
        {
            var rows = new Dictionary<uint, PlayerMatchStats>();
            var first = balls.Count > 0 ? balls[0] : null;

            PlayerMatchStats Row(uint playerId, uint teamId)
            {
                if (!rows.TryGetValue(playerId, out var r))
                {
                    r = new PlayerMatchStats
                    {
                        FixtureId = match.FixtureId,
                        PlayerId = playerId,
                        TeamId = teamId,
                        MatchRpball = match.MatchRpball,
                        IsNoResult = (byte)(match.IsNoResult ? 1 : 0),
                        GameDate = match.GameDate,
                        LeagueId = first?.LeagueId ?? 0,
                        DivisionId = first?.DivisionId ?? 0,
                        SeasonId = first?.SeasonId ?? 0,
                        SeasonName = first?.SeasonName ?? "",
                        VenueId = first?.VenueId ?? 0,
                        RegionId = first?.RegionId ?? 0,
                        CountryId = first?.CountryId ?? 0,
                        LeagueStrengthWeighting = leagueStrengthWeighting
                    };
                    rows[playerId] = r;
                }
                return r;
            }

            // ── Batting & bowling from the authoritative summaries ───────
            foreach (var s in summaries)
            {
                var r = Row(s.PlayerId, s.TeamId);
                if (s.Batted)
                {
                    r.BattingRunsScored = s.RunsScored;
                    r.BattingBallsFaced = s.BallsFaced;
                    r.BattingIsNotOut = (byte)(s.IsNotOut ? 1 : 0);
                }
                if (s.Bowled)
                {
                    r.BowlingBallsBowled = s.BallsBowled;
                    r.BowlingRunsConceded = s.RunsConceded;
                    r.BowlingWickets = s.Wickets;
                }
            }

            // ── Fielding credits from wicket balls ───────────────────────
            foreach (var b in balls)
            {
                if (!b.IsWicket) continue;
                var type = b.WicketType ?? "";

                if (type.Equals("Caught", StringComparison.OrdinalIgnoreCase) && b.FielderId != 0)
                    Row(b.FielderId, b.BowlingTeamId).FieldingCatches++;
                else if (type.Equals("RunOut", StringComparison.OrdinalIgnoreCase) && b.FielderId != 0)
                    Row(b.FielderId, b.BowlingTeamId).FieldingRunOuts++;
                else if (type.Equals("Stumped", StringComparison.OrdinalIgnoreCase) && b.KeeperId != 0)
                    Row(b.KeeperId, b.BowlingTeamId).FieldingStumpings++;
                else if (type.Equals("DoublePlay", StringComparison.OrdinalIgnoreCase) && b.FielderId != 0)
                    Row(b.FielderId, b.BowlingTeamId).FieldingDoublePlays++;
            }

            // ── Finalise each row ────────────────────────────────────────
            foreach (var r in rows.Values)
            {
                if (match.IsNoResult)
                    r.MatchResult = "NoResult";
                else if (match.WinningTeamId == 0)
                    r.MatchResult = "Tie";
                else
                    r.MatchResult = r.TeamId == match.WinningTeamId ? "Win" : "Loss";

                r.ParticipationPoints = (ushort)(hasConfirmedEmail?.Invoke(r.PlayerId) == true ? 150 : 0);

                uint opposingTeam = first == null ? 0
                    : (first.BattingTeamId == r.TeamId ? first.BowlingTeamId : first.BattingTeamId);
                r.OppositionStrength = oppositionStrengthLookup?.Invoke(r.TeamId, opposingTeam) ?? 1.0f;

                PointsCalculator.Calculate(r);
            }

            return rows.Values.ToList();
        }

        /// <summary>
        /// Computes match-level facts. Runs and the winner come from the
        /// OFFICIAL innings Score objects (authoritative — live scorer
        /// corrections update those, not the ball stream); legal ball count
        /// comes from the parsed deliveries.
        /// </summary>
        public MatchInfo BuildMatchInfo(
            uint fixtureId,
            List<BallEvent> balls,
            List<(uint BattingTeamId, int Runs, byte Wickets)> inningsScores,
            string? matchResultRaw)
        {
            int totalLegalBalls = balls.Count(b => b.IsLegalBall);
            int totalRuns = inningsScores.Sum(s => s.Runs);

            bool isNoResult =
                string.Equals(matchResultRaw, "NoResult", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(matchResultRaw, "Abandoned", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(matchResultRaw, "RainedOut", StringComparison.OrdinalIgnoreCase) ||
                totalLegalBalls == 0 ||
                inningsScores.Count < 2;           // incomplete match

            uint winner = 0;
            if (!isNoResult)
            {
                var i1 = inningsScores[0];
                var i2 = inningsScores[1];
                if (i1.Runs > i2.Runs) winner = i1.BattingTeamId;
                else if (i2.Runs > i1.Runs) winner = i2.BattingTeamId;
                // equal → tie (winner stays 0)
            }

            return new MatchInfo
            {
                FixtureId = fixtureId,
                GameDate = balls.Count > 0 ? balls[0].GameDate : DateTime.UnixEpoch,
                MatchResultRaw = matchResultRaw,
                IsNoResult = isNoResult,
                WinningTeamId = winner,
                MatchRpball = totalLegalBalls == 0 ? 0f : (float)totalRuns / totalLegalBalls
            };
        }
    }
}
