-- ============================================================================
-- 2026-10-25_MaterialStockTransfers_Rollback.sql   *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- Undoes 2026-10-25_MaterialStockTransfers.sql, restoring every changed object exactly:
--   * TR_MaterialTransactions_Ledger -> the 2026-10-08_MaterialStockApprovals version, TR_MaterialStock_Rules -> the
--     2026-10-08_MaterialStock version (text copied byte-for-byte from those scripts; SHA-256 verified, 53441);
--   * CK_MaterialTransactions_Type / _Direction / _Sowing -> their previous definitions (verified, 53442);
--   * drops UX_MaterialTransactions_TransferLink, TR_MaterialTransfers_Rules and dbo.MaterialTransfers;
--   * removes the MaterialStock.Transfer grant of Sowing Supervisor if (and only if) the migration added it last, and
--     deletes the permission code only if the migration created it and no role holds it any more.
--
-- REFUSED (53440, nothing changed) once ANY transfer was recorded: a dbo.MaterialTransfers row, a TRANSFER_OUT /
--   TRANSFER_IN ledger row, or a material balance outside a Main Office Area. Transfers moved real stock; discarding
--   them would leave balances that no longer add up. Move the material back by transfer and keep the schema instead.
--
-- TARGET SAFEGUARDS and operator checklist: exactly as 2026-10-25_MaterialStockTransfers.sql (7 checks; DRYRUN first).
--   sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--             TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-25_MaterialStockTransfers_Rollback.sql
-- One transaction. Idempotent (a second run finds nothing to undo).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53431, '2026-10-25_MaterialStockTransfers_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53432, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53433, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53434, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53435, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53436, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53437, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MaterialTransfers1025Rollback;

