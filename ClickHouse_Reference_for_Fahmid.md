# LMS ClickHouse — Tables, Materialized Views & Query Patterns

Database: `lms`  
Total tables: 7 (4 raw/state, 3 MV target tables not listed separately — see Section 2)  
Total materialized views: 5

---

## Section 1 — Raw Tables

### 1.1 `lms.ball_events`

The core analytics table. One row per ball bowled across all fixtures.  
Engine: `MergeTree`, partitioned by `toYYYYMM(game_date)`.  
Order key: `(bowler_id, striker_id, fixture_id, innings_number, over_number, ball_sequence)` — chosen so H2H queries (bowler vs striker) read co-located data with no full scan.

**Current size: 32M+ rows.**

| Column | Type | Description |
|---|---|---|
| `fixture_id` | UInt32 | SQL Server Fixture ID |
| `innings_number` | UInt8 | 1 or 2 |
| `over_number` | UInt8 | 1-indexed (1–20). Values > 20 = post-innings penalty balls |
| `ball_sequence` | UInt8 | Ball within the over (1-indexed) |
| `ball_timestamp` | DateTime | UTC timestamp from live-scoring |
| `bowler_id` | UInt32 | SQL Server User ID of the bowler |
| `striker_id` | UInt32 | SQL Server User ID of the batter on strike |
| `non_striker_id` | UInt32 | SQL Server User ID of the non-striker |
| `runs_off_bat` | UInt8 | Runs credited to the batter (0–12; 12 = home run — see §3.5) |
| `extras_wide` | UInt8 | Wide runs (0 or 1+) |
| `extras_no_ball` | UInt8 | No-ball penalty runs |
| `extras_legbye` | UInt8 | Leg-bye runs |
| `extras_bye` | UInt8 | Bye runs |
| `is_wicket` | UInt8 | 1 = wicket fell this ball |
| `wicket_type` | LowCardinality(String) | e.g. `caught`, `bowled`, `run out`, `lbw` |
| `batting_position` | UInt8 | Batting order position (1–11) |
| `batting_team_id` | UInt32 | SQL Server Team ID |
| `bowling_team_id` | UInt32 | SQL Server Team ID |
| `score_at_ball` | UInt16 | Team score at the point this ball was bowled |
| `wickets_at_ball` | UInt8 | Wickets fallen at the point this ball was bowled |
| `game_date` | Date | Match date (from SQL Server Fixture.DateTime; JSON fallback) |
| `league_id` | UInt32 | From FixtureLeagueDivisionRoundSeason → League |
| `division_id` | UInt32 | From FixtureLeagueDivisionRoundSeason → Division |
| `season_id` | UInt32 | From FixtureLeagueDivisionRoundSeason → Season |
| `season_name` | LowCardinality(String) | Season display name |
| `venue_id` | UInt32 | SQL Server Venue ID |
| `region_id` | UInt32 | SQL Server Region ID |
| `country_id` | UInt8 | SQL Server Country ID |
| `home_runs` | UInt8 | 1 = this ball is a home run event (see §3.5) |
| `steal` | UInt8 | 1 = steal (runs go to non-striker) |
| `double_play` | UInt8 | 1 = double play fielding event |
| `balls_per_over` | UInt8 | Always 5 for LMS |
| `pitch_condition` | UInt8 | Pitch condition code |
| `fielder_id` | UInt32 | Fielder involved in the dismissal |
| `keeper_id` | UInt32 | Wicketkeeper ID |
| `pulse_after_pct` | Float32 | Win probability after this ball (future feature) |
| `pulse_change_pct` | Float32 | Win probability change on this ball (future feature) |

**MATERIALIZED columns** (computed at insert time, stored, zero write cost):

| Column | Definition |
|---|---|
| `is_legal_ball` | `extras_wide = 0 AND extras_no_ball = 0` |
| `is_dot_ball` | `runs_off_bat = 0 AND extras_wide = 0 AND extras_no_ball = 0` |
| `is_boundary` | `runs_off_bat = 4` |
| `is_six` | `runs_off_bat = 6` |
| `over_phase` | `over_number <= 6` → `Powerplay`; `<= 15` → `Middle`; else → `Death` |

