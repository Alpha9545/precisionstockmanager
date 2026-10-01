-- ============================================================================
-- 2026-09-30_UserLoginHistory.sql
--
-- Step 3B: Employee Login Tracking. Adds dbo.UserLoginHistory, a new,
-- ISOLATED, insert-only event table -- Option B from the 2026-09-30
-- analysis, chosen over adding a LastLoginAt column directly to
-- dbo.IMSUsers (Option A) because:
--   - dbo.IMSUsers is referenced by 56 foreign keys across nearly every
--     operational table in this app; a new table keeps this concern fully
--     isolated instead of growing the one table everything else depends on.
--   - A single mutable column can only ever hold the MOST RECENT login,
--     destroying same-day multiple-login history and any cross-day trend;
--     a row-per-login table answers "first login today" (MIN(LoginAt) per
--     day) correctly and keeps full history for free.
--
-- WHAT THIS DOES: creates exactly one new table, no changes to any
-- existing table, column, trigger, constraint, or index anywhere.
--     dbo.UserLoginHistory
--         Id        INT IDENTITY(1,1) PK
--         UserId    INT NOT NULL, FK -> dbo.IMSUsers(Id)
--         LoginAt   DATETIME2 NOT NULL   (UTC, written by
--                    UserLoginHistoryRepository.RecordLoginAsync via
--                    SYSUTCDATETIME() -- never client-supplied)
--     Index IX_UserLoginHistory_UserId_LoginAt (UserId, LoginAt) -- the
--     exact shape UserLoginHistoryRepository.GetTodaysActivityAsync's
--     OUTER APPLY / MIN(LoginAt) per-user-per-day query needs.
--
-- DELIBERATELY NOT INCLUDED (see the analysis): no Status/Success column
-- (only successful logins are ever inserted -- a row's existence IS the
-- fact), no IP address, no user agent -- none of the approved report's 5
-- questions need them.
--
-- Existing data: dbo.IMSUsers and every other table are completely
-- untouched -- this script only creates a new, empty table. No existing
-- row anywhere is read or modified.
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction; idempotent
-- (IF NOT EXISTS). Ends in ROLLBACK -- this is a DRY RUN. Take a backup,
-- review the verification output below, get explicit approval, THEN
-- change the final line to COMMIT TRANSACTION UserLoginHistory and re-run.
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50340, '2026-09-30_UserLoginHistory.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION UserLoginHistory;

BEGIN TRY

    ----------------------------------------------------------------------
    -- 1. The new table, only if missing (idempotent)
    ----------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'UserLoginHistory' AND schema_id = SCHEMA_ID('dbo'))
    BEGIN
        CREATE TABLE dbo.UserLoginHistory
        (
            Id      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserLoginHistory PRIMARY KEY,
            UserId  INT NOT NULL CONSTRAINT FK_UserLoginHistory_User REFERENCES dbo.IMSUsers(Id),
            LoginAt DATETIME2 NOT NULL
        );
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserLoginHistory_UserId_LoginAt'
                                               AND object_id = OBJECT_ID('dbo.UserLoginHistory'))
        CREATE INDEX IX_UserLoginHistory_UserId_LoginAt ON dbo.UserLoginHistory (UserId, LoginAt);

    ----------------------------------------------------------------------
    -- VERIFICATION (before the ROLLBACK, so it shows what WOULD happen)
    ----------------------------------------------------------------------
    PRINT '=== Table exists with the expected 3 columns ===';
    SELECT name, is_nullable, TYPE_NAME(system_type_id) AS DataType
    FROM sys.columns WHERE object_id = OBJECT_ID('dbo.UserLoginHistory') ORDER BY column_id;

    PRINT '=== FK to dbo.IMSUsers present (expect 1) ===';
    SELECT COUNT(*) AS FkPresent FROM sys.foreign_keys WHERE name = N'FK_UserLoginHistory_User';

    PRINT '=== Index present (expect 1) ===';
    SELECT COUNT(*) AS IndexPresent FROM sys.indexes WHERE name = N'IX_UserLoginHistory_UserId_LoginAt';

    -- Dynamic SQL: a direct SELECT referencing the table created earlier in
    -- this SAME batch is a lower-risk pattern than the column-level case
    -- that bit 2026-09-30_SeedSowingSurvivorshipTracking.sql (that was
    -- about a COLUMN added by ALTER TABLE mid-batch, not a whole TABLE
    -- created by CREATE TABLE mid-batch), but this wrap is cheap insurance
    -- against the same class of same-batch name-resolution issue, applied
    -- as a precaution per the 2026-09-30 migration review.
    PRINT '=== Table is empty (expect 0 -- brand new) ===';
    EXEC(N'SELECT COUNT(*) AS TableRowCount FROM dbo.UserLoginHistory;');

    PRINT '=== dbo.IMSUsers row count unchanged by this script (informational only) ===';
    SELECT COUNT(*) AS IMSUsersRows FROM dbo.IMSUsers;

    PRINT '=== dbo.IMSUsers column list unchanged (expect the same 7 columns as before: Id, Username, Password, Name, Email, DesignationID, IsActive) ===';
    SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.IMSUsers') ORDER BY column_id;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION UserLoginHistory;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-30: user reviewed the dry run (table created with
-- exactly the 3 expected columns, FK present, index present, table empty,
-- dbo.IMSUsers row count/columns unchanged) and explicitly approved
-- applying it. Verified backup:
-- PlantsIMS2_Test_PreUserLoginHistoryCommit_20260930_141116.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION UserLoginHistory;
GO
