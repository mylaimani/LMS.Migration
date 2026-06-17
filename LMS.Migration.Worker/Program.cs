using LMS.Migration.Core;
using LMS.Migration.Core.Parsers;
using LMS.Migration.Worker;

// ── Configuration ──────────────────────────────────────────────
// Connection strings come from environment variables — never commit
// credentials. Set once per machine (then restart the terminal/VS):
//   setx LMS_SQL_CONN "Server=localhost;Database=lastmanstands;Trusted_Connection=True;TrustServerCertificate=True;"
//   setx LMS_CH_CONN  "Host=localhost;Port=8123;Database=lms;Username=lms_admin;Password=..."
var sqlConn = Environment.GetEnvironmentVariable("LMS_SQL_CONN")
    ?? throw new InvalidOperationException("Environment variable LMS_SQL_CONN is not set.");
var chConn = Environment.GetEnvironmentVariable("LMS_CH_CONN")
    ?? throw new InvalidOperationException("Environment variable LMS_CH_CONN is not set.");

// ── Services ───────────────────────────────────────────────────
var fixtureReader = new SqlServerReader(sqlConn);
var metaReader = new FixtureMetadataReader(sqlConn);
var parser = new FixtureStateParser();
var extractor = new PlayerStatsExtractor();
var writer = new ClickHouseWriter(chConn);

