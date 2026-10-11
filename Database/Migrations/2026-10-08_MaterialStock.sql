-- ============================================================================
-- 2026-10-08_MaterialStock.sql
--
-- WHY: growing material (Cocopeat, Pitmos) is a production-side stock kept in
-- the Polyhouses of the Main Office Area. The Sowing Supervisor adds it; every
-- sowing in a Main Office Polyhouse uses exactly ONE material, deducted from
-- that SAME Polyhouse in the sowing's own transaction. NOT the Inventory Store
-- (dbo.StoreItems / StoreTransactions) and NOT the retired dbo.Inventory /
-- dbo.InventoryTransactions -- none of them is read or changed (row counts are
-- printed before / after).
--
-- MASTER DATA (approximate business values, changeable later as DATA):
--   MaterialTypes              Code, Name, Unit, ConversionFactor (dry kg -> usable kg)
--                              COCOPEAT 5.2 (5 kg dry = 26 kg usable), PITMOS 1.0
--   MaterialConsumptionRules   kg of material per tray, per Cavity and Material
--                              (all five cavities x both materials = 1.5 kg / tray)
--
-- STOCK MODEL (the same design as the Inventory Store ledger)
--   MaterialStock          one balance per MATERIAL + AREA + POLYHOUSE; CurrentQuantity changes ONLY
--                          through the ledger; only at a Polyhouse of an active MainOffice Area
--   MaterialTransactions   insert-only ledger: RECEIVE (+), CONSUME (-), REVERSAL (+, a cancelled
--                          sowing), ADJUSTMENT (+/-, reason required). StockQuantity is always > 0;
--                          Direction (+1/-1) gives the sign; BalanceBefore / BalanceAfter per row.
--                          RECEIVE keeps the entered (dry) InputQuantity and the ConversionFactor used;
--                          CONSUME keeps the Trays and QuantityPerTray used.
--   Enforced in the database:
--     CK_MaterialStock_NotNegative / CK_MaterialTransactions_Balance   never negative
--     TR_MaterialStock_Rules          new balance starts at 0; key never changes; Main Office Polyhouse
--                                     only; CurrentQuantity = exactly SUM(its ledger)
--     TR_MaterialTransactions_Ledger  insert-only; BalanceBefore = the current balance; a CONSUME /
--                                     REVERSAL matches its sowing's material, Area and Polyhouse; a
--                                     REVERSAL returns exactly what was consumed
--     UX_MaterialTransactions_SowingLink   one CONSUME and at most one REVERSAL per sowing
--   The application takes sp_getapplock + UPDLOCK on the balance first (same as Tray Stock), so
--   concurrent operations on one balance run strictly one after the other.
--
-- SOWINGS (dbo.SeedSowings, two new NULLable columns -- existing rows untouched)
--   GrowingMaterialTypeId   the ONE material of the sowing. TR_SeedSowings_GrowingMaterial: a NEW
--                           sowing in a Main Office Area must name one active material; a sowing
--                           elsewhere must name none (no material stock exists there); never changes.
--   SubmissionToken         the form's one-time token; UX_SeedSowings_SubmissionToken makes a double
--                           submit unable to create a second sowing (or a second deduction).
--
-- ALSO: Permissions MaterialStock.View / .Add / .History (Sowing Supervisor) and MaterialStock.Manage
-- (adjustments, conversion factors, consumption rules -- full-access users only; no role gets it).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO named PlantsIMS2_Test). One transaction.
-- Idempotent (re-run = no-op; master values already present are never overwritten).
-- Undo: 2026-10-08_MaterialStock_Rollback.sql
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51901, '2026-10-08_MaterialStock.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51902, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51903, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION MaterialStock2026_10_08;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: row counts of tables this script must NOT change (Permissions / RolePermissions only grow) ===';
    SELECT 'Inventory (legacy)' AS TableName, COUNT(*) AS Rows FROM dbo.Inventory
    UNION ALL SELECT 'InventoryTransactions (legacy)', COUNT(*) FROM dbo.InventoryTransactions
    UNION ALL SELECT 'StoreItems', COUNT(*) FROM dbo.StoreItems
    UNION ALL SELECT 'StoreTransactions', COUNT(*) FROM dbo.StoreTransactions
    UNION ALL SELECT 'SeedSowings', COUNT(*) FROM dbo.SeedSowings
    UNION ALL SELECT 'TrayStock', COUNT(*) FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions;

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles r WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor')
        THROW 51904, 'Role Sowing Supervisor is missing.', 1;

    ------------------------------------------------------------------
    -- 1. Permissions (Id identity or not -- the live database may differ) and grants
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'MaterialStock.View',    N'View Cocopeat / Pitmos stock of own Main Office Polyhouses', 1),
        (N'MaterialStock.Add',     N'Add Cocopeat / Pitmos stock to own Main Office Polyhouses', 2),
        (N'MaterialStock.History', N'View material stock transactions and consumption', 3),
        (N'MaterialStock.Manage',  N'Adjust material stock (with a reason) and change conversion factors and consumption per tray', 4);
    DECLARE @Code NVARCHAR(200), @PermDescription NVARCHAR(400);
    DECLARE perm_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Code, Description FROM @Perms ORDER BY SortOrder;
    OPEN perm_cursor;
    FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = @Code)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @PermDescription);
            ELSE
                INSERT INTO dbo.Permissions (Id, Code, Description) SELECT ISNULL(MAX(Id), 0) + 1, @Code, @PermDescription FROM dbo.Permissions;
            PRINT CONCAT('=== Permission ', @Code, ' added ===');
        END
        ELSE PRINT CONCAT('=== Permission ', @Code, ' already exists -- no-op ===');
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r CROSS JOIN dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor'
      AND p.Code IN (N'MaterialStock.View', N'MaterialStock.Add', N'MaterialStock.History')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Sowing Supervisor grants inserted: ', @@ROWCOUNT, ' (expect 3 on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- 2. Master data
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.MaterialTypes', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaterialTypes (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialTypes PRIMARY KEY,
            Code              VARCHAR(20)    NOT NULL CONSTRAINT UQ_MaterialTypes_Code UNIQUE,
            Name              NVARCHAR(50)   NOT NULL CONSTRAINT UQ_MaterialTypes_Name UNIQUE,
            Unit              VARCHAR(10)    NOT NULL CONSTRAINT DF_MaterialTypes_Unit DEFAULT ('KG'),
            ConversionFactor  DECIMAL(9,4)   NOT NULL,   -- usable kg per entered (dry) kg
            IsActive          BIT            NOT NULL CONSTRAINT DF_MaterialTypes_IsActive DEFAULT (1),
            SortOrder         INT            NOT NULL CONSTRAINT DF_MaterialTypes_SortOrder DEFAULT (0),
            CreatedAt         DATETIME2      NOT NULL CONSTRAINT DF_MaterialTypes_CreatedAt DEFAULT (SYSUTCDATETIME()),
            UpdatedById       INT            NULL CONSTRAINT FK_MaterialTypes_UpdatedBy REFERENCES dbo.IMSUsers(Id),
            UpdatedAt         DATETIME2      NULL,
            CONSTRAINT CK_MaterialTypes_Factor CHECK (ConversionFactor > 0),
            CONSTRAINT CK_MaterialTypes_Text   CHECK (LEN(LTRIM(RTRIM(Code))) > 0 AND Code = UPPER(Code) AND LEN(LTRIM(RTRIM(Name))) > 0 AND LEN(LTRIM(RTRIM(Unit))) > 0)
        );
        PRINT '=== dbo.MaterialTypes created ===';
    END
    ELSE PRINT '=== dbo.MaterialTypes already exists -- no-op ===';

    IF NOT EXISTS (SELECT 1 FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT')
        INSERT INTO dbo.MaterialTypes (Code, Name, Unit, ConversionFactor, SortOrder) VALUES ('COCOPEAT', N'Cocopeat', 'KG', 5.2, 1);
    IF NOT EXISTS (SELECT 1 FROM dbo.MaterialTypes WHERE Code = 'PITMOS')
        INSERT INTO dbo.MaterialTypes (Code, Name, Unit, ConversionFactor, SortOrder) VALUES ('PITMOS', N'Pitmos', 'KG', 1.0, 2);

    IF OBJECT_ID('dbo.MaterialConsumptionRules', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaterialConsumptionRules (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialConsumptionRules PRIMARY KEY,
            MaterialTypeId    INT            NOT NULL CONSTRAINT FK_MaterialConsumptionRules_Material REFERENCES dbo.MaterialTypes(Id),
            CavityType        NVARCHAR(20)   NOT NULL,
            QuantityPerTray   DECIMAL(18,3)  NOT NULL,   -- kg of the material per tray
            IsActive          BIT            NOT NULL CONSTRAINT DF_MaterialConsumptionRules_IsActive DEFAULT (1),
            CreatedAt         DATETIME2      NOT NULL CONSTRAINT DF_MaterialConsumptionRules_CreatedAt DEFAULT (SYSUTCDATETIME()),
            UpdatedById       INT            NULL CONSTRAINT FK_MaterialConsumptionRules_UpdatedBy REFERENCES dbo.IMSUsers(Id),
            UpdatedAt         DATETIME2      NULL,
            -- the same closed cavity list as CK_SeedSowings_CavityType
            CONSTRAINT CK_MaterialConsumptionRules_Cavity CHECK (CavityType IN (N'9 Cavity', N'24 Cavity', N'42 Cavity', N'102 Cavity', N'150 Cavity')),
            CONSTRAINT CK_MaterialConsumptionRules_Quantity CHECK (QuantityPerTray > 0)
        );
        CREATE UNIQUE INDEX UX_MaterialConsumptionRules_Active ON dbo.MaterialConsumptionRules (MaterialTypeId, CavityType) WHERE IsActive = 1;
        PRINT '=== dbo.MaterialConsumptionRules created ===';
    END
    ELSE PRINT '=== dbo.MaterialConsumptionRules already exists -- no-op ===';

    INSERT INTO dbo.MaterialConsumptionRules (MaterialTypeId, CavityType, QuantityPerTray)
    SELECT m.Id, c.CavityType, 1.5
    FROM dbo.MaterialTypes m
    CROSS JOIN (VALUES (N'150 Cavity'), (N'102 Cavity'), (N'42 Cavity'), (N'24 Cavity'), (N'9 Cavity')) c(CavityType)
    WHERE m.Code IN ('COCOPEAT', 'PITMOS')
      AND NOT EXISTS (SELECT 1 FROM dbo.MaterialConsumptionRules r WHERE r.MaterialTypeId = m.Id AND r.CavityType = c.CavityType);
    PRINT CONCAT('Consumption rules inserted: ', @@ROWCOUNT, ' (expect 10 on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- 3. Stock balance and ledger
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.MaterialStock', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaterialStock (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialStock PRIMARY KEY,
            MaterialTypeId    INT            NOT NULL CONSTRAINT FK_MaterialStock_Material REFERENCES dbo.MaterialTypes(Id),
            AreaId            INT            NOT NULL CONSTRAINT FK_MaterialStock_Area REFERENCES dbo.Area(Id),
            PolyhouseId       INT            NOT NULL CONSTRAINT FK_MaterialStock_Polyhouse REFERENCES dbo.Polyhouses(Id),
            CurrentQuantity   DECIMAL(18,3)  NOT NULL CONSTRAINT DF_MaterialStock_Current DEFAULT (0),
            CreatedAt         DATETIME2      NOT NULL CONSTRAINT DF_MaterialStock_CreatedAt DEFAULT (SYSUTCDATETIME()),
            UpdatedAt         DATETIME2      NULL,
            CONSTRAINT UQ_MaterialStock_Location UNIQUE (MaterialTypeId, AreaId, PolyhouseId),
            CONSTRAINT CK_MaterialStock_NotNegative CHECK (CurrentQuantity >= 0)
        );
        CREATE INDEX IX_MaterialStock_Location ON dbo.MaterialStock (AreaId, PolyhouseId) INCLUDE (MaterialTypeId, CurrentQuantity);
        PRINT '=== dbo.MaterialStock created ===';
    END
    ELSE PRINT '=== dbo.MaterialStock already exists -- no-op ===';

    IF OBJECT_ID('dbo.MaterialTransactions', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaterialTransactions (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaterialTransactions PRIMARY KEY,
            MaterialStockId   INT              NOT NULL CONSTRAINT FK_MaterialTransactions_Stock REFERENCES dbo.MaterialStock(Id),
            TransactionType   VARCHAR(12)      NOT NULL,
            Direction         SMALLINT         NOT NULL,          -- +1 adds to the balance, -1 takes from it
            StockQuantity     DECIMAL(18,3)    NOT NULL,          -- usable kg moved (always > 0)
            InputQuantity     DECIMAL(18,3)    NULL,              -- RECEIVE: the kg entered (dry for Cocopeat)
            ConversionFactor  DECIMAL(9,4)     NULL,              -- RECEIVE: the factor applied
            Trays             INT              NULL,              -- CONSUME: physical trays of the sowing
            QuantityPerTray   DECIMAL(18,3)    NULL,              -- CONSUME: kg per tray applied
            BalanceBefore     DECIMAL(18,3)    NOT NULL,
            BalanceAfter      DECIMAL(18,3)    NOT NULL,
            ReferenceType     VARCHAR(20)      NULL,              -- 'SeedSowing' for CONSUME / REVERSAL
            ReferenceId       INT              NULL,
            SubmissionToken   UNIQUEIDENTIFIER NULL,              -- the Add Material form's one-time token
            Remarks           NVARCHAR(500)    NULL,
            CreatedById       INT              NULL CONSTRAINT FK_MaterialTransactions_CreatedBy REFERENCES dbo.IMSUsers(Id),
            CreatedAt         DATETIME2        NOT NULL CONSTRAINT DF_MaterialTransactions_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_MaterialTransactions_Type      CHECK (TransactionType IN ('RECEIVE', 'CONSUME', 'REVERSAL', 'ADJUSTMENT')),
            CONSTRAINT CK_MaterialTransactions_Direction CHECK ((TransactionType IN ('RECEIVE', 'REVERSAL') AND Direction = 1)
                                                             OR (TransactionType = 'CONSUME' AND Direction = -1)
                                                             OR (TransactionType = 'ADJUSTMENT' AND Direction IN (1, -1))),
            CONSTRAINT CK_MaterialTransactions_Quantity  CHECK (StockQuantity > 0),
            CONSTRAINT CK_MaterialTransactions_Balance   CHECK (BalanceBefore >= 0 AND BalanceAfter >= 0 AND BalanceAfter = BalanceBefore + Direction * StockQuantity),
            CONSTRAINT CK_MaterialTransactions_Receive   CHECK ((TransactionType = 'RECEIVE' AND InputQuantity > 0 AND ConversionFactor > 0
                                                                 AND StockQuantity = ROUND(InputQuantity * ConversionFactor, 3) AND ReferenceType IS NULL AND ReferenceId IS NULL)
                                                             OR (TransactionType <> 'RECEIVE' AND InputQuantity IS NULL AND ConversionFactor IS NULL)),
            CONSTRAINT CK_MaterialTransactions_Sowing    CHECK ((TransactionType IN ('CONSUME', 'REVERSAL') AND ReferenceType = 'SeedSowing' AND ReferenceId IS NOT NULL)
                                                             OR (TransactionType IN ('RECEIVE', 'ADJUSTMENT') AND ReferenceType IS NULL AND ReferenceId IS NULL)),
            CONSTRAINT CK_MaterialTransactions_Consume   CHECK ((TransactionType = 'CONSUME' AND Trays > 0 AND QuantityPerTray > 0 AND StockQuantity = Trays * QuantityPerTray)
                                                             OR (TransactionType <> 'CONSUME' AND Trays IS NULL AND QuantityPerTray IS NULL)),
            CONSTRAINT CK_MaterialTransactions_Adjust    CHECK (TransactionType <> 'ADJUSTMENT' OR (LEN(LTRIM(RTRIM(ISNULL(Remarks, N'')))) > 0 AND CreatedById IS NOT NULL))
        );
        CREATE INDEX IX_MaterialTransactions_Stock ON dbo.MaterialTransactions (MaterialStockId, Id) INCLUDE (Direction, StockQuantity, TransactionType);
        CREATE INDEX IX_MaterialTransactions_Reference ON dbo.MaterialTransactions (ReferenceType, ReferenceId, TransactionType) INCLUDE (MaterialStockId, StockQuantity);
        CREATE INDEX IX_MaterialTransactions_Date ON dbo.MaterialTransactions (CreatedAt, TransactionType) INCLUDE (MaterialStockId, StockQuantity, Direction);
        CREATE UNIQUE INDEX UX_MaterialTransactions_SowingLink ON dbo.MaterialTransactions (ReferenceType, ReferenceId, TransactionType) WHERE TransactionType IN ('CONSUME', 'REVERSAL');
        CREATE UNIQUE INDEX UX_MaterialTransactions_Token ON dbo.MaterialTransactions (SubmissionToken) WHERE SubmissionToken IS NOT NULL;
        PRINT '=== dbo.MaterialTransactions created ===';
    END
    ELSE PRINT '=== dbo.MaterialTransactions already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 4. Sowing columns
    ------------------------------------------------------------------
    IF COL_LENGTH('dbo.SeedSowings', 'GrowingMaterialTypeId') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.SeedSowings ADD GrowingMaterialTypeId INT NULL CONSTRAINT FK_SeedSowings_GrowingMaterial REFERENCES dbo.MaterialTypes(Id);');
        PRINT '=== SeedSowings.GrowingMaterialTypeId added ===';
    END
    ELSE PRINT '=== SeedSowings.GrowingMaterialTypeId already exists -- no-op ===';
    IF COL_LENGTH('dbo.SeedSowings', 'SubmissionToken') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.SeedSowings ADD SubmissionToken UNIQUEIDENTIFIER NULL;');
        PRINT '=== SeedSowings.SubmissionToken added ===';
    END
    ELSE PRINT '=== SeedSowings.SubmissionToken already exists -- no-op ===';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'UX_SeedSowings_SubmissionToken')
        EXEC (N'CREATE UNIQUE INDEX UX_SeedSowings_SubmissionToken ON dbo.SeedSowings (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');

    ------------------------------------------------------------------
    -- 5. Triggers (CREATE OR ALTER: a re-run brings them to this version). Lookups are FORCESEEK,
    --    as in the Inventory Store triggers, so they never scan rows a concurrent operation holds.
    ------------------------------------------------------------------
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
    PRINT '=== TR_MaterialStock_Rules created / updated ===';

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
    -- A consumption (or its reversal) belongs to a sowing of the SAME material, Area and Polyhouse.
    IF EXISTS (SELECT 1 FROM inserted t
               JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId
               LEFT JOIN dbo.SeedSowings w WITH (FORCESEEK) ON w.Id = t.ReferenceId
               WHERE t.TransactionType IN (''CONSUME'', ''REVERSAL'')
                 AND (w.Id IS NULL OR ISNULL(w.GrowingMaterialTypeId, -1) <> s.MaterialTypeId OR w.AreaId <> s.AreaId OR ISNULL(w.PolyhouseId, -1) <> s.PolyhouseId))
        THROW 51935, ''A material consumption must match its sowing''''s growing material, Area and Polyhouse.'', 1;
    -- A reversal returns exactly what that sowing consumed, to the same balance.
    IF EXISTS (SELECT 1 FROM inserted t
               WHERE t.TransactionType = ''REVERSAL''
                 AND NOT EXISTS (SELECT 1 FROM dbo.MaterialTransactions c WITH (FORCESEEK)
                                 WHERE c.ReferenceType = t.ReferenceType AND c.ReferenceId = t.ReferenceId AND c.TransactionType = ''CONSUME''
                                   AND c.MaterialStockId = t.MaterialStockId AND c.StockQuantity = t.StockQuantity))
        THROW 51936, ''A reversal must return exactly the quantity its sowing consumed, to the same Polyhouse.'', 1;
END');
    PRINT '=== TR_MaterialTransactions_Ledger created / updated ===';

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_GrowingMaterial ON dbo.SeedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- Fixed once sown (cancel and record again instead).
        IF (UPDATE(GrowingMaterialTypeId) OR UPDATE(SubmissionToken)) AND EXISTS (
            SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
            WHERE ISNULL(i.GrowingMaterialTypeId, -1) <> ISNULL(d.GrowingMaterialTypeId, -1)
               OR ISNULL(CONVERT(NCHAR(36), i.SubmissionToken), N'''') <> ISNULL(CONVERT(NCHAR(36), d.SubmissionToken), N''''))
            THROW 51911, ''The growing material of a sowing cannot be changed. Cancel the sowing and record it again.'', 1;
        RETURN;
    END
    -- A new sowing in a Main Office Area uses exactly ONE active growing material; anywhere else none
    -- (material stock exists only at Main Office Polyhouses).
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Area a WITH (FORCESEEK) ON a.Id = i.AreaId
               WHERE a.AreaType = N''MainOffice'' AND i.GrowingMaterialTypeId IS NULL)
        THROW 51912, ''Select the growing material (Cocopeat or Pitmos) for a sowing in a Main Office Polyhouse.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Area a WITH (FORCESEEK) ON a.Id = i.AreaId
               WHERE ISNULL(a.AreaType, N'''') <> N''MainOffice'' AND i.GrowingMaterialTypeId IS NOT NULL)
        THROW 51913, ''Growing material is stocked only at Main Office Polyhouses -- this sowing cannot use one.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.MaterialTypes m WITH (FORCESEEK) ON m.Id = i.GrowingMaterialTypeId WHERE m.IsActive = 0)
        THROW 51914, ''The selected growing material is not active.'', 1;
END');
    PRINT '=== TR_SeedSowings_GrowingMaterial created / updated ===';

    PRINT '=== After: master data, grants, constraints, indexes, triggers ===';
    SELECT Id, Code, Name, Unit, ConversionFactor, IsActive FROM dbo.MaterialTypes ORDER BY SortOrder;
    SELECT m.Code, r.CavityType, r.QuantityPerTray, r.IsActive FROM dbo.MaterialConsumptionRules r JOIN dbo.MaterialTypes m ON m.Id = r.MaterialTypeId ORDER BY m.SortOrder, r.CavityType;
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'MaterialStock.%' ORDER BY RoleName, p.Code;
    SELECT OBJECT_NAME(parent_object_id) AS TableName, name AS CheckConstraint FROM sys.check_constraints
    WHERE parent_object_id IN (OBJECT_ID('dbo.MaterialTypes'), OBJECT_ID('dbo.MaterialConsumptionRules'), OBJECT_ID('dbo.MaterialStock'), OBJECT_ID('dbo.MaterialTransactions'))
    ORDER BY 1, 2;
    SELECT OBJECT_NAME(parent_id) AS TableName, name AS TriggerName FROM sys.triggers
    WHERE name IN ('TR_MaterialStock_Rules', 'TR_MaterialTransactions_Ledger', 'TR_SeedSowings_GrowingMaterial') ORDER BY 1, 2;

    PRINT '=== After: row counts (legacy / other tables must equal Before) ===';
    SELECT 'Inventory (legacy)' AS TableName, COUNT(*) AS Rows FROM dbo.Inventory
    UNION ALL SELECT 'InventoryTransactions (legacy)', COUNT(*) FROM dbo.InventoryTransactions
    UNION ALL SELECT 'StoreItems', COUNT(*) FROM dbo.StoreItems
    UNION ALL SELECT 'StoreTransactions', COUNT(*) FROM dbo.StoreTransactions
    UNION ALL SELECT 'SeedSowings', COUNT(*) FROM dbo.SeedSowings
    UNION ALL SELECT 'TrayStock', COUNT(*) FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'MaterialStock', COUNT(*) FROM dbo.MaterialStock
    UNION ALL SELECT 'MaterialTransactions', COUNT(*) FROM dbo.MaterialTransactions;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaterialStock2026_10_08;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaterialStock2026_10_08;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaterialStock2026_10_08;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
