-- By Mani 15 Oct 2026
-- Step 1 of the merged-player-id reload: empty copies of the three per-fixture tables.
-- The worker in reload mode (LMS.Migration.Worker.exe reload) writes into these while the app
-- keeps reading the live tables. Same engine, partition key and order key as the originals.
-- Safe to re-run: the tables are only created when missing. To start the reload from zero,
-- drop the three _reload tables first.

CREATE TABLE IF NOT EXISTS LMSClickHouseDB.ball_events_reload        AS LMSClickHouseDB.ball_events;
CREATE TABLE IF NOT EXISTS LMSClickHouseDB.partnerships_reload       AS LMSClickHouseDB.partnerships;
CREATE TABLE IF NOT EXISTS LMSClickHouseDB.player_match_stats_reload AS LMSClickHouseDB.player_match_stats;

-- Check: all three exist and are empty
SELECT name, total_rows
FROM system.tables
WHERE database = 'LMSClickHouseDB' AND name LIKE '%_reload'
ORDER BY name;
