/* ============================================================
   Phase 13: Labour Log
   ------------------------------------------------------------
   A pure record-keeping module -- no stock table is touched by
   anything here (labour hours/days worked are not a stock quantity,
   so there is no ledger to post against and no "never negative
   stock" rule that applies). WorkerId/SupervisorId FK to IMSUsers,
   continuing this project's existing convention exactly: every
   earlier phase already comments its own "responsible person" field
   as "Labour / responsible person, IMSUsers.Id" (see e.g.
   Phase6_PropagationBatch.sql's ResponsiblePersonId), so field labour
   here is represented as IMSUsers rows too rather than introducing a
   brand new "casual worker" master nothing in the project asked for
   or already does differently.

   ReferenceType/ReferenceId is an OPTIONAL, loosely-typed pointer to
   whatever job the labour was for (a CuttingPlan, PropagationBatch,
   PotProduction, InternalTransfer, Dispatch, or anything else) -- no
   FK is enforced against a specific table, matching how the stock
   ledgers themselves (e.g. dbo.PottedPlantStockTransactions) already
   treat ReferenceType/ReferenceId as a loose pointer rather than a
   hard per-table FK.

   No prefix for Labour Log was in the originally-approved list
   (MP/CUT/AC/CD/PROP/POT/TR/BK/DIS/PO stopped at Phase 11) -- "LBR"
   is used here as the obvious, self-evident extension via the same
   generic dbo.BatchNumberSequences mechanism, exactly like "LAB" was
   for Phase 12 -- a mechanical naming choice, not a business rule.

   TotalWage (= WageRate * UnitsWorked) is computed and stored
   server-side at save time rather than as a SQL computed column, so
   a later change to a worker's wage rate never silently rewrites the
   historical amount actually paid for a past log entry.

   ADDITIVE ONLY. Run this AFTER Phase12_LabRequest.sql has been
   applied. Take a backup first per your own DB safety rules.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LabourLogs' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LabourLogs
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabourLogs PRIMARY KEY,
        LabourLogCode       NVARCHAR(20)   NOT NULL,

        WorkerId            INT            NOT NULL,
        WorkDate            DATE           NOT NULL,
        AreaId              INT            NULL,
        WorkType            NVARCHAR(100)  NOT NULL,

        -- Optional, loosely-typed pointer to the job this labour was
        -- for -- see header comment. ReferenceCode is a denormalized
        -- display label (e.g. the batch code) so the log reads
        -- sensibly without joining to a dozen different tables.
        ReferenceType       NVARCHAR(50)   NULL,
        ReferenceId         INT            NULL,
        ReferenceCode       NVARCHAR(20)   NULL,

        WageType            NVARCHAR(20)   NOT NULL,
        WageRate            DECIMAL(18,2)  NOT NULL,
        UnitsWorked         DECIMAL(5,2)   NOT NULL,
        TotalWage           DECIMAL(18,2)  NOT NULL,

        SupervisorId        INT            NULL,

        Status              NVARCHAR(20)   NOT NULL CONSTRAINT DF_LabourLogs_Status DEFAULT ('Recorded'),
        Remarks             NVARCHAR(500)  NULL,

        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_LabourLogs_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_LabourLogs_Code       UNIQUE (LabourLogCode),
        CONSTRAINT FK_LabourLogs_Worker     FOREIGN KEY (WorkerId)     REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_LabourLogs_Area       FOREIGN KEY (AreaId)       REFERENCES dbo.Area(Id),
        CONSTRAINT FK_LabourLogs_Supervisor FOREIGN KEY (SupervisorId) REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_LabourLogs_WageType     CHECK (WageType IN ('Daily', 'Hourly')),
        CONSTRAINT CK_LabourLogs_WageRate     CHECK (WageRate > 0),
        CONSTRAINT CK_LabourLogs_UnitsWorked  CHECK (UnitsWorked > 0),
        CONSTRAINT CK_LabourLogs_TotalWage    CHECK (TotalWage >= 0),
        CONSTRAINT CK_LabourLogs_Status       CHECK (Status IN ('Recorded', 'Cancelled'))
    );
    CREATE INDEX IX_LabourLogs_Worker   ON dbo.LabourLogs(WorkerId);
    CREATE INDEX IX_LabourLogs_WorkDate ON dbo.LabourLogs(WorkDate);
    CREATE INDEX IX_LabourLogs_Area     ON dbo.LabourLogs(AreaId);
END
GO
