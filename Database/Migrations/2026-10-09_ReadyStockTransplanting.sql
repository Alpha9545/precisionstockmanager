-- ============================================================================
-- 2026-10-09_ReadyStockTransplanting.sql
--
-- WHY: Main Area Ready Stock gets a second path besides customer dispatch:
--   Main Area Ready Stock -> transfer (in transit) -> received in a Mother Plant
--   Area/Polyhouse -> transplanted into larger cavity trays -> new Ready Stock there.
-- No second stock system: received and transplanted stock are NEW dbo.ReadyStock
-- batches (bookable / dispatchable like any other) that point back to the batch
-- they came from, so every plant stays traceable to its original sowing.
--
-- dbo.ReadyStock (existing rows: OriginType 'Sowing', nothing else changes for them)
--   OriginType          'Sowing' (one per sowing, as before) | 'Transfer' (received in a
--                       Mother Plant Area) | 'Transplant' (output of a transplanting)
--   ParentReadyStockId  the batch this one came from (NULL for 'Sowing')
--   OriginReferenceId   InternalTransfers.Id ('Transfer') / ReadyStockTransplants.Id ('Transplant');
--                       UX_ReadyStock_Origin: ONE batch per transfer / transplant (repeated
--                       "receive" or "save" can never create a second one)
--   InTransitQuantity   plants sent to a Mother Plant Area and not yet received: still part of
--                       Quantity, but neither physically here nor available
--   PhysicalQuantity    = Quantity - Dispatched - InTransit   (computed, redefined)
--   AvailableQuantity   = Quantity - Reserved - Dispatched - InTransit (computed, redefined)
--   CK_ReadyStock_ReservedDispatched  Reserved + Dispatched + InTransit <= Quantity
--   UQ_ReadyStock_SeedSowing -> UX_ReadyStock_SowingBatch: one SOWING batch per sowing
--                       (derived batches share the sowing reference for traceability)
--   FK_ReadyStock_SowingCavity: the cavity must still equal the sowing's -- except for a
--                       transplant output, which is in its new target cavity
--   TR_ReadyStock_WholeTrays: whole trays only for SOWING batches (Main Area); received and
--                       transplanted batches keep exact plant counts (a partial last tray is
--                       allowed -- business decision 2026-10-09)
-- dbo.ReadyStockTransactions: new ledger types
--   TransferHold / TransferHoldRelease  InTransit +/- (do NOT change Quantity, like Reservation)
--   TransferOut (-) / TransferLoss (-)  at the source when a transfer is received
--   TransferIn (+)                       the received batch
--   TransplantOut (-) / TransplantLoss (-) / TransplantIn (+)   a transplanting
--   ReversalReturn (+)                   source credited back when a transplanting is reversed
--                                        (ReversalRemoval, existing, takes the output back out)
-- dbo.InternalTransfers: StockType 'MotherPlantTransfer' (PendingConfirmation ->
--   Completed | Rejected | Cancelled); + DestinationPolyhouseId, TransferDate, SubmissionToken,
--   CancelledById / CancelledDate / CancelReason
-- dbo.ReadyStockTransplants: one row per transplanting (Completed | Reversed)
-- dbo.TrayStockTransactions: type 'Transplant' (empty target trays used by a transplanting)
-- Permissions: ReadyStock.TransferToMotherPlant (Sowing Supervisor, Office Coordinator),
--   ReadyStock.ReceiveTransfer, Transplant.Enter, Transplant.View (Mother Plant Supervisor),
--   Transplant.Reverse (no role -- full-access users only),
--   TrayStock.EnterMotherPlant (Office Coordinator, Booking Executive): add EMPTY trays to the
--   Tray Stock of any active Mother Plant Area polyhouse (Add Material > Trays) -- nothing else.
--   Owner decision 2026-10-09: these roles (and the Administrator) stock Mother Plant trays;
--   their users are assigned to the Main Office only, so the ordinary TrayStock.Enter (own Areas)
--   could not reach a Mother Plant polyhouse.
-- NOT CHANGED: growing material (cocopeat / pitmos). Owner decision 2026-10-09: transplanting
--   uses none, so nothing is deducted (Material Stock is Main-Office-only anyway).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Undo: 2026-10-09_ReadyStockTransplanting_Rollback.sql
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51931, '2026-10-09_ReadyStockTransplanting.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51932, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51933, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION RsTransplant2026_10_09;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: rows that must not change ===';
    SELECT 'ReadyStock' AS TableName, COUNT(*) AS Rows, SUM(Quantity) AS Qty, SUM(ReservedQuantity) AS Reserved, SUM(DispatchedQuantity) AS Dispatched FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyStockTransactions', COUNT(*), SUM(Quantity), NULL, NULL FROM dbo.ReadyStockTransactions
    UNION ALL SELECT 'InternalTransfers', COUNT(*), SUM(Quantity), NULL, NULL FROM dbo.InternalTransfers
    UNION ALL SELECT 'TrayStock', COUNT(*), SUM(PhysicalQuantity), NULL, NULL FROM dbo.TrayStock;

    DECLARE @Role NVARCHAR(100);
    DECLARE @Roles TABLE (Name NVARCHAR(100));
    INSERT INTO @Roles VALUES (N'Sowing Supervisor'), (N'Office Coordinator'), (N'Mother Plant Supervisor'), (N'Booking Executive');
    SELECT @Role = r.Name FROM @Roles r
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles x WHERE COALESCE(NULLIF(LTRIM(RTRIM(x.Name)), N''), x.RoleName) = r.Name);
    IF @Role IS NOT NULL
        THROW 51934, 'A required role (Sowing Supervisor / Office Coordinator / Mother Plant Supervisor / Booking Executive) is missing.', 1;

    ------------------------------------------------------------------
    -- 1. dbo.ReadyStock: origin / lineage / in-transit
    ------------------------------------------------------------------
    IF COL_LENGTH('dbo.ReadyStock', 'OriginType') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD
            OriginType NVARCHAR(20) NOT NULL CONSTRAINT DF_ReadyStock_OriginType DEFAULT N''Sowing'',
            ParentReadyStockId INT NULL CONSTRAINT FK_ReadyStock_Parent REFERENCES dbo.ReadyStock(Id),
            OriginReferenceId INT NULL,
            InTransitQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_ReadyStock_InTransit DEFAULT 0;');
        PRINT '=== ReadyStock: OriginType, ParentReadyStockId, OriginReferenceId, InTransitQuantity added ===';
    END
    ELSE PRINT '=== ReadyStock origin columns already exist -- no-op ===';

    IF OBJECT_ID('dbo.CK_ReadyStock_Origin', 'C') IS NULL
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD
            CONSTRAINT CK_ReadyStock_OriginType CHECK (OriginType IN (N''Sowing'', N''Transfer'', N''Transplant'')),
            CONSTRAINT CK_ReadyStock_Origin CHECK (
                (OriginType = N''Sowing'' AND ParentReadyStockId IS NULL AND OriginReferenceId IS NULL)
             OR (OriginType <> N''Sowing'' AND ParentReadyStockId IS NOT NULL AND OriginReferenceId IS NOT NULL AND ParentReadyStockId <> Id));');

    -- Computed availability now includes in-transit (both columns are only used by this table).
    IF NOT EXISTS (SELECT 1 FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'AvailableQuantity' AND definition LIKE '%InTransitQuantity%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN AvailableQuantity;');
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP COLUMN PhysicalQuantity;');
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD
            PhysicalQuantity AS (Quantity - DispatchedQuantity - InTransitQuantity) PERSISTED,
            AvailableQuantity AS (Quantity - ReservedQuantity - DispatchedQuantity - InTransitQuantity) PERSISTED;');
        PRINT '=== ReadyStock: Physical / Available now exclude in-transit ===';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStock_ReservedDispatched' AND definition LIKE '%InTransitQuantity%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_ReservedDispatched;');
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_ReservedDispatched CHECK (
            ReservedQuantity >= 0 AND DispatchedQuantity >= 0 AND InTransitQuantity >= 0
            AND ReservedQuantity + DispatchedQuantity + InTransitQuantity <= Quantity);');
    END
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStock_WholeQuantities' AND definition LIKE '%InTransitQuantity%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT CK_ReadyStock_WholeQuantities;');
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_WholeQuantities CHECK (
            Quantity = FLOOR(Quantity) AND ReservedQuantity = FLOOR(ReservedQuantity)
            AND DispatchedQuantity = FLOOR(DispatchedQuantity) AND InTransitQuantity = FLOOR(InTransitQuantity));');
    END

    -- One SOWING batch per sowing (derived batches keep the sowing reference).
    IF OBJECT_ID('dbo.UQ_ReadyStock_SeedSowing', 'UQ') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT UQ_ReadyStock_SeedSowing;');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'UX_ReadyStock_SowingBatch')
        EXEC (N'CREATE UNIQUE INDEX UX_ReadyStock_SowingBatch ON dbo.ReadyStock (SeedSowingId) WHERE OriginType = N''Sowing'';');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'UX_ReadyStock_Origin')
        EXEC (N'CREATE UNIQUE INDEX UX_ReadyStock_Origin ON dbo.ReadyStock (OriginType, OriginReferenceId) WHERE OriginType <> N''Sowing'';');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'IX_ReadyStock_Parent')
        EXEC (N'CREATE INDEX IX_ReadyStock_Parent ON dbo.ReadyStock (ParentReadyStockId) WHERE ParentReadyStockId IS NOT NULL;');

    -- The cavity equals the sowing's, except for a transplant output (new target cavity).
    IF COL_LENGTH('dbo.ReadyStock', 'SowingCavityKey') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD SowingCavityKey AS (CASE WHEN OriginType = N''Transplant'' THEN NULL ELSE CavityType END) PERSISTED;');
        IF OBJECT_ID('dbo.FK_ReadyStock_SowingCavity', 'F') IS NOT NULL
            EXEC (N'ALTER TABLE dbo.ReadyStock DROP CONSTRAINT FK_ReadyStock_SowingCavity;');
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT FK_ReadyStock_SowingCavity
            FOREIGN KEY (SeedSowingId, SowingCavityKey) REFERENCES dbo.SeedSowings (Id, CavityType);');
        PRINT '=== ReadyStock: sowing-cavity rule now skips transplant outputs ===';
    END
    IF OBJECT_ID('dbo.CK_ReadyStock_TransplantCavity', 'C') IS NULL
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_TransplantCavity
            CHECK (OriginType <> N''Transplant'' OR CavityType IN (N''9 Cavity'', N''24 Cavity'', N''42 Cavity'', N''102 Cavity'', N''150 Cavity''));');

    -- Whole trays only for Main Area SOWING batches.
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyStock_WholeTrays
ON dbo.ReadyStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(Quantity) RETURN;
    -- Only a NEW or CHANGED Quantity of a SOWING batch is tested. Received (''Transfer'') and
    -- transplanted (''Transplant'') batches keep exact plant counts (partial last tray allowed).
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.OriginType = N''Sowing''
                 AND (d.Id IS NULL OR d.Quantity <> i.Quantity)
                 AND i.Quantity % CASE i.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                    WHEN N''42 Cavity'' THEN 42 WHEN N''102 Cavity'' THEN 102
                                                    WHEN N''150 Cavity'' THEN 150 END <> 0)
        THROW 50125, ''Ready Stock quantity must be a whole number of trays of the sowing cavity.'', 1;
