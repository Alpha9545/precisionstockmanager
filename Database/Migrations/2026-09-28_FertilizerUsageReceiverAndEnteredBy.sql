-- ============================================================================
-- 2026-09-28_FertilizerUsageReceiverAndEnteredBy.sql   (Correction #8)
--
-- Fertilizer issue ("Fertilizer Usage"): the person who RECEIVED the fertilizer is chosen
-- from the active users (not typed), and the person who ENTERED the issue is recorded.
--
-- TODAY: dbo.FertilizerUsage(UsageId, StockId, UsedQuantity, IssueDate, ReceivedBy NVARCHAR(300) NOT NULL,
-- Remarks, CreatedAt). ReceivedBy is free text; who entered the issue is not stored at all.
--
-- WHAT CHANGES (dbo.FertilizerUsage only):
--   1. + ReceivedById INT NULL  -> FK_FertilizerUsage_ReceivedBy  -> dbo.IMSUsers(Id)
--   2. + EnteredById  INT NULL  -> FK_FertilizerUsage_EnteredBy   -> dbo.IMSUsers(Id)
--
-- The existing IMSUsers table is reused (no second user table). Both columns are NULLABLE and have no
-- default, so every existing row keeps NULL in them: historical issues are NOT linked to a user by guessing a
-- name -- their typed ReceivedBy text stays exactly as it is, is still shown, and "Entered By" of an old issue
-- is "Not recorded". The existing ReceivedBy column is NOT changed or dropped: for a NEW issue the application
-- stores the chosen user's name in it as well (so anything that already reads ReceivedBy keeps working).
--
-- NOT changed: no existing row (no UPDATE/DELETE), no other table, view, index, trigger, constraint, permission,
-- role or Area assignment. FertilizerStock and its balances are untouched. No index is added (the table is
-- small and the filters are date/equality lookups); the two foreign keys are the only new constraints.
-- There are no triggers on the fertilizer tables, so no trigger is affected.
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction; idempotent (each step checks first); ends in
-- ROLLBACK (dry run) -- switch the final ROLLBACK to COMMIT only after explicit approval. Deploy together with
-- the matching application build (the new build reads/writes these columns).
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50330, '2026-09-28_FertilizerUsageReceiverAndEnteredBy.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION FertilizerReceiver;

BEGIN TRY

    IF OBJECT_ID(N'dbo.FertilizerUsage', N'U') IS NULL OR OBJECT_ID(N'dbo.IMSUsers', N'U') IS NULL
        THROW 50331, 'dbo.FertilizerUsage / dbo.IMSUsers not found -- aborting rather than guess.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.IMSUsers') AND type = 'PK')
        THROW 50332, 'dbo.IMSUsers has no primary key -- aborting.', 1;

    IF COL_LENGTH(N'dbo.FertilizerUsage', N'ReceivedById') IS NULL
        ALTER TABLE dbo.FertilizerUsage ADD ReceivedById INT NULL;
    IF COL_LENGTH(N'dbo.FertilizerUsage', N'EnteredById') IS NULL
        ALTER TABLE dbo.FertilizerUsage ADD EnteredById INT NULL;

    -- the columns are new in this batch, so the foreign keys are created dynamically
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_FertilizerUsage_ReceivedBy')
        EXEC (N'ALTER TABLE dbo.FertilizerUsage ADD CONSTRAINT FK_FertilizerUsage_ReceivedBy FOREIGN KEY (ReceivedById) REFERENCES dbo.IMSUsers (Id);');
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_FertilizerUsage_EnteredBy')
        EXEC (N'ALTER TABLE dbo.FertilizerUsage ADD CONSTRAINT FK_FertilizerUsage_EnteredBy FOREIGN KEY (EnteredById) REFERENCES dbo.IMSUsers (Id);');

    ----------------------------------------------------------------------
    -- Verification (inside the transaction)
    ----------------------------------------------------------------------
    DECLARE @Rows INT, @Linked INT;
    EXEC sp_executesql N'SELECT @Rows = COUNT(*), @Linked = SUM(CASE WHEN ReceivedById IS NOT NULL OR EnteredById IS NOT NULL THEN 1 ELSE 0 END) FROM dbo.FertilizerUsage',
         N'@Rows INT OUTPUT, @Linked INT OUTPUT', @Rows OUTPUT, @Linked OUTPUT;
    IF ISNULL(@Linked, 0) <> 0
        THROW 50333, 'Existing FertilizerUsage rows must keep NULL ReceivedById/EnteredById -- aborting.', 1;

    SELECT N'FertilizerUsage rows (unchanged)' AS Item, @Rows AS Value
    UNION ALL SELECT N'rows with a receiver/entered-by link', ISNULL(@Linked, 0)
    UNION ALL SELECT N'FK_FertilizerUsage_ReceivedBy present', COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_FertilizerUsage_ReceivedBy'
    UNION ALL SELECT N'FK_FertilizerUsage_EnteredBy present', COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_FertilizerUsage_EnteredBy';

    COMMIT TRANSACTION;   -- APPROVED 2026-09-28 (was ROLLBACK dry run)
    PRINT 'Migration committed.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @Msg NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(N'Migration failed and was rolled back: %s', 16, 1, @Msg);
END CATCH;
