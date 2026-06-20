-- ── LMS Powerplay Phase Fix ──────────────────────────────────────────────────
-- Adds total_overs to ball_events and rewrites the over_phase MATERIALIZED
-- column to use actual LMS powerplay rules (P1/P2 vary by game length).
--
-- Sources:
--   Rule 12.1 (LMS Rules of Play): "first 4 overs and last 2 overs" (20-over game)
--   OutdoorCricketSummaryComponent scorer code: P1/P2 thresholds per game length
--
-- LMS powerplay boundaries (over_number is 1-indexed):
--
--   total_overs  | P1 (Powerplay) overs | P2 (Death) overs
--   -------------|----------------------|------------------
--   8            | 1                    | 8           (last 1)
--   9–11         | 1–2                  | N           (last 1)
--   12–14        | 1–3                  | N           (last 1)
--   15–18        | 1–3                  | (N-1)–N     (last 2)
--   19–20        | 1–4                  | (N-1)–N     (last 2)
--
-- Formula:
--   P1_end   = if(total_overs <= 8, 1, if(total_overs <= 11, 2, if(total_overs <= 18, 3, 4)))
--   P2_start = if(total_overs <= 14, total_overs, total_overs - 1)
--
-- Verification (20-over game, Rule 12.1):
--   P1_end = 4  → overs 1-4  ✓ ("first 4 overs")
--   P2_start = 19 → overs 19-20 ✓ ("last 2 overs")
--
-- Run on BOTH localhost and 34.185.193.227 before rerunning the migration.
-- ─────────────────────────────────────────────────────────────────────────────

-- 1. Add total_overs column (default 20 for any already-migrated rows)
ALTER TABLE lms.ball_events
    ADD COLUMN IF NOT EXISTS total_overs UInt8 DEFAULT 20
    AFTER balls_per_over;

-- 2. Rewrite over_phase MATERIALIZED expression using LMS powerplay boundaries.
--    Existing rows keep their old value until the migration is rerun
--    (MATERIALIZED columns only recompute at insert time).
ALTER TABLE lms.ball_events
    MODIFY COLUMN over_phase LowCardinality(String) MATERIALIZED
        multiIf(
            -- Powerplay (P1): first overs of innings
            over_number <= if(total_overs <= 8,  1,
                           if(total_overs <= 11, 2,
                           if(total_overs <= 18, 3, 4))),          'Powerplay',
            -- Death (P2): last overs of innings
            -- 8-14 overs: last 1 over | 15-20 overs: last 2 overs
            over_number >= if(total_overs <= 14,
                              toInt16(total_overs),
                              toInt16(total_overs) - 1),           'Death',
            -- Middle: everything in between
                                                                   'Middle'
        );

-- After running this script on both servers:
--   1. Compile and run the full migration:
--        $env:LMS_SQL_CONN = "..."
--        $env:LMS_CH_CONN  = "..."
--        .\LMS.Migration.Worker.exe    (no args = full rerun)
--   2. Rebuild MVs on both servers: run rebuild_mvs.sql
