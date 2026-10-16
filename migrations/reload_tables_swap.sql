-- By Mani 15 Oct 2026
-- Step 3 of the merged-player-id reload, run ONLY after "LMS.Migration.Worker.exe reload" finished
-- with 0 failed. Run the statements one block at a time and look at each check.

-- 1. Before the swap: the reload must have at least as many fixtures as the live table
SELECT 'live' AS which, uniq(fixture_id) AS fixtures, count() AS balls FROM LMSClickHouseDB.ball_events
UNION ALL
SELECT 'reload', uniq(fixture_id), count() FROM LMSClickHouseDB.ball_events_reload;

-- 2. Swap. Each EXCHANGE is atomic; after it the live name holds the reloaded data and
--    the _reload name holds the old data (kept until step 5 in case we need to go back).
EXCHANGE TABLES LMSClickHouseDB.ball_events        AND LMSClickHouseDB.ball_events_reload;
EXCHANGE TABLES LMSClickHouseDB.partnerships       AND LMSClickHouseDB.partnerships_reload;
EXCHANGE TABLES LMSClickHouseDB.player_match_stats AND LMSClickHouseDB.player_match_stats_reload;

-- 3. The materialized views (h2h_stats_mv, player_batting_mv, player_bowling_mv, team_phase_mv,
--    league_avg_mv) still point at the name ball_events, so new inserts keep feeding them.
--    Their target tables still hold the OLD ids though - rebuild them now:
--        run rebuild_mvs.sql

-- 4. After rebuild_mvs.sql: no merged-away id should be left in the live tables.
--    (Paste a few OldUserId values from SQL Server dbo.Rep_MergedUserMap, e.g. the test ones.)
SELECT count() AS balls_with_old_ids
FROM LMSClickHouseDB.ball_events
WHERE striker_id IN (408924, 408981) OR bowler_id IN (408924, 408981);

-- 5. Then pick up fixtures saved while the reload was running, and refresh Abser's pairs table:
--        LMS.Migration.Worker.exe catchup
--        LMS.Migration.Worker.exe export-opposition pairs
--
-- 6. When everything checks out, drop the old copies (they now sit under the _reload names):
-- DROP TABLE LMSClickHouseDB.ball_events_reload;
-- DROP TABLE LMSClickHouseDB.partnerships_reload;
-- DROP TABLE LMSClickHouseDB.player_match_stats_reload;
