-- ============================================================================
-- 2026-09-28_CuttingEntryDestination.sql
--
-- CORRECTION #1 -- Cutting Entry -> destination selection.
--
-- Every Cutting Entry now says where the cuttings go:
--   MainOffice     harvest recorded in the Mother Plant's Area pool AND, in the same
--                  transaction, the existing Cutting delivery to Main Office is created
--                  (InternalTransfers, StockType 'Cutting', PendingConfirmation). It becomes
--                  Main Office stock only when Main Office confirms receipt (existing flow).
--   PotProduction  harvest stays in the Mother Plant's own Area pool (used by that Area's
--                  Pot Production / Cutting Tray Sowing); no transfer.
--
-- WHAT THIS SCRIPT CHANGES (additive; no data is written or changed):
--   dbo.CuttingProductions   + DestinationType NVARCHAR(30) NULL
--                            + DestinationAreaId INT NULL     (Main Office Area, or the own Area)
--                            + SubmissionToken UNIQUEIDENTIFIER NULL  (one-time form key)
--        CK_CuttingProductions_Destination      allowed combinations of the two columns
--        FK_CuttingProductions_DestinationArea  -> dbo.Area
--        UX_CuttingProductions_SubmissionToken  filtered UNIQUE (a repeated submit saves nothing)
--        TR_CuttingProductions_Rules            existing rules kept VERBATIM + 2 new rules:
--            every NEW row must carry a destination; a MainOffice destination must be an
--            active Main Office Area.
--   dbo.InternalTransfers    + SourceCuttingProductionId INT NULL  (which Cutting Entry created it)
--        FK_InternalTransfers_CuttingProduction -> dbo.CuttingProductions
--        UX_InternalTransfers_CuttingProduction filtered UNIQUE (one entry -> at most one delivery)
--
-- Existing rows: all new columns are NULL (destination "unknown / recorded before destinations
-- existed"). The 6 historical Cutting Entries and every transfer stay exactly as they are; the
-- trigger only fires for new inserts (rows are immutable), so nothing is re-validated.
--
-- NOT changed: CuttingStock, CuttingStockTransactions, MotherPlants, Pot Production tables,
-- permissions, roles, any other trigger or constraint.
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction (was a ROLLBACK dry run first;
-- switched to COMMIT with explicit approval). Idempotent (existence checks / CREATE OR ALTER).
-- Deploy together with the matching application build (the app reads/writes the new columns).
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50350, '2026-09-28_CuttingEntryDestination.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingEntryDestination;

BEGIN TRY

    IF OBJECT_ID(N'dbo.CuttingProductions', N'U') IS NULL OR OBJECT_ID(N'dbo.InternalTransfers', N'U') IS NULL OR OBJECT_ID(N'dbo.Area', N'U') IS NULL
        THROW 50351, 'Expected tables dbo.CuttingProductions, dbo.InternalTransfers and dbo.Area not found -- aborting.', 1;

    ----------------------------------------------------------------------
    -- 1. New nullable columns (existing rows keep NULL)
    ----------------------------------------------------------------------
    IF COL_LENGTH(N'dbo.CuttingProductions', N'DestinationType') IS NULL
        ALTER TABLE dbo.CuttingProductions ADD DestinationType NVARCHAR(30) NULL;
    IF COL_LENGTH(N'dbo.CuttingProductions', N'DestinationAreaId') IS NULL
        ALTER TABLE dbo.CuttingProductions ADD DestinationAreaId INT NULL;
    IF COL_LENGTH(N'dbo.CuttingProductions', N'SubmissionToken') IS NULL
        ALTER TABLE dbo.CuttingProductions ADD SubmissionToken UNIQUEIDENTIFIER NULL;
    IF COL_LENGTH(N'dbo.InternalTransfers', N'SourceCuttingProductionId') IS NULL
        ALTER TABLE dbo.InternalTransfers ADD SourceCuttingProductionId INT NULL;

    ----------------------------------------------------------------------
    -- 2. Constraints and indexes (dynamic SQL: the columns above must exist first)
    ----------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CuttingProductions_Destination' AND parent_object_id = OBJECT_ID(N'dbo.CuttingProductions'))
        EXEC (N'
ALTER TABLE dbo.CuttingProductions WITH CHECK ADD CONSTRAINT CK_CuttingProductions_Destination CHECK (
       (DestinationType IS NULL AND DestinationAreaId IS NULL)
    OR (DestinationType = N''MainOffice''    AND DestinationAreaId IS NOT NULL AND DestinationAreaId <> AreaId)
    OR (DestinationType = N''PotProduction'' AND DestinationAreaId = AreaId))');

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CuttingProductions_DestinationArea')
        EXEC (N'ALTER TABLE dbo.CuttingProductions WITH CHECK ADD CONSTRAINT FK_CuttingProductions_DestinationArea FOREIGN KEY (DestinationAreaId) REFERENCES dbo.Area (Id)');

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_InternalTransfers_CuttingProduction')
        EXEC (N'ALTER TABLE dbo.InternalTransfers WITH CHECK ADD CONSTRAINT FK_InternalTransfers_CuttingProduction FOREIGN KEY (SourceCuttingProductionId) REFERENCES dbo.CuttingProductions (Id)');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CuttingProductions_SubmissionToken' AND object_id = OBJECT_ID(N'dbo.CuttingProductions'))
        EXEC (N'CREATE UNIQUE NONCLUSTERED INDEX UX_CuttingProductions_SubmissionToken ON dbo.CuttingProductions (SubmissionToken) WHERE SubmissionToken IS NOT NULL');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_InternalTransfers_CuttingProduction' AND object_id = OBJECT_ID(N'dbo.InternalTransfers'))
        EXEC (N'CREATE UNIQUE NONCLUSTERED INDEX UX_InternalTransfers_CuttingProduction ON dbo.InternalTransfers (SourceCuttingProductionId) WHERE SourceCuttingProductionId IS NOT NULL');

    ----------------------------------------------------------------------
    -- 3. Trigger: the two existing rules VERBATIM + destination rules for new rows
    ----------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_CuttingProductions_Rules
ON dbo.CuttingProductions
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- A production record is a ledger fact: nothing about it changes later.
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50206, ''A cutting production record cannot be changed after it is saved.'', 1;
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''Mother Plant Supervisor''))
        THROW 50207, ''The supervisor of a cutting production must be an active Mother Plant Supervisor of its Area.'', 1;
    -- Every NEW cutting entry says where the cuttings go (rows recorded before destinations
    -- existed keep NULL: rows are immutable, so this only ever sees new rows).
    IF EXISTS (SELECT 1 FROM inserted WHERE DestinationType IS NULL)
        THROW 50352, ''A cutting entry must say where the cuttings go: Main Office or Use for Pot Production.'', 1;
    -- A Main Office destination is an ACTIVE Area of type MainOffice.
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE i.DestinationType = N''MainOffice''
                 AND NOT EXISTS (SELECT 1 FROM dbo.Area a
                                 WHERE a.Id = i.DestinationAreaId AND a.AreaType = N''MainOffice'' AND a.IsActive = 1))
        THROW 50353, ''The destination of a Main Office cutting entry must be an active Main Office Area.'', 1;
