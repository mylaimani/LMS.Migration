# Batting Profile API — End-to-End Walkthrough

This document walks through the complete implementation of the Batting Profile API — from the HTTP request, through dependency injection, service layer, and ClickHouse queries, to the JSON response. The Bowling and Team Profile APIs follow the exact same pattern.

---

## 1. The Endpoint

```
GET /api/batting/{playerId}
```

**Example calls:**

```
# Career stats (no filters)
GET /api/batting/12345

# This season only
GET /api/batting/12345?seasonId=88

# Specific league + year
GET /api/batting/12345?leagueId=5&year=2024

# Custom date range
GET /api/batting/12345?fromDate=2024-01-01&toDate=2024-06-30
```

All filter parameters are optional. When none are provided, the response covers the player's full career in ClickHouse.

---

## 2. What the Response Contains

The API returns a single JSON object with five sections:

```json
{
  "playerId": 12345,
  "seasonId": null,
  "leagueId": null,

  "phaseStats": [ ... ],          // Powerplay / Middle / Death breakdown
  "scoringPattern": {
    "distribution": { ... },      // Dots, 1s, 2s, 3s, 4s, 5s, 6s, home runs, steals
    "overTrend": [ ... ]          // Runs and run rate per over (over 1 → 20)
  },
  "favouriteBowlers": [ ... ],    // Bowlers this batter scores fastest against
  "nemesisBowlers": [ ... ],      // Bowlers who dismiss this batter most
  "partnerships": [ ... ]         // Best batting partners sorted by total runs
}
```

All averages, strike rates, dot %, and run rates are **computed properties on the C# model** — they are not stored in ClickHouse. Only the raw counts (runs, balls, wickets, dots, etc.) come from the database.

---

## 3. Program.cs — Startup and DI Registration

```csharp
// Read connection string from environment variable
var chConn = Environment.GetEnvironmentVariable("LMS_CH_CONN")
    ?? builder.Configuration["ClickHouse:ConnectionString"]
    ?? throw new InvalidOperationException("ClickHouse connection string not found.");

// Register service — one instance per HTTP request (Scoped)
// The connection string is passed directly; the service manages its own connection internally.
builder.Services.AddScoped<IBattingProfileService>(_ => new BattingProfileService(chConn));
```

**Why not inject `ClickHouseConnection` directly?**
`ClickHouseConnection` is NOT thread-safe. Rather than managing it in DI, each service opens its own connection at the start of the request and runs all queries sequentially on it. This is the safe pattern for this library.

---

## 4. The Controller — `BattingController.cs`

```csharp
[ApiController]
[Route("api/batting")]
public class BattingController : ControllerBase
{
    private readonly IBattingProfileService _service;

    public BattingController(IBattingProfileService service)
    {
        _service = service;
    }

    [HttpGet("{playerId:int}")]
    public async Task<IActionResult> GetBattingProfile(
        [FromRoute] uint      playerId,
        [FromQuery] uint?     seasonId = null,
        [FromQuery] uint?     leagueId = null,
        [FromQuery] int?      year     = null,
        [FromQuery] DateOnly? fromDate = null,
        [FromQuery] DateOnly? toDate   = null,
        CancellationToken ct = default)
    {
        // Input validation
        if (playerId == 0)
            return BadRequest("playerId must be greater than 0.");
        if (year.HasValue && (year < 2000 || year > DateTime.UtcNow.Year))
            return BadRequest($"year must be between 2000 and {DateTime.UtcNow.Year}.");
        if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
            return BadRequest("fromDate must be before toDate.");

        var result = await _service.GetBattingProfileAsync(
            playerId, seasonId, leagueId, year, fromDate, toDate, ct);

        return Ok(result);
    }
}
```

The controller is deliberately thin — validation only. All ClickHouse logic is in the service.

---

## 5. The Interface — `IBattingProfileService.cs`

```csharp
public interface IBattingProfileService
{
    Task<BattingProfileResponse> GetBattingProfileAsync(
        uint      playerId,
        uint?     seasonId,
        uint?     leagueId,
        int?      year,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken ct = default);
}
```

