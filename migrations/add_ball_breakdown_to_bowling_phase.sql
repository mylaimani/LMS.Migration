-- =====================================================================
-- Migration: Add sixes/fours/threes/twos/ones to lms.player_bowling_phase
-- Run once on GCP staging + localhost ClickHouse
--
-- Simon's requirement: break all balls down as
--   Wickets | Dots | 6s | 4s | 3s | 2s | 1s | No Balls | Wides
--
-- All counts use is_legal_ball = 1 so they align with legal_balls total.
-- Wides/no-balls are counted separately (not in the 1s–6s breakdown).
-- =====================================================================

-- Step 1: Add new columns
ALTER TABLE lms.player_bowling_phase
    ADD COLUMN IF NOT EXISTS sixes   UInt64 DEFAULT 0,
    ADD COLUMN IF NOT EXISTS fours   UInt64 DEFAULT 0,
    ADD COLUMN IF NOT EXISTS threes  UInt64 DEFAULT 0,
    ADD COLUMN IF NOT EXISTS twos    UInt64 DEFAULT 0,
    ADD COLUMN IF NOT EXISTS ones    UInt64 DEFAULT 0;

-- Step 2: Recreate MV with all columns
DROP VIEW IF EXISTS lms.player_bowling_mv;

CREATE MATERIALIZED VIEW lms.player_bowling_mv TO lms.player_bowling_phase AS
SELECT bowler_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat + extras_wide + extras_no_ball)  AS runs_conceded,
       sum(is_legal_ball)                                AS legal_balls,
       sum(is_wicket)                                    AS wickets,
       sum(is_dot_ball)                                  AS dots,
       countIf(extras_wide > 0)                          AS wides,
       countIf(extras_no_ball > 0)                       AS no_balls,
       countIf(runs_off_bat = 6 AND is_legal_ball = 1)   AS sixes,
       countIf(runs_off_bat = 4 AND is_legal_ball = 1)   AS fours,
       countIf(runs_off_bat = 3 AND is_legal_ball = 1)   AS threes,
       countIf(runs_off_bat = 2 AND is_legal_ball = 1)   AS twos,
       countIf(runs_off_bat = 1 AND is_legal_ball = 1)   AS ones
FROM lms.ball_events
GROUP BY bowler_id, season_id, league_id, division_id, venue_id, over_phase;

-- Step 3: Backfill
TRUNCATE TABLE lms.player_bowling_phase;

INSERT INTO lms.player_bowling_phase
SELECT bowler_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat + extras_wide + extras_no_ball)  AS runs_conceded,
       sum(is_legal_ball)                                AS legal_balls,
       sum(is_wicket)                                    AS wickets,
       sum(is_dot_ball)                                  AS dots,
       countIf(extras_wide > 0)                          AS wides,
       countIf(extras_no_ball > 0)                       AS no_balls,
       countIf(runs_off_bat = 6 AND is_legal_ball = 1)   AS sixes,
       countIf(runs_off_bat = 4 AND is_legal_ball = 1)   AS fours,
       countIf(runs_off_bat = 3 AND is_legal_ball = 1)   AS threes,
       countIf(runs_off_bat = 2 AND is_legal_ball = 1)   AS twos,
       countIf(runs_off_bat = 1 AND is_legal_ball = 1)   AS ones
FROM lms.ball_events
GROUP BY bowler_id, season_id, league_id, division_id, venue_id, over_phase;

-- Verify
-- SELECT over_phase, wickets, dots, sixes, fours, threes, twos, ones, wides, no_balls
-- FROM lms.player_bowling_phase
-- WHERE bowler_id = 3
-- ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3);
