-- ============================================================================
-- 2026-11-03_FieldWorkCustomers.sql          *** PREPARED FOR REVIEW -- NOT APPLIED to PlantsIMS2_Test (local or live) ***
--
-- WHY (owner requirement 2026-11-03, Field Work module Phase 2): field employees keep their own customers / leads, log
--   visits (GPS check-in linked to today's Field Day, items given, live photos), calls / notes and follow-ups; managers
--   see every customer, reassign (also in bulk), archive and correct visits.
--
-- WHAT (ADDS ONLY, except the two changes to Phase 1's own dbo.FieldPhotos below):
--   dbo.FieldCustomers       customers / leads (phone stored as digits; PhoneKey = last 10 digits for duplicate warnings,
--                            deliberately NOT unique). Archive instead of delete (IsActive).
--   dbo.FieldVisits          GPS check-ins, each linked to the employee's OPEN Field Day (FieldDayId) -- Phase 3 builds
--                            the day route from these rows
--   dbo.FieldVisitItems      items given at a visit ("Rose sample" x 10)
--   dbo.FieldVisitPhotos     visit -> dbo.FieldPhotos (Purpose 'VisitPhoto')
--   dbo.FieldCustomerNotes   Call / Note entries (no GPS) and Corrections to a visit (append-only text)
--   dbo.FieldFollowUps       Pending / Done / Cancelled; at most ONE Pending per customer (filtered unique index)
--   dbo.FieldCustomerEvents  audit: created / updated / reassigned / status changes / archive / visit edits / follow-ups
--   Triggers: TR_FieldVisits_Guard, TR_FieldVisitPhotos_Guard, TR_FieldCustomers_Guard, TR_FieldCustomerNotes_Guard,
--             TR_FieldFollowUps_Guard, TR_FieldCustomerEvents_Guard (see each one)
--   dbo.FieldPhotos (Phase 1, ours): CK_FieldPhotos_Purpose also allows 'CustomerPhoto' and 'VisitPhoto'; new nullable
--     column ConsentReminderVersion, required for those two purposes (the "take the customer's permission" reminder
--     version that was shown on the capture screen).
--   No permission, role or grant changes: Phase 2 uses FieldWork.Day / FieldWork.Manage from 2026-11-03_FieldWorkDays.sql.
--
-- LATER LINK TO A MAIN CUSTOMER RECORD: dbo.FieldCustomers is standalone (the database has no customer master today). A
--   later migration can add a NULLABLE column (e.g. MainCustomerId + FK) without changing any existing row; no code or
--   constraint here depends on its absence.
--
-- APPLICATION WITHOUT THIS SCRIPT: Data/FieldCustomerSchema.cs reports "not ready": the customer / visit / follow-up
--   pages explain that the update is missing, the follow-up badge is hidden, and Phase 1 works as before.
--
-- TARGET SAFEGUARDS (as 2026-11-03_FieldWorkDays.sql): the first batch refuses to continue unless ALL of these hold:
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is DRYRUN or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--   Predecessor: CK_FieldPhotos_Purpose must be EXACTLY the 2026-11-03_FieldWorkDays.sql version (SHA-256 of its
--   definition), or already this script's version; anything else = schema drift, nothing changes.
--
-- OPERATOR CHECKLIST: as 2026-11-03_FieldWorkDays.sql (identity query, independent machine check, COPY_ONLY backup,
--   DRYRUN first; COMMIT only with the owner's explicit approval for that database):
--   sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN 'TARGET_SERVER="<ServerName>"' 'TARGET_MACHINE="<MachineName>"'
--             'TARGET_DB="<DatabaseName>"' 'TARGET_DB_CREATED="<DatabaseCreated>"' -i 2026-11-03_FieldWorkCustomers.sql
--
-- Requires: 2026-11-03_FieldWorkDays.sql. One transaction. Idempotent (a re-run reports "already exists" and changes nothing).
-- Undo: 2026-11-03_FieldWorkCustomers_Rollback.sql (refused once any Phase 2 row or customer / visit photo exists).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54201, '2026-11-03_FieldWorkCustomers.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54202, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54203, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54204, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54205, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54206, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54207, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
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

BEGIN TRANSACTION FieldWorkCustomers1103;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';

    ------------------------------------------------------------------
    -- 0. prerequisites and predecessor
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldDays', 'U') IS NULL OR OBJECT_ID('dbo.FieldPhotos', 'U') IS NULL OR OBJECT_ID('dbo.TR_FieldDays_Guard', 'TR') IS NULL
        THROW 54208, 'Run 2026-11-03_FieldWorkDays.sql first (dbo.FieldDays / dbo.FieldPhotos).', 1;
    DECLARE @PurposeDef NVARCHAR(MAX) = OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose'));
    DECLARE @PurposeHash CHAR(64) = CONVERT(CHAR(64), HASHBYTES('SHA2_256', @PurposeDef), 2);
    DECLARE @PurposeIsOurs BIT = CASE WHEN @PurposeDef LIKE N'%CustomerPhoto%' AND @PurposeDef LIKE N'%VisitPhoto%' THEN 1 ELSE 0 END;
    IF @PurposeIsOurs = 0 AND ISNULL(@PurposeHash, '') <> '851B7E5A5E96ACC963F2B5C6E403BFA2D04A5B1509C6D4526DC899AD59C41ECE'
        THROW 54209, 'CK_FieldPhotos_Purpose is not the 2026-11-03_FieldWorkDays.sql version -- schema drift, nothing changed.', 1;

    PRINT '=== Before: row counts (no existing row may change) ===';
    SELECT 'IMSUsers' AS TableName, COUNT(*) AS Rows FROM dbo.IMSUsers
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'FieldDays', COUNT(*) FROM dbo.FieldDays
    UNION ALL SELECT 'FieldPhotos', COUNT(*) FROM dbo.FieldPhotos;

    ------------------------------------------------------------------
    -- 1. dbo.FieldPhotos: purposes + consent reminder version
    ------------------------------------------------------------------
    IF COL_LENGTH('dbo.FieldPhotos', 'ConsentReminderVersion') IS NULL
    BEGIN
        ALTER TABLE dbo.FieldPhotos ADD ConsentReminderVersion INT NULL;
        PRINT '=== dbo.FieldPhotos.ConsentReminderVersion added ===';
    END
    ELSE PRINT '=== dbo.FieldPhotos.ConsentReminderVersion already exists -- no-op ===';

    IF @PurposeIsOurs = 0
    BEGIN
        ALTER TABLE dbo.FieldPhotos DROP CONSTRAINT CK_FieldPhotos_Purpose;
        ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_Purpose
            CHECK (Purpose IN ('DayStart', 'DayEnd', 'CustomerPhoto', 'VisitPhoto'));
        PRINT '=== CK_FieldPhotos_Purpose now allows CustomerPhoto / VisitPhoto ===';
    END
    ELSE PRINT '=== CK_FieldPhotos_Purpose already allows CustomerPhoto / VisitPhoto -- no-op ===';

    IF OBJECT_ID('dbo.CK_FieldPhotos_ConsentReminder', 'C') IS NULL
    BEGIN
        -- dynamic: the column was added in this same batch
        EXEC (N'ALTER TABLE dbo.FieldPhotos WITH CHECK ADD CONSTRAINT CK_FieldPhotos_ConsentReminder
                CHECK (Purpose NOT IN (''CustomerPhoto'', ''VisitPhoto'') OR ConsentReminderVersion >= 1);');
        PRINT '=== CK_FieldPhotos_ConsentReminder added ===';
    END
    ELSE PRINT '=== CK_FieldPhotos_ConsentReminder already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 2. dbo.FieldCustomers
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldCustomers', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldCustomers (
            Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldCustomers PRIMARY KEY,
            Name                NVARCHAR(150)  NOT NULL CONSTRAINT CK_FieldCustomers_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
            ContactPerson       NVARCHAR(150)  NULL,
            Phone               VARCHAR(15)    NOT NULL CONSTRAINT CK_FieldCustomers_Phone CHECK (Phone NOT LIKE '%[^0-9]%' AND LEN(Phone) BETWEEN 10 AND 13),
            AlternatePhone      VARCHAR(15)    NULL CONSTRAINT CK_FieldCustomers_AltPhone
                                               CHECK (AlternatePhone IS NULL OR (AlternatePhone NOT LIKE '%[^0-9]%' AND LEN(AlternatePhone) BETWEEN 10 AND 13)),
            PhoneKey            AS CAST(RIGHT(Phone, 10) AS VARCHAR(10)) PERSISTED,
            AlternatePhoneKey   AS CAST(RIGHT(AlternatePhone, 10) AS VARCHAR(10)) PERSISTED,
            CustomerType        VARCHAR(15)    NOT NULL CONSTRAINT CK_FieldCustomers_Type
                                               CHECK (CustomerType IN ('Farmer', 'Nursery', 'Dealer', 'Florist', 'Institution', 'Other')),
            VillageCity         NVARCHAR(100)  NOT NULL CONSTRAINT CK_FieldCustomers_Village CHECK (LEN(LTRIM(RTRIM(VillageCity))) > 0),
            District            NVARCHAR(100)  NULL,
            Address             NVARCHAR(400)  NULL,
            Latitude            DECIMAL(9,6)   NULL,
            Longitude           DECIMAL(9,6)   NULL,
            AccuracyM           DECIMAL(9,1)   NULL,
            GpsExceptionReason  NVARCHAR(300)  NULL,
            FlagReasons         NVARCHAR(500)  NULL,
            PhotoId             INT            NULL CONSTRAINT FK_FieldCustomers_Photo REFERENCES dbo.FieldPhotos(Id),
            LeadStatus          VARCHAR(15)    NOT NULL CONSTRAINT DF_FieldCustomers_LeadStatus DEFAULT ('New')
                                               CONSTRAINT CK_FieldCustomers_LeadStatus
                                               CHECK (LeadStatus IN ('New', 'Contacted', 'Interested', 'SampleGiven', 'Negotiation', 'Won', 'Lost')),
            AssignedUserId      INT            NOT NULL CONSTRAINT FK_FieldCustomers_Assigned REFERENCES dbo.IMSUsers(Id),
            CreatedByUserId     INT            NOT NULL CONSTRAINT FK_FieldCustomers_CreatedBy REFERENCES dbo.IMSUsers(Id),
            CreatedAtUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldCustomers_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
            ModifiedAtUtc       DATETIME2(0)   NULL,
            IsActive            BIT            NOT NULL CONSTRAINT DF_FieldCustomers_IsActive DEFAULT (1),
            ArchivedByUserId    INT            NULL CONSTRAINT FK_FieldCustomers_ArchivedBy REFERENCES dbo.IMSUsers(Id),
            ArchivedAtUtc       DATETIME2(0)   NULL,
            ArchiveReason       NVARCHAR(300)  NULL,
            SubmissionToken     UNIQUEIDENTIFIER NOT NULL,
            CONSTRAINT CK_FieldCustomers_Position CHECK (
                (Latitude IS NULL AND Longitude IS NULL AND AccuracyM IS NULL)
             OR (Latitude BETWEEN -90 AND 90 AND Longitude BETWEEN -180 AND 180 AND AccuracyM > 0)),
            CONSTRAINT CK_FieldCustomers_Archive CHECK (
                (IsActive = 1 AND ArchivedAtUtc IS NULL AND ArchivedByUserId IS NULL)
             OR (IsActive = 0 AND ArchivedAtUtc IS NOT NULL AND ArchivedByUserId IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(ArchiveReason, N'')))) > 0))
        );
        CREATE INDEX IX_FieldCustomers_PhoneKey ON dbo.FieldCustomers (PhoneKey);
        CREATE INDEX IX_FieldCustomers_AltPhoneKey ON dbo.FieldCustomers (AlternatePhoneKey) WHERE AlternatePhone IS NOT NULL;
        CREATE INDEX IX_FieldCustomers_Assigned ON dbo.FieldCustomers (AssignedUserId, IsActive) INCLUDE (LeadStatus);
        CREATE INDEX IX_FieldCustomers_CreatedBy ON dbo.FieldCustomers (CreatedByUserId);
        CREATE UNIQUE INDEX UX_FieldCustomers_Token ON dbo.FieldCustomers (SubmissionToken);
        CREATE UNIQUE INDEX UX_FieldCustomers_Photo ON dbo.FieldCustomers (PhotoId) WHERE PhotoId IS NOT NULL;
        PRINT '=== dbo.FieldCustomers created ===';
    END
    ELSE PRINT '=== dbo.FieldCustomers already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 3. dbo.FieldVisits / FieldVisitItems / FieldVisitPhotos
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldVisits', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldVisits (
            Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldVisits PRIMARY KEY,
            CustomerId          INT            NOT NULL CONSTRAINT FK_FieldVisits_Customer REFERENCES dbo.FieldCustomers(Id),
            UserId              INT            NOT NULL CONSTRAINT FK_FieldVisits_User REFERENCES dbo.IMSUsers(Id),
            FieldDayId          INT            NOT NULL CONSTRAINT FK_FieldVisits_Day REFERENCES dbo.FieldDays(Id),
            CheckInAtUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldVisits_CheckInAtUtc DEFAULT (SYSUTCDATETIME()),
            Latitude            DECIMAL(9,6)   NULL,
            Longitude           DECIMAL(9,6)   NULL,
            AccuracyM           DECIMAL(9,1)   NULL,
            GpsExceptionReason  NVARCHAR(300)  NULL,
            FlagReasons         NVARCHAR(500)  NULL,
            Discussed           NVARCHAR(1000) NOT NULL CONSTRAINT CK_FieldVisits_Discussed CHECK (LEN(LTRIM(RTRIM(Discussed))) > 0),
            Notes               NVARCHAR(2000) NULL,
            LeadStatusBefore    VARCHAR(15)    NOT NULL,
            LeadStatusAfter     VARCHAR(15)    NOT NULL,
            SubmissionToken     UNIQUEIDENTIFIER NOT NULL,
            ModifiedByUserId    INT            NULL CONSTRAINT FK_FieldVisits_ModifiedBy REFERENCES dbo.IMSUsers(Id),
            ModifiedAtUtc       DATETIME2(0)   NULL,
            CONSTRAINT CK_FieldVisits_Position CHECK (
                (Latitude IS NULL AND Longitude IS NULL AND AccuracyM IS NULL)
             OR (Latitude BETWEEN -90 AND 90 AND Longitude BETWEEN -180 AND 180 AND AccuracyM > 0)),
            CONSTRAINT CK_FieldVisits_Evidence CHECK (Latitude IS NOT NULL OR LEN(LTRIM(RTRIM(ISNULL(GpsExceptionReason, N'')))) > 0),
            CONSTRAINT CK_FieldVisits_Status CHECK (
                LeadStatusBefore IN ('New', 'Contacted', 'Interested', 'SampleGiven', 'Negotiation', 'Won', 'Lost')
            AND LeadStatusAfter IN ('New', 'Contacted', 'Interested', 'SampleGiven', 'Negotiation', 'Won', 'Lost'))
        );
        CREATE INDEX IX_FieldVisits_Day ON dbo.FieldVisits (FieldDayId, CheckInAtUtc);
        CREATE INDEX IX_FieldVisits_Customer ON dbo.FieldVisits (CustomerId, CheckInAtUtc);
        CREATE INDEX IX_FieldVisits_User ON dbo.FieldVisits (UserId, CheckInAtUtc);
        CREATE UNIQUE INDEX UX_FieldVisits_Token ON dbo.FieldVisits (SubmissionToken);
        PRINT '=== dbo.FieldVisits created ===';
    END
    ELSE PRINT '=== dbo.FieldVisits already exists -- no-op ===';

    IF OBJECT_ID('dbo.FieldVisitItems', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldVisitItems (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldVisitItems PRIMARY KEY,
            VisitId     INT            NOT NULL CONSTRAINT FK_FieldVisitItems_Visit REFERENCES dbo.FieldVisits(Id),
            LineNumber  INT            NOT NULL CONSTRAINT CK_FieldVisitItems_LineNumber CHECK (LineNumber >= 1),
            ItemText    NVARCHAR(200)  NOT NULL CONSTRAINT CK_FieldVisitItems_Text CHECK (LEN(LTRIM(RTRIM(ItemText))) > 0),
            Quantity    DECIMAL(10,2)  NULL CONSTRAINT CK_FieldVisitItems_Qty CHECK (Quantity IS NULL OR Quantity > 0)
        );
        CREATE UNIQUE INDEX UX_FieldVisitItems_Line ON dbo.FieldVisitItems (VisitId, LineNumber);
        PRINT '=== dbo.FieldVisitItems created ===';
    END
    ELSE PRINT '=== dbo.FieldVisitItems already exists -- no-op ===';

    IF OBJECT_ID('dbo.FieldVisitPhotos', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldVisitPhotos (
            VisitId     INT NOT NULL CONSTRAINT FK_FieldVisitPhotos_Visit REFERENCES dbo.FieldVisits(Id),
            PhotoId     INT NOT NULL CONSTRAINT FK_FieldVisitPhotos_Photo REFERENCES dbo.FieldPhotos(Id),
            SortOrder   INT NOT NULL,
            CONSTRAINT PK_FieldVisitPhotos PRIMARY KEY (VisitId, PhotoId)
        );
        CREATE UNIQUE INDEX UX_FieldVisitPhotos_Photo ON dbo.FieldVisitPhotos (PhotoId);
        PRINT '=== dbo.FieldVisitPhotos created ===';
    END
    ELSE PRINT '=== dbo.FieldVisitPhotos already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 4. dbo.FieldCustomerNotes / FieldFollowUps / FieldCustomerEvents
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldCustomerNotes', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldCustomerNotes (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldCustomerNotes PRIMARY KEY,
            CustomerId        INT            NOT NULL CONSTRAINT FK_FieldCustomerNotes_Customer REFERENCES dbo.FieldCustomers(Id),
            VisitId           INT            NULL CONSTRAINT FK_FieldCustomerNotes_Visit REFERENCES dbo.FieldVisits(Id),
            UserId            INT            NOT NULL CONSTRAINT FK_FieldCustomerNotes_User REFERENCES dbo.IMSUsers(Id),
            NoteType          VARCHAR(12)    NOT NULL CONSTRAINT CK_FieldCustomerNotes_Type CHECK (NoteType IN ('Call', 'Note', 'Correction')),
            Text              NVARCHAR(1000) NOT NULL CONSTRAINT CK_FieldCustomerNotes_Text CHECK (LEN(LTRIM(RTRIM(Text))) > 0),
            LeadStatusBefore  VARCHAR(15)    NULL,
            LeadStatusAfter   VARCHAR(15)    NULL,
            CreatedAtUtc      DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldCustomerNotes_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
            SubmissionToken   UNIQUEIDENTIFIER NOT NULL,
            -- a Correction always belongs to one visit; Call / Note never do
            CONSTRAINT CK_FieldCustomerNotes_Visit CHECK ((NoteType = 'Correction' AND VisitId IS NOT NULL) OR (NoteType <> 'Correction' AND VisitId IS NULL)),
            CONSTRAINT CK_FieldCustomerNotes_Status CHECK (
                (LeadStatusBefore IS NULL AND LeadStatusAfter IS NULL)
             OR (LeadStatusBefore IN ('New', 'Contacted', 'Interested', 'SampleGiven', 'Negotiation', 'Won', 'Lost')
                 AND LeadStatusAfter IN ('New', 'Contacted', 'Interested', 'SampleGiven', 'Negotiation', 'Won', 'Lost')))
        );
        CREATE INDEX IX_FieldCustomerNotes_Customer ON dbo.FieldCustomerNotes (CustomerId, CreatedAtUtc);
        CREATE INDEX IX_FieldCustomerNotes_Visit ON dbo.FieldCustomerNotes (VisitId) WHERE VisitId IS NOT NULL;
        CREATE UNIQUE INDEX UX_FieldCustomerNotes_Token ON dbo.FieldCustomerNotes (SubmissionToken);
        PRINT '=== dbo.FieldCustomerNotes created ===';
    END
    ELSE PRINT '=== dbo.FieldCustomerNotes already exists -- no-op ===';

    IF OBJECT_ID('dbo.FieldFollowUps', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldFollowUps (
            Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldFollowUps PRIMARY KEY,
            CustomerId          INT            NOT NULL CONSTRAINT FK_FieldFollowUps_Customer REFERENCES dbo.FieldCustomers(Id),
            DueDate             DATE           NOT NULL,
            Note                NVARCHAR(500)  NULL,
            Status              VARCHAR(10)    NOT NULL CONSTRAINT CK_FieldFollowUps_Status CHECK (Status IN ('Pending', 'Done', 'Cancelled')),
            CreatedFromVisitId  INT            NULL CONSTRAINT FK_FieldFollowUps_FromVisit REFERENCES dbo.FieldVisits(Id),
            CreatedFromNoteId   INT            NULL CONSTRAINT FK_FieldFollowUps_FromNote REFERENCES dbo.FieldCustomerNotes(Id),
            CreatedByUserId     INT            NOT NULL CONSTRAINT FK_FieldFollowUps_CreatedBy REFERENCES dbo.IMSUsers(Id),
            CreatedAtUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldFollowUps_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
            ClosedByVisitId     INT            NULL CONSTRAINT FK_FieldFollowUps_ClosedByVisit REFERENCES dbo.FieldVisits(Id),
            ClosedByNoteId      INT            NULL CONSTRAINT FK_FieldFollowUps_ClosedByNote REFERENCES dbo.FieldCustomerNotes(Id),
            ClosedByUserId      INT            NULL CONSTRAINT FK_FieldFollowUps_ClosedBy REFERENCES dbo.IMSUsers(Id),
            ClosedAtUtc         DATETIME2(0)   NULL,
            CancelReason        NVARCHAR(300)  NULL,
            CONSTRAINT CK_FieldFollowUps_Shape CHECK (
                (Status = 'Pending' AND ClosedAtUtc IS NULL AND ClosedByUserId IS NULL AND ClosedByVisitId IS NULL AND ClosedByNoteId IS NULL AND CancelReason IS NULL)
             -- Done only by logging a visit or a call / note against it
             OR (Status = 'Done' AND ClosedAtUtc IS NOT NULL AND ClosedByUserId IS NOT NULL AND CancelReason IS NULL
                 AND ((ClosedByVisitId IS NOT NULL AND ClosedByNoteId IS NULL) OR (ClosedByVisitId IS NULL AND ClosedByNoteId IS NOT NULL)))
             OR (Status = 'Cancelled' AND ClosedAtUtc IS NOT NULL AND ClosedByUserId IS NOT NULL AND ClosedByVisitId IS NULL AND ClosedByNoteId IS NULL
                 AND LEN(LTRIM(RTRIM(ISNULL(CancelReason, N'')))) > 0))
        );
        CREATE UNIQUE INDEX UX_FieldFollowUps_OnePending ON dbo.FieldFollowUps (CustomerId) WHERE Status = 'Pending';
        CREATE INDEX IX_FieldFollowUps_Due ON dbo.FieldFollowUps (Status, DueDate) INCLUDE (CustomerId);
        CREATE INDEX IX_FieldFollowUps_Customer ON dbo.FieldFollowUps (CustomerId, Id);
        PRINT '=== dbo.FieldFollowUps created ===';
    END
    ELSE PRINT '=== dbo.FieldFollowUps already exists -- no-op ===';

    IF OBJECT_ID('dbo.FieldCustomerEvents', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldCustomerEvents (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldCustomerEvents PRIMARY KEY,
            CustomerId  INT            NOT NULL CONSTRAINT FK_FieldCustomerEvents_Customer REFERENCES dbo.FieldCustomers(Id),
            VisitId     INT            NULL CONSTRAINT FK_FieldCustomerEvents_Visit REFERENCES dbo.FieldVisits(Id),
            FollowUpId  INT            NULL CONSTRAINT FK_FieldCustomerEvents_FollowUp REFERENCES dbo.FieldFollowUps(Id),
            EventType   VARCHAR(20)    NOT NULL CONSTRAINT CK_FieldCustomerEvents_Type CHECK (EventType IN
                        ('Created', 'Updated', 'Reassigned', 'StatusChanged', 'Archived', 'Restored', 'VisitEdited',
                         'FollowUpSet', 'FollowUpDone', 'FollowUpCancelled')),
            FromStatus  VARCHAR(15)    NULL,
            ToStatus    VARCHAR(15)    NULL,
            FromUserId  INT            NULL CONSTRAINT FK_FieldCustomerEvents_FromUser REFERENCES dbo.IMSUsers(Id),
            ToUserId    INT            NULL CONSTRAINT FK_FieldCustomerEvents_ToUser REFERENCES dbo.IMSUsers(Id),
            ByUserId    INT            NOT NULL CONSTRAINT FK_FieldCustomerEvents_By REFERENCES dbo.IMSUsers(Id),
            AtUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldCustomerEvents_AtUtc DEFAULT (SYSUTCDATETIME()),
            Details     NVARCHAR(2000) NULL
        );
        CREATE INDEX IX_FieldCustomerEvents_Customer ON dbo.FieldCustomerEvents (CustomerId, Id);
        CREATE INDEX IX_FieldCustomerEvents_Status ON dbo.FieldCustomerEvents (EventType, AtUtc) INCLUDE (CustomerId, FromStatus, ToStatus);
        PRINT '=== dbo.FieldCustomerEvents created ===';
    END
    ELSE PRINT '=== dbo.FieldCustomerEvents already exists -- no-op ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkCustomers1103;
    THROW;
END CATCH;
GO

------------------------------------------------------------------
-- 5. Triggers (each CREATE TRIGGER in dynamic SQL; still inside the transaction)
------------------------------------------------------------------
IF @@TRANCOUNT = 0
    THROW 54210, 'The transaction was lost before the trigger batch -- nothing changed.', 1;

IF OBJECT_ID('dbo.TR_FieldVisits_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldVisits_Guard ON dbo.FieldVisits
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    -- 2026-11-03_FieldWorkCustomers.sql: a visit is checked in on the employee''s OWN, OPEN Field Day.
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1 FROM inserted i LEFT JOIN dbo.FieldDays d ON d.Id = i.FieldDayId
                   WHERE d.Id IS NULL OR d.UserId <> i.UserId OR d.Status <> ''Open'')
        THROW 54251, ''A visit needs your open Field Day (Start Day first).'', 1;
    -- the check-in itself never changes (manager edits change only the text and items)
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted o ON o.Id = i.Id
               WHERE i.CustomerId <> o.CustomerId OR i.UserId <> o.UserId OR i.FieldDayId <> o.FieldDayId
                  OR i.CheckInAtUtc <> o.CheckInAtUtc OR i.SubmissionToken <> o.SubmissionToken
                  OR ISNULL(i.Latitude, 999) <> ISNULL(o.Latitude, 999) OR ISNULL(i.Longitude, 999) <> ISNULL(o.Longitude, 999)
                  OR ISNULL(i.AccuracyM, -1) <> ISNULL(o.AccuracyM, -1) OR i.LeadStatusBefore <> o.LeadStatusBefore)
        THROW 54252, ''The check-in of a visit cannot be changed.'', 1;
END');

IF OBJECT_ID('dbo.TR_FieldVisitPhotos_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldVisitPhotos_Guard ON dbo.FieldVisitPhotos
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- a visit photo was taken by the visit''s employee for a visit
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldVisits v ON v.Id = i.VisitId JOIN dbo.FieldPhotos p ON p.Id = i.PhotoId
               WHERE p.Purpose <> ''VisitPhoto'' OR p.OwnerUserId <> v.UserId)
        THROW 54253, ''The photo does not belong to this visit.'', 1;
END');

IF OBJECT_ID('dbo.TR_FieldCustomers_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldCustomers_Guard ON dbo.FieldCustomers
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted o ON o.Id = i.Id
               WHERE i.CreatedByUserId <> o.CreatedByUserId OR i.CreatedAtUtc <> o.CreatedAtUtc OR i.SubmissionToken <> o.SubmissionToken
                  OR ISNULL(i.Latitude, 999) <> ISNULL(o.Latitude, 999) OR ISNULL(i.Longitude, 999) <> ISNULL(o.Longitude, 999))
        THROW 54254, ''The creation record of a customer cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldPhotos p ON p.Id = i.PhotoId WHERE p.Purpose <> ''CustomerPhoto'')
        THROW 54255, ''The photo is not a customer photo.'', 1;
END');

IF OBJECT_ID('dbo.TR_FieldCustomerNotes_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldCustomerNotes_Guard ON dbo.FieldCustomerNotes
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- notes and corrections are append-only: history stays trustworthy
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 54256, ''A note cannot be changed; add a new note instead.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldVisits v ON v.Id = i.VisitId WHERE v.CustomerId <> i.CustomerId)
        THROW 54257, ''The corrected visit belongs to another customer.'', 1;
END');

IF OBJECT_ID('dbo.TR_FieldFollowUps_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldFollowUps_Guard ON dbo.FieldFollowUps
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    -- a closed follow-up stays closed; the customer and due date of a follow-up never change
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted o ON o.Id = i.Id
               WHERE o.Status <> ''Pending'' OR i.CustomerId <> o.CustomerId OR i.DueDate <> o.DueDate)
        THROW 54258, ''A closed follow-up cannot be changed.'', 1;
    -- the visit / note it came from or was closed by belongs to the same customer
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN dbo.FieldVisits a ON a.Id = i.CreatedFromVisitId LEFT JOIN dbo.FieldVisits b ON b.Id = i.ClosedByVisitId
                                       LEFT JOIN dbo.FieldCustomerNotes c ON c.Id = i.CreatedFromNoteId LEFT JOIN dbo.FieldCustomerNotes d ON d.Id = i.ClosedByNoteId
               WHERE a.CustomerId <> i.CustomerId OR b.CustomerId <> i.CustomerId OR c.CustomerId <> i.CustomerId OR d.CustomerId <> i.CustomerId)
        THROW 54259, ''The follow-up refers to another customer''''s visit or note.'', 1;
END');

IF OBJECT_ID('dbo.TR_FieldCustomerEvents_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldCustomerEvents_Guard ON dbo.FieldCustomerEvents
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54260, ''The customer history is append-only.'', 1;
END');
GO
IF @@TRANCOUNT = 0
    THROW 54211, 'The transaction was lost after the trigger batch -- nothing changed.', 1;
GO

SET NOCOUNT ON;
PRINT '=== After: new objects (expect 7 tables, 6 triggers, 2 photo checks) ===';
SELECT name, type_desc FROM sys.objects
WHERE name IN (N'FieldCustomers', N'FieldVisits', N'FieldVisitItems', N'FieldVisitPhotos', N'FieldCustomerNotes', N'FieldFollowUps',
               N'FieldCustomerEvents', N'TR_FieldVisits_Guard', N'TR_FieldVisitPhotos_Guard', N'TR_FieldCustomers_Guard',
               N'TR_FieldCustomerNotes_Guard', N'TR_FieldFollowUps_Guard', N'TR_FieldCustomerEvents_Guard',
               N'CK_FieldPhotos_Purpose', N'CK_FieldPhotos_ConsentReminder')
ORDER BY type_desc, name;
SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.CK_FieldPhotos_Purpose')) AS PurposeCheck;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkCustomers1103;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkCustomers1103;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