The interface exists so that if you integrate this into LMST20's CQRS handler pattern, the handler depends on the interface, not the concrete class — making it testable and replaceable.

---

## 6. The Service — `BattingProfileService.cs`

This is where all the ClickHouse work happens. The service runs **4 sequential queries** on a single connection.

### 6.1 Entry Point

```csharp
public async Task<BattingProfileResponse> GetBattingProfileAsync(
    uint playerId, uint? seasonId, uint? leagueId,
    int? year, DateOnly? fromDate, DateOnly? toDate,
    CancellationToken ct = default)
{
    // ONE connection for the entire request
    using var conn = new ClickHouseConnection(_connectionString);
    await conn.OpenAsync(ct);

    // 4 queries run SEQUENTIALLY — ClickHouseConnection is not thread-safe
    var phaseStats     = await GetPhaseStatsAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
    var scoringPattern = await GetScoringPatternAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
    var (fav, nem)     = await GetH2HBowlersAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
    var partnerships   = await GetPartnershipsAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);

    return new BattingProfileResponse
    {
        PlayerId         = playerId,
        PhaseStats       = phaseStats,
        ScoringPattern   = scoringPattern,
        FavouriteBowlers = fav,
        NemesisBowlers   = nem,
        Partnerships     = partnerships,
    };
}
```

---

### 6.2 Query 1 — Phase Stats (Powerplay / Middle / Death)

Always queries `ball_events` directly (not the `player_batting_phase` MV). Reason: LMS Rule 8 requires counting all deliveries including penalty balls using `count()`, but the MV only stores `legal_balls`.

```sql
SELECT over_phase,
       sum(toUInt64(runs_off_bat))  AS runs,
       count()                      AS total_balls,   -- ALL deliveries, including penalty (Rule 8)
       sum(toUInt64(is_wicket))     AS dismissals,
       sum(toUInt64(is_boundary))   AS boundaries,
       sum(toUInt64(is_six))        AS sixes,
       sum(toUInt64(is_dot_ball))   AS dots
FROM lms.ball_events
WHERE striker_id = 12345
  AND season_id  = 88                -- if seasonId filter applied
GROUP BY over_phase
ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)
```

`over_phase` is a MATERIALIZED column on `ball_events` — ClickHouse computes it at insert time from `over_number` (Powerplay = overs 1–6, Middle = 7–15, Death = 16–20). No calculation needed at query time.

Computed properties on the C# model (not stored, derived from the above counts):
- `Average` = `Runs / Dismissals`
- `StrikeRate` = `Runs / Balls × 100`
- `BoundaryPct` = `Boundaries / Balls × 100`
- `DotPct` = `Dots / Balls × 100`

---

### 6.3 Query 2 — Scoring Pattern

Two sub-queries run on the same connection in sequence.

**2a. Run Distribution** — how often does the batter score 0, 1, 2, 3, 4, 5, 6, home run?

```sql
SELECT
    countIf(runs_off_bat = 0 AND is_legal_ball = 1)                       AS dots,
    countIf(runs_off_bat = 1 AND extras_wide = 0)                         AS ones,
    countIf(runs_off_bat = 2 AND extras_wide = 0)                         AS twos,
    countIf(runs_off_bat = 3 AND extras_wide = 0)                         AS threes,
    countIf(runs_off_bat = 4 AND extras_wide = 0)                         AS fours,
    countIf(runs_off_bat = 5 AND extras_wide = 0)                         AS fives,
    -- Home runs store runs_off_bat=12, so exclude from the sixes bucket
    countIf(runs_off_bat = 6 AND extras_wide = 0 AND home_runs = 0)       AS sixes,
    countIf(home_runs > 0)                                                 AS home_run_count,
    sum(if(home_runs > 0, toUInt64(runs_off_bat), 0))                     AS home_run_runs,
    sum(toUInt64(steal))                                                   AS steals,
    sum(toUInt64(runs_off_bat))                                            AS total_runs,
    count()                                                                AS total_balls,
    sumIf(toUInt64(runs_off_bat) + toUInt64(extras_wide) + toUInt64(extras_no_ball),
          is_legal_ball = 0)                                               AS penalty_runs,
    countIf(is_legal_ball = 0)                                             AS penalty_balls
FROM lms.ball_events
WHERE striker_id = 12345
```

