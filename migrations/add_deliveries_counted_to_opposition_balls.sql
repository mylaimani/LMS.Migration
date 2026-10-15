-- By Mani 15 Oct 2026
-- Rep_PlayerOppositionBalls: add DeliveriesCounted = every ball the bowler sent down to this batsman,
-- including the re-bowled first wide / no-ball (BallsCounted keeps the LMS over rule).
-- Agreed with Hasib 15 Oct 2026: uplift uses DeliveriesCounted for both batting and bowling.
-- Run on the rating database (LMS_RATING_SQL_CONN), then re-run:  LMS.Migration.Worker.exe export-opposition pairs
-- ============================================================================================

IF COL_LENGTH('dbo.Rep_PlayerOppositionBalls', 'DeliveriesCounted') IS NULL
BEGIN
    ALTER TABLE dbo.Rep_PlayerOppositionBalls
        ADD DeliveriesCounted SMALLINT NOT NULL CONSTRAINT DF_Rep_PlayerOppositionBalls_DeliveriesCounted DEFAULT 0;
END;
GO

-- Check after the export: DeliveriesCounted is never below BallsCounted, and no row is left at the default 0
SELECT COUNT(*) AS RowsBelowBallsCounted
FROM dbo.Rep_PlayerOppositionBalls
WHERE DeliveriesCounted < BallsCounted;

SELECT COUNT(*) AS RowsStillZero
FROM dbo.Rep_PlayerOppositionBalls
WHERE DeliveriesCounted = 0;
