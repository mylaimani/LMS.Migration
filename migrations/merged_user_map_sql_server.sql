-- By Mani 15 Oct 2026
-- Merged player ids: one lookup row per old (merged-away) user id -> the id that survives today.
-- Run on the database that holds dbo.MergedUsersLog (same database as FixtureState, LMS_SQL_CONN).
--
--   1. dbo.Rep_MergedUserMap          OldUserId -> FinalUserId, following chains (A->B, B->C gives A->C)
--   2. dbo.BUILD_Rep_MergedUserMap    rebuilds the table from dbo.MergedUsersLog (safe to re-run any time)
--   3. check queries at the bottom    row count, longest chain, and "no FinalUserId is itself an old id"
--
-- Rules used:
--   * When an old id was merged more than once (3 cases), the row with the latest MergeDate wins.
--   * A chain stops after 10 hops or when the next hop would return to the starting id (loop guard).
--   * Rows where OldUserId = NewUserId are ignored.
-- ============================================================================================

IF OBJECT_ID('dbo.Rep_MergedUserMap', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Rep_MergedUserMap
    (
        OldUserId      INT          NOT NULL,
        FinalUserId    INT          NOT NULL,
        ChainLength    TINYINT      NOT NULL,   -- 1 = merged once, 2 = A->B->C, ...
        LastMergeDate  DATETIME     NOT NULL,   -- latest MergeDate on the chain
        BuiltAt        DATETIME2(0) NOT NULL CONSTRAINT DF_Rep_MergedUserMap_BuiltAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Rep_MergedUserMap PRIMARY KEY CLUSTERED (OldUserId)
    );
    CREATE INDEX IX_Rep_MergedUserMap_FinalUserId ON dbo.Rep_MergedUserMap (FinalUserId);
END;
GO

CREATE OR ALTER PROCEDURE dbo.BUILD_Rep_MergedUserMap
AS
BEGIN
    SET NOCOUNT ON;

    -- Step 1: one direct merge per old id (latest MergeDate wins when merged more than once), indexed
    SELECT OldUserId, NewUserId, MergeDate
    INTO #DirectMerge
    FROM (
        SELECT OldUserId, NewUserId, MergeDate,
               ROW_NUMBER() OVER (PARTITION BY OldUserId ORDER BY MergeDate DESC, Id DESC) AS RowNumber
        FROM dbo.MergedUsersLog
        WHERE OldUserId <> NewUserId
    ) latest
    WHERE RowNumber = 1;
    CREATE UNIQUE CLUSTERED INDEX IX_DirectMerge_OldUserId ON #DirectMerge (OldUserId);

    -- Step 2: start every chain with its direct merge
    SELECT OldUserId, NewUserId AS FinalUserId, CAST(1 AS INT) AS ChainLength, MergeDate AS LastMergeDate
    INTO #NewMap
    FROM #DirectMerge;
    CREATE UNIQUE CLUSTERED INDEX IX_NewMap_OldUserId ON #NewMap (OldUserId);

    -- Step 3: follow the chains one hop at a time (A->B, B->C gives A->C). Stops when no row moves,
    --         after 10 hops, or when the next hop would come back to the starting id (loop guard).
    DECLARE @hop INT = 1;
    WHILE @hop < 10
    BEGIN
        UPDATE map
        SET FinalUserId   = nextMerge.NewUserId,
            ChainLength   = map.ChainLength + 1,
            LastMergeDate = CASE WHEN nextMerge.MergeDate > map.LastMergeDate THEN nextMerge.MergeDate ELSE map.LastMergeDate END
        FROM #NewMap map
        JOIN #DirectMerge nextMerge ON nextMerge.OldUserId = map.FinalUserId
        WHERE nextMerge.NewUserId <> map.OldUserId;

        IF @@ROWCOUNT = 0 BREAK;
        SET @hop = @hop + 1;
    END;

    -- Step 4: swap the table contents in one transaction
    BEGIN TRANSACTION;
        DELETE FROM dbo.Rep_MergedUserMap;
        INSERT INTO dbo.Rep_MergedUserMap (OldUserId, FinalUserId, ChainLength, LastMergeDate)
        SELECT OldUserId, FinalUserId, ChainLength, LastMergeDate
        FROM #NewMap;
    COMMIT TRANSACTION;

    DROP TABLE #NewMap;
    DROP TABLE #DirectMerge;
END;
GO

-- Build it now
EXEC dbo.BUILD_Rep_MergedUserMap;
GO

-- ============================================================================================
-- Checks (expected on staging, Oct 2026: about 27,096 rows, longest chain 4, last query returns 0 rows)
-- ============================================================================================
SELECT COUNT(*) AS MapRows, MAX(ChainLength) AS LongestChain, MAX(LastMergeDate) AS LatestMerge
FROM dbo.Rep_MergedUserMap;

SELECT ChainLength, COUNT(*) AS Rows
FROM dbo.Rep_MergedUserMap
GROUP BY ChainLength
ORDER BY ChainLength;

-- Must be empty: a final id that is itself still an old id means a chain was not followed to the end
SELECT map.OldUserId, map.FinalUserId
FROM dbo.Rep_MergedUserMap map
JOIN dbo.Rep_MergedUserMap deeper ON deeper.OldUserId = map.FinalUserId;