**Key points:**
- Dots count only legal deliveries (`is_legal_ball = 1`). A wide that results in 0 runs is not a batting dot.
- Ones through Fives include no-ball deliveries (`extras_wide = 0`) because runs scored off a no-ball still go to the batter.
- Sixes exclude home runs (`home_runs = 0`) — when `runs_off_bat = 12` it's a home run, not a six.
- Never alias a column `home_runs` in this query — it shadows the `home_runs` column and ClickHouse throws `ILLEGAL_AGGREGATION`.

**2b. Over Trend** — runs and run rate per over (overs 1–20):

```sql
SELECT over_number,
       sum(toUInt64(runs_off_bat)) AS runs,
       count()                     AS total_balls
FROM lms.ball_events
WHERE striker_id = 12345
  AND over_number BETWEEN 1 AND 20   -- cap required to exclude post-innings penalty balls
GROUP BY over_number
ORDER BY over_number
```

The cap `over_number BETWEEN 1 AND 20` is mandatory. Some scorers record penalty deliveries after the 20th over, which would create phantom over 21, 22, etc. in the trend chart without this filter.

Run rate is computed in C#: `RunRate = (double)Runs / TotalBalls * 5` (LMS 5-ball over, not 6).

---

### 6.4 Query 3 — H2H Bowlers (Favourite + Nemesis)

This is where the **dual-path pattern** is applied. The decision is made in C# before building the SQL:

```csharp
bool anyFilter = seasonId.HasValue || leagueId.HasValue || year.HasValue
              || fromDate.HasValue || toDate.HasValue;
```

**Path A — No filters → use `lms.h2h_stats` MV (fast, pre-aggregated):**

```sql
SELECT bowler_id,
       sum(legal_balls) AS balls,
       sum(runs)        AS runs,
       sum(wickets)     AS wickets,
       sum(sixes)       AS sixes,
       sum(boundaries)  AS boundaries,
       sum(dots)        AS dots
FROM lms.h2h_stats
WHERE striker_id = 12345
GROUP BY bowler_id
HAVING balls >= 10
```

Note: `sum()` is required on all columns — `lms.h2h_stats` is a `SummingMergeTree` and may have multiple unmerged parts. Reading a column value directly without `sum()` would return a partial (incorrect) result.

**Path B — Any filter applied → fall back to `ball_events`:**

```sql
SELECT bowler_id,
       sum(toUInt64(is_legal_ball)) AS balls,
       sum(toUInt64(runs_off_bat))  AS runs,
       sum(toUInt64(is_wicket))     AS wickets,
       sum(toUInt64(is_six))        AS sixes,
       sum(toUInt64(is_boundary))   AS boundaries,
       sum(toUInt64(is_dot_ball))   AS dots
FROM lms.ball_events
WHERE striker_id = 12345
  AND season_id  = 88
GROUP BY bowler_id
HAVING balls >= 10
```

The MV path is used because `lms.h2h_stats` has no `game_date`, `season_id`, or `league_id` columns — it is career-only.

After the query, results are sorted in C# to produce two lists:
- `FavouriteBowlers` — sorted by `StrikeRate` descending (top 10)
- `NemesisBowlers` — sorted by `Wickets` descending, then `StrikeRate` ascending (top 10)

---

### 6.5 Query 4 — Partnerships

