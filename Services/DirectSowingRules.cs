namespace PlantStockManager.Services
{
    // Phase B: the pure (database-free) business rules of
    // Seed Stock -> Direct Sowing -> Supervisor Approval -> Ready Stock.
    // The repositories call these inside their locked transactions; keeping
    // them here makes every rule unit-testable and keeps the page-level
    // validation and the repository validation identical.
    public static class DirectSowingRules
    {
        // Seed may only be sown directly from Main Office Seed Stock.
        public const string MainOfficeAreaType = "MainOffice";

        // Closed cavity list -- identical to CK_SeedSowings_CavityType
        // (Database/Phase23_SeedSowing.sql). Stored as the display string.
        public static readonly IReadOnlyList<string> CavityTypes =
            new[] { "9 Cavity", "24 Cavity", "42 Cavity", "102 Cavity", "150 Cavity" };

        // Closed wastage-reason list -- identical to
        // CK_ReadyConfirmations_WastageReason (Database/PhaseB_DirectSowing.sql).
        public static readonly IReadOnlyList<string> WastageReasons =
            new[] { "Germination failure", "Disease", "Damaged plants", "Poor growth", "Other" };

        public static bool IsValidCavityType(string? cavityType)
            => cavityType != null && CavityTypes.Contains(cavityType, StringComparer.Ordinal);

        public static bool IsValidWastageReason(string? reason)
            => reason != null && WastageReasons.Contains(reason, StringComparer.Ordinal);

        public static bool IsMainOfficeSeedLocation(string? areaType, bool areaIsActive)
            => areaIsActive && string.Equals(areaType, MainOfficeAreaType, StringComparison.Ordinal);

        // ---- Tray calculation ----------------------------------------------
        // Tray size = cavities per tray, taken from the closed cavity list
        // ("102 Cavity" -> 102). Null for anything else (e.g. "10 Cavity").
        public static int? CavityCount(string? cavityType)
        {
            if (!IsValidCavityType(cavityType))
                return null;
            return int.Parse(cavityType!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        // NoOfTrays = FLOOR(SeedQuantity / TraySize) -- complete trays only,
        // never rounded to nearest and never rounded up. SeedsUsed = the seeds
        // that fill those complete trays; RemainingSeeds = the rest.
        // The server always recalculates with this function; any tray count
        // sent by the browser is ignored.
        public static (bool Ok, int Trays, decimal SeedsUsed, decimal RemainingSeeds, string? Error) CalculateTrays(
            decimal seedQuantity, string? cavityType)
        {
            var cavity = CavityCount(cavityType);
            if (cavity == null)
                return (false, 0, 0, 0, $"Tray size must be one of: {string.Join(", ", CavityTypes)}.");
            if (seedQuantity <= 0)
                return (false, 0, 0, 0, "Seed Quantity must be greater than zero.");
            var trays = decimal.Floor(seedQuantity / cavity.Value);
            if (trays < 1)
                return (false, 0, 0, seedQuantity, $"Seed Quantity ({seedQuantity:N0}) is less than one complete {cavity.Value}-cavity tray.");
            if (trays > int.MaxValue)
                return (false, 0, 0, 0, "Seed Quantity is too large.");
            var seedsUsed = trays * cavity.Value;
            return (true, (int)trays, seedsUsed, seedQuantity - seedsUsed, null);
        }

        // Direct Sowing seed consumption. The operator enters the Seed
        // Quantity; only the seeds that fill COMPLETE trays (SeedsUsed) are
        // sown and deducted from the Main Office lot. RemainingSeeds stay in
        // the lot. The entered quantity must be a whole number and must be
        // available in the lot.
        //   SeedSowings.QuantitySown   = SeedsUsed
        //   SeedSowings.NumberOfTrays  = Trays
        //   'Sown' ledger entry         = -SeedsUsed
        //   lot available afterwards   = available - SeedsUsed
        public static (bool Ok, int Trays, decimal SeedsUsed, decimal RemainingSeeds, decimal AvailableAfter, string? Error) PlanSowing(
            decimal seedQuantity, string? cavityType, decimal physical, decimal inTransit)
        {
            var available = physical - inTransit;
            if (seedQuantity <= 0)
                return (false, 0, 0, 0, available, "Seed Quantity must be greater than zero.");
            if (!IsWholeNumber(seedQuantity))
                return (false, 0, 0, 0, available, "Seed Quantity must be a whole number.");
            var (traysOk, trays, seedsUsed, remaining, trayError) = CalculateTrays(seedQuantity, cavityType);
            if (!traysOk)
                return (false, 0, 0, 0, available, trayError);
            var (enough, _, stockError) = CheckSeedAvailability(physical, inTransit, seedQuantity);
            if (!enough)
                return (false, 0, 0, 0, available, stockError);
            return (true, trays, seedsUsed, remaining, available - seedsUsed, null);
        }

        // ---- Approval authority --------------------------------------------
        // A sowing is approved ONLY by the supervisor assigned to it
        // (SeedSowings.SupervisorId). Holding ReadyStock.Confirm is necessary
        // (page permission) but not sufficient, and nobody approves a sowing
        // they recorded. Applied by ReadyConfirmationRepository.ConfirmAsync
        // under the sowing's row lock, and by the pages for their messages.
        public static (bool Ok, string? Error) CanApprove(
            int? assignedSupervisorId, int? createdById, string? createdBy, int? approverId, string? approverName)
        {
            if (!approverId.HasValue)
                return (false, "Your user could not be identified.");
            if (IsOwnSowing(createdById, createdBy, approverId, approverName))
                return (false, OwnSowingMessage);
            if (!assignedSupervisorId.HasValue)
                return (false, NoSupervisorMessage);
            if (assignedSupervisorId.Value != approverId.Value)
                return (false, NotAssignedMessage);
            return (true, null);
        }

        public const string NoSupervisorMessage =
            "No supervisor is assigned to this sowing. Assign its supervisor (Direct Sowing > Edit) before it can be approved.";
        public const string NotAssignedMessage =
            "Only the supervisor assigned to this sowing can approve it.";

        // Who may be ASSIGNED as a sowing's supervisor: an active user who can
        // approve (eligible list from UserRoleRepository), and never the person
        // who records the sowing (they could not approve it anyway).
        public static (bool Ok, string? Error) ValidateSupervisorAssignment(
            int? supervisorId, int? createdById, IReadOnlyCollection<int> eligibleSupervisorIds)
        {
            if (!supervisorId.HasValue || supervisorId.Value <= 0)
                return (false, "Supervisor is required: choose the Sowing Supervisor who will approve this batch.");
            if (createdById.HasValue && supervisorId.Value == createdById.Value)
                return (false, "You cannot assign yourself as the supervisor of a sowing you record (you could not approve it).");
            if (!eligibleSupervisorIds.Contains(supervisorId.Value))
                return (false, "The selected supervisor is not an active user who can approve sowings.");
            return (true, null);
        }

        // The supervisor can be changed only while nothing has been approved.
        public static bool CanChangeSupervisor(string? status, decimal approvedReady, decimal approvedWastage)
            => status == "Sown" && approvedReady == 0 && approvedWastage == 0;

        // Expected Ready Date = Sowing Date + the variety's growing days
        // (dbo.PlantSpecies.ReadyStockDays). Null when the variety has no
        // growing days configured (sowing is then refused).
        public static DateTime? ExpectedReadyDate(DateTime sowingDate, int? growingDays)
            => growingDays is > 0 ? sowingDate.Date.AddDays(growingDays.Value) : null;

        // Seeds and plants are counted in whole units throughout the seedling
        // workflow (also enforced by the *_WholeQuantities CHECK constraints,
        // Database/PhaseB_SeedlingRules.sql).
        public static bool IsWholeNumber(decimal quantity)
            => quantity == decimal.Truncate(quantity);

        // Where a sowing grows. Polyhouse/Area configuration is optional for
        // now: with no growing Area chosen, the batch is recorded at the Area
        // its seed lot is held in (the Main Office), or at the chosen
        // Polyhouse's Area when that Polyhouse has one. A Polyhouse that is
        // assigned to an Area can only be used for sowings in that Area; a
        // Polyhouse without an Area can be recorded with any Area.
        public static (bool Ok, int AreaId, int? PolyhouseId, string? Error) ResolveGrowingLocation(
            int seedLotAreaId, int? requestedAreaId, int? polyhouseId, int? polyhouseAreaId)
        {
            var polyhouse = polyhouseId is > 0 ? polyhouseId : null;
            var areaId = requestedAreaId is > 0
                ? requestedAreaId.Value
                : (polyhouse.HasValue && polyhouseAreaId.HasValue ? polyhouseAreaId.Value : seedLotAreaId);
            if (areaId <= 0)
                return (false, 0, null, "The growing Area could not be determined.");
            if (polyhouse.HasValue && polyhouseAreaId.HasValue && polyhouseAreaId.Value != areaId)
                return (false, 0, null, "Selected Polyhouse belongs to a different Area.");
            return (true, areaId, polyhouse, null);
        }

        // Nobody approves a sowing they recorded themselves (segregation of
        // duties). Compared by user id; the username is only used for rows
        // recorded without an id.
        public static bool IsOwnSowing(int? createdById, string? createdBy, int? approverId, string? approverName)
        {
            if (createdById.HasValue && approverId.HasValue)
                return createdById.Value == approverId.Value;
            return !string.IsNullOrWhiteSpace(createdBy)
                   && !string.IsNullOrWhiteSpace(approverName)
                   && string.Equals(createdBy.Trim(), approverName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public const string OwnSowingMessage =
            "You recorded this sowing, so you cannot approve it. Another Sowing Supervisor or a System Administrator must approve it.";

        // Available Main Office seed after a sowing, or an error when the
        // sowing would make stock negative.
        public static (bool Ok, decimal Remaining, string? Error) CheckSeedAvailability(decimal physical, decimal inTransit, decimal quantitySown)
        {
            if (quantitySown <= 0)
                return (false, physical - inTransit, "Seed Quantity must be greater than zero.");
            if (!IsWholeNumber(quantitySown))
                return (false, physical - inTransit, "Seed Quantity must be a whole number.");
            var available = physical - inTransit;
            if (quantitySown > available)
                return (false, available, $"Insufficient Main Office seed stock (available {available:N2}, requested {quantitySown:N2}).");
            return (true, available - quantitySown, null);
        }

        // Supervisor Approval (closes the sowing):
        //   Wastage = Sown - (already approved Ready + Ready now)
        //   Ready + Wastage must equal Sown; nothing may be negative;
        //   a reason is required whenever Wastage > 0.
        // Supervisor Approval by ACTUAL READY TRAYS (the supervisor's only
        // quantity input). The cavity is the SOWING's (never chosen again):
        //   Actual Ready Seedlings = Actual Ready Trays x sowing cavity
        //   Wastage                = Seeds Used - Actual Ready Seedlings
        //   Wastage %              = Wastage / Seeds Used x 100 (2 decimals, display)
        // Seeds Used = SeedSowings.QuantitySown (complete trays only; leftover
        // seeds stayed in the seed lot and are never counted here). There is
        // no fixed wastage percentage: it is always the recorded difference.
        public static (bool Ok, int ActualTrays, decimal ActualSeedlings, decimal Wastage, decimal WastagePercent, string? Error) ComputeTrayApproval(
            decimal seedsUsed, int? sowingTrays, string? cavityType, decimal alreadyReady, decimal alreadyWasted,
            decimal actualTrays, string? wastageReason)
        {
            var cavity = CavityCount(cavityType);
            if (cavity == null)
                return (false, 0, 0, 0, 0, "The sowing has no valid tray cavity; it cannot be approved by trays.");
            if (actualTrays <= 0)
                return (false, 0, 0, 0, 0, "Actual Ready Trays must be at least 1 complete tray.");
            if (!IsWholeNumber(actualTrays))
                return (false, 0, 0, 0, 0, "Actual Ready Trays must be a whole number of complete trays.");
            if (!sowingTrays.HasValue || sowingTrays.Value < 1)
                return (false, 0, 0, 0, 0, "The sowing has no tray quantity recorded; it cannot be approved by trays.");
            if (actualTrays > sowingTrays.Value)
                return (false, 0, 0, 0, 0, $"Actual Ready Trays ({actualTrays:N0}) cannot exceed the {sowingTrays.Value:N0} trays sown.");

            var trays = (int)actualTrays;
            var seedlings = trays * (decimal)cavity.Value;
            // Same balance rule as before (Ready + Wastage = Sown, no double
            // approval, reason required when wastage > 0), on the derived seedlings.
            var (ok, wastage, error) = ComputeApproval(seedsUsed, alreadyReady, alreadyWasted, seedlings, wastageReason);
            if (!ok)
                return (false, trays, seedlings, wastage, WastagePercent(wastage, seedsUsed), error);
            return (true, trays, seedlings, wastage, WastagePercent(wastage, seedsUsed), null);
        }

        public static decimal WastagePercent(decimal wastage, decimal seedsUsed)
            => seedsUsed > 0 ? Math.Round(wastage / seedsUsed * 100m, 2, MidpointRounding.AwayFromZero) : 0m;

        public static (bool Ok, decimal Wastage, string? Error) ComputeApproval(
            decimal quantitySown, decimal alreadyReady, decimal alreadyWasted, decimal readyNow, string? wastageReason)
        {
            if (readyNow < 0)
                return (false, 0, "Actual Ready Quantity cannot be negative.");
            if (!IsWholeNumber(readyNow))
                return (false, 0, "Actual Ready Quantity must be a whole number of plants.");
            var remaining = quantitySown - alreadyReady - alreadyWasted;
            if (remaining <= 0)
                return (false, 0, "This sowing has already been fully approved.");
            if (readyNow > remaining)
                return (false, 0, $"Actual Ready Quantity ({readyNow:N2}) cannot exceed the sown quantity still to be approved ({remaining:N2}).");
            var wastage = remaining - readyNow;
            if (wastage > 0 && !IsValidWastageReason(wastageReason))
                return (false, wastage, $"A Wastage Reason is required when wastage is {wastage:N2} (one of: {string.Join(", ", WastageReasons)}).");
            if (wastage == 0 && !string.IsNullOrWhiteSpace(wastageReason) && !IsValidWastageReason(wastageReason))
                return (false, 0, "Invalid Wastage Reason.");
            if (alreadyReady + readyNow + alreadyWasted + wastage != quantitySown)
                return (false, wastage, "Ready + Wastage must equal the Sown Quantity.");
            return (true, wastage, null);
        }
    }
}