> **Do not include MATERIALIZED columns in INSERT column lists.** ClickHouse computes them automatically. The C# `BallEventColumns` array in `ClickHouseWriter.cs` already excludes them.

---

### 1.2 `lms.partnerships`

One row per batting partnership per innings per fixture.  
Engine: `MergeTree`, partitioned by `toYYYYMM(game_date)`.  
Order key: `(batter1_id, batter2_id, fixture_id)`.

| Column | Type | Description |
|---|---|---|
| `fixture_id` | UInt32 | |
| `innings_number` | UInt8 | |
| `partnership_number` | UInt8 | Sequence within the innings (1st, 2nd, …) |
| `batter1_id` | UInt32 | Lower User ID of the pair |
| `batter2_id` | UInt32 | Higher User ID of the pair |
| `batting_team_id` | UInt32 | |
| `bowling_team_id` | UInt32 | |
| `runs_together` | UInt16 | Total runs scored during this partnership |
| `balls_together` | UInt16 | Total balls faced during this partnership |
| `run_rate` | Float32 | `runs / (balls / 5)` — LMS 5-ball over formula |
| `fours_together` | UInt8 | |
| `sixes_together` | UInt8 | |
| `start_over` | UInt8 | Over number when partnership began |
| `end_over` | UInt8 | Over number when partnership ended |
| `over_phase` | LowCardinality(String) | Phase when the partnership started |
| `game_date` | Date | |
| `league_id`, `division_id`, `season_id`, `season_name`, `venue_id`, `region_id` | | Context from SQL Server |

**Query note:** A batter can appear as either `batter1_id` or `batter2_id`. Always filter with `batter1_id = {id} OR batter2_id = {id}`, then use `if(batter1_id = {id}, batter2_id, batter1_id)` to derive the partner ID.

---

### 1.3 `lms.clips`

One row per highlight video clip. Migrated from SQL Server `Highlights` table.  
Engine: `MergeTree`, partitioned by `toYYYYMM(game_date)`.  
Order key: `(striker_id, bowler_id, fixture_id, clip_id)`.

| Column | Type | Description |
|---|---|---|
| `clip_id` | UInt64 | Source Highlights row ID |
| `fixture_id` | UInt32 | |
| `innings_number` | UInt8 | |
| `over_number` | UInt8 | 1-indexed (source is 0-indexed; corrected at migration time) |
| `ball_sequence` | UInt8 | |
| `ball_timestamp` | DateTime | |
| `clip_url` | String | CDN URL of the video |
| `clip_type` | LowCardinality(String) | `six`, `four`, `wicket` (future: `partnership`, `spell`, `innings`) |
| `bowler_id` | UInt32 | |
| `striker_id` | UInt32 | |
| `non_striker_id` | UInt32 | |
| `keeper_id` | UInt32 | |
| `fielder_id` | UInt32 | |
| `wicket_type` | LowCardinality(String) | |
| `is_six` | UInt8 | 1 = six clip |
| `duration_secs` | UInt16 | Clip length |
| `league_id`, `season_id`, `game_date` | | Context |

---

### 1.4 `lms.player_match_stats`

One row per player per completed match. Drives ratings, rankings, legends, and leaderboards.  
Engine: `MergeTree`, partitioned by `toYYYYMM(game_date)`.  
Order key: `(player_id, game_date, fixture_id)`.

This table contains the full Points Engine output — batting match points, bowling match points, fielding points, opposition strength, league strength weighting, legends points, etc. It is currently computed by the Migration Worker but **not yet persisted** (Phase 2). The C# models and insert code are complete and ready.

---

### 1.5 `lms.player_ratings`

Current player rating state. One logical row per player.  
Engine: `ReplacingMergeTree(last_updated)` — keeps only the most recent row per `player_id` after background merges.  
Order key: `player_id`.

