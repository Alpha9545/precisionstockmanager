-- ============================================================================
-- Phase B (add-on): assigned-supervisor approval + complete-tray rule
-- ============================================================================
-- Run AFTER PhaseB_DirectSowing.sql and PhaseB_SeedlingRules.sql.
-- Additive and idempotent. No table, column or row is created, changed or
-- deleted; only one CHECK constraint and one INSERT trigger are added.
-- Run with QUOTED_IDENTIFIER ON / ANSI_NULLS ON (sqlcmd -I, SSMS default).
--
--   1. CK_SeedSowings_TrayCount: NumberOfTrays = FLOOR(QuantitySown / tray
--      size) and at least 1 complete tray (tray size from CavityType). The
--      application calculates the same value (DirectSowingRules.CalculateTrays);
--      this is the database backstop against any other writer. It is only
--      added if every existing sowing already satisfies it.
--   2. TR_ReadyConfirmations_AssignedSupervisor (AFTER INSERT): a Supervisor
--      Approval row is refused unless ApprovedById is the supervisor assigned
--      to the sowing (SeedSowings.SupervisorId) and not the user who recorded
--      the sowing (SeedSowings.CreatedById). The application applies the same
--      rule under the sowing's row lock (DirectSowingRules.CanApprove); this is
--      the database backstop. INSERT only: existing approvals and later
--      status updates (cancellation) are not affected.
-- ============================================================================

SET XACT_ABORT ON;
GO

IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50199, 'PhaseB_ApprovalAndTrayRules.sql may only be run against PlantsIMS2_Test.', 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_TrayCount')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.SeedSowings
               WHERE NumberOfTrays IS NULL OR NumberOfTrays < 1
                  OR NumberOfTrays <> FLOOR(QuantitySown / CASE CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                           WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                           WHEN N'150 Cavity' THEN 150 END))
        THROW 50110, 'CK_SeedSowings_TrayCount not added: an existing sowing does not satisfy NumberOfTrays = FLOOR(QuantitySown / tray size).', 1;

    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_TrayCount
        CHECK (NumberOfTrays IS NOT NULL AND NumberOfTrays >= 1
               AND NumberOfTrays = FLOOR(QuantitySown / CASE CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                        WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                        WHEN N'150 Cavity' THEN 150 END));
END
GO

CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ApprovedById IS NULL
                  OR sw.SupervisorId IS NULL
                  OR i.ApprovedById <> sw.SupervisorId
                  OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById))
        THROW 50111, 'Only the supervisor assigned to the sowing (and not the user who recorded it) can approve it.', 1;
END
GO
