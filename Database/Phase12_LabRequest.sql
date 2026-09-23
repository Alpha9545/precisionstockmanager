/* ============================================================
   Phase 12: Lab Request
   ------------------------------------------------------------
   Business-rule scope (confirmed with the user before writing this
   migration -- the "full loop" option): a Lab Request sends a
   quantity of existing dbo.PottedPlantStock out to a lab for
   multiplication/processing, and later records however much comes
   back -- which is deliberately independent of (usually greater
   than) what was sent, e.g. 10-15 sent, 30-35 received a few days
   later. Both movements post against the SAME pool's existing
   ledger (dbo.PottedPlantStockTransactions, Phase 7), continuing
   Decision 1 exactly as Phases 8-10 did -- no new ledger table:
     - Sending the request writes a 'LabSent' entry (negative delta,
       reduces PhysicalQuantity -- real stock left the building).
       RecordTransactionAsync's existing "after >= Reserved" check
       means a request can never send more than the pool's own
       Available quantity, satisfying the project's "never exceed
       available" rule for free.
     - Recording a result writes a 'LabReceived' entry (positive
       delta, increases PhysicalQuantity by the RECEIVED quantity,
       not the sent quantity -- this is exactly how the multiplication
       is captured).
   Widens CK_PottedStockTx_Type to add these two values, exactly like
   Phase 8 widened CK_EmptyPotInvTx_Type for 'Transfer'.

   No prefix for Lab Request was in the originally-approved list
   (MP/CUT/AC/CD/PROP/POT/TR/BK/DIS/PO stopped at Phase 11) -- "LAB"
   is used here as the obvious, self-evident extension via the same
   generic dbo.BatchNumberSequences mechanism (BatchNumberRepository
   takes any prefix), not a business-rule choice, so it wasn't put to
   a question.

   No separate "Lab" master table is created -- LabName is recorded
   as free text on the request itself (no lab-vendor relationship was
   requested, and this avoids over-building a master nothing asked
   for, matching how dbo.VendorPurchases originally modeled VendorName
   as text before this project's own Vendor master existed).

   ADDITIVE ONLY. Run this AFTER Phase11_PurchaseOrderReceipt.sql has
   been applied. Take a backup first per your own DB safety rules.
   ============================================================ */

-- ---------- Widen CK_PottedStockTx_Type for 'LabSent' / 'LabReceived' ----------
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type')
BEGIN
    ALTER TABLE dbo.PottedPlantStockTransactions DROP CONSTRAINT CK_PottedStockTx_Type;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type')
BEGIN
    ALTER TABLE dbo.PottedPlantStockTransactions
        ADD CONSTRAINT CK_PottedStockTx_Type CHECK (TransactionType IN
            ('Production', 'Reservation', 'ReservationRelease', 'Dispatch', 'Wastage', 'Transfer', 'Adjustment', 'ReversalRemoval', 'LabSent', 'LabReceived'));
END
GO

-- ---------- dbo.LabRequests ----------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LabRequests' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LabRequests
    (
        Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabRequests PRIMARY KEY,
        LabRequestCode          NVARCHAR(20)   NOT NULL,

        -- The pool the sample is taken from AND returned to -- both
        -- movements are against this same (SpeciesId, PotSize, AreaId)
        -- identity, since this models in-house multiplication of
        -- existing stock, not a transfer to a different pool.
        PottedPlantStockId      INT            NOT NULL,
        -- Denormalized copies, always derived server-side from the
        -- locked stock row -- never trusted from the caller. See
        -- fn_LabRequests_MatchesStock below.
        SpeciesId               INT            NOT NULL,
        PotSize                 NVARCHAR(50)   NOT NULL,
        AreaId                  INT            NULL,

        LabName                 NVARCHAR(200)  NOT NULL,

        SentDate                DATETIME2      NOT NULL CONSTRAINT DF_LabRequests_SentDate DEFAULT (SYSUTCDATETIME()),
        SentQuantity            DECIMAL(18,2)  NOT NULL,
        ExpectedResultDate      DATE           NULL,

        -- 'Sent' | 'Completed' | 'Cancelled'.
        Status                  NVARCHAR(30)   NOT NULL CONSTRAINT DF_LabRequests_Status DEFAULT ('Sent'),

        ReceivedDate            DATETIME2      NULL,
        -- Deliberately independent of SentQuantity -- a lab multiplies
        -- stock, it doesn't return the same amount it was given.
        ReceivedQuantity        DECIMAL(18,2)  NULL,
        ResultNotes             NVARCHAR(500)  NULL,

        ResponsiblePersonId     INT            NULL,
        Remarks                 NVARCHAR(500)  NULL,

        CreatedDate             DATETIME2      NOT NULL CONSTRAINT DF_LabRequests_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy               NVARCHAR(100)  NULL,
        ModifiedDate            DATETIME2      NULL,
        ModifiedBy              NVARCHAR(100)  NULL,

        CONSTRAINT UQ_LabRequests_Code           UNIQUE (LabRequestCode),
        CONSTRAINT FK_LabRequests_Stock          FOREIGN KEY (PottedPlantStockId) REFERENCES dbo.PottedPlantStock(Id),
        CONSTRAINT FK_LabRequests_Area           FOREIGN KEY (AreaId)             REFERENCES dbo.Area(Id),
        CONSTRAINT FK_LabRequests_Responsible    FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_LabRequests_Status         CHECK (Status IN ('Sent', 'Completed', 'Cancelled')),
        CONSTRAINT CK_LabRequests_SentQuantity   CHECK (SentQuantity > 0),
        CONSTRAINT CK_LabRequests_ReceivedQuantity CHECK (ReceivedQuantity IS NULL OR ReceivedQuantity > 0),
        -- Completed requires the result fields; Sent/Cancelled must not
        -- have them yet (mirrors CancelAsync never back-filling results).
        CONSTRAINT CK_LabRequests_ResultConsistency CHECK (
            (Status = 'Completed' AND ReceivedDate IS NOT NULL AND ReceivedQuantity IS NOT NULL)
            OR (Status IN ('Sent', 'Cancelled') AND ReceivedDate IS NULL AND ReceivedQuantity IS NULL)
        )
    );
    CREATE INDEX IX_LabRequests_Stock  ON dbo.LabRequests(PottedPlantStockId);
    CREATE INDEX IX_LabRequests_Status ON dbo.LabRequests(Status);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_LabRequests_MatchesStock' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_LabRequests_MatchesStock(@PottedPlantStockId INT, @SpeciesId INT, @PotSize NVARCHAR(50), @AreaId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.PottedPlantStock
            WHERE Id = @PottedPlantStockId
              AND SpeciesId = @SpeciesId
              AND PotSize = @PotSize
              AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_LabRequests_MatchesStock')
BEGIN
    ALTER TABLE dbo.LabRequests
        ADD CONSTRAINT CK_LabRequests_MatchesStock
        CHECK (dbo.fn_LabRequests_MatchesStock(PottedPlantStockId, SpeciesId, PotSize, AreaId) = 1);
END
GO
