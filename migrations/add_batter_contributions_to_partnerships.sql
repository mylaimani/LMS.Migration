-- Adds each batter's own share of a partnership to LMSClickHouseDB.partnerships.
-- Run once on GCP staging + localhost ClickHouse (LMSClickHouseDB, the database LMST20.Api reads),
-- then rerun the partnerships migration
-- (TruncatePartnershipsAsync + re-parse) so runs_together includes extras,
-- balls_together follows the LMS ball-counting rule, and the new columns are filled.
-- Fixture 395792 partnership 2 (Wayne Greve / Ryan Kearney) is the reference case:
-- expected runs_together = 33, balls_together = 25, batter shares 19/13 and 14/12.

ALTER TABLE LMSClickHouseDB.partnerships
    ADD COLUMN IF NOT EXISTS batter1_runs  UInt16 DEFAULT 0 AFTER sixes_together,
    ADD COLUMN IF NOT EXISTS batter1_balls UInt16 DEFAULT 0 AFTER batter1_runs,
    ADD COLUMN IF NOT EXISTS batter2_runs  UInt16 DEFAULT 0 AFTER batter1_balls,
    ADD COLUMN IF NOT EXISTS batter2_balls UInt16 DEFAULT 0 AFTER batter2_runs;
