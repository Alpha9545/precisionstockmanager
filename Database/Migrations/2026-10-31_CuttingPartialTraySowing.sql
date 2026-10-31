-- ============================================================================
-- 2026-10-31_CuttingPartialTraySowing.sql          *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- WHY (owner requirement 2026-10-31): a cutting tray sowing whose Cutting Quantity is less than one complete tray
--   (e.g. 62 cuttings at 150 Cavity) was refused ("Cutting Quantity (62) is less than one complete 150-cavity tray.").
--   It is now recorded as ONE PARTIAL TRAY, approved, and kept in Ready Stock as one tray of its own cuttings.
--
-- WHAT (only these four objects; each accepts exactly one new shape and keeps every existing rule):
--   1. CK_SeedSowings_TrayCount     + OR a CUTTING sowing with NumberOfTrays = 1 and 0 < QuantitySown < one tray.
--   2. CK_SeedSowings_SeedQuantity  + OR a CUTTING sowing with NumberOfTrays = 1, whole SeedQuantity = QuantitySown,
--                                       0 < QuantitySown < one tray (nothing left over, so no sub-tray wastage).
--   3. TR_ReadyConfirmations_TrayQuantity: the approval of such a sowing is exactly 1 Actual Ready Tray holding the
--      sowing's still-expected cuttings (never 1 x cavity). Every other sowing: unchanged (trays x cavity).
--   4. TR_ReadyStock_WholeTrays: the Ready Stock batch of such a sowing may hold 1 .. QuantitySown plants (under one
--      tray). Every other sowing batch: unchanged (whole trays of its cavity).
--   Seed sowings, Bed Sowing / Transplant tray batches and every quantity of one tray or more are untouched.
--
-- NOT changed: any data row, any other table, constraint, trigger or permission.
--
-- APPLICATION WITHOUT THIS SCRIPT: WorkflowCorrectionsSchema.CuttingPartialTrayAsync is false, so a cutting sowing under
--   one tray is refused exactly as before (fail closed).
--
-- TARGET SAFEGUARDS (as 2026-10-27_ReportExportPermission.sql): the local AND the live database can both be named
--   PlantsIMS2_Test. The first batch refuses to continue unless ALL of these hold (nothing is read or written before them):
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is DRYRUN or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--   Then each of the four objects must be EXACTLY the expected earlier version (SHA-256 of its definition), else nothing
--   changes ("schema drift"). Those versions are the ones 2026-10-19_Cavity70.sql leaves.
--
-- OPERATOR CHECKLIST
--   a) read-only identity query, keep its output:
--        SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) AS ServerProperty,
--               CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName, DB_NAME() AS DatabaseName,
--               CONVERT(CHAR(19), d.create_date, 126) AS DatabaseCreated
--        FROM sys.databases d WHERE d.database_id = DB_ID();
--   b) confirm independently that this machine IS the intended one; c) take a COPY_ONLY backup;
--   d) sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--             TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-31_CuttingPartialTraySowing.sql
--   It ends "DRY RUN -- rolled back". Then MODE=COMMIT -- only with the owner's explicit approval for that database.
--
-- Requires: 2026-10-19_Cavity70.sql. One transaction. Idempotent. Undo: 2026-10-31_CuttingPartialTraySowing_Rollback.sql.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53701, '2026-10-31_CuttingPartialTraySowing.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53702, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53703, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53704, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53705, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53706, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53707, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CuttingPartialTray1031;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_CavityType')) LIKE N'%70 Cavity%')
        THROW 53708, 'Run 2026-10-19_Cavity70.sql first.', 1;

    IF OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_TrayCount')) LIKE N'%Cutting%'
       AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_SeedQuantity')) LIKE N'%Cutting%'
       AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%CuttingPartialTray%'
       AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')) LIKE N'%CuttingPartialTray%'
        PRINT '=== Already applied -- no-op ===';
    ELSE
    BEGIN
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_TrayCount'))), 2)
           <> '1811E48C16A70593D52EC1C0D3B671753FD6BDBC4AAE124872C95AA51AA6BCD5'
            THROW 53709, 'CK_SeedSowings_TrayCount is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_SeedQuantity'))), 2)
           <> 'CDBDD099D3151355B1DB726EBADCCC29B224F0E76FC1924B820528C88A610AF6'
            THROW 53709, 'CK_SeedSowings_SeedQuantity is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 2)
           <> 'C6FA08B87C055F0D9E9B5F136D47E67001D4D32E0A7426104B59EC896CC995EC'
           AND CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', REPLACE(REPLACE(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')), CHARINDEX(N'ON dbo.ReadyConfirmations', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity'))), 8000), CHAR(13), N''), CHAR(10), N''), CHAR(9), N''), N' ', N'')), 2)
           <> '36E0381FE2FF925C6678310BF34AA100548C45FC1B7EA4BBACDF4C4B9E6E898A'
            THROW 53709, 'TR_ReadyConfirmations_TrayQuantity is not the expected version -- schema drift, nothing changed.', 1;
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays'))), 2)
           <> '1C81A4A2C6C0B91D4AA193080498CF0C137C3F9EA687EB7161032B8255BBD5B8'
           AND CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', REPLACE(REPLACE(REPLACE(REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')), CHARINDEX(N'ON dbo.ReadyStock', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays'))), 8000), CHAR(13), N''), CHAR(10), N''), CHAR(9), N''), N' ', N'')), 2)
           <> '605D421A435C63EC476F6A350595BEA633CEBF378422798C81590BFE790CD506'
            THROW 53709, 'TR_ReadyStock_WholeTrays is not the expected version -- schema drift, nothing changed.', 1;

        PRINT '=== Before: sowings / Ready Stock rows (every one already satisfies the widened rules -- they only add a shape) ===';
        SELECT (SELECT COUNT(*) FROM dbo.SeedSowings) AS Sowings, (SELECT COUNT(*) FROM dbo.ReadyStock) AS ReadyStockRows;

        -- 1 + 2: the SeedSowings tray CHECKs (re-validated WITH CHECK against every existing row)
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_TrayCount;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_TrayCount CHECK (
            ([NumberOfTrays] IS NOT NULL AND [NumberOfTrays]>=(1) AND [NumberOfTrays]=floor([QuantitySown]/case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end))
            OR ([SourceType]=N'Cutting' AND [NumberOfTrays] IS NOT NULL AND [NumberOfTrays]=(1) AND [QuantitySown]>(0)
                AND [QuantitySown]<coalesce(case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end,(0))));
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_SeedQuantity;
        ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_SeedQuantity CHECK (
            ([SeedQuantity] IS NULL OR [SeedQuantity]=floor([SeedQuantity]) AND [NumberOfTrays] IS NOT NULL AND [QuantitySown]=[NumberOfTrays]*case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end AND [SeedQuantity]>=[QuantitySown] AND ([SeedQuantity]-[QuantitySown])<case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end)
            OR ([SourceType]=N'Cutting' AND [SeedQuantity] IS NOT NULL AND [SeedQuantity]=floor([SeedQuantity]) AND [NumberOfTrays] IS NOT NULL AND [NumberOfTrays]=(1)
                AND [QuantitySown]=[SeedQuantity] AND [QuantitySown]>(0)
                AND [QuantitySown]<coalesce(case [CavityType] when N'9 Cavity' then (9) when N'24 Cavity' then (24) when N'42 Cavity' then (42) when N'70 Cavity' then (70) when N'102 Cavity' then (102) when N'150 Cavity' then (150)  end,(0))));

        -- 3: the approval of a partial-tray cutting sowing = 1 tray holding the sowing's still-expected cuttings
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_TrayQuantity
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- Seedlings come only from complete trays of the sowing''s own cavity;
    -- wastage is whatever of the sowing''s still-expected quantity did not
    -- become a ready seedling THIS approval (never negative -- an overage
    -- against the original sowing quantity is a legitimate outcome and
    -- produces zero wastage, matching DirectSowingRules.ComputeApproval).
    -- 2026-10-31 CuttingPartialTray: a cutting sowing recorded as ONE partial tray (NumberOfTrays 1, QuantitySown under
    -- one tray) is approved as exactly 1 tray holding the sowing''s own still-expected cuttings -- never 1 x cavity.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               CROSS APPLY (SELECT CASE sw.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                      WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102
                                                      WHEN N''150 Cavity'' THEN 150 END AS Cavity) c
               CROSS APPLY (SELECT CASE WHEN sw.SourceType = N''Cutting'' AND sw.NumberOfTrays = 1
                                         AND sw.QuantitySown > 0 AND sw.QuantitySown < c.Cavity THEN 1 ELSE 0 END AS IsPartialTray) p
               WHERE i.ActualTrayQuantity IS NULL
                  OR sw.NumberOfTrays IS NULL
                  OR (p.IsPartialTray = 0 AND i.ConfirmedQuantity <> i.ActualTrayQuantity * c.Cavity)
                  OR (p.IsPartialTray = 1 AND (i.ActualTrayQuantity <> 1
                                              OR i.ConfirmedQuantity <> sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity))
                  OR i.WastageQuantity <> (CASE WHEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity) > 0
                                                 THEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity)
                                                 ELSE 0 END))
        THROW 50124, ''Approval must be whole Actual Ready Trays; seedlings = trays x the sowing cavity; wastage = the sowing''''s still-expected quantity minus seedlings this approval, never negative.'', 1;
END
');

        -- 4: the Ready Stock batch of a partial-tray cutting sowing holds 1 .. QuantitySown plants (under one tray)
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
    -- 2026-10-31 CuttingPartialTray: the batch of a cutting sowing recorded as ONE partial tray (NumberOfTrays 1,
    -- QuantitySown under one tray) holds at most that tray''s cuttings.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.OriginType = N''Sowing''
                 AND (d.Id IS NULL OR d.Quantity <> i.Quantity)
                 AND i.Quantity % CASE i.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                    WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102
                                                    WHEN N''150 Cavity'' THEN 150 END <> 0
                 AND NOT EXISTS (SELECT 1 FROM dbo.SeedSowings sw
                                 WHERE sw.Id = i.SeedSowingId AND sw.SourceType = N''Cutting'' AND sw.NumberOfTrays = 1
                                   AND sw.CavityType = i.CavityType
                                   AND sw.QuantitySown < CASE i.CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24
                                                                           WHEN N''42 Cavity'' THEN 42 WHEN N''70 Cavity'' THEN 70 WHEN N''102 Cavity'' THEN 102
                                                                           WHEN N''150 Cavity'' THEN 150 END
                                   AND i.Quantity > 0 AND i.Quantity <= sw.QuantitySown))
        THROW 50125, ''Ready Stock quantity must be a whole number of trays of the sowing cavity.'', 1;
END
');

        IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_TrayCount')) LIKE N'%Cutting%'
                AND OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_SeedQuantity')) LIKE N'%Cutting%'
                AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_TrayQuantity')) LIKE N'%CuttingPartialTray%'
                AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyStock_WholeTrays')) LIKE N'%CuttingPartialTray%')
            THROW 53710, 'The application probe (WorkflowCorrectionsSchema.CuttingPartialTrayProbeSql) would not see this change -- rolling back.', 1;
        IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name IN (N'CK_SeedSowings_TrayCount', N'CK_SeedSowings_SeedQuantity')
                   AND (is_not_trusted = 1 OR is_disabled = 1))
            THROW 53711, 'A widened CHECK constraint is not trusted -- rolling back.', 1;
        PRINT '=== Applied: 2 CHECK constraints widened, 2 triggers altered ===';
        SELECT name, CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(object_id)), 2) AS DefinitionSha256
        FROM sys.objects WHERE name IN (N'CK_SeedSowings_TrayCount', N'CK_SeedSowings_SeedQuantity',
                                        N'TR_ReadyConfirmations_TrayQuantity', N'TR_ReadyStock_WholeTrays') ORDER BY name;
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingPartialTray1031;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION CuttingPartialTray1031;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION CuttingPartialTray1031;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