Contains batting/bowling rating, Z-score, reliability %, games played, unique teams faced, and previous rating for movement tracking. **Phase 2 — not yet populated.**

**Query note:** Always use `FINAL` modifier or `argMax()` aggregation when reading from `ReplacingMergeTree` tables in production, because background merges may not have run yet:
```sql
SELECT * FROM lms.player_ratings FINAL WHERE player_id = 12345
```

---

### 1.6 `lms.league_rankings`

Live league leaderboard. One logical row per `(player_id, team_id, league_id, division_id, season_id)`.  
Engine: `ReplacingMergeTree(last_updated)`.  
**Phase 2 — not yet populated.**

---

### 1.7 `lms.global_rankings_snapshot`

Permanent monthly prestige rankings. Append-only (never overwritten).  
Engine: `MergeTree`, partitioned by `toYYYYMM(snapshot_date)`.  
Order key: `(snapshot_id, ranking_scope, ranking_category, rank)`.  
**Phase 2 — not yet populated.**

---

## Section 2 — Materialized Views

All 5 MVs use `SummingMergeTree`. They aggregate `ball_events` into pre-computed counters, so averages and rates are always derived at query time from stored sums — never stored directly.

> **Critical rule: always use `sum(column)` when reading from SummingMergeTree tables, never read column values directly.** ClickHouse may have multiple unmerged parts, so a direct column read can return a partial (un-summed) value. See §3.1 for detail.

---

### MV 2.1 `lms.h2h_stats` ← populated by `lms.h2h_stats_mv`

Career head-to-head stats for every bowler vs batter pair.  
Engine: `SummingMergeTree`, ORDER BY `(bowler_id, striker_id)`.

| Column | Description |
|---|---|
| `bowler_id` | |
| `striker_id` | |
| `legal_balls` | Legal deliveries faced |
| `runs` | Runs off bat |
| `wickets` | Dismissals |
| `sixes` | Sixes |
| `boundaries` | Fours |
| `dots` | Dot balls |

**Limitation:** No `game_date`, `season_id`, or `league_id` columns. If any date or league filter is applied, this MV cannot be used — fall back to `ball_events` (see §3.2 dual-path pattern).

---

### MV 2.2 `lms.player_batting_phase` ← populated by `lms.player_batting_mv`

Player batting stats grouped by `(striker_id, season_id, league_id, division_id, venue_id, over_phase)`.  
Engine: `SummingMergeTree`.

| Column | Description |
|---|---|
| `striker_id` | |
| `season_id`, `league_id`, `division_id`, `venue_id`, `over_phase` | Group key |
| `runs` | |
| `legal_balls` | Legal deliveries only |
| `dismissals` | |
| `boundaries` | Fours |
| `sixes` | |
| `dots` | |

**Limitation:** Stores `legal_balls` only — no `total_balls` column. LMS Rule 8 requires counting all deliveries (including penalty balls) for ball-count purposes, so this MV currently cannot be used for phase stats queries. The API falls back to `ball_events` with `count()` for total balls. **Re-enable once `total_balls` is added to this MV and the migration has been rerun.**

---

### MV 2.3 `lms.player_bowling_phase` ← populated by `lms.player_bowling_mv`

Player bowling stats grouped by `(bowler_id, season_id, league_id, division_id, venue_id, over_phase)`.  
Engine: `SummingMergeTree`.

| Column | Description |
|---|---|
| `bowler_id` | |
| `season_id`, `league_id`, `division_id`, `venue_id`, `over_phase` | Group key |
| `runs_conceded` | `runs_off_bat + extras_wide + extras_no_ball` |
| `legal_balls` | |
| `wickets` | |
| `dots` | |
| `wides` | Count of wide deliveries |
| `no_balls` | Count of no-ball deliveries |
| `sixes`, `fours`, `threes`, `twos`, `ones` | Count of legal deliveries by runs scored |

