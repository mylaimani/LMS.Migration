-- By Mani
-- Ball-by-ball opposition strength (Category 1 of Adjusted Match Points) - SQL Server side.
-- Run on the database that holds Abser's Rep_PlayerRatingHistory (staging for now).
--
--   1. dbo.Rep_PlayerOppositionBalls     static facts from ClickHouse: balls per batsman v bowler per fixture
--                                        (option B - Abser's rebuild loop calculates the uplift from these)
--   2. dbo.Rep_PlayerOppositionStrength  finished results per player per fixture per role
--                                        (option A, and the ongoing/new-match output for the app)
--   3. dbo.Rep_PlayerOppositionDetail    per-opponent rows behind each summary row (for the app's breakdown screen)
--   4. dbo.API_GetBallByBallOpposition   the calculation in T-SQL for one fixture, same rules as the C# code
--   5. dbo.API_GetOppositionBreakdown    read procedure for the app endpoint (summary + per-opponent rows)
--                                        (Wayne 05/10/2026: reliability threshold 50%, positive uplift x2.5)
-- ============================================================================================

IF OBJECT_ID('dbo.Rep_PlayerOppositionBalls', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Rep_PlayerOppositionBalls
    (
        FixtureId       INT          NOT NULL,
        GameDate        DATE         NOT NULL,
        BatsmanId       INT          NOT NULL,
        BowlerId        INT          NOT NULL,
        BattingTeamId   INT          NOT NULL,
        BowlingTeamId   INT          NOT NULL,
        BallsCounted    SMALLINT     NOT NULL,   -- LMS over rule applied (first wide/no-ball re-bowled, later ones count)
        DeliveriesCounted SMALLINT   NOT NULL CONSTRAINT DF_Rep_PlayerOppositionBalls_DeliveriesCounted DEFAULT 0,   -- every delivery (15 Oct 2026)
        LoadedAt        DATETIME2(0) NOT NULL CONSTRAINT DF_Rep_PlayerOppositionBalls_LoadedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Rep_PlayerOppositionBalls PRIMARY KEY CLUSTERED (FixtureId, BatsmanId, BowlerId)
    );
    CREATE INDEX IX_Rep_PlayerOppositionBalls_GameDate ON dbo.Rep_PlayerOppositionBalls (GameDate);
END;
GO

IF OBJECT_ID('dbo.Rep_PlayerOppositionStrength', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Rep_PlayerOppositionStrength
    (
        FixtureId                   INT           NOT NULL,
        UserId                      INT           NOT NULL,
        Role                        VARCHAR(10)   NOT NULL,   -- 'Batting' or 'Bowling'
        GameDate                    DATE          NOT NULL,
        TeamId                      INT           NOT NULL,
        BallsCounted                SMALLINT      NOT NULL,
        OpponentsCounted            TINYINT       NOT NULL,
        OpponentsWithoutRating      TINYINT       NOT NULL,
        EffectiveOppositionRating   DECIMAL(5,2)  NOT NULL,   -- e.g. 6.13   (display / worksheet)
        UpliftPercent               DECIMAL(7,2)  NOT NULL,   -- e.g. 28.32  (display / worksheet), can be negative
        EffectiveOppositionRatingRaw DECIMAL(18,8) NOT NULL,  -- full precision, for the Adjusted Match Points calculation
        UpliftPercentRaw            DECIMAL(18,8) NOT NULL,   -- full precision, for the Adjusted Match Points calculation
        RatingSource                VARCHAR(20)   NOT NULL,   -- 'BeforeFixture', 'Replay', ...
        FormulaVersion              SMALLINT      NOT NULL,
        CalculatedAt                DATETIME2(0)  NOT NULL,
        CONSTRAINT PK_Rep_PlayerOppositionStrength PRIMARY KEY CLUSTERED (FixtureId, UserId, Role),
        CONSTRAINT CK_Rep_PlayerOppositionStrength_Role CHECK (Role IN ('Batting', 'Bowling'))
    );
    CREATE INDEX IX_Rep_PlayerOppositionStrength_User ON dbo.Rep_PlayerOppositionStrength (UserId, GameDate);
END;
GO

-- Per-opponent rows behind each summary row (the app's "breakdown" screen). Written by the same procedure
-- call, so the breakdown always matches the uplift that was actually used.
IF OBJECT_ID('dbo.Rep_PlayerOppositionDetail', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Rep_PlayerOppositionDetail
    (
        FixtureId               INT           NOT NULL,
        UserId                  INT           NOT NULL,
        Role                    VARCHAR(10)   NOT NULL,   -- 'Batting' or 'Bowling'
        OpponentUserId          INT           NOT NULL,   -- bowler faced (Batting) or batsman bowled to (Bowling)
        OpponentRating          DECIMAL(5,2)  NULL,       -- opponent's rating before the fixture (NULL = no history yet)
        OpponentReliability     DECIMAL(5,2)  NULL,       -- 0-100
        AdjustedReliability     DECIMAL(6,4)  NOT NULL,   -- MIN(Reliability / 50, 1); 0 when no rating
        EffectiveRating         DECIMAL(18,8) NOT NULL,   -- 5 + (Rating - 5) * AdjustedReliability; 5 when no rating
        BallsCounted            SMALLINT      NOT NULL,
        WeightedContribution    DECIMAL(18,8) NOT NULL,   -- EffectiveRating * BallsCounted
        CONSTRAINT PK_Rep_PlayerOppositionDetail PRIMARY KEY CLUSTERED (FixtureId, UserId, Role, OpponentUserId)
    );
END;
GO

-- ============================================================================================
-- Calculation for one fixture. Returns one row per player per role and (by default) stores them in
-- Rep_PlayerOppositionStrength. This is the ONLY thing Abser's rating procedure needs to call.
--
-- Rules (Wayne, 30/09 and 05/10/2026):
--   AdjustedReliability = MIN(Reliability / 50, 1)
--   EffectiveRating     = 5 + (Rating - 5) * AdjustedReliability
--   Opposition rating   = SUM(EffectiveRating * Balls) / SUM(Balls)        (unrounded)
--   Uplift %            = (Opposition - 5) * 10 * 2.5   when Opposition >= 5
--                       = (Opposition - 5) * 10         when Opposition <  5
--   Raw (full precision) values are stored for the points calculation; 2 dp copies for display.
--   A batsman's opponents are the bowlers he faced -> their BowlRatingLevel.
--   A bowler's opponents are the batsmen he bowled to -> their BatRatingLevel.
--   An opponent with no rating row before the fixture counts as neutral 5.0.
--
-- Rating source: the "previous row before this fixture" from Rep_PlayerRatingHistory, using Abser's
-- rule (earlier MatchDate, or same MatchDate with a lower FixtureId). In the historical rebuild loop,
-- Abser replaces the two OUTER APPLY blocks with his in-progress ratings for that point in the loop.
-- ============================================================================================
CREATE OR ALTER PROCEDURE dbo.API_GetBallByBallOpposition
(
    @FixtureId     INT,
    @StoreResults  BIT = 1      -- 1 = also write to Rep_PlayerOppositionStrength and Rep_PlayerOppositionDetail (replacing this fixture's rows)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NeutralRating            DECIMAL(18,8) = 5.0;
    DECLARE @FullReliabilityThreshold DECIMAL(18,8) = 50.0;
    DECLARE @UpliftPerRatingPoint     DECIMAL(18,8) = 10.0;
    DECLARE @PositiveUpliftMultiplier DECIMAL(18,8) = 2.5;
    DECLARE @FormulaVersion           SMALLINT      = 2;

    DECLARE @FixtureDate DATETIME =
        (SELECT TOP 1 MatchDate FROM dbo.Rep_PlayerRatingHistory WHERE FixtureId = @FixtureId AND MatchDate IS NOT NULL);
    IF @FixtureDate IS NULL
        SET @FixtureDate = (SELECT [DateTime] FROM dbo.Fixture WHERE Id = @FixtureId);

    ------------------------------------------------------------------
    -- Step 1: one row per (player, role, opponent) with the opponent's pre-fixture rating.
    --   Batting rows: batsman v each bowler he faced   -> bowler's BOWLING rating
    --   Bowling rows: bowler v each batsman bowled to  -> batsman's BATTING rating
    ------------------------------------------------------------------
    ;WITH BattingPairs AS
    (
        SELECT
            'Batting'                             AS Role,
            pairs.BatsmanId                       AS UserId,
            pairs.BowlerId                        AS OpponentUserId,
            pairs.BattingTeamId                   AS TeamId,
            pairs.GameDate,
            pairs.BallsCounted,
            opponent.BowlRatingLevel              AS OpponentRating,
            opponent.Reliability                  AS OpponentReliability
        FROM dbo.Rep_PlayerOppositionBalls pairs
        OUTER APPLY
        (
            SELECT TOP 1 history.BowlRatingLevel, history.Reliability
            FROM dbo.Rep_PlayerRatingHistory history
            WHERE history.UserId = pairs.BowlerId
              AND (ISNULL(history.MatchDate, '19000101') < @FixtureDate
                   OR (ISNULL(history.MatchDate, '19000101') = @FixtureDate AND history.FixtureId < @FixtureId))
            ORDER BY ISNULL(history.MatchDate, '19000101') DESC, history.FixtureId DESC, history.PlayerRatingHistoryId DESC
        ) opponent
        WHERE pairs.FixtureId = @FixtureId
    ),
    BowlingPairs AS
    (
        SELECT
            'Bowling'                             AS Role,
            pairs.BowlerId                        AS UserId,
            pairs.BatsmanId                       AS OpponentUserId,
            pairs.BowlingTeamId                   AS TeamId,
            pairs.GameDate,
            pairs.BallsCounted,
            opponent.BatRatingLevel               AS OpponentRating,
            opponent.Reliability                  AS OpponentReliability
        FROM dbo.Rep_PlayerOppositionBalls pairs
        OUTER APPLY
        (
            SELECT TOP 1 history.BatRatingLevel, history.Reliability
            FROM dbo.Rep_PlayerRatingHistory history
            WHERE history.UserId = pairs.BatsmanId
              AND (ISNULL(history.MatchDate, '19000101') < @FixtureDate
                   OR (ISNULL(history.MatchDate, '19000101') = @FixtureDate AND history.FixtureId < @FixtureId))
            ORDER BY ISNULL(history.MatchDate, '19000101') DESC, history.FixtureId DESC, history.PlayerRatingHistoryId DESC
        ) opponent
        WHERE pairs.FixtureId = @FixtureId
    ),
    AllPairs AS
    (
        SELECT * FROM BattingPairs
        UNION ALL
        SELECT * FROM BowlingPairs
    )
    SELECT
        Role, UserId, OpponentUserId, TeamId, GameDate, BallsCounted,
        OpponentRating, OpponentReliability,
        CASE
            WHEN OpponentRating IS NULL THEN CAST(0 AS DECIMAL(18,8))
            WHEN ISNULL(OpponentReliability, 0) / @FullReliabilityThreshold > 1 THEN CAST(1 AS DECIMAL(18,8))
            WHEN ISNULL(OpponentReliability, 0) / @FullReliabilityThreshold < 0 THEN CAST(0 AS DECIMAL(18,8))
            ELSE CAST(ISNULL(OpponentReliability, 0) / @FullReliabilityThreshold AS DECIMAL(18,8))
        END AS AdjustedReliability
    INTO #Pairs
    FROM AllPairs;

    -- Effective rating per opponent (neutral 5.0 when the opponent has no history yet)
    SELECT
        *,
        CASE WHEN OpponentRating IS NULL THEN @NeutralRating
             ELSE @NeutralRating + (CAST(OpponentRating AS DECIMAL(18,8)) - @NeutralRating) * AdjustedReliability
        END AS EffectiveRating
    INTO #Detail
    FROM #Pairs;

    ------------------------------------------------------------------
    -- Step 2: balls-weighted average per player/role, then uplift (unrounded internally)
    ------------------------------------------------------------------
    SELECT
        @FixtureId                                           AS FixtureId,
        UserId,
        Role,
        GameDate,
        TeamId,
        BallsCounted,
        OpponentsCounted,
        OpponentsWithoutRating,
        CAST(ROUND(OppositionRatingUnrounded, 2) AS DECIMAL(5,2)) AS EffectiveOppositionRating,
        CAST(ROUND(UpliftUnrounded, 2) AS DECIMAL(7,2))          AS UpliftPercent,
        CAST(OppositionRatingUnrounded AS DECIMAL(18,8))         AS EffectiveOppositionRatingRaw,
        CAST(UpliftUnrounded AS DECIMAL(18,8))                   AS UpliftPercentRaw,
        'BeforeFixture'                                      AS RatingSource,
        @FormulaVersion                                      AS FormulaVersion,
        SYSUTCDATETIME()                                     AS CalculatedAt
    INTO #Results
    FROM
    (
        SELECT *,
               CASE WHEN OppositionRatingUnrounded >= @NeutralRating
                    THEN (OppositionRatingUnrounded - @NeutralRating) * @UpliftPerRatingPoint * @PositiveUpliftMultiplier
                    ELSE (OppositionRatingUnrounded - @NeutralRating) * @UpliftPerRatingPoint
               END AS UpliftUnrounded
        FROM
        (
            SELECT
                Role, UserId, TeamId, MIN(GameDate) AS GameDate,
                SUM(BallsCounted)                                        AS BallsCounted,
                COUNT(*)                                                 AS OpponentsCounted,
                SUM(CASE WHEN OpponentRating IS NULL THEN 1 ELSE 0 END)  AS OpponentsWithoutRating,
                SUM(EffectiveRating * BallsCounted) / SUM(CAST(BallsCounted AS DECIMAL(18,8))) AS OppositionRatingUnrounded
            FROM #Detail
            GROUP BY Role, UserId, TeamId
        ) PerPlayer
    ) PerPlayerWithUplift;

    ------------------------------------------------------------------
    -- Step 3: store (replace this fixture's rows in both tables)
    ------------------------------------------------------------------
    IF @StoreResults = 1
    BEGIN
        DELETE FROM dbo.Rep_PlayerOppositionDetail   WHERE FixtureId = @FixtureId;
        DELETE FROM dbo.Rep_PlayerOppositionStrength WHERE FixtureId = @FixtureId;

        INSERT INTO dbo.Rep_PlayerOppositionStrength
            (FixtureId, UserId, Role, GameDate, TeamId, BallsCounted, OpponentsCounted, OpponentsWithoutRating,
             EffectiveOppositionRating, UpliftPercent, EffectiveOppositionRatingRaw, UpliftPercentRaw,
             RatingSource, FormulaVersion, CalculatedAt)
        SELECT
             FixtureId, UserId, Role, GameDate, TeamId, BallsCounted, OpponentsCounted, OpponentsWithoutRating,
             EffectiveOppositionRating, UpliftPercent, EffectiveOppositionRatingRaw, UpliftPercentRaw,
             RatingSource, FormulaVersion, CalculatedAt
        FROM #Results;

        INSERT INTO dbo.Rep_PlayerOppositionDetail
            (FixtureId, UserId, Role, OpponentUserId, OpponentRating, OpponentReliability,
             AdjustedReliability, EffectiveRating, BallsCounted, WeightedContribution)
        SELECT
             @FixtureId, UserId, Role, OpponentUserId, OpponentRating, OpponentReliability,
             CAST(AdjustedReliability AS DECIMAL(6,4)), EffectiveRating, BallsCounted,
             EffectiveRating * BallsCounted
        FROM #Detail;
    END;

    SELECT * FROM #Results ORDER BY Role, UserId;
END;
GO

-- ============================================================================================
-- Read procedure for the app (LMST20 MobileApp API: GET /api/lms/OppositionStrength/GetBreakdown).
-- Returns TWO result sets for one player's batting or bowling performance in one fixture:
--   1. the summary row (0 or 1 rows)        from Rep_PlayerOppositionStrength
--   2. the per-opponent rows (0..n rows)    from Rep_PlayerOppositionDetail, most balls first
-- Names and profile images are resolved in the API layer (IPlayerDirectoryService), not here.
-- ============================================================================================
CREATE OR ALTER PROCEDURE dbo.API_GetOppositionBreakdown
(
    @FixtureId  INT,
    @UserId     INT,
    @Role       VARCHAR(10)      -- 'Batting' or 'Bowling'
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT FixtureId, UserId, Role, GameDate, TeamId,
           BallsCounted, OpponentsCounted, OpponentsWithoutRating,
           EffectiveOppositionRating, UpliftPercent,
           EffectiveOppositionRatingRaw, UpliftPercentRaw,
           RatingSource, FormulaVersion, CalculatedAt
    FROM dbo.Rep_PlayerOppositionStrength
    WHERE FixtureId = @FixtureId AND UserId = @UserId AND Role = @Role;

    SELECT OpponentUserId, OpponentRating, OpponentReliability,
           AdjustedReliability, EffectiveRating, BallsCounted, WeightedContribution
    FROM dbo.Rep_PlayerOppositionDetail
    WHERE FixtureId = @FixtureId AND UserId = @UserId AND Role = @Role
    ORDER BY BallsCounted DESC, OpponentUserId;
END;
GO
-- End of Mani