BEGIN TRY
    PRINT '=== Server / machine / database: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    -- ---- data guard ----------------------------------------------------------------------------------------------------
    DECLARE @Transfers INT = 0, @TransferRows INT = 0, @OutsideBalances INT = 0;
    IF OBJECT_ID('dbo.MaterialTransfers', 'U') IS NOT NULL
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.MaterialTransfers WITH (UPDLOCK, HOLDLOCK)', N'@n INT OUTPUT', @n = @Transfers OUTPUT;
    SELECT @TransferRows = COUNT(*) FROM dbo.MaterialTransactions WITH (UPDLOCK, HOLDLOCK) WHERE TransactionType IN ('TRANSFER_OUT', 'TRANSFER_IN');
    SELECT @OutsideBalances = COUNT(*) FROM dbo.MaterialStock s WITH (UPDLOCK, HOLDLOCK) JOIN dbo.Area a ON a.Id = s.AreaId WHERE ISNULL(a.AreaType, N'') <> N'MainOffice';
    PRINT '=== Transfers ' + CAST(@Transfers AS NVARCHAR(20)) + ', transfer ledger rows ' + CAST(@TransferRows AS NVARCHAR(20))
        + ', balances outside the Main Office ' + CAST(@OutsideBalances AS NVARCHAR(20)) + ' ===';
    IF @Transfers + @TransferRows + @OutsideBalances > 0
        THROW 53440, 'Material transfers have been recorded -- the rollback would leave balances that no longer add up. Nothing changed.', 1;

    -- ---- 1. permission -------------------------------------------------------------------------------------------------
    DECLARE @Code NVARCHAR(100) = N'MaterialStock.Transfer';
    DECLARE @Script NVARCHAR(200) = N'2026-10-25_MaterialStockTransfers.sql';
    DECLARE @RollbackScript NVARCHAR(200) = N'2026-10-25_MaterialStockTransfers_Rollback.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Transfer Cocopeat / Pitmos between Polyhouses (from own Areas) [created by 2026-10-25_MaterialStockTransfers.sql]';
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code);

    IF @Perm IS NULL
        PRINT '=== Permission MaterialStock.Transfer does not exist -- no grant or code to remove ===';
    ELSE
    BEGIN
        DECLARE @PermText NVARCHAR(20) = CAST(@Perm AS NVARCHAR(20));
        DECLARE @Roles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, RoleId INT NULL);
        INSERT INTO @Roles (RoleName) VALUES (N'Sowing Supervisor');
        UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;

        DECLARE @RoleName NVARCHAR(100), @Role INT, @LastChange NVARCHAR(10), @LastScript NVARCHAR(200);
        DECLARE revoke_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName, RoleId FROM @Roles WHERE RoleId IS NOT NULL ORDER BY RoleName;
        OPEN revoke_cursor;
        FETCH NEXT FROM revoke_cursor INTO @RoleName, @Role;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
            BEGIN
                SET @LastChange = NULL; SET @LastScript = NULL;
                SELECT TOP 1 @LastChange = Change, @LastScript = Script
                FROM dbo.RolePermissionChangeLog WHERE RoleId = @Role AND PermissionCode = @Code ORDER BY Id DESC;
                IF ISNULL(@LastChange, N'') <> N'Added' OR ISNULL(@LastScript, N'') <> @Script
                    THROW 53438, 'The MaterialStock.Transfer grant was not (last) added by 2026-10-25_MaterialStockTransfers.sql -- nothing changed.', 1;
                INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
                VALUES (@Role, @RoleName, @Code, N'Removed', @RollbackScript, N'Rollback of 2026-10-25_MaterialStockTransfers.sql [PermissionId=' + @PermText + N']');
                DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;
                PRINT '=== MaterialStock.Transfer removed from ' + @RoleName + ' ===';
            END
            ELSE
                PRINT '=== ' + @RoleName + ': not granted -- nothing to remove ===';
            FETCH NEXT FROM revoke_cursor INTO @RoleName, @Role;
        END
        CLOSE revoke_cursor;
        DEALLOCATE revoke_cursor;

        DECLARE @CreatedByMigration BIT = CASE WHEN
                EXISTS (SELECT 1 FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription)
            AND EXISTS (SELECT 1 FROM dbo.RolePermissionChangeLog
                        WHERE PermissionCode = @Code AND Change = N'Added' AND Script = @Script
                          AND Reason LIKE N'%[[]code created by this script; PermissionId=' + @PermText + N']')
            THEN 1 ELSE 0 END;
        IF @CreatedByMigration = 0
            PRINT '=== Permission code KEPT: it was not created by the migration ===';
        ELSE IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE PermissionId = @Perm)
            PRINT '=== Permission code KEPT: a role still holds MaterialStock.Transfer ===';
        ELSE
        BEGIN
            DELETE FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription;
            IF @@ROWCOUNT <> 1
                THROW 53439, 'The migration-owned permission code could not be deleted exactly once -- nothing changed.', 1;
            PRINT '=== Permission MaterialStock.Transfer (created by the migration) deleted ===';
        END
    END

    -- ---- 2. triggers: the 2026-10-08 versions, byte-for-byte -------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_MaterialStock_Rules ON dbo.MaterialStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- A new balance starts empty: stock arrives only through the ledger.
    IF EXISTS (SELECT 1 FROM inserted i WHERE i.CurrentQuantity <> 0 AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id))
        THROW 51921, ''A new material stock balance starts at 0 -- add stock with Add Material.'', 1;
    -- The material and location of a balance never change.
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.MaterialTypeId <> d.MaterialTypeId OR i.AreaId <> d.AreaId OR i.PolyhouseId <> d.PolyhouseId)
        THROW 51922, ''The material, Area and Polyhouse of a material stock balance cannot be changed.'', 1;
    -- A new balance: an active material at a Polyhouse of THIS Area, which is an active Main Office Area.
    IF EXISTS (SELECT 1 FROM inserted i
               WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
                 AND NOT EXISTS (SELECT 1 FROM dbo.Polyhouses p WITH (FORCESEEK)
                                 JOIN dbo.Area a WITH (FORCESEEK) ON a.Id = p.AreaId
                                 WHERE p.Id = i.PolyhouseId AND p.AreaId = i.AreaId AND a.AreaType = N''MainOffice'' AND a.IsActive = 1))
        THROW 51923, ''Material stock is kept only at a Polyhouse of an active Main Office Area.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.MaterialTypes m WITH (FORCESEEK) ON m.Id = i.MaterialTypeId
               WHERE m.IsActive = 0 AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id))
        THROW 51924, ''This material is not active.'', 1;
    -- The balance may only change to exactly the sum of its ledger.
    IF UPDATE(CurrentQuantity) AND EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
        WHERE i.CurrentQuantity <> d.CurrentQuantity
          AND i.CurrentQuantity <> ISNULL((SELECT SUM(t.Direction * t.StockQuantity) FROM dbo.MaterialTransactions t WITH (FORCESEEK) WHERE t.MaterialStockId = i.Id), 0))
        THROW 51925, ''Material stock can only change through a material transaction.'', 1;