// ── Clips-only mode:  .\LMS.Migration.Worker.exe clips ─────────
// Migrates the Highlights table (sixes/fours/wickets with batsman, bowler,
// keeper, fielder ids + clip URL) into lms.clips. Independent of the main
// ball-by-ball migration — safe to run before, after, or alongside it.
if (args.Length > 0 && args[0].Equals("clips", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Clips migration: loading fixture metadata...");
    var clipMeta = await metaReader.LoadAllAsync();
    var highlightsReader = new HighlightsReader(sqlConn);

    int clipTotal = 0;
    var clipBuffer = new List<LMS.Migration.Core.Models.ClipRecord>(5000);

    await foreach (var clip in highlightsReader.ReadAllAsync())
    {
        if (clipMeta.TryGetValue(clip.FixtureId, out var m))
        {
            clip.LeagueId = m.LeagueId;
            clip.SeasonId = m.SeasonId;
            clip.GameDate = m.FixtureDate == DateTime.UnixEpoch ? clip.BallTimestamp.Date : m.FixtureDate.Date;
        }
        else
        {
            clip.GameDate = clip.BallTimestamp.Date;
        }

        clipBuffer.Add(clip);
        if (clipBuffer.Count >= 5000)
        {
            await writer.InsertClipsAsync(clipBuffer);
            clipTotal += clipBuffer.Count;
            clipBuffer.Clear();
            Console.WriteLine($"  {clipTotal} clips migrated...");
        }
    }

    if (clipBuffer.Count > 0)
    {
        await writer.InsertClipsAsync(clipBuffer);
        clipTotal += clipBuffer.Count;
    }

    Console.WriteLine($"Done. {clipTotal} clips migrated.");
    return;
}

// ── Opposition strength inputs ─────────────────────────────────
// Historical world ranking snapshots (twice weekly, StatisticsLMSRankingDate)
// + running form guide. Each match uses the latest snapshot published
// BEFORE that match — spec §3.3, locked pre-match.
// TODO: confirm with business that WORLD ranking is the right scope
// (vs Country/Region).
var rankingProvider = new HistoricalTeamRankingProvider(new TeamRankingReader(sqlConn));
await rankingProvider.InitAsync();
var formTracker = new FormTracker();
Console.WriteLine($"Loaded {rankingProvider.SnapshotCount} ranking snapshot dates.");

float OppositionStrength(uint playerTeamId, uint opposingTeamId)
{
    int rank = rankingProvider.GetRank(opposingTeamId);
    return PointsCalculator.OppositionStrength(rank, formTracker.FormScore(opposingTeamId));
}

// ── Ratings & league rankings accumulators ─────────────────────
//var ratingAccumulator = new PlayerRatingAccumulator();
//var leagueRankings = new LeagueRankingAccumulator();

// ── Fixture metadata: one bulk load instead of 176k queries ────
Console.WriteLine("Loading fixture metadata...");
var metaMap = await metaReader.LoadAllAsync();
Console.WriteLine($"Loaded metadata for {metaMap.Count} fixtures.");

// ── Catch-up mode flag:  .\LMS.Migration.Worker.exe catchup ────
// Set-reconciliation: processes exactly the fixtures present in SQL Server
// but absent from ClickHouse — guarantees no misses (even late-recorded
// old fixtures) and no duplicates. Used during the cutover window while
// new games are still being played.
bool catchupMode = args.Length > 0 && args[0].Equals("catchup", StringComparison.OrdinalIgnoreCase);

// ── Resume support (full-migration mode only) ───────────────────
// If a previous run crashed, continue after the last fully-flushed fixture.
// Anything above the safe point (possibly partial flushes) is deleted.
uint startAfter = 0;
if (!catchupMode)
{
    startAfter = await writer.GetResumePointAsync();
    if (startAfter > 0)
    {
        Console.WriteLine($"Resuming: cleaning rows above fixture {startAfter} and continuing from there.");
        await writer.DeleteFixturesAfterAsync(startAfter);
    }
}

// ── Insert buffering ────────────────────────────────────────────
// ClickHouse prefers few large inserts over many small ones. Buffer
// ~200 fixtures (~40k ball rows) per insert for a large speedup.
const int FlushEveryFixtures = 200;
var ballBuffer = new List<LMS.Migration.Core.Models.BallEvent>(50_000);
var partnershipBuffer = new List<LMS.Migration.Core.Models.Partnership>(4_000);
int fixturesInBuffer = 0;

async Task FlushAsync()
{
    if (fixturesInBuffer == 0) return;
    await writer.InsertBallEventsAsync(ballBuffer);
    await writer.InsertPartnershipsAsync(partnershipBuffer);
    ballBuffer.Clear();
    partnershipBuffer.Clear();
    fixturesInBuffer = 0;
}

// ── Run ────────────────────────────────────────────────────────
int total = 0;
int failed = 0;

// Shared per-fixture pipeline (used by full migration AND catch-up mode)
async Task ProcessFixtureAsync(uint fixtureId, string fixtureJson)
{
    try
    {
        // 1. Parse the FixtureState JSON → balls + partnerships + match facts
        var parsed = parser.Parse(fixtureId, fixtureJson);

        if (parsed.Balls.Count == 0)
        {
            Console.WriteLine($"[SKIP] {fixtureId} — no balls found");
            return;
        }

        // 2. Context metadata from the preloaded dictionary
        metaMap.TryGetValue(fixtureId, out var meta);

        // 3. Stamp metadata onto every ball and partnership.
        //    Game date: Fixture.DateTime from SQL is authoritative
        //    (JSON GameSetUp timestamp is the fallback).
        var gameDate = meta != null && meta.FixtureDate != DateTime.UnixEpoch
            ? meta.FixtureDate.Date
            : parsed.GameDate;

        foreach (var b in parsed.Balls)
        {
            b.LeagueId = meta?.LeagueId ?? 0;
            b.DivisionId = meta?.DivisionId ?? 0;
            b.SeasonId = meta?.SeasonId ?? 0;
            b.SeasonName = meta?.SeasonName ?? "";
            b.VenueId = meta?.VenueId ?? b.VenueId;     // JSON root also carries VenueId
            b.RegionId = meta?.RegionId ?? b.RegionId;
            b.CountryId = meta?.CountryId ?? b.CountryId;
            b.GameDate = gameDate;
        }

        foreach (var p in parsed.Partnerships)
        {
            p.LeagueId = meta?.LeagueId ?? 0;
            p.DivisionId = meta?.DivisionId ?? 0;
            p.SeasonId = meta?.SeasonId ?? 0;
            p.SeasonName = meta?.SeasonName ?? "";
            p.VenueId = meta?.VenueId ?? 0;
            p.RegionId = meta?.RegionId ?? 0;
            p.GameDate = gameDate;
        }

        // 4. Points Engine — match facts + one row per player per match.
        //    TODO before production run:
        //      - hasConfirmedEmail lookup (150 participation points)
        //      - leagueStrengthWeighting (>1.00 for blue ribbon events)
        // Rained-out flag from SQL (FixtureLMSExtraInformation) → no-result
        var matchResultRaw = meta?.RainedOut == true ? "RainedOut" : parsed.MatchResultRaw;
        var matchInfo = extractor.BuildMatchInfo(fixtureId, parsed.Balls, parsed.InningsScores, matchResultRaw);

        // Drift check: ball stream vs official innings score. Drift means the
        // live-scoring stream is missing/extra deliveries (scorer corrections)
        // — player stats and results are unaffected (they use aggregates).
        for (int i = 0; i < parsed.InningsScores.Count; i++)
        {
            var official = parsed.InningsScores[i];
            int streamTotal = parsed.Balls
                .Where(x => x.InningsNumber == i + 1)
                .Sum(x => x.RunsOffBat + x.ExtrasWide + x.ExtrasNoBall + x.ExtrasBye + x.ExtrasLegBye);
            if (streamTotal != official.Runs)
                Console.WriteLine($"[DRIFT] {fixtureId} innings {i + 1}: ball stream {streamTotal} vs official {official.Runs}");
        }

        // Load the ranking snapshot that was current at this match's date
        // (fixtures are processed chronologically, so this only moves forward)
        await rankingProvider.AdvanceToAsync(matchInfo.GameDate);

        var playerStats = extractor.Build(parsed.Balls, parsed.PlayerSummaries, matchInfo,
            oppositionStrengthLookup: OppositionStrength);

        // 5. Buffer for ClickHouse (flushed every FlushEveryFixtures)
        ballBuffer.AddRange(parsed.Balls);
        partnershipBuffer.AddRange(parsed.Partnerships);
        fixturesInBuffer++;
        if (fixturesInBuffer >= FlushEveryFixtures)
            await FlushAsync();
        // PHASE 2: await writer.InsertPlayerMatchStatsAsync(playerStats);

        // 6. Feed ratings + league ranking accumulators
        var teamA = parsed.Balls[0].BattingTeamId;
        var teamB = parsed.Balls[0].BowlingTeamId;
        /*foreach (var ps in playerStats)
        {
            ratingAccumulator.Add(ps, ps.TeamId == teamA ? teamB : teamA);
            leagueRankings.Add(ps);
        }*/

        // 7. Record this match's result in the form guide AFTER processing,
        //    so the next match sees form "prior to Match N" (spec §3).
        if (matchInfo.IsNoResult || matchInfo.WinningTeamId == 0)
        {
            formTracker.Record(teamA, 0);
            formTracker.Record(teamB, 0);
        }
        else
        {
            formTracker.Record(matchInfo.WinningTeamId, +1);
            formTracker.Record(matchInfo.WinningTeamId == teamA ? teamB : teamA, -1);
        }

        total++;
        Console.WriteLine($"[OK] {fixtureId} — {parsed.Balls.Count} balls, " +
                          $"{parsed.Partnerships.Count} partnerships, {playerStats.Count} player stats" +
                          (matchInfo.IsNoResult ? " (NO RESULT — points excluded)" : ""));
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {fixtureId} — {ex.Message}");
    }
}

// ── Catch-up mode ───────────────────────────────────────────────
if (catchupMode)
{
    Console.WriteLine("Catch-up: comparing fixture ids between SQL Server and ClickHouse...");
    var sqlIds = await fixtureReader.GetAllFixtureIdsAsync();
    var chIds = await writer.GetAllFixtureIdsAsync();
    var missing = sqlIds.Where(id => !chIds.Contains(id)).OrderBy(id => id).ToList();

    Console.WriteLine($"SQL Server fixtures: {sqlIds.Count} | ClickHouse fixtures: {chIds.Count} | to migrate: {missing.Count}");

    foreach (var id in missing)
    {
        var json = await fixtureReader.GetFixtureStateAsync(id);
        if (string.IsNullOrEmpty(json))
        {
            Console.WriteLine($"[SKIP] {id} — empty state");
            continue;
        }
        await ProcessFixtureAsync(id, json);
    }

    await FlushAsync();
    Console.WriteLine($"\nCatch-up complete: {total} fixtures migrated, {failed} failed.");
    if (total > 0)
        Console.WriteLine("Reminder: run rebuild_mvs.sql and `clips` mode to refresh aggregates and new clips.");
    return;
}

// ── Full migration ──────────────────────────────────────────────
Console.WriteLine("Starting LMS migration...");

await foreach (var (fixtureId, fixtureJson) in fixtureReader.ReadAllFixturesAsync(startAfter))
{
    await ProcessFixtureAsync(fixtureId, fixtureJson);
}

await FlushAsync();   // write any remaining buffered fixtures

Console.WriteLine($"\n{total} fixtures migrated, {failed} failed.");

// ── Final pass: launch-baseline ratings + league rankings ──────
// Initial calibration (spec §8): population benchmarks from ALL players,
// then the rating model applied to everyone. Ratings computed here become
// each player's baseline (movement tracking starts from launch).
/*Console.WriteLine("Calculating player ratings (initial calibration)...");
var ratings = ratingAccumulator.BuildRatings(DateTime.UtcNow);
await writer.InsertPlayerRatingsAsync(ratings);
Console.WriteLine($"Inserted {ratings.Count} player ratings.");

Console.WriteLine("Calculating league rankings...");
var leagueRows = leagueRankings.BuildRankings(DateTime.UtcNow);
await writer.InsertLeagueRankingsAsync(leagueRows);
Console.WriteLine($"Inserted {leagueRows.Count} league ranking entries.");*/

Console.WriteLine("Done.");