END');

    ----------------------------------------------------------------------
    -- VERIFICATION (before the ROLLBACK, so it shows what WOULD happen)
    ----------------------------------------------------------------------
    PRINT '=== New columns (expect 3 on CuttingProductions + 1 on InternalTransfers, all nullable) ===';
    SELECT OBJECT_NAME(c.object_id) AS tbl, c.name, t.name AS type, c.max_length, c.is_nullable AS nullable
    FROM sys.columns c INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE (c.object_id = OBJECT_ID(N'dbo.CuttingProductions') AND c.name IN (N'DestinationType', N'DestinationAreaId', N'SubmissionToken'))
       OR (c.object_id = OBJECT_ID(N'dbo.InternalTransfers') AND c.name = N'SourceCuttingProductionId')
    ORDER BY 1, c.column_id;

    PRINT '=== New constraints / indexes (expect 5 rows, all trusted/enabled) ===';
    SELECT N'CHECK' AS kind, name, is_disabled, is_not_trusted FROM sys.check_constraints WHERE name = N'CK_CuttingProductions_Destination'
    UNION ALL SELECT N'FK', name, is_disabled, is_not_trusted FROM sys.foreign_keys WHERE name IN (N'FK_CuttingProductions_DestinationArea', N'FK_InternalTransfers_CuttingProduction')
    UNION ALL SELECT N'INDEX', name, is_disabled, 0 FROM sys.indexes WHERE name IN (N'UX_CuttingProductions_SubmissionToken', N'UX_InternalTransfers_CuttingProduction');

    PRINT '=== Trigger enabled, existing rules retained + new rules present (expect 1 / 1 / 1 / 1) ===';
    SELECT (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_CuttingProductions_Rules' AND is_disabled = 0) AS TriggerEnabled,
           (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_CuttingProductions_Rules' AND OBJECT_DEFINITION(object_id) LIKE N'%50206%') AS ImmutabilityRuleKept,
           (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_CuttingProductions_Rules' AND OBJECT_DEFINITION(object_id) LIKE N'%50207%') AS SupervisorRuleKept,
           (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_CuttingProductions_Rules' AND OBJECT_DEFINITION(object_id) LIKE N'%50352%50353%') AS DestinationRulesAdded;

    PRINT '=== Historical rows untouched: every existing entry has NULL destination (expect Total = NoDestination (every existing entry), TransferLinks = 0) ===';
    -- (dynamic SQL: these columns do not exist yet when this batch is compiled)
    EXEC (N'
SELECT COUNT(*) AS Total, SUM(CASE WHEN DestinationType IS NULL AND DestinationAreaId IS NULL AND SubmissionToken IS NULL THEN 1 ELSE 0 END) AS NoDestination,
       (SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceCuttingProductionId IS NOT NULL) AS TransferLinks
FROM dbo.CuttingProductions');

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingEntryDestination;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-28: user reviewed the ROLLBACK dry run (23/23 real-data
-- scenarios; before/after snapshots differ only by the intended objects) and
-- explicitly approved applying it. Verified backup:
-- PlantsIMS2_Test_PreCuttingEntryDestinationCommit_20260928_165643.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION CuttingEntryDestination;
GO
