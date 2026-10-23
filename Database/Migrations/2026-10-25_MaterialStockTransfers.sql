-- ============================================================================
-- 2026-10-25_MaterialStockTransfers.sql          *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- WHY (requirement 2026-10-23): staff must move Cocopeat and Pitmos between Polyhouses -- inside the Main Office Area and
--   between the Main Office and the Mother Plant Areas -- like empty trays. Material stock is ALREADY kept per
--   MATERIAL + AREA + POLYHOUSE (dbo.MaterialStock, kg, 2026-10-08_MaterialStock.sql), so no stock is moved, merged or
--   invented here. What the schema cannot do today:
--     * hold a balance outside a Main Office Area (TR_MaterialStock_Rules, 51923);
--     * record a movement that is not RECEIVE / CONSUME / REVERSAL / ADJUSTMENT (CK_MaterialTransactions_Type,
--       _Direction, _Sowing);
--     * tie the two sides of one move together (there is no transfer record).
--
-- WHAT:
--   1. dbo.MaterialTransfers -- one row per transfer: material, source and destination (Area, Polyhouse and their
--      dbo.MaterialStock balance), quantity in the material's own stock unit (kg -- the usable kg shown on the stock
--      page; NO conversion factor is applied to a transfer), business date, remark, who and when, and the form's
--      one-time SubmissionToken (UNIQUE: a double submit can never move stock twice). Insert-only
--      (TR_MaterialTransfers_Rules); its stock rows must match its material / Area / Polyhouse.
--   2. dbo.MaterialTransactions: two new ledger types, written by the application in ONE transaction with the transfer:
--        TRANSFER_OUT  -1  on the source balance        ReferenceType 'MaterialTransfer', ReferenceId = the transfer
--        TRANSFER_IN   +1  on the destination balance   (same reference)
--      CK_MaterialTransactions_Type / _Direction / _Sowing are re-created to allow them (existing rows re-checked WITH
--      CHECK); UX_MaterialTransactions_TransferLink: at most one OUT and one IN per transfer.
--      TR_MaterialTransactions_Ledger (new version): every rule of the current version, plus
--        51937  a TRANSFER row matches its transfer's balance (OUT = source, IN = destination) and quantity exactly;
--        51938  a balance outside the Main Office moves ONLY by transfers (no sowing uses material there, and Add
--               Material / Adjust stay Main Office only, as today).
--   3. TR_MaterialStock_Rules (new version): a NEW balance may be opened at a Polyhouse of an active Main Office OR
--      Mother Plant Area (Outlet and other Area types still refused). Every other rule is unchanged. Existing balances
--      are not touched. TR_SeedSowings_GrowingMaterial is NOT changed: sowings outside the Main Office still use no
--      material.
--   4. Permission MaterialStock.Transfer ("transfer Cocopeat / Pitmos between Polyhouses"), created only if missing and
--      granted to "Sowing Supervisor" (the role that already holds MaterialStock.View / Add / History). The
--      application checks it together with the Area rule: the SOURCE Area must be one the user is assigned to; a
--      non-administrator may send only to an Area where an active user assigned to it can see material
--      (MaterialStock.View) -- so stock is never put where nobody can see it. Logged in dbo.RolePermissionChangeLog.
--
-- NOT changed: any existing balance or ledger row, MaterialTypes / conversion factors / consumption rules, sowings,
--   trays, the Inventory Store, any other role or user. NO opening balance is created: a balance at a new location
--   starts at 0 and is filled only by a transfer from a location that holds the stock.
--
-- APPLICATION WITHOUT THIS SCRIPT: the code probes for it (Data/MaterialTransferSchema.cs). Without it the Material
--   Transfers page says the update is not applied, and every other Material Stock screen works exactly as before.
--
-- PREDECESSORS (checked, nothing changes otherwise): TR_MaterialTransactions_Ledger is the 2026-10-08_MaterialStockApprovals
--   version and TR_MaterialStock_Rules the 2026-10-08_MaterialStock version (SHA-256 of their definitions, 53408), and the
--   three CHECK constraints have their current definitions (53409) -- or this script's own versions (re-run = no-op).
--
-- TARGET SAFEGUARDS (as 2026-10-24_ReadyStockOvergrowth.sql): the local AND the live database can both be named
--   PlantsIMS2_Test. The first batch refuses to continue unless ALL of these hold (nothing is read or written before them):
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*
--     2. MODE is DRYRUN or COMMIT
--     3. @@SERVERNAME = $(TARGET_SERVER)
--     4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)
--     6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--
-- OPERATOR CHECKLIST
--   a) read-only identity query, keep its output:
--        SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) AS ServerProperty,
--               CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName, DB_NAME() AS DatabaseName,
--               CONVERT(CHAR(19), d.create_date, 126) AS DatabaseCreated
--        FROM sys.databases d WHERE d.database_id = DB_ID();
--   b) confirm independently that this machine IS the intended one; c) take a COPY_ONLY backup;
--   d) sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--             TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-25_MaterialStockTransfers.sql
--   Review the DRYRUN output (identity line, predecessor checks, the Sowing Supervisor holders and their Areas,
--   "created" / "pre-existed", balances before = after). It ends "DRY RUN -- rolled back". Then MODE=COMMIT.
--
-- Requires: 2026-10-08_MaterialStock.sql, 2026-10-08_MaterialStockApprovals.sql, 2026-10-12_BedSowing.sql
--   (MaterialTransactions.TransactionDate), 2026-10-11_TrayAccessByLocation.sql (dbo.RolePermissionChangeLog).
-- One transaction. Idempotent. Undo: 2026-10-25_MaterialStockTransfers_Rollback.sql (refused once any transfer exists).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53401, '2026-10-25_MaterialStockTransfers.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53402, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53403, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53404, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53405, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53406, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53407, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
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

