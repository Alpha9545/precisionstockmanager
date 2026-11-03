-- ============================================================================
-- 2026-11-03_FieldWorkExpenses.sql          *** PREPARED FOR REVIEW -- NOT APPLIED to PlantsIMS2_Test (local or live) ***
--
-- WHY (owner requirement 2026-11-03, Field Work module Phase 4, simple version): a field employee records an expense
--   (category, amount, date, optional note, ONE live bill photo); a field manager approves or rejects it. No automatic
--   per-km claim, no rates, limits or settings.
--
-- WHAT (ADDS ONLY, except the two changes to dbo.FieldPhotos below):
--   dbo.FieldExpenses        one row per expense. Status Submitted -> Approved / Rejected / Cancelled, nothing else; the
--                            content (date, category, amount, note, bill photo) never changes (no edit: cancel and add again).
--                            Never deleted. SubmissionToken UNIQUE (double submit); BillPhotoId UNIQUE (one photo, one expense).
--   dbo.FieldExpenseEvents   who did what and when (Submitted / Cancelled / Approved / Rejected, with the comment). Append-only.
--   Triggers: TR_FieldExpenses_Guard (bill photo owner + purpose, no future date, content immutable, only Submitted moves
--             once), TR_FieldExpenseEvents_Guard (append-only).
--   dbo.FieldPhotos (Phase 1/2, ours): CK_FieldPhotos_Purpose also allows 'ExpenseBill'; CK_FieldPhotos_ConsentReminder also
--     requires the reminder version for 'ExpenseBill' ("photograph only the bill, no people or card numbers"), and is now
--     NULL-safe: the Phase 2 version (... OR ConsentReminderVersion >= 1) let a NULL version through, because a CHECK passes
--     on UNKNOWN. Re-adding it WITH CHECK refuses (nothing changes) if any customer / visit photo already has no version.
--   No permission, role or grant changes: Phase 4 uses FieldWork.Day / FieldWork.Manage from 2026-11-03_FieldWorkDays.sql.
--   "A manager cannot approve their own expense (an administrator can)" is checked by the application under the row lock:
--   who is an administrator comes from configuration (Authorization:FullAccessRoleNames), which SQL cannot see.
--
-- APPLICATION WITHOUT THIS SCRIPT: Data/FieldExpenseSchema.cs reports "not ready": the expense pages explain that the
--   update is missing (and refuse every action); Phases 1-3 work as before.
--
-- TARGET SAFEGUARDS (as 2026-11-03_FieldWorkDays.sql): the first batch refuses to continue unless ALL of these hold:
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is DRYRUN or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--   Predecessors: CK_FieldPhotos_Purpose and CK_FieldPhotos_ConsentReminder must be EXACTLY the
--   2026-11-03_FieldWorkCustomers.sql versions (SHA-256 of their definitions), or already this script's versions; anything
--   else = schema drift, nothing changes.
--
-- OPERATOR CHECKLIST: identity query, independent machine check, COPY_ONLY backup, DRYRUN first; COMMIT only with the owner's
--   explicit approval for that database:
--   sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN 'TARGET_SERVER="<ServerName>"' 'TARGET_MACHINE="<MachineName>"'
--             'TARGET_DB="<DatabaseName>"' 'TARGET_DB_CREATED="<DatabaseCreated>"' -i 2026-11-03_FieldWorkExpenses.sql
--
-- Requires: 2026-11-03_FieldWorkDays.sql and 2026-11-03_FieldWorkCustomers.sql. One transaction. Idempotent (a re-run
--   reports "already exists" and changes nothing).
-- Undo: 2026-11-03_FieldWorkExpenses_Rollback.sql (refused once any expense or bill photo exists).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54401, '2026-11-03_FieldWorkExpenses.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54402, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54403, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54404, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54405, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54406, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54407, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION FieldWorkExpenses1103;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    ------------------------------------------------------------------
    -- 0. prerequisites and predecessors
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldDays', 'U') IS NULL OR OBJECT_ID('dbo.FieldPhotos', 'U') IS NULL OR OBJECT_ID('dbo.FieldCustomers', 'U') IS NULL
       OR COL_LENGTH('dbo.FieldPhotos', 'ConsentReminderVersion') IS NULL OR OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder', 'C') IS NULL
        THROW 54408, 'Run 2026-11-03_FieldWorkDays.sql and 2026-11-03_FieldWorkCustomers.sql first.', 1;
    DECLARE @PurposeDef NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose'));
    DECLARE @PurposeIsOurs BIT = CASE WHEN @PurposeDef LIKE N'%ExpenseBill%' THEN 1 ELSE 0 END;
    IF @PurposeIsOurs = 0 AND ISNULL(CONVERT(CHAR(64), HASHBYTES('SHA2_256', @PurposeDef), 2), '') <> '7F643662666D8A5DFF56A919054CC3621646811A546D20737DEB0A58674A4B55'
        THROW 54409, 'CK_FieldPhotos_Purpose is not the 2026-11-03_FieldWorkCustomers.sql version -- schema drift, nothing changed.', 1;
    DECLARE @ConsentDef NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder'));
    DECLARE @ConsentIsOurs BIT = CASE WHEN @ConsentDef LIKE N'%ExpenseBill%' THEN 1 ELSE 0 END;
    IF @ConsentIsOurs = 0 AND ISNULL(CONVERT(CHAR(64), HASHBYTES('SHA2_256', @ConsentDef), 2), '') <> '04EC4934B24EF54CA562C5EEFB9F7D197216306841CF8F9D778BDF61060C826D'
        THROW 54410, 'CK_FieldPhotos_ConsentReminder is not the 2026-11-03_FieldWorkCustomers.sql version -- schema drift, nothing changed.', 1;

    PRINT '=== Before: row counts (no existing row may change) ===';
    SELECT 'IMSUsers' AS TableName, COUNT(*) AS Rows FROM dbo.IMSUsers
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'FieldDays', COUNT(*) FROM dbo.FieldDays
    UNION ALL SELECT 'FieldCustomers', COUNT(*) FROM dbo.FieldCustomers
    UNION ALL SELECT 'FieldPhotos', COUNT(*) FROM dbo.FieldPhotos;

    ------------------------------------------------------------------
    -- 1. dbo.FieldPhotos: purpose 'ExpenseBill' + its reminder version
    ------------------------------------------------------------------
    IF @PurposeIsOurs = 0
    BEGIN
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_Purpose;
        ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_Purpose
            CHECK (Purpose IN ('DayStart', 'DayEnd', 'CustomerPhoto', 'VisitPhoto', 'ExpenseBill'));
        PRINT '=== CK_FieldPhotos_Purpose now allows ExpenseBill ===';
    END
    ELSE PRINT '=== CK_FieldPhotos_Purpose already allows ExpenseBill -- no-op ===';

    IF @ConsentIsOurs = 0
    BEGIN
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_ConsentReminder;
        ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_ConsentReminder
            CHECK (Purpose NOT IN ('CustomerPhoto', 'VisitPhoto', 'ExpenseBill') OR ISNULL(ConsentReminderVersion, 0) >= 1);
        PRINT '=== CK_FieldPhotos_ConsentReminder now also covers ExpenseBill ===';
    END
    ELSE PRINT '=== CK_FieldPhotos_ConsentReminder already covers ExpenseBill -- no-op ===';

    ------------------------------------------------------------------
    -- 2. dbo.FieldExpenses
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldExpenses', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldExpenses (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldExpenses PRIMARY KEY,
            UserId            INT              NOT NULL CONSTRAINT FK_FieldExpenses_User REFERENCES dbo.IMSUsers(Id),
            ExpenseDate       DATE             NOT NULL,
            Category          VARCHAR(10)      NOT NULL CONSTRAINT CK_FieldExpenses_Category
                                               CHECK (Category IN ('Petrol', 'Food', 'Lodging', 'Toll', 'Parking', 'Other')),
            Amount            DECIMAL(10,2)    NOT NULL CONSTRAINT CK_FieldExpenses_Amount CHECK (Amount > 0 AND Amount <= 100000),
            Note              NVARCHAR(200)    NULL,
            BillPhotoId       INT              NOT NULL CONSTRAINT FK_FieldExpenses_BillPhoto REFERENCES dbo.FieldPhotos(Id)
                                               CONSTRAINT UQ_FieldExpenses_BillPhoto UNIQUE,
            Status            VARCHAR(10)      NOT NULL CONSTRAINT DF_FieldExpenses_Status DEFAULT ('Submitted')
                                               CONSTRAINT CK_FieldExpenses_Status CHECK (Status IN ('Submitted', 'Approved', 'Rejected', 'Cancelled')),
            CancelReason      NVARCHAR(200)    NULL,
            CancelledAtUtc    DATETIME2(0)     NULL,
            DecidedByUserId   INT              NULL CONSTRAINT FK_FieldExpenses_DecidedBy REFERENCES dbo.IMSUsers(Id),
            DecidedAtUtc      DATETIME2(0)     NULL,
            DecisionComment   NVARCHAR(300)    NULL,
            SubmissionToken   UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_FieldExpenses_Token UNIQUE,
            CreatedAtUtc      DATETIME2(0)     NOT NULL CONSTRAINT DF_FieldExpenses_Created DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_FieldExpenses_Note CHECK (Category <> 'Other' OR LEN(LTRIM(RTRIM(ISNULL(Note, N'')))) > 0),
            CONSTRAINT CK_FieldExpenses_Cancel CHECK (
                (Status = 'Cancelled' AND CancelledAtUtc IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(CancelReason, N'')))) > 0)
                OR (Status <> 'Cancelled' AND CancelledAtUtc IS NULL AND CancelReason IS NULL)),
            CONSTRAINT CK_FieldExpenses_Decision CHECK (
                (Status IN ('Approved', 'Rejected') AND DecidedByUserId IS NOT NULL AND DecidedAtUtc IS NOT NULL)
                OR (Status NOT IN ('Approved', 'Rejected') AND DecidedByUserId IS NULL AND DecidedAtUtc IS NULL AND DecisionComment IS NULL)),
            CONSTRAINT CK_FieldExpenses_RejectComment CHECK (Status <> 'Rejected' OR LEN(LTRIM(RTRIM(ISNULL(DecisionComment, N'')))) > 0)
        );
        CREATE INDEX IX_FieldExpenses_User_Date ON dbo.FieldExpenses (UserId, ExpenseDate) INCLUDE (Category, Amount, Status);
        CREATE INDEX IX_FieldExpenses_Date ON dbo.FieldExpenses (ExpenseDate) INCLUDE (UserId, Category, Amount, Status);
        PRINT '=== dbo.FieldExpenses created ===';
    END
    ELSE PRINT '=== dbo.FieldExpenses already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 3. dbo.FieldExpenseEvents
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldExpenseEvents', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldExpenseEvents (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldExpenseEvents PRIMARY KEY,
            ExpenseId   INT            NOT NULL CONSTRAINT FK_FieldExpenseEvents_Expense REFERENCES dbo.FieldExpenses(Id),
            Action      VARCHAR(10)    NOT NULL CONSTRAINT CK_FieldExpenseEvents_Action CHECK (Action IN ('Submitted', 'Cancelled', 'Approved', 'Rejected')),
            FromStatus  VARCHAR(10)    NULL,
            ToStatus    VARCHAR(10)    NOT NULL,
            ByUserId    INT            NOT NULL CONSTRAINT FK_FieldExpenseEvents_By REFERENCES dbo.IMSUsers(Id),
            AtUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldExpenseEvents_At DEFAULT (SYSUTCDATETIME()),
            Comment     NVARCHAR(300)  NULL
        );
        CREATE INDEX IX_FieldExpenseEvents_Expense ON dbo.FieldExpenseEvents (ExpenseId, Id);
        PRINT '=== dbo.FieldExpenseEvents created ===';
    END
    ELSE PRINT '=== dbo.FieldExpenseEvents already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 4. triggers
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.TR_FieldExpenses_Guard', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_FieldExpenses_Guard ON dbo.FieldExpenses
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    -- 2026-11-03_FieldWorkExpenses.sql: the bill photo belongs to the expense''s employee and was taken as a bill.
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN dbo.FieldPhotos p ON p.Id = i.BillPhotoId
               WHERE p.Id IS NULL OR p.OwnerUserId <> i.UserId OR p.Purpose <> ''ExpenseBill'')
        THROW 54451, ''The bill photo does not belong to this expense.'', 1;

    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- a new expense starts Submitted and is never dated after today (India date)
        IF EXISTS (SELECT 1 FROM inserted WHERE Status <> ''Submitted'' OR ExpenseDate > CAST(DATEADD(MINUTE, 330, SYSUTCDATETIME()) AS DATE))
            THROW 54452, ''A new expense must be Submitted and cannot be dated in the future.'', 1;
        RETURN;
    END

    -- the content of an expense never changes (no edit: cancel it and add a new one)
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.UserId <> d.UserId OR i.ExpenseDate <> d.ExpenseDate OR i.Category <> d.Category OR i.Amount <> d.Amount
                  OR CAST(ISNULL(i.Note, N'''') AS VARBINARY(400)) <> CAST(ISNULL(d.Note, N'''') AS VARBINARY(400))
                  OR i.BillPhotoId <> d.BillPhotoId OR i.SubmissionToken <> d.SubmissionToken OR i.CreatedAtUtc <> d.CreatedAtUtc)
        THROW 54453, ''An expense cannot be edited. Cancel it and add a new one.'', 1;

    -- only a Submitted expense changes, and only to Approved / Rejected / Cancelled (once; those are final)
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id WHERE d.Status <> ''Submitted'' OR i.Status = ''Submitted'')
        THROW 54454, ''This expense was already approved, rejected or cancelled.'', 1;
