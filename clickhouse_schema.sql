-- =====================================================================
-- LMS Analytics — ClickHouse Schema v4.1
-- 7 tables + 5 materialized views
-- Source of truth: FixtureState JSON (SQL Server) -> C# worker -> ClickHouse
-- Run: clickhouse-client --user lms_admin --password *** --multiquery < clickhouse_schema.sql
-- =====================================================================

CREATE DATABASE IF NOT EXISTS lms;

-- =====================================================================
-- Table 1 — lms.ball_events  (one row per ball bowled)
-- Ordered by (bowler_id, striker_id, fixture_id) so H2H queries hit
-- co-located data.
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.ball_events
(
    fixture_id        UInt32,
    innings_number    UInt8,
    over_number       UInt8,
    ball_sequence     UInt8,
    ball_timestamp    DateTime,
    bowler_id         UInt32,
    striker_id        UInt32,
    non_striker_id    UInt32,
    runs_off_bat      UInt8,
    extras_wide       UInt8,
    extras_no_ball    UInt8,
    extras_legbye     UInt8,
    extras_bye        UInt8,
    is_wicket         UInt8,
    wicket_type       LowCardinality(String),

    -- computed at insert time, no extra storage cost
    is_legal_ball     UInt8 MATERIALIZED if(extras_wide = 0 AND extras_no_ball = 0, 1, 0),
    is_dot_ball       UInt8 MATERIALIZED if(runs_off_bat = 0 AND extras_wide = 0 AND extras_no_ball = 0, 1, 0),
    is_boundary       UInt8 MATERIALIZED if(runs_off_bat = 4, 1, 0),
    is_six            UInt8 MATERIALIZED if(runs_off_bat = 6, 1, 0),
    over_phase        LowCardinality(String) MATERIALIZED multiIf(over_number <= 6, 'Powerplay', over_number <= 15, 'Middle', 'Death'),

    batting_position  UInt8,
    batting_team_id   UInt32,
    bowling_team_id   UInt32,
    score_at_ball     UInt16,
    wickets_at_ball   UInt8,
    game_date         Date,

    -- SQL Server lookup: FixtureLeagueDivisionRoundSeason + Venue
    -- (no Competition table — LMS uses League + Division + Season)
    league_id         UInt32,
    division_id       UInt32,
    season_id         UInt32,
    season_name       LowCardinality(String),
    venue_id          UInt32,
    region_id         UInt32,
    country_id        UInt8,

    -- LMS-specific
    home_runs         UInt8,
    steal             UInt8,
    double_play       UInt8,
    balls_per_over    UInt8 DEFAULT 5,
    pitch_condition   UInt8,
    fielder_id        UInt32,
    keeper_id         UInt32,

    -- LMS Pulse (Simon's requirement)
    pulse_after_pct   Float32,            -- Pulse % recalculated after this ball
    pulse_change_pct  Float32             -- vs previous ball = Win Probability Added
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(game_date)
ORDER BY (bowler_id, striker_id, fixture_id, innings_number, over_number, ball_sequence);

-- =====================================================================
-- Table 2 — lms.player_match_stats  (Points Engine — one row per player
-- per completed match). Drives ratings, rankings, legends, leaderboards.
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.player_match_stats
(
    fixture_id                  UInt32,
    player_id                   UInt32,
    team_id                     UInt32,
    match_result                LowCardinality(String),   -- Win/Loss/Tie/NoResult
    is_no_result                UInt8,                    -- 1 = excluded from points & ratings
    match_rpball                Float32,

    -- batting
    batting_balls_faced         UInt16,
    batting_runs_scored         UInt16,
    batting_is_not_out          UInt8,
    batting_player_rpball       Nullable(Float32),
    batting_efficiency_ratio    Nullable(Float32),
    batting_base_points         Nullable(Float32),
    batting_after_win           Nullable(Float32),
    batting_not_out_bonus       Nullable(Float32),
    batting_raw_points          Nullable(Float32),        -- audit, before 300 cap
    batting_match_points        Nullable(Float32),        -- min(300, raw); NULL if balls_faced = 0
    batting_rating_impact       Nullable(Float32),        -- match_points x opp_strength x league_weighting

    -- bowling
    bowling_balls_bowled        UInt16,
    bowling_runs_conceded       UInt16,
    bowling_wickets             UInt8,
    bowling_rpball              Nullable(Float32),
    bowling_improvement         Nullable(Float32),        -- NULL if runs_conceded = 0
    bowling_base_economy_pts    Nullable(Float32),
    bowling_scaling_factor      Nullable(Float32),
    bowling_weighted_economy_pts Nullable(Float32),
    bowling_wicket_points       Nullable(Float32),
    bowling_base_points         Nullable(Float32),
    bowling_raw_points          Nullable(Float32),        -- audit, before 300 cap
    bowling_match_points        Nullable(Float32),        -- min(300, raw); NULL if balls_bowled = 0
    bowling_rating_impact       Nullable(Float32),

    -- fielding (raw only — never opposition-adjusted)
    fielding_catches            UInt8,
    fielding_run_outs           UInt8,
    fielding_stumpings          UInt8,
    fielding_double_plays       UInt8,
    fielding_points             Float32,                  -- c*10 + ro*10 + st*25 + dp*15

    -- combined (NULL treated as 0 in sums)
    all_rounder_match_points    Float32,                  -- bat + bowl + field
    opposition_strength         Float32,                  -- locked pre-match
    league_strength_weighting   Float32 DEFAULT 1.0,
    participation_points        UInt16,                   -- 150 if confirmed email, else 0
    legends_points              Float32,                  -- RAW bat + bowl + field + participation

    -- context (SQL Server lookup)
    league_id                   UInt32,
    division_id                 UInt32,
    season_id                   UInt32,
    season_name                 LowCardinality(String),
    venue_id                    UInt32,
    region_id                   UInt32,
    country_id                  UInt8,
    game_date                   Date
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(game_date)
ORDER BY (player_id, game_date, fixture_id);

-- =====================================================================
-- Table 3 — lms.partnerships  (one row per batting partnership)
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.partnerships
(
    fixture_id          UInt32,
    innings_number      UInt8,
    partnership_number  UInt8,
    batter1_id          UInt32,
    batter2_id          UInt32,
    batting_team_id     UInt32,
    bowling_team_id     UInt32,
    runs_together       UInt16,
    balls_together      UInt16,
    run_rate            Float32,          -- runs / (balls/5)
    fours_together      UInt8,
    sixes_together      UInt8,
    start_over          UInt8,
    end_over            UInt8,
    over_phase          LowCardinality(String),
    game_date           Date,
    league_id           UInt32,
    division_id         UInt32,
    season_id           UInt32,
    season_name         LowCardinality(String),
    venue_id            UInt32,
    region_id           UInt32
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(game_date)
ORDER BY (batter1_id, batter2_id, fixture_id);

-- =====================================================================
-- Table 4 — lms.clips  (one row per video clip, linked to exact ball)
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.clips
(
    clip_id         UInt64,
    fixture_id      UInt32,
    innings_number  UInt8,
    over_number     UInt8,               -- ClipGenerated IOver is 0-indexed: IOver=0 -> Over 1
    ball_sequence   UInt8,
    ball_timestamp  DateTime,
    clip_url        String,
    clip_type       LowCardinality(String),   -- six / four / wicket (future: partnership/spell/innings)
    bowler_id       UInt32,
    striker_id      UInt32,
    non_striker_id  UInt32,
    keeper_id       UInt32,
    fielder_id      UInt32,
    wicket_type     LowCardinality(String),
    is_six          UInt8,
    duration_secs   UInt16,
    league_id       UInt32,
    season_id       UInt32,
    game_date       Date
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(game_date)
ORDER BY (striker_id, bowler_id, fixture_id, clip_id);

-- =====================================================================
-- Table 5 — lms.player_ratings  (current rating state, one row per player)
-- ReplacingMergeTree keeps the latest row per player_id.
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.player_ratings
(
    player_id                   UInt32,
    batting_games_used          UInt16,
    batting_total_adjusted_pts  Float32,
    batting_apg                 Float32,
    batting_z_score             Float32,
    batting_base_rating         Float32,
    batting_rating              Float32,   -- min(elite_compressed, experience_cap), 2dp
    batting_max_cap             Float32,
    batting_prev_rating         Float32,
    batting_rating_change       Float32,
    bowling_games_used          UInt16,
    bowling_total_adjusted_pts  Float32,
    bowling_apg                 Float32,
    bowling_z_score             Float32,
    bowling_base_rating         Float32,
    bowling_rating              Float32,
    bowling_max_cap             Float32,
    bowling_prev_rating         Float32,
    bowling_rating_change       Float32,
    all_rounder_rating          Float32,   -- 50/50 model
    games_played                UInt16,    -- ALL qualifying matches (any discipline) — reliability input
    unique_teams_faced          UInt16,
    reliability_pct             Float32,   -- min(95, games_played*0.75 + unique_teams*0.75)
    population_mean_batting     Float32,
    population_stddev_batting   Float32,
    population_mean_bowling     Float32,
    population_stddev_bowling   Float32,
    last_updated_fixture_id     UInt32,
    last_updated                DateTime
)
ENGINE = ReplacingMergeTree(last_updated)
ORDER BY player_id;

-- =====================================================================
-- Table 6 — lms.league_rankings  (live league leaderboard)
-- One row per player + team + league + division + season.
-- RAW points only — no opposition strength, no divisor, no runner.
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.league_rankings
(
    player_id                UInt32,
    team_id                  UInt32,
    league_id                UInt32,    -- no Competition table: League + Division + Season
    division_id              UInt32,
    season_id                UInt32,
    season_name              LowCardinality(String),
    batting_total_points     Float32,
    bowling_total_points     Float32,
    fielding_total_points    Float32,
    all_rounder_total_points Float32,
    batting_rank             UInt32,    -- RANK() within league_id + division_id + season_id
    bowling_rank             UInt32,
    all_rounder_rank         UInt32,
    games_played             UInt16,
    last_fixture_id          UInt32,
    last_updated             DateTime
)
ENGINE = ReplacingMergeTree(last_updated)
ORDER BY (league_id, division_id, season_id, player_id, team_id);

-- =====================================================================
-- Table 7 — lms.global_rankings_snapshot  (permanent monthly prestige
-- rankings — never overwritten)
-- =====================================================================
CREATE TABLE IF NOT EXISTS lms.global_rankings_snapshot
(
    snapshot_id           String,        -- e.g. GLOBAL_RANKINGS_2026_05
    snapshot_date         Date,
    ranking_scope         LowCardinality(String),  -- League/Regional/National/Global
    ranking_category      LowCardinality(String),  -- Batting/Bowling/AllRounder
    player_id             UInt32,        -- USER ID, survives account merges
    rank                  UInt32,
    previous_rank         UInt32,
    movement_delta        Int32,
    adjusted_points       Float32,       -- 3yr window, max 50 matches; includes
                                         -- matches with no contribution (count toward divisor)
    divisor               UInt8,         -- 35 if matches<=35 else actual (36-50)
    ranking_score         Float32,       -- adjusted/divisor, 5dp; ties -> lower USER ID ranks higher
    matches_in_window     UInt16,
    fielding_points_total Float32,       -- AllRounder snapshots only
    region                LowCardinality(String),
    country               LowCardinality(String),
    formula_version       UInt8,         -- 1 = old, 2 = new match points formula
    rollback_available    UInt8
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(snapshot_date)
ORDER BY (snapshot_id, ranking_scope, ranking_category, rank);

-- =====================================================================
-- Materialized Views (5) — pre-aggregated counters.
-- Averages / rates (strike rate, economy, dot %) are computed at query
-- time from the stored sums.
-- =====================================================================

-- MV 1: H2H batter vs bowler career stats
CREATE TABLE IF NOT EXISTS lms.h2h_stats
(
    bowler_id   UInt32,
    striker_id  UInt32,
    legal_balls UInt64,
    runs        UInt64,
    wickets     UInt64,
    sixes       UInt64,
    boundaries  UInt64,
    dots        UInt64
)
ENGINE = SummingMergeTree
ORDER BY (bowler_id, striker_id);

CREATE MATERIALIZED VIEW IF NOT EXISTS lms.h2h_stats_mv TO lms.h2h_stats AS
SELECT bowler_id, striker_id,
       sum(is_legal_ball)              AS legal_balls,
       sum(runs_off_bat)               AS runs,
       sum(is_wicket)                  AS wickets,
       sum(is_six)                     AS sixes,
       sum(is_boundary)                AS boundaries,
       sum(is_dot_ball)                AS dots
FROM lms.ball_events
GROUP BY bowler_id, striker_id;

-- MV 2: player batting by season/league/venue/phase
CREATE TABLE IF NOT EXISTS lms.player_batting_phase
(
    striker_id  UInt32,
    season_id   UInt32,
    league_id   UInt32,
    division_id UInt32,
    venue_id    UInt32,
    over_phase  LowCardinality(String),
    runs        UInt64,
    legal_balls UInt64,
    dismissals  UInt64,
    boundaries  UInt64,
    sixes       UInt64,
    dots        UInt64
)
ENGINE = SummingMergeTree
ORDER BY (striker_id, season_id, league_id, division_id, venue_id, over_phase);

CREATE MATERIALIZED VIEW IF NOT EXISTS lms.player_batting_mv TO lms.player_batting_phase AS
SELECT striker_id, season_id, league_id, division_id, venue_id, over_phase,
       sum(runs_off_bat)  AS runs,
       sum(is_legal_ball) AS legal_balls,
       sum(is_wicket)     AS dismissals,
       sum(is_boundary)   AS boundaries,
       sum(is_six)        AS sixes,
       sum(is_dot_ball)   AS dots
FROM lms.ball_events
GROUP BY striker_id, season_id, league_id, division_id, venue_id, over_phase;

-- MV 3: bowler economy/wickets/dot% by season/league/venue/phase
CREATE TABLE IF NOT EXISTS lms.player_bowling_phase
(
    bowler_id     UInt32,
    season_id     UInt32,
    league_id     UInt32,
    division_id   UInt32,
    venue_id      UInt32,
    over_phase    LowCardinality(String),
    runs_conceded UInt64,
    legal_balls   UInt64,
    wickets       UInt64,
    dots          UInt64,
    wides         UInt64,
    no_balls      UInt64,
    sixes         UInt64,
    fours         UInt64,
    threes        UInt64,
    twos          UInt64,
    ones          UInt64
)
ENGINE = SummingMergeTree
ORDER BY (bowler_id, season_id, league_id, division_id, venue_id, over_phase);

CREATE MATERIALIZED VIEW IF NOT EXISTS lms.player_bowling_mv TO lms.player_bowling_phase AS
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

-- MV 4: team scoring patterns by phase
CREATE TABLE IF NOT EXISTS lms.team_phase
(
    batting_team_id UInt32,
    season_id       UInt32,
    league_id       UInt32,
    venue_id        UInt32,
    over_phase      LowCardinality(String),
    runs            UInt64,
    legal_balls     UInt64,
    wickets         UInt64,
    boundaries      UInt64
)
ENGINE = SummingMergeTree
ORDER BY (batting_team_id, season_id, league_id, venue_id, over_phase);

CREATE MATERIALIZED VIEW IF NOT EXISTS lms.team_phase_mv TO lms.team_phase AS
SELECT batting_team_id, season_id, league_id, venue_id, over_phase,
       sum(runs_off_bat)  AS runs,
       sum(is_legal_ball) AS legal_balls,
       sum(is_wicket)     AS wickets,
       sum(is_boundary)   AS boundaries
FROM lms.ball_events
GROUP BY batting_team_id, season_id, league_id, venue_id, over_phase;

-- MV 5: league / regional / national averages
CREATE TABLE IF NOT EXISTS lms.league_avg
(
    league_id   UInt32,
    division_id UInt32,
    region_id   UInt32,
    country_id  UInt8,
    season_id   UInt32,
    over_phase  LowCardinality(String),
    runs        UInt64,
    legal_balls UInt64,
    wickets     UInt64,
    boundaries  UInt64,
    sixes       UInt64
)
ENGINE = SummingMergeTree
ORDER BY (league_id, division_id, region_id, country_id, season_id, over_phase);

CREATE MATERIALIZED VIEW IF NOT EXISTS lms.league_avg_mv TO lms.league_avg AS
SELECT league_id, division_id, region_id, country_id, season_id, over_phase,
       sum(runs_off_bat)  AS runs,
       sum(is_legal_ball) AS legal_balls,
       sum(is_wicket)     AS wickets,
       sum(is_boundary)   AS boundaries,
       sum(is_six)        AS sixes
FROM lms.ball_events
GROUP BY league_id, division_id, region_id, country_id, season_id, over_phase;
