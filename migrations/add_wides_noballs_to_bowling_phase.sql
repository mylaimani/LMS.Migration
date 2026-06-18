-- =====================================================================
-- Migration: Add wides + no_balls columns to lms.player_bowling_phase
-- Run once on GCP staging ClickHouse server (34.185.193.227)
--
-- Steps:
--   1. ALTER TABLE to add the two new columns (zero-downtime, existing
--      rows get DEFAULT 0 — ClickHouse lazy-fills on next merge)
--   2. DROP + recreate the MV so new inserts populate the columns
--   3. TRUNCATE + backfill so historical data gets correct counts
--
-- Safe to re-run (ALTER IF NOT EXISTS, DROP IF EXISTS, TRUNCATE is idempotent).
-- =====================================================================

-- Step 1: Add columns to the target table (lazy, existing rows = 0)
ALTER TABLE lms.player_bowling_phase
    ADD COLUMN IF NOT EXISTS wides    UInt64 DEFAULT 0,
    ADD COLUMN IF NOT EXISTS no_balls UInt64 DEFAULT 0;

-- Step 2: Recreate the MV to include the new columns
DROP VIEW IF EXISTS lms.player_bowling_mv;

CREATE MATERIALIZED VIEW lms.player_bowling_mv TO lms.player_bowling_phase AS
SELECT bowler_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat + extras_wide + extras_no_ball) AS runs_conceded,
       sum(is_legal_ball)                               AS legal_balls,
       sum(is_wicket)                                   AS wickets,
       sum(is_dot_ball)                                 AS dots,
       countIf(extras_wide > 0)                         AS wides,
       countIf(extras_no_ball > 0)                      AS no_balls
FROM lms.ball_events
GROUP BY bowler_id, season_id, league_id, division_id, venue_id, over_phase;

-- Step 3: Backfill historical data with correct wides/no_balls counts
TRUNCATE TABLE lms.player_bowling_phase;

INSERT INTO lms.player_bowling_phase
SELECT bowler_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat + extras_wide + extras_no_ball) AS runs_conceded,
       sum(is_legal_ball)                               AS legal_balls,
       sum(is_wicket)                                   AS wickets,
       sum(is_dot_ball)                                 AS dots,
       countIf(extras_wide > 0)                         AS wides,
       countIf(extras_no_ball > 0)                      AS no_balls
FROM lms.ball_events
GROUP BY bowler_id, season_id, league_id, division_id, venue_id, over_phase;

-- Verify: spot-check a bowler (replace 3 with a known bowler_id)
-- SELECT over_phase, runs_conceded, legal_balls, wickets, dots, wides, no_balls
-- FROM lms.player_bowling_phase
-- WHERE bowler_id = 3
-- ORDER BY over_phase;