END');
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_MaterialTransactions_Ledger ON dbo.MaterialTransactions
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Insert-only. A scratch copy (never PlantsIMS2_Test / production) may remove its own test rows
    -- when the session explicitly asks for it (end-to-end test clean-up).
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        IF EXISTS (SELECT 1 FROM inserted)
            THROW 51931, ''Material transactions cannot be changed. Record a correcting ADJUSTMENT instead.'', 1;
        IF NOT (DB_NAME() LIKE N''PlantsIMS2[_]Scratch[_]%'' AND ISNULL(CAST(SESSION_CONTEXT(N''MaterialTestCleanup'') AS INT), 0) = 1)
            THROW 51932, ''Material transactions cannot be deleted.'', 1;
        RETURN;
    END
    IF EXISTS (SELECT MaterialStockId FROM inserted GROUP BY MaterialStockId HAVING COUNT(*) > 1)
        THROW 51933, ''One material transaction per stock balance per statement.'', 1;
    -- The balance before must be the current balance (the balance row is updated afterwards).
    IF EXISTS (SELECT 1 FROM inserted t JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId WHERE t.BalanceBefore <> s.CurrentQuantity)
        THROW 51934, ''Material stock changed meanwhile -- the transaction balance does not match the current stock. Please try again.'', 1;
    -- A consumption (or its reversal) belongs to a sowing of the SAME material, Area and Polyhouse:
    -- the sowing itself (SeedSowing) or the sowing of the approval (ReadyConfirmation, overage trays).
    IF EXISTS (SELECT 1 FROM inserted t
               JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId
               LEFT JOIN dbo.ReadyConfirmations rc WITH (FORCESEEK) ON t.ReferenceType = ''ReadyConfirmation'' AND rc.Id = t.ReferenceId
               LEFT JOIN dbo.SeedSowings ws WITH (FORCESEEK) ON t.ReferenceType = ''SeedSowing'' AND ws.Id = t.ReferenceId
               LEFT JOIN dbo.SeedSowings wa WITH (FORCESEEK) ON wa.Id = rc.SeedSowingId
               WHERE t.TransactionType IN (''CONSUME'', ''REVERSAL'')
                 AND (COALESCE(ws.Id, wa.Id) IS NULL
                      OR ISNULL(COALESCE(ws.GrowingMaterialTypeId, wa.GrowingMaterialTypeId), -1) <> s.MaterialTypeId
                      OR COALESCE(ws.AreaId, wa.AreaId) <> s.AreaId
                      OR ISNULL(COALESCE(ws.PolyhouseId, wa.PolyhouseId), -1) <> s.PolyhouseId))
        THROW 51935, ''A material consumption must match its sowing''''s growing material, Area and Polyhouse.'', 1;
    -- A reversal returns exactly what that sowing (or approval) consumed, to the same balance.
    IF EXISTS (SELECT 1 FROM inserted t
               WHERE t.TransactionType = ''REVERSAL''
                 AND NOT EXISTS (SELECT 1 FROM dbo.MaterialTransactions c WITH (FORCESEEK)
                                 WHERE c.ReferenceType = t.ReferenceType AND c.ReferenceId = t.ReferenceId AND c.TransactionType = ''CONSUME''
                                   AND c.MaterialStockId = t.MaterialStockId AND c.StockQuantity = t.StockQuantity))
        THROW 51936, ''A reversal must return exactly the quantity its sowing consumed, to the same Polyhouse.'', 1;
END');
    IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialTransactions_Ledger'))), 2) <> 'CA57327288377E8D6EB93F9C57CDCF7957A95E29E9B762259DBDB4908B10BCF0'
       OR CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialStock_Rules'))), 2) <> 'CE35E15C73548CA1F6A314DFBF8E4F3A88F90F8D55DECAA38B6B029DE35514F3'
        THROW 53441, 'The restored triggers are not byte-identical to their 2026-10-08 versions -- nothing changed.', 1;
    PRINT '=== TR_MaterialStock_Rules and TR_MaterialTransactions_Ledger restored (SHA-256 verified) ===';

    -- ---- 3. ledger constraints and index ---------------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.MaterialTransactions') AND name = N'UX_MaterialTransactions_TransferLink')
    BEGIN
        EXEC (N'DROP INDEX UX_MaterialTransactions_TransferLink ON dbo.MaterialTransactions;');
        PRINT '=== UX_MaterialTransactions_TransferLink dropped ===';
    END
    ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Type;
    ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Type CHECK (TransactionType IN ('RECEIVE', 'CONSUME', 'REVERSAL', 'ADJUSTMENT'));
    ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Direction;
    ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Direction CHECK ((TransactionType IN ('RECEIVE', 'REVERSAL') AND Direction = 1)
                                                             OR (TransactionType = 'CONSUME' AND Direction = -1)
                                                             OR (TransactionType = 'ADJUSTMENT' AND Direction IN (1, -1)));
    ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Sowing;
    ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Sowing
        CHECK ((TransactionType IN ('CONSUME', 'REVERSAL') AND ReferenceType IN ('SeedSowing', 'ReadyConfirmation') AND ReferenceId IS NOT NULL)
            OR (TransactionType IN ('RECEIVE', 'ADJUSTMENT') AND ReferenceType IS NULL AND ReferenceId IS NULL));
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Type')) <> N'([TransactionType]=''ADJUSTMENT'' OR [TransactionType]=''REVERSAL'' OR [TransactionType]=''CONSUME'' OR [TransactionType]=''RECEIVE'')'
       OR OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Direction')) <> N'(([TransactionType]=''REVERSAL'' OR [TransactionType]=''RECEIVE'') AND [Direction]=(1) OR [TransactionType]=''CONSUME'' AND [Direction]=(-1) OR [TransactionType]=''ADJUSTMENT'' AND ([Direction]=(-1) OR [Direction]=(1)))'
       OR OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Sowing')) <> N'(([TransactionType]=''REVERSAL'' OR [TransactionType]=''CONSUME'') AND ([ReferenceType]=''ReadyConfirmation'' OR [ReferenceType]=''SeedSowing'') AND [ReferenceId] IS NOT NULL OR ([TransactionType]=''ADJUSTMENT'' OR [TransactionType]=''RECEIVE'') AND [ReferenceType] IS NULL AND [ReferenceId] IS NULL)'
        THROW 53442, 'The restored CHECK constraints are not their previous definitions -- nothing changed.', 1;
    PRINT '=== CK_MaterialTransactions_Type / _Direction / _Sowing restored (definitions verified) ===';

    -- ---- 4. dbo.MaterialTransfers ----------------------------------------------------------------------------------------
    IF OBJECT_ID('dbo.TR_MaterialTransfers_Rules', 'TR') IS NOT NULL
        EXEC (N'DROP TRIGGER dbo.TR_MaterialTransfers_Rules;');
    IF OBJECT_ID('dbo.MaterialTransfers', 'U') IS NOT NULL
    BEGIN
        EXEC (N'DROP TABLE dbo.MaterialTransfers;');
        PRINT '=== dbo.MaterialTransfers dropped ===';
    END
    ELSE
        PRINT '=== dbo.MaterialTransfers does not exist -- no-op ===';

    SELECT COUNT(*) AS Balances, SUM(CurrentQuantity) AS TotalKg FROM dbo.MaterialStock;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaterialTransfers1025Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaterialTransfers1025Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaterialTransfers1025Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
