using LMS.Migration.Core;
using LMS.Migration.Core.Models;
using LMS.Migration.Core.Parsers;
using LMS.Migration.Core.Pulse;

namespace LMS.Migration.Worker
{
    /// <summary>What happened to one fixture. Message is empty on success.</summary>
    public class FixtureIngestResult
    {
        public uint FixtureId { get; set; }
        public bool Succeeded { get; set; }
        public string Message { get; set; } = "";
        public int BallCount { get; set; }
        public int PartnershipCount { get; set; }
        public int PlayerStatsCount { get; set; }
        public int MergedIdsReplaced { get; set; }
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// By Mani 21 Oct 2026
    /// One fixture: FixtureState (SQL Server) → ClickHouse. This is ProcessFixtureAsync
    /// from LMS.Migration.Worker/Program.cs made callable for a single fixture, so the
    /// LMST20 Ball Worker can run it for each saved match. Same parser, same merged-id
    /// remap, same Pulse fill, same stats extractor, same writer. The two things that
    /// used run-wide state in Program.cs are loaded per fixture instead:
    ///   - ranking snapshot: the latest one on or before the match date
    ///   - form score: read from ClickHouse (TeamFormReader) instead of FormTracker
    /// A fixture already in ClickHouse is replaced, not added again, and the five
    /// summing tables are corrected by AggregateTableRefresher.
    /// </summary>
    public class FixtureIngestService
    {
        private static readonly TimeSpan MergedMapMaxAge = TimeSpan.FromMinutes(10);

        private readonly SqlServerReader _fixtureReader;
        private readonly FixtureMetadataReader _metadataReader;
        private readonly MergedUserMapReader _mergedUserMapReader;
        private readonly TeamRankingReader _teamRankingReader;
        private readonly ClickHouseWriter _writer;
        private readonly TeamFormReader _formReader;
        private readonly AggregateTableRefresher _aggregateRefresher;
        private readonly FixtureStateParser _parser = new();
        private readonly PlayerStatsExtractor _extractor = new();

        private MergedUserRemapper? _mergedUserRemapper;
        private DateTime _mergedMapLoadedAt = DateTime.MinValue;

        public FixtureIngestService(string sqlConnectionString, string clickHouseConnectionString, string clickHouseDatabase = "LMSClickHouseDB")
        {
            _fixtureReader = new SqlServerReader(sqlConnectionString);
            _metadataReader = new FixtureMetadataReader(sqlConnectionString);
            _mergedUserMapReader = new MergedUserMapReader(sqlConnectionString);
            _teamRankingReader = new TeamRankingReader(sqlConnectionString);
            _writer = new ClickHouseWriter(clickHouseConnectionString, clickHouseDatabase);
            _formReader = new TeamFormReader(clickHouseConnectionString, clickHouseDatabase);
            _aggregateRefresher = new AggregateTableRefresher(clickHouseConnectionString, clickHouseDatabase);
        }

        public async Task<FixtureIngestResult> IngestAsync(uint fixtureId)
        {
            var result = new FixtureIngestResult { FixtureId = fixtureId };

            string? fixtureJson = await _fixtureReader.GetFixtureStateAsync(fixtureId);
            if (string.IsNullOrEmpty(fixtureJson))
                return Failed(result, "no FixtureState row");

            ParsedFixture parsed = _parser.Parse(fixtureId, fixtureJson);
            if (parsed.Balls.Count == 0)
                return Failed(result, "no balls found");

            result.MergedIdsReplaced = await RemapMergedUsersAsync(parsed, result);
            PulseReplayer.FillPulse(parsed);

            FixtureMetadata? metadata = await _metadataReader.GetAsync(fixtureId);
            StampMetadata(parsed, metadata);
            MatchInfo matchInfo = BuildMatchInfo(fixtureId, parsed, metadata);
            AddDriftWarnings(parsed, result);

            List<PlayerMatchStats> playerStats = await BuildPlayerStatsAsync(parsed, matchInfo);
            await ReplaceInClickHouseAsync(fixtureId, parsed, playerStats);

            result.Succeeded = true;
            result.BallCount = parsed.Balls.Count;
            result.PartnershipCount = parsed.Partnerships.Count;
            result.PlayerStatsCount = playerStats.Count;
            return result;
        }

        private static FixtureIngestResult Failed(FixtureIngestResult result, string message)
        {
            result.Succeeded = false;
            result.Message = message;
            return result;
        }

        /// <summary>Old merged ids → surviving id on balls, partnerships and summaries. Map is cached for 10 minutes.</summary>
        private async Task<int> RemapMergedUsersAsync(ParsedFixture parsed, FixtureIngestResult result)
        {
            if (_mergedUserRemapper == null || DateTime.UtcNow - _mergedMapLoadedAt > MergedMapMaxAge)
            {
                _mergedUserRemapper = new MergedUserRemapper(await _mergedUserMapReader.LoadAsync());
                _mergedMapLoadedAt = DateTime.UtcNow;
            }

            int replaced = _mergedUserRemapper.Apply(parsed);
            if (replaced == 0)
                return 0;

            List<uint> duplicatePlayerIds = _mergedUserRemapper.DuplicatePlayerIds(parsed);
            if (duplicatePlayerIds.Count > 0)
                result.Warnings.Add($"both accounts of player(s) {string.Join(",", duplicatePlayerIds)} played in this fixture");
            return replaced;
        }