END');
        PRINT '=== TR_FieldExpenses_Guard created ===';
    END
    ELSE PRINT '=== TR_FieldExpenses_Guard already exists -- no-op ===';

    IF OBJECT_ID('dbo.TR_FieldExpenseEvents_Guard', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_FieldExpenseEvents_Guard ON dbo.FieldExpenseEvents
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54460, ''The expense history is append-only.'', 1;
END');
        PRINT '=== TR_FieldExpenseEvents_Guard created ===';
    END
    ELSE PRINT '=== TR_FieldExpenseEvents_Guard already exists -- no-op ===';

    PRINT '=== After: new objects (expect 2 tables, 2 triggers, 2 photo checks) ===';
    SELECT name, type_desc FROM sys.objects
    WHERE name IN (N'FieldExpenses', N'FieldExpenseEvents', N'TR_FieldExpenses_Guard', N'TR_FieldExpenseEvents_Guard',
                   N'CK_FieldPhotos_Purpose', N'CK_FieldPhotos_ConsentReminder')
    ORDER BY type_desc, name;
    SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose')) AS PurposeCheck,
           OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder')) AS ConsentReminderCheck,
           (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID('dbo.FieldExpenses'), OBJECT_ID('dbo.FieldExpenseEvents'), OBJECT_ID('dbo.FieldPhotos'))
                AND is_not_trusted = 1) AS UntrustedChecks;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkExpenses1103;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkExpenses1103;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkExpenses1103;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