BEGIN TRANSACTION MaterialTransfers1025;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    -- ---- 0. prerequisites and predecessors -------------------------------------------------------------------------
    IF OBJECT_ID('dbo.MaterialStock', 'U') IS NULL OR OBJECT_ID('dbo.MaterialTransactions', 'U') IS NULL
        THROW 53410, 'Run 2026-10-08_MaterialStock.sql and 2026-10-08_MaterialStockApprovals.sql first.', 1;
    IF COL_LENGTH('dbo.MaterialTransactions', 'TransactionDate') IS NULL
        THROW 53411, 'dbo.MaterialTransactions.TransactionDate is missing -- run 2026-10-12_BedSowing.sql first.', 1;
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 53412, 'dbo.RolePermissionChangeLog is missing -- run 2026-10-11_TrayAccessByLocation.sql first.', 1;

    DECLARE @LedgerHash VARCHAR(64) = CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialTransactions_Ledger'))), 2);
    DECLARE @StockHash VARCHAR(64) = CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialStock_Rules'))), 2);
    DECLARE @LedgerIsOurs BIT = CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialTransactions_Ledger')) LIKE N'%[[]2026-10-25 MaterialTransfers]%' THEN 1 ELSE 0 END;
    DECLARE @StockIsOurs BIT = CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_MaterialStock_Rules')) LIKE N'%[[]2026-10-25 MaterialTransfers]%' THEN 1 ELSE 0 END;
    PRINT '=== TR_MaterialTransactions_Ledger SHA-256 ' + ISNULL(@LedgerHash, '(missing)') + CASE WHEN @LedgerIsOurs = 1 THEN ' (this script''s version)' ELSE '' END + ' ===';
    PRINT '=== TR_MaterialStock_Rules        SHA-256 ' + ISNULL(@StockHash, '(missing)') + CASE WHEN @StockIsOurs = 1 THEN ' (this script''s version)' ELSE '' END + ' ===';
    IF (@LedgerIsOurs = 0 AND ISNULL(@LedgerHash, '') <> 'CA57327288377E8D6EB93F9C57CDCF7957A95E29E9B762259DBDB4908B10BCF0')
       OR (@StockIsOurs = 0 AND ISNULL(@StockHash, '') <> 'CE35E15C73548CA1F6A314DFBF8E4F3A88F90F8D55DECAA38B6B029DE35514F3')
        THROW 53408, 'TR_MaterialTransactions_Ledger / TR_MaterialStock_Rules are not the expected 2026-10-08 versions -- schema drift, nothing changed.', 1;

    DECLARE @OldType NVARCHAR(MAX) = N'([TransactionType]=''ADJUSTMENT'' OR [TransactionType]=''REVERSAL'' OR [TransactionType]=''CONSUME'' OR [TransactionType]=''RECEIVE'')';
    DECLARE @OldDirection NVARCHAR(MAX) = N'(([TransactionType]=''REVERSAL'' OR [TransactionType]=''RECEIVE'') AND [Direction]=(1) OR [TransactionType]=''CONSUME'' AND [Direction]=(-1) OR [TransactionType]=''ADJUSTMENT'' AND ([Direction]=(-1) OR [Direction]=(1)))';
    DECLARE @OldSowing NVARCHAR(MAX) = N'(([TransactionType]=''REVERSAL'' OR [TransactionType]=''CONSUME'') AND ([ReferenceType]=''ReadyConfirmation'' OR [ReferenceType]=''SeedSowing'') AND [ReferenceId] IS NOT NULL OR ([TransactionType]=''ADJUSTMENT'' OR [TransactionType]=''RECEIVE'') AND [ReferenceType] IS NULL AND [ReferenceId] IS NULL)';
    -- ISNULL: a MISSING constraint must fail the drift check below (NULL would make it neither true nor false).
    DECLARE @Type NVARCHAR(MAX) = ISNULL(OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Type')), N'(missing)');
    DECLARE @Direction NVARCHAR(MAX) = ISNULL(OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Direction')), N'(missing)');
    DECLARE @Sowing NVARCHAR(MAX) = ISNULL(OBJECT_DEFINITION(OBJECT_ID('dbo.CK_MaterialTransactions_Sowing')), N'(missing)');
    IF NOT ((@Type = @OldType OR @Type LIKE N'%TRANSFER[_]OUT%')
        AND (@Direction = @OldDirection OR @Direction LIKE N'%TRANSFER[_]OUT%')
        AND (@Sowing = @OldSowing OR @Sowing LIKE N'%MaterialTransfer%'))
        THROW 53409, 'CK_MaterialTransactions_Type / _Direction / _Sowing are not their expected definitions -- schema drift, nothing changed.', 1;

    PRINT '=== Before: material balances and ledger (must be identical After) ===';
    SELECT COUNT(*) AS Balances, SUM(CurrentQuantity) AS TotalKg FROM dbo.MaterialStock;
    SELECT TransactionType, COUNT(*) AS Rows, SUM(Direction * StockQuantity) AS NetKg FROM dbo.MaterialTransactions GROUP BY TransactionType ORDER BY 1;

    -- ---- 1. dbo.MaterialTransfers ------------------------------------------------------------------------------------
    IF OBJECT_ID('dbo.MaterialTransfers', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.MaterialTransfers (
            Id                      INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_MaterialTransfers PRIMARY KEY,
            TransferCode            NVARCHAR(30)       NOT NULL CONSTRAINT UQ_MaterialTransfers_Code UNIQUE,
            MaterialTypeId          INT                NOT NULL CONSTRAINT FK_MaterialTransfers_Material REFERENCES dbo.MaterialTypes (Id),
            SourceStockId           INT                NOT NULL CONSTRAINT FK_MaterialTransfers_SourceStock REFERENCES dbo.MaterialStock (Id),
            SourceAreaId            INT                NOT NULL CONSTRAINT FK_MaterialTransfers_SourceArea REFERENCES dbo.Area (Id),
            SourcePolyhouseId       INT                NOT NULL CONSTRAINT FK_MaterialTransfers_SourcePolyhouse REFERENCES dbo.Polyhouses (Id),
            DestinationStockId      INT                NOT NULL CONSTRAINT FK_MaterialTransfers_DestinationStock REFERENCES dbo.MaterialStock (Id),
            DestinationAreaId       INT                NOT NULL CONSTRAINT FK_MaterialTransfers_DestinationArea REFERENCES dbo.Area (Id),
            DestinationPolyhouseId  INT                NOT NULL CONSTRAINT FK_MaterialTransfers_DestinationPolyhouse REFERENCES dbo.Polyhouses (Id),
            Quantity                DECIMAL(18,3)      NOT NULL,   -- in the material''s stock unit (kg); no conversion
            TransferDate            DATE               NOT NULL,   -- India business date
            Remarks                 NVARCHAR(500)      NULL,
            SubmissionToken         UNIQUEIDENTIFIER   NOT NULL CONSTRAINT UQ_MaterialTransfers_Token UNIQUE,
            CreatedById             INT                NOT NULL CONSTRAINT FK_MaterialTransfers_CreatedBy REFERENCES dbo.IMSUsers (Id),
            CreatedAt               DATETIME2          NOT NULL CONSTRAINT DF_MaterialTransfers_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_MaterialTransfers_Quantity CHECK (Quantity > 0),
            CONSTRAINT CK_MaterialTransfers_Locations CHECK (SourcePolyhouseId <> DestinationPolyhouseId AND SourceStockId <> DestinationStockId)
        );');
        EXEC (N'CREATE INDEX IX_MaterialTransfers_Date ON dbo.MaterialTransfers (TransferDate, Id) INCLUDE (SourceAreaId, DestinationAreaId, MaterialTypeId);');
        EXEC (N'CREATE INDEX IX_MaterialTransfers_SourceArea ON dbo.MaterialTransfers (SourceAreaId, TransferDate);');
        EXEC (N'CREATE INDEX IX_MaterialTransfers_DestinationArea ON dbo.MaterialTransfers (DestinationAreaId, TransferDate);');
        PRINT '=== dbo.MaterialTransfers created ===';
    END
    ELSE
        PRINT '=== dbo.MaterialTransfers already exists -- no-op ===';

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_MaterialTransfers_Rules ON dbo.MaterialTransfers
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- [2026-10-25 MaterialTransfers] Insert-only: a transfer is corrected by a transfer back, never edited. A scratch copy
    -- (never PlantsIMS2_Test / production) may remove its own test rows when the session explicitly asks for it.
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        IF EXISTS (SELECT 1 FROM inserted)
            THROW 53421, ''Material transfers cannot be changed. Transfer the material back instead.'', 1;
        IF NOT (DB_NAME() LIKE N''PlantsIMS2[_]Scratch[_]%'' AND ISNULL(CAST(SESSION_CONTEXT(N''MaterialTestCleanup'') AS INT), 0) = 1)
            THROW 53422, ''Material transfers cannot be deleted.'', 1;
        RETURN;
    END
    -- Both balances are THIS material at THIS Area + Polyhouse.
    IF EXISTS (SELECT 1 FROM inserted i
               WHERE NOT EXISTS (SELECT 1 FROM dbo.MaterialStock s WITH (FORCESEEK)
                                 WHERE s.Id = i.SourceStockId AND s.MaterialTypeId = i.MaterialTypeId AND s.AreaId = i.SourceAreaId AND s.PolyhouseId = i.SourcePolyhouseId)
                  OR NOT EXISTS (SELECT 1 FROM dbo.MaterialStock s WITH (FORCESEEK)
                                 WHERE s.Id = i.DestinationStockId AND s.MaterialTypeId = i.MaterialTypeId AND s.AreaId = i.DestinationAreaId AND s.PolyhouseId = i.DestinationPolyhouseId))
        THROW 53423, ''A material transfer must name the source and destination balances of its own material, Area and Polyhouse.'', 1;
END');
    PRINT '=== TR_MaterialTransfers_Rules created / updated ===';

    -- ---- 2. ledger types --------------------------------------------------------------------------------------------
    IF @Type = @OldType
    BEGIN
        ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Type;
        ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Type
            CHECK (TransactionType IN ('RECEIVE', 'CONSUME', 'REVERSAL', 'ADJUSTMENT', 'TRANSFER_OUT', 'TRANSFER_IN'));
        PRINT '=== CK_MaterialTransactions_Type: + TRANSFER_OUT, TRANSFER_IN ===';
    END
    ELSE PRINT '=== CK_MaterialTransactions_Type already allows transfers -- no-op ===';

    IF @Direction = @OldDirection
    BEGIN
        ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Direction;
        ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Direction
            CHECK ((TransactionType IN ('RECEIVE', 'REVERSAL') AND Direction = 1)
                OR (TransactionType = 'CONSUME' AND Direction = -1)
                OR (TransactionType = 'ADJUSTMENT' AND Direction IN (1, -1))
                OR (TransactionType = 'TRANSFER_OUT' AND Direction = -1)
                OR (TransactionType = 'TRANSFER_IN' AND Direction = 1));
        PRINT '=== CK_MaterialTransactions_Direction: TRANSFER_OUT -1, TRANSFER_IN +1 ===';
    END
    ELSE PRINT '=== CK_MaterialTransactions_Direction already allows transfers -- no-op ===';

    IF @Sowing = @OldSowing
    BEGIN
        ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Sowing;
        ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Sowing
            CHECK ((TransactionType IN ('CONSUME', 'REVERSAL') AND ReferenceType IN ('SeedSowing', 'ReadyConfirmation') AND ReferenceId IS NOT NULL)
                OR (TransactionType IN ('RECEIVE', 'ADJUSTMENT') AND ReferenceType IS NULL AND ReferenceId IS NULL)
                OR (TransactionType IN ('TRANSFER_OUT', 'TRANSFER_IN') AND ReferenceType = 'MaterialTransfer' AND ReferenceId IS NOT NULL));
        PRINT '=== CK_MaterialTransactions_Sowing: + MaterialTransfer reference ===';
    END
    ELSE PRINT '=== CK_MaterialTransactions_Sowing already allows transfers -- no-op ===';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.MaterialTransactions') AND name = N'UX_MaterialTransactions_TransferLink')
    BEGIN
        EXEC (N'CREATE UNIQUE INDEX UX_MaterialTransactions_TransferLink ON dbo.MaterialTransactions (ReferenceType, ReferenceId, TransactionType)
            WHERE TransactionType IN (''TRANSFER_OUT'', ''TRANSFER_IN'');');
        PRINT '=== UX_MaterialTransactions_TransferLink created (one OUT and one IN per transfer) ===';
    END
    ELSE PRINT '=== UX_MaterialTransactions_TransferLink already exists -- no-op ===';

    -- ---- 3. triggers (CREATE OR ALTER: a re-run brings them to this version) ------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_MaterialStock_Rules ON dbo.MaterialStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- [2026-10-25 MaterialTransfers] as 2026-10-08_MaterialStock.sql, except that a new balance may also be opened at a
    -- Polyhouse of an active Mother Plant Area (filled only by a transfer -- see TR_MaterialTransactions_Ledger, 51938).
    -- A new balance starts empty: stock arrives only through the ledger.
    IF EXISTS (SELECT 1 FROM inserted i WHERE i.CurrentQuantity <> 0 AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id))
        THROW 51921, ''A new material stock balance starts at 0 -- add stock with Add Material.'', 1;
    -- The material and location of a balance never change.
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.MaterialTypeId <> d.MaterialTypeId OR i.AreaId <> d.AreaId OR i.PolyhouseId <> d.PolyhouseId)
        THROW 51922, ''The material, Area and Polyhouse of a material stock balance cannot be changed.'', 1;
    -- A new balance: an active material at a Polyhouse of THIS Area, which is an active Main Office or Mother Plant Area.
    IF EXISTS (SELECT 1 FROM inserted i
               WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
                 AND NOT EXISTS (SELECT 1 FROM dbo.Polyhouses p WITH (FORCESEEK)
                                 JOIN dbo.Area a WITH (FORCESEEK) ON a.Id = p.AreaId
                                 WHERE p.Id = i.PolyhouseId AND p.AreaId = i.AreaId AND a.AreaType IN (N''MainOffice'', N''MotherPlant'') AND a.IsActive = 1))
        THROW 51923, ''Material stock is kept only at a Polyhouse of an active Main Office or Mother Plant Area.'', 1;
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
    PRINT '=== TR_MaterialStock_Rules updated (Main Office or Mother Plant Polyhouse) ===';

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_MaterialTransactions_Ledger ON dbo.MaterialTransactions
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- [2026-10-25 MaterialTransfers] every rule of the 2026-10-08_MaterialStockApprovals version, plus 51937 / 51938.
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
    -- A transfer row moves exactly its transfer''s quantity: OUT from the source balance, IN to the destination balance.
    IF EXISTS (SELECT 1 FROM inserted t
               WHERE t.TransactionType IN (''TRANSFER_OUT'', ''TRANSFER_IN'')
                 AND NOT EXISTS (SELECT 1 FROM dbo.MaterialTransfers x WITH (FORCESEEK)
                                 WHERE x.Id = t.ReferenceId AND x.Quantity = t.StockQuantity
                                   AND ((t.TransactionType = ''TRANSFER_OUT'' AND x.SourceStockId = t.MaterialStockId)
                                     OR (t.TransactionType = ''TRANSFER_IN'' AND x.DestinationStockId = t.MaterialStockId))))
        THROW 51937, ''A material transfer row must move exactly its transfer''''s quantity between its own source and destination.'', 1;
    -- Outside the Main Office no sowing uses material and nothing is added or adjusted there: such a balance moves only
    -- by transfers.
    IF EXISTS (SELECT 1 FROM inserted t
               JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId
               JOIN dbo.Area a WITH (FORCESEEK) ON a.Id = s.AreaId
               WHERE ISNULL(a.AreaType, N'''') <> N''MainOffice'' AND t.TransactionType NOT IN (''TRANSFER_OUT'', ''TRANSFER_IN''))
        THROW 51938, ''Material stock outside the Main Office changes only by a material transfer.'', 1;
END');
    PRINT '=== TR_MaterialTransactions_Ledger updated (transfers) ===';

    -- ---- 4. Permission MaterialStock.Transfer -> Sowing Supervisor ------------------------------------------------------
    DECLARE @Code NVARCHAR(100) = N'MaterialStock.Transfer';
    DECLARE @Script NVARCHAR(200) = N'2026-10-25_MaterialStockTransfers.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Transfer Cocopeat / Pitmos between Polyhouses (from own Areas) [created by 2026-10-25_MaterialStockTransfers.sql]';
    DECLARE @Roles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, RoleId INT NULL);
    INSERT INTO @Roles (RoleName) VALUES (N'Sowing Supervisor');
    UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;
    IF EXISTS (SELECT 1 FROM @Roles WHERE RoleId IS NULL)
        THROW 53413, 'Role Sowing Supervisor not found -- nothing changed.', 1;
    IF EXISTS (SELECT x.RoleName FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName GROUP BY x.RoleName HAVING COUNT(*) > 1)
        THROW 53414, 'A role name matches more than one dbo.Roles row -- nothing changed.', 1;

    PRINT '=== Holders of the role and the Area of each assignment (they may send only FROM these Areas) ===';
    SELECT x.RoleName AS Role, u.Id AS UserId, u.Name, u.IsActive, a.Id AS AreaId, a.Name AS Area, a.AreaType
    FROM @Roles x JOIN dbo.UserRoles ur ON ur.RoleId = x.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
    ORDER BY x.RoleName, u.Id;

    DECLARE @CreatedHere BIT = 0;
    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code)
    BEGIN
        IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
            INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @OwnedDescription);
        ELSE
            INSERT INTO dbo.Permissions (Id, Code, Description)
            SELECT ISNULL(MAX(Id), 0) + 1, @Code, @OwnedDescription FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK);
        SET @CreatedHere = 1;
        PRINT '=== Permission MaterialStock.Transfer created by this script ===';
    END
    ELSE
        PRINT '=== Permission MaterialStock.Transfer already exists -- used as it is (the rollback deletes it only if this script created it) ===';
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = @Code);

    DECLARE @RoleName NVARCHAR(100), @Role INT;
    DECLARE grant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName, RoleId FROM @Roles ORDER BY RoleName;
    OPEN grant_cursor;
    FETCH NEXT FROM grant_cursor INTO @RoleName, @Role;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
            PRINT '=== ' + @RoleName + ': already granted -- no-op ===';
        ELSE
        BEGIN
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            VALUES (@Role, @RoleName, @Code, N'Added', @Script,
                    N'Material holders transfer Cocopeat / Pitmos from their own Areas (requirement 2026-10-23) '
                    + CASE WHEN @CreatedHere = 1 THEN N'[code created by this script; PermissionId=' ELSE N'[code pre-existed; PermissionId=' END
                    + CAST(@Perm AS NVARCHAR(20)) + N']');
            INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);
            PRINT '=== MaterialStock.Transfer granted to ' + @RoleName + ' ===';
        END
        FETCH NEXT FROM grant_cursor INTO @RoleName, @Role;
    END
    CLOSE grant_cursor;
    DEALLOCATE grant_cursor;

    PRINT '=== After: definitions, grants, balances and ledger (balances / ledger must equal Before) ===';
    SELECT name, definition FROM sys.check_constraints
    WHERE name IN ('CK_MaterialTransactions_Type', 'CK_MaterialTransactions_Direction', 'CK_MaterialTransactions_Sowing') ORDER BY name;
    SELECT name, CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(object_id)), 2) AS Sha256 FROM sys.triggers
    WHERE name IN ('TR_MaterialTransactions_Ledger', 'TR_MaterialStock_Rules', 'TR_MaterialTransfers_Rules') ORDER BY name;
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, p.Code, p.Id AS PermissionId, p.Description
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code = @Code;
    SELECT COUNT(*) AS Balances, SUM(CurrentQuantity) AS TotalKg FROM dbo.MaterialStock;
    SELECT TransactionType, COUNT(*) AS Rows, SUM(Direction * StockQuantity) AS NetKg FROM dbo.MaterialTransactions GROUP BY TransactionType ORDER BY 1;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaterialTransfers1025;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaterialTransfers1025;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaterialTransfers1025;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
