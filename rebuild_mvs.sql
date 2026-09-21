-- =====================================================================
-- Rebuild the 5 materialized-view target tables from ball_events.
-- Run ONCE after the full migration completes (fixes any double-counting
-- caused by crash-resume re-processing). Safe to re-run.
-- =====================================================================

TRUNCATE TABLE LMSClickHouseDB.h2h_stats;
INSERT INTO LMSClickHouseDB.h2h_stats
SELECT bowler_id, striker_id,
       sum(is_legal_ball), sum(runs_off_bat), sum(is_wicket),
       sum(is_six), sum(is_boundary), sum(is_dot_ball)
FROM LMSClickHouseDB.ball_events
GROUP BY bowler_id, striker_id;

TRUNCATE TABLE LMSClickHouseDB.player_batting_phase;
INSERT INTO LMSClickHouseDB.player_batting_phase
SELECT striker_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket),
       sum(is_boundary), sum(is_six), sum(is_dot_ball)
FROM LMSClickHouseDB.ball_events
GROUP BY striker_id, season_id, league_id, division_id, venue_id, over_phase;

TRUNCATE TABLE LMSClickHouseDB.player_bowling_phase;
INSERT INTO LMSClickHouseDB.player_bowling_phase
SELECT bowler_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat + extras_wide + extras_no_ball),
       sum(is_legal_ball), sum(is_wicket), sum(is_dot_ball),
       countIf(extras_wide > 0), countIf(extras_no_ball > 0),
       countIf(runs_off_bat = 6 AND is_legal_ball = 1),
       countIf(runs_off_bat = 4 AND is_legal_ball = 1),
       countIf(runs_off_bat = 3 AND is_legal_ball = 1),
       countIf(runs_off_bat = 2 AND is_legal_ball = 1),
       countIf(runs_off_bat = 1 AND is_legal_ball = 1)
FROM LMSClickHouseDB.ball_events
GROUP BY bowler_id, season_id, league_id, division_id, venue_id, over_phase;

TRUNCATE TABLE LMSClickHouseDB.team_phase;
INSERT INTO LMSClickHouseDB.team_phase
SELECT batting_team_id, season_id, league_id, venue_id, over_phase,
       sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket), sum(is_boundary)
FROM LMSClickHouseDB.ball_events
GROUP BY batting_team_id, season_id, league_id, venue_id, over_phase;

TRUNCATE TABLE LMSClickHouseDB.league_avg;
INSERT INTO LMSClickHouseDB.league_avg
SELECT league_id, division_id, region_id, country_id, season_id, over_phase,
       sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket),
       sum(is_boundary), sum(is_six)
FROM LMSClickHouseDB.ball_events
GROUP BY league_id, division_id, region_id, country_id, season_id, over_phase;