        /// <summary>League, season, venue, region, country and game date from SQL onto every ball and partnership.</summary>
        private static void StampMetadata(ParsedFixture parsed, FixtureMetadata? metadata)
        {
            DateTime gameDate = metadata != null && metadata.FixtureDate != DateTime.UnixEpoch
                ? metadata.FixtureDate.Date
                : parsed.GameDate;

            foreach (BallEvent ball in parsed.Balls)
            {
                ball.LeagueId = metadata?.LeagueId ?? 0;
                ball.DivisionId = metadata?.DivisionId ?? 0;
                ball.SeasonId = metadata?.SeasonId ?? 0;
                ball.SeasonName = metadata?.SeasonName ?? "";
                ball.VenueId = metadata?.VenueId ?? ball.VenueId;
                ball.RegionId = metadata?.RegionId ?? ball.RegionId;
                ball.CountryId = metadata?.CountryId ?? ball.CountryId;
                ball.GameDate = gameDate;
            }

            foreach (Partnership partnership in parsed.Partnerships)
            {
                partnership.LeagueId = metadata?.LeagueId ?? 0;
                partnership.DivisionId = metadata?.DivisionId ?? 0;
                partnership.SeasonId = metadata?.SeasonId ?? 0;
                partnership.SeasonName = metadata?.SeasonName ?? "";
                partnership.VenueId = metadata?.VenueId ?? 0;
                partnership.RegionId = metadata?.RegionId ?? 0;
                partnership.GameDate = gameDate;
            }
        }

        private MatchInfo BuildMatchInfo(uint fixtureId, ParsedFixture parsed, FixtureMetadata? metadata)
        {
            string? matchResultRaw = metadata?.RainedOut == true ? "RainedOut" : parsed.MatchResultRaw;
            return _extractor.BuildMatchInfo(fixtureId, parsed.Balls, parsed.InningsScores, matchResultRaw);
        }

        /// <summary>Ball stream total vs official innings score. A difference means the scorer corrected something; stats are unaffected.</summary>
        private static void AddDriftWarnings(ParsedFixture parsed, FixtureIngestResult result)
        {
            for (int inningsIndex = 0; inningsIndex < parsed.InningsScores.Count; inningsIndex++)
            {
                var official = parsed.InningsScores[inningsIndex];
                int streamTotal = parsed.Balls
                    .Where(ball => ball.InningsNumber == inningsIndex + 1)
                    .Sum(ball => ball.RunsOffBat + ball.ExtrasWide + ball.ExtrasNoBall + ball.ExtrasBye + ball.ExtrasLegBye);
                if (streamTotal != official.Runs)
                    result.Warnings.Add($"innings {inningsIndex + 1}: ball stream {streamTotal} vs official {official.Runs}");
            }
        }

        /// <summary>
        /// Opposition strength is locked pre-match (spec §3.3): rank from the latest
        /// snapshot on or before the match date, form from the six results before it.
        /// </summary>
        private async Task<List<PlayerMatchStats>> BuildPlayerStatsAsync(ParsedFixture parsed, MatchInfo matchInfo)
        {
            var rankingProvider = new HistoricalTeamRankingProvider(_teamRankingReader);
            await rankingProvider.InitAsync();
            await rankingProvider.AdvanceToAsync(matchInfo.GameDate);

            uint teamA = parsed.Balls[0].BattingTeamId;
            uint teamB = parsed.Balls[0].BowlingTeamId;
            float formA = await _formReader.FormScoreBeforeAsync(teamA, matchInfo.GameDate);
            float formB = await _formReader.FormScoreBeforeAsync(teamB, matchInfo.GameDate);

            float OppositionStrength(uint playerTeamId, uint opposingTeamId)
            {
                int rank = rankingProvider.GetRank(opposingTeamId);
                float form = opposingTeamId == teamA ? formA : formB;
                return PointsCalculator.OppositionStrength(rank, form);
            }

            return _extractor.Build(parsed.Balls, parsed.PlayerSummaries, matchInfo,
                oppositionStrengthLookup: OppositionStrength);
        }

        /// <summary>Delete the fixture's old rows, write the new ones, then fix the summing tables.</summary>
        private async Task ReplaceInClickHouseAsync(uint fixtureId, ParsedFixture parsed, List<PlayerMatchStats> playerStats)
        {
            Dictionary<string, HashSet<string>> keysBeforeDelete = await _aggregateRefresher.CaptureKeysAsync(fixtureId);
            await _writer.DeleteFixtureAsync(fixtureId);
            await _writer.InsertBallEventsAsync(parsed.Balls);
            await _writer.InsertPartnershipsAsync(parsed.Partnerships);
            await _writer.InsertPlayerMatchStatsAsync(playerStats);
            await _aggregateRefresher.RefreshAsync(fixtureId, keysBeforeDelete);
        }
    }
}