This MV is **fully usable** for bowling profile queries that filter by season/league/phase without date filtering.

---

### MV 2.4 `lms.team_phase` ← populated by `lms.team_phase_mv`

Team batting patterns grouped by `(batting_team_id, season_id, league_id, venue_id, over_phase)`.  
Engine: `SummingMergeTree`.

| Column | Description |
|---|---|
| `batting_team_id` | |
| `season_id`, `league_id`, `venue_id`, `over_phase` | Group key |
| `runs` | |
| `legal_balls` | |
| `wickets` | |
| `boundaries` | |

---

### MV 2.5 `lms.league_avg` ← populated by `lms.league_avg_mv`

League/region/national average run rates. Used to show context (e.g. "this league averages X runs/over in Death overs").  
Engine: `SummingMergeTree`, grouped by `(league_id, division_id, region_id, country_id, season_id, over_phase)`.

| Column | Description |
|---|---|
| `league_id`, `division_id`, `region_id`, `country_id`, `season_id`, `over_phase` | Group key |
| `runs` | |
| `legal_balls` | |
| `wickets` | |
| `boundaries` | |
| `sixes` | |

---

## Section 3 — Query Patterns

### 3.1 SummingMergeTree: Always Use `sum()`

`SummingMergeTree` accumulates counters across multiple parts in the background. Until parts are merged, a raw column read returns only the partial value for that part.

**Wrong:**
```sql
SELECT legal_balls FROM lms.h2h_stats
WHERE bowler_id = 1 AND striker_id = 2
```

**Correct:**
```sql
SELECT sum(legal_balls) AS legal_balls
FROM lms.h2h_stats
WHERE bowler_id = 1 AND striker_id = 2
```

This applies to all 5 MV target tables: `h2h_stats`, `player_batting_phase`, `player_bowling_phase`, `team_phase`, `league_avg`.

---

### 3.2 Dual-Path Query Pattern (MV vs `ball_events`)

The MVs do not have `game_date` columns (except implicitly via `season_id`). When a date range filter is needed, the query must go to `ball_events` directly. The pattern is:

```
No date filter + MV has the group key columns?
  → Use the MV fast path (pre-aggregated, very fast)

Date filter applied OR MV missing a required column?
  → Fall back to ball_events directly (full scan within partition)
```

**Example — H2H career (MV fast path):**
```sql
SELECT bowler_id,
       sum(legal_balls) AS balls,
       sum(runs)        AS runs,
       sum(wickets)     AS wickets
FROM lms.h2h_stats
WHERE striker_id = @playerId
GROUP BY bowler_id
HAVING balls >= 10
```

**Example — H2H with season filter (ball_events fallback):**
```sql
SELECT bowler_id,
       sum(toUInt64(is_legal_ball)) AS balls,
       sum(toUInt64(runs_off_bat))  AS runs,
       sum(toUInt64(is_wicket))     AS wickets
FROM lms.ball_events
WHERE striker_id = @playerId
  AND season_id  = @seasonId
GROUP BY bowler_id
HAVING balls >= 10
```

This dual-path decision is made in C# based on whether any filter parameters are present before building the SQL string.

---

### 3.3 ClickHouseConnection Is Not Thread-Safe

`ClickHouseConnection` (the `ClickHouse.Client` library used in this project) does **not** support concurrent queries on a single connection. All queries within a single API request must run sequentially on one connection.

**Pattern used in the API services:**
```csharp
// Open ONE connection per request
using var conn = new ClickHouseConnection(_connectionString);
await conn.OpenAsync(ct);

// Run queries ONE AT A TIME — never Task.WhenAll on the same conn
var phaseStats     = await GetPhaseStatsAsync(conn, ...);
var scoringPattern = await GetScoringPatternAsync(conn, ...);
var h2hBowlers     = await GetH2HBowlersAsync(conn, ...);
var partnerships   = await GetPartnershipsAsync(conn, ...);
```