```sql
SELECT
    if(batter1_id = 12345, batter2_id, batter1_id) AS partner_id,
    count()                       AS partnership_count,
    sum(toUInt64(runs_together))  AS total_runs,
    sum(toUInt64(balls_together)) AS total_balls,
    sum(toUInt64(fours_together)) AS total_fours,
    sum(toUInt64(sixes_together)) AS total_sixes
FROM lms.partnerships
WHERE (batter1_id = 12345 OR batter2_id = 12345)
  AND season_id = 88
GROUP BY partner_id
HAVING total_balls >= 10
ORDER BY total_runs DESC
LIMIT 50
```

Key points:
- A batter can appear as either `batter1_id` or `batter2_id` — always filter with `OR`.
- `if(batter1_id = {id}, batter2_id, batter1_id)` extracts the partner's ID regardless of storage order.
- `HAVING total_balls >= 10` excludes trivial 1-ball partnerships.

Computed in C#:
- `AvgRunsTogether` = `TotalRuns / PartnershipCount`
- `RunRate` = `TotalRuns / TotalBalls × 5` (LMS 5-ball over)

---

## 7. WHERE Clause Builders

All four queries share the same filter logic built by helper methods:

```csharp
private static string BuildBallEventsWhere(
    uint playerId, uint? seasonId, uint? leagueId,
    int? year, DateOnly? fromDate, DateOnly? toDate)
{
    var parts = new List<string> { $"striker_id = {playerId}" };
    if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
    if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
    if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
    if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
    if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
    return "WHERE " + string.Join(" AND ", parts);
}
```

`partnerships` uses a separate builder because the player can be in either `batter1_id` or `batter2_id`.

---

## 8. Response Model Summary

```
BattingProfileResponse
├── PhaseStats: List<PhaseStatRow>
│     phase, runs, balls, dismissals, boundaries, sixes, dots
│     [computed] average, strikeRate, boundaryPct, sixPct, dotPct
│
├── ScoringPattern
│   ├── Distribution: RunDistribution
│   │     dots, ones, twos, threes, fours, fives, sixes, homeRuns,
│   │     homeRunRuns, steals, totalRuns, totalBalls, penaltyRuns, penaltyBalls
│   │     [computed] overallStrikeRate
│   └── OverTrend: List<OverTrendRow>
│         over (1–20), runs, totalBalls
│         [computed] runsPerBall, runRate (× 5)
│
├── FavouriteBowlers: List<H2HBowlerRow>   (top 10 by strike rate)
│     bowlerId, balls, runs, wickets, sixes, boundaries, dots
│     [computed] strikeRate, dotPct
│
├── NemesisBowlers: List<H2HBowlerRow>     (top 10 by wickets)
│     (same structure as FavouriteBowlers)
│
└── Partnerships: List<PartnershipRow>     (top 50 by total runs)
      partnerId, partnershipCount, totalRuns, totalBalls, totalFours, totalSixes
      [computed] avgRunsTogether, runRate (× 5)
```

---

## 9. Health Check Endpoint

Before testing any batting endpoint, hit the health check first to confirm ClickHouse connectivity:

```
GET /api/health
```

Returns `200 OK` with column sample data if the connection is good, or a `500` with the full exception detail if not. This is the first thing to check on a new deployment.

---

## 10. Notes for Integration into LMST20

If absorbing this into `LMST20.API.MobileApp`:

**Connection management:** Each CQRS query handler should open its own `ClickHouseConnection`, run its queries sequentially, and dispose it before returning. Do not inject a shared `ClickHouseConnection` as a singleton or scoped service — it is not thread-safe.

**Handler pattern:** The service method maps naturally to a single MediatR query handler. Register `IBattingProfileService` → `BattingProfileService` in the application's DI container exactly as shown in `Program.cs` above.

**.NET version:** This API targets .NET 9. `LMST20.API.MobileApp` targets .NET 8. The `ClickHouse.Client` NuGet package supports both — no changes to the service or query code are needed. Only the project file `<TargetFramework>` tag differs.

**Connection string:** Store in environment variable `LMS_CH_CONN`. Do not commit it to appsettings or source control.

---

*Document generated from `BattingController.cs`, `BattingProfileService.cs`, and `BattingProfileResponse.cs` — June 2026.*