END');

    ------------------------------------------------------------------
    -- 2. Ledger types
    ------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type' AND definition LIKE '%TransplantIn%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStockTransactions DROP CONSTRAINT CK_ReadyStockTx_Type;');
        EXEC (N'ALTER TABLE dbo.ReadyStockTransactions ADD CONSTRAINT CK_ReadyStockTx_Type CHECK (TransactionType IN (
            N''Confirmed'', N''ReversalRemoval'', N''Transfer'', N''Reservation'', N''ReservationRelease'', N''Dispatch'', N''Wastage'',
            N''TransferHold'', N''TransferHoldRelease'', N''TransferOut'', N''TransferLoss'', N''TransferIn'',
            N''TransplantOut'', N''TransplantLoss'', N''TransplantIn'', N''ReversalReturn''));');
        PRINT '=== ReadyStockTransactions: transfer / transplant ledger types added ===';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_TrayStockTx_Type' AND definition LIKE '%Transplant%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.TrayStockTransactions DROP CONSTRAINT CK_TrayStockTx_Type;');
        EXEC (N'ALTER TABLE dbo.TrayStockTransactions ADD CONSTRAINT CK_TrayStockTx_Type CHECK (TransactionType IN (
            N''Allocation'', N''Sowing'', N''ReversalReturn'', N''Adjustment'', N''Transplant''));');
        PRINT '=== TrayStockTransactions: type Transplant added ===';
    END

    ------------------------------------------------------------------
    -- 3. dbo.InternalTransfers: 'MotherPlantTransfer'
    ------------------------------------------------------------------
    IF COL_LENGTH('dbo.InternalTransfers', 'DestinationPolyhouseId') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.InternalTransfers ADD
            DestinationPolyhouseId INT NULL CONSTRAINT FK_InternalTransfers_DestinationPolyhouse REFERENCES dbo.Polyhouses(Id),
            TransferDate DATE NULL,
            SubmissionToken UNIQUEIDENTIFIER NULL,
            CancelledById INT NULL CONSTRAINT FK_InternalTransfers_CancelledBy REFERENCES dbo.IMSUsers(Id),
            CancelledDate DATETIME2 NULL,
            CancelReason NVARCHAR(500) NULL;');
        PRINT '=== InternalTransfers: DestinationPolyhouseId, TransferDate, SubmissionToken, Cancelled* added ===';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'UX_InternalTransfers_SubmissionToken')
        EXEC (N'CREATE UNIQUE INDEX UX_InternalTransfers_SubmissionToken ON dbo.InternalTransfers (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType' AND definition LIKE '%MotherPlantTransfer%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_StockType;');
        EXEC (N'ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_StockType CHECK (StockType IN (
            N''EmptyPot'', N''PottedPlant'', N''Cutting'', N''MainOfficeIssue'', N''GrowingPartnerToOutlet'', N''ReadyStock'', N''MotherPlantTransfer''));');
        EXEC (N'ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_SourceMatchesStockType;');
        EXEC (N'ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
               StockType = N''EmptyPot'' AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
            OR StockType = N''PottedPlant'' AND SourcePottedPlantStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
            OR StockType = N''Cutting'' AND SourceCuttingStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL AND SourceReadyStockId IS NULL
            OR StockType = N''MainOfficeIssue'' AND SourcePottedPlantStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
            OR StockType = N''GrowingPartnerToOutlet'' AND SourcePottedPlantStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL AND SourceReadyStockId IS NULL
            OR StockType IN (N''ReadyStock'', N''MotherPlantTransfer'') AND SourceReadyStockId IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL);');
        PRINT '=== InternalTransfers: StockType MotherPlantTransfer allowed ===';
    END
    IF OBJECT_ID('dbo.CK_InternalTransfers_MotherPlantTransfer', 'C') IS NULL
        EXEC (N'ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_MotherPlantTransfer CHECK (
            StockType <> N''MotherPlantTransfer''
            OR (DestinationAreaId IS NOT NULL AND DestinationPolyhouseId IS NOT NULL AND TransferDate IS NOT NULL
                AND Status IN (N''PendingConfirmation'', N''Completed'', N''Rejected'', N''Cancelled'')
                AND (Status <> N''Completed'' OR (ConfirmedQuantity IS NOT NULL AND ConfirmedQuantity > 0 AND ConfirmedQuantity <= Quantity
                                                  AND ConfirmedBy IS NOT NULL AND ConfirmedDate IS NOT NULL
                                                  AND (ConfirmedQuantity = Quantity OR DiscrepancyReason IS NOT NULL)))
                AND (Status <> N''Rejected'' OR (ConfirmedBy IS NOT NULL AND ConfirmedDate IS NOT NULL AND DiscrepancyReason IS NOT NULL))
                AND (Status <> N''Cancelled'' OR (CancelledById IS NOT NULL AND CancelledDate IS NOT NULL AND CancelReason IS NOT NULL))));');

    ------------------------------------------------------------------
    -- 4. dbo.ReadyStockTransplants
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NULL
    BEGIN
        EXEC (N'
CREATE TABLE dbo.ReadyStockTransplants (
    Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReadyStockTransplants PRIMARY KEY,
    TransplantCode       NVARCHAR(30)  NOT NULL CONSTRAINT UQ_ReadyStockTransplants_Code UNIQUE,
    SourceReadyStockId   INT           NOT NULL CONSTRAINT FK_RsTransplants_Source REFERENCES dbo.ReadyStock(Id),
    OutputReadyStockId   INT           NULL     CONSTRAINT FK_RsTransplants_Output REFERENCES dbo.ReadyStock(Id),
    AreaId               INT           NOT NULL CONSTRAINT FK_RsTransplants_Area REFERENCES dbo.Area(Id),
    PolyhouseId          INT           NOT NULL CONSTRAINT FK_RsTransplants_Polyhouse REFERENCES dbo.Polyhouses(Id),
    TargetCavityType     NVARCHAR(20)  NOT NULL,
    SelectedQuantity     DECIMAL(18,2) NOT NULL,
    TransplantedQuantity DECIMAL(18,2) NOT NULL,
    LostQuantity         DECIMAL(18,2) NOT NULL,
    OutputTrays          INT           NOT NULL,
    TrayStockId          INT           NOT NULL CONSTRAINT FK_RsTransplants_TrayStock REFERENCES dbo.TrayStock(Id),
    TransplantDate       DATE          NOT NULL,
    Notes                NVARCHAR(500) NULL,
    Status               NVARCHAR(20)  NOT NULL CONSTRAINT DF_RsTransplants_Status DEFAULT N''Completed'',
    SubmissionToken      UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_ReadyStockTransplants_Token UNIQUE,
    CreatedById          INT           NULL     CONSTRAINT FK_RsTransplants_CreatedBy REFERENCES dbo.IMSUsers(Id),
    CreatedBy            NVARCHAR(256) NULL,
    CreatedAt            DATETIME2     NOT NULL CONSTRAINT DF_RsTransplants_CreatedAt DEFAULT SYSUTCDATETIME(),
    ReversedById         INT           NULL     CONSTRAINT FK_RsTransplants_ReversedBy REFERENCES dbo.IMSUsers(Id),
    ReversedAt           DATETIME2     NULL,
    ReversalReason       NVARCHAR(500) NULL,
    CONSTRAINT CK_RsTransplants_Cavity CHECK (TargetCavityType IN (N''9 Cavity'', N''24 Cavity'', N''42 Cavity'', N''102 Cavity'', N''150 Cavity'')),
    CONSTRAINT CK_RsTransplants_Quantities CHECK (
        TransplantedQuantity > 0 AND LostQuantity >= 0 AND SelectedQuantity = TransplantedQuantity + LostQuantity
        AND SelectedQuantity = FLOOR(SelectedQuantity) AND TransplantedQuantity = FLOOR(TransplantedQuantity) AND LostQuantity = FLOOR(LostQuantity)),
    -- Output trays = CEILING(transplanted / target cavity): exact plants, partial last tray allowed.
    CONSTRAINT CK_RsTransplants_Trays CHECK (OutputTrays = CEILING(TransplantedQuantity / CASE TargetCavityType
        WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24 WHEN N''42 Cavity'' THEN 42 WHEN N''102 Cavity'' THEN 102 WHEN N''150 Cavity'' THEN 150 END)),
    -- OutputReadyStockId is set in the SAME transaction right after the row is inserted (the output
    -- batch needs the Id of this row as its OriginReferenceId); UX_RsTransplants_Output keeps it 1:1.
    CONSTRAINT CK_RsTransplants_Status CHECK (
        (Status = N''Completed'' AND ReversedAt IS NULL AND ReversedById IS NULL AND ReversalReason IS NULL)
     OR (Status = N''Reversed'' AND OutputReadyStockId IS NOT NULL AND ReversedAt IS NOT NULL AND ReversedById IS NOT NULL AND ReversalReason IS NOT NULL)),
    CONSTRAINT CK_RsTransplants_NotItself CHECK (OutputReadyStockId IS NULL OR OutputReadyStockId <> SourceReadyStockId)
);');
        EXEC (N'CREATE INDEX IX_RsTransplants_Date ON dbo.ReadyStockTransplants (TransplantDate) INCLUDE (Status, TransplantedQuantity, LostQuantity);');
        EXEC (N'CREATE INDEX IX_RsTransplants_Source ON dbo.ReadyStockTransplants (SourceReadyStockId);');
        EXEC (N'CREATE UNIQUE INDEX UX_RsTransplants_Output ON dbo.ReadyStockTransplants (OutputReadyStockId) WHERE OutputReadyStockId IS NOT NULL;');
        PRINT '=== dbo.ReadyStockTransplants created ===';
    END
    ELSE PRINT '=== dbo.ReadyStockTransplants already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 5. Permissions + grants (least privilege)
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'ReadyStock.TransferToMotherPlant', N'Send Main Area Ready Stock to a Mother Plant Area (own Areas only)', 1),
        (N'ReadyStock.ReceiveTransfer',       N'Receive or reject Ready Stock sent to own Mother Plant Areas', 2),
        (N'Transplant.Enter',                 N'Record transplanting of Ready Stock into larger cavity trays (own Mother Plant Areas)', 3),
        (N'Transplant.View',                  N'View transplanting history and stock lineage', 4),
        (N'Transplant.Reverse',               N'Reverse a transplanting whose output has not been used (full-access users only)', 5),
        (N'TrayStock.EnterMotherPlant',       N'Add empty trays to the Tray Stock of any active Mother Plant Area polyhouse', 6);
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

    DECLARE @Grants TABLE (RoleName NVARCHAR(100), Code NVARCHAR(200));
    INSERT INTO @Grants VALUES
        (N'Sowing Supervisor', N'ReadyStock.TransferToMotherPlant'),
        (N'Office Coordinator', N'ReadyStock.TransferToMotherPlant'),
        (N'Mother Plant Supervisor', N'ReadyStock.ReceiveTransfer'),
        (N'Mother Plant Supervisor', N'Transplant.Enter'),
        (N'Mother Plant Supervisor', N'Transplant.View'),
        (N'Office Coordinator', N'TrayStock.EnterMotherPlant'),
        (N'Booking Executive', N'TrayStock.EnterMotherPlant');
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM @Grants g
    JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = g.RoleName
    JOIN dbo.Permissions p ON p.Code = g.Code
    WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Grants inserted: ', @@ROWCOUNT, ' (expect 7 on first run, 0 on re-run)');

    PRINT '=== After ===';
    SELECT 'ReadyStock' AS TableName, COUNT(*) AS Rows, SUM(Quantity) AS Qty, SUM(ReservedQuantity) AS Reserved, SUM(DispatchedQuantity) AS Dispatched FROM dbo.ReadyStock
    UNION ALL SELECT 'ReadyStockTransactions', COUNT(*), SUM(Quantity), NULL, NULL FROM dbo.ReadyStockTransactions
    UNION ALL SELECT 'InternalTransfers', COUNT(*), SUM(Quantity), NULL, NULL FROM dbo.InternalTransfers
    UNION ALL SELECT 'TrayStock', COUNT(*), SUM(PhysicalQuantity), NULL, NULL FROM dbo.TrayStock;
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code IN (SELECT Code FROM @Perms) ORDER BY RoleName, p.Code;
    EXEC (N'SELECT OriginType, COUNT(*) AS Batches, SUM(InTransitQuantity) AS InTransit FROM dbo.ReadyStock GROUP BY OriginType;');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RsTransplant2026_10_09;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RsTransplant2026_10_09;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RsTransplant2026_10_09;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