This is different from SQL Server's `DbContext` which can handle some concurrent operations. If integrating into LMST20 CQRS handlers, ensure each handler opens its own connection and queries sequentially within it.

---

### 3.4 LMS Run Rate Formula (5-Ball Overs)

LMS uses 5-ball overs, not 6. All run rate calculations must reflect this.

```
RunRate = runs / legal_balls × 5
```

**Example:**
```sql
SELECT over_phase,
       sum(runs)        AS runs,
       sum(legal_balls) AS legal_balls,
       -- compute run_rate in application layer:
       -- RunRate = (double)runs / legal_balls * 5
FROM lms.league_avg
WHERE league_id = @leagueId AND season_id = @seasonId
GROUP BY over_phase
```

Do not use `/ 6` anywhere for LMS data. The `balls_per_over` column on `ball_events` is always `5` for LMS.

---

### 3.5 LMS Rule 8 — Penalty Ball Counting

LMS Rule 8 specifies that all deliveries (including wides and no-balls) count toward the ball count for certain stats. Use `count()` not `sum(is_legal_ball)` when the spec requires total balls.

```sql
-- Total balls (legal + penalty) — Rule 8
count() AS total_balls

-- Legal balls only (for economy rate, strike rate)
sum(is_legal_ball) AS legal_balls
```

This is why `player_batting_phase` MV (which only stores `legal_balls`) is currently bypassed for phase stats — the API queries `ball_events` directly with `count()`.

---

### 3.6 Over Number Cap for Over Trend Queries

`over_number` is stored 1-indexed (1–20). Some scorers record penalty deliveries after the 20th over, resulting in `over_number > 20` in the data. Always cap over trend queries to exclude these:

```sql
WHERE over_number BETWEEN 1 AND 20
```

Without this cap, the over trend chart will show phantom overs beyond the 20th.

---

### 3.7 Home Runs — Excluded from Sixes Bucket

When a batter hits a six on the last ball of the last over and qualifies for a home run, the parser stores `runs_off_bat = 12` and `home_runs = 1`. This means:

- `runs_off_bat = 6 AND home_runs = 0` → a normal six
- `runs_off_bat = 12 AND home_runs > 0` → a home run (NOT a six)

Always exclude home run deliveries from the sixes bucket in run distribution queries:

```sql
-- Sixes (normal sixes only, excludes home runs)
countIf(runs_off_bat = 6 AND extras_wide = 0 AND home_runs = 0) AS sixes,

-- Home runs (separate bucket)
countIf(home_runs > 0)                                          AS home_run_count,
sum(if(home_runs > 0, toUInt64(runs_off_bat), 0))              AS home_run_runs
```

> **Alias warning:** Do not name an alias `home_runs` in a query that also references the `home_runs` column. ClickHouse will shadow the column name with the alias and throw `ILLEGAL_AGGREGATION`. Use `home_run_count` or similar.

---

### 3.8 `lower(wicket_type)` for Dismissal Filtering

`wicket_type` is stored as-is from the live-scoring JSON (mixed case). Always apply `lower()` when filtering by wicket type:

```sql
WHERE lower(wicket_type) = 'caught'
```

---

### 3.9 Rebuilding MVs After a Full Remigration

The 5 MV target tables are populated automatically by the materialized view as data arrives. After a full truncate-and-remigration of `ball_events`, run `rebuild_mvs.sql` to repopulate them from scratch:

```bash
clickhouse-client --user lms_admin --password *** \
  --multiquery < rebuild_mvs.sql
```

This script truncates each MV target table and reinserts from `ball_events`. It is safe to rerun.

---

## Section 4 — Connection String Format

```
Host=<host>;Port=8123;Database=lms;Username=lms_admin;Password=<password>
```

The C# library used is `ClickHouse.Client` (NuGet). The connection string is stored in environment variable `LMS_CH_CONN` — never in `appsettings.json` or source control.

---

*Document generated from `clickhouse_schema.sql` and `LMS.PremiumProfile.Api` service code — June 2026.*
