-- =====================================================================
-- Rebuild the 5 materialized-view target tables from ball_events.
-- Run ONCE after the full migration completes (fixes any double-counting
-- caused by crash-resume re-processing). Safe to re-run.
-- =====================================================================

TRUNCATE TABLE lms.h2h_stats;
INSERT INTO lms.h2h_stats
SELECT bowler_id, striker_id,
       sum(is_legal_ball), sum(runs_off_bat), sum(is_wicket),
       sum(is_six), sum(is_boundary), sum(is_dot_ball)
FROM lms.ball_events
GROUP BY bowler_id, striker_id;

TRUNCATE TABLE lms.player_batting_phase;
INSERT INTO lms.player_batting_phase
SELECT striker_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket),
       sum(is_boundary), sum(is_six), sum(is_dot_ball)
FROM lms.ball_events
GROUP BY striker_id, season_id, league_id, division_id, venue_id, over_phase;

TRUNCATE TABLE lms.player_bowling_phase;
INSERT INTO lms.player_bowling_phase
SELECT bowler_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat + extras_wide + extras_no_ball), sum(is_legal_ball),
       sum(is_wicket), sum(is_dot_ball)
FROM lms.ball_events
GROUP BY bowler_id, season_id, league_id, division_id, venue_id, over_phase;

TRUNCATE TABLE lms.team_phase;
INSERT INTO lms.team_phase
SELECT batting_team_id, season_id, league_id, venue_id, over_phase,
       sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket), sum(is_boundary)
FROM lms.ball_events
GROUP BY batting_team_id, season_id, league_id, venue_id, over_phase;

TRUNCATE TABLE lms.league_avg;
INSERT INTO lms.league_avg
SELECT league_id, division_id, region_id, country_id, season_id, over_phase,
       sum(runs_off_bat), sum(is_legal_ball), sum(is_wicket),
       sum(is_boundary), sum(is_six)
FROM lms.ball_events
GROUP BY league_id, division_id, region_id, country_id, season_id, over_phase;
