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

        // Physical Tray Stock consumed by a sowing = CEILING(quantity / cavity):
        // the sub-tray remainder still occupies a physical tray. Deliberately
        // NOT NumberOfTrays (FLOOR, below), which drives the Sown/Wastage
        // plant-count split. e.g. 950 at 24 Cavity -> 39 complete trays, 40
        // physical trays. 0 for an invalid cavity or a non-positive quantity.
        public static decimal PhysicalTraysRequired(decimal quantity, string? cavityType)
        {
            var cavity = CavityCount(cavityType);
            return cavity is > 0 && quantity > 0 ? Math.Ceiling(quantity / cavity.Value) : 0;
        }

        // NoOfTrays = FLOOR(SeedQuantity / TraySize) -- complete trays only,
        // never rounded to nearest and never rounded up. SeedsUsed = the seeds
        // that fill those complete trays; RemainingSeeds = the rest. This is
        // pure arithmetic only -- it does not itself touch any stock ledger
        // or decide what happens to RemainingSeeds; that is each caller's own
        // business decision (SeedSowingRepository.InsertAsync/
        // InsertFromCuttingAsync both waste it automatically in the same
        // transaction as the 'Sown' deduction -- see their own comments).
        // The server always recalculates with this function; any tray count
        // sent by the browser is ignored.
        // The same rule serves cutting tray sowing (quantityLabel "Cutting
        // Quantity"): only complete trays are produced from either material.
        public static (bool Ok, int Trays, decimal SeedsUsed, decimal RemainingSeeds, string? Error) CalculateTrays(
            decimal seedQuantity, string? cavityType, string quantityLabel = SeedQuantityLabel)
        {
            var cavity = CavityCount(cavityType);
            if (cavity == null)
                return (false, 0, 0, 0, $"Tray size must be one of: {string.Join(", ", CavityTypes)}.");
            if (seedQuantity <= 0)
                return (false, 0, 0, 0, $"{quantityLabel} must be greater than zero.");
            var trays = decimal.Floor(seedQuantity / cavity.Value);
            if (trays < 1)
                return (false, 0, 0, seedQuantity, $"{quantityLabel} ({QuantityFormat.Qty(seedQuantity)}) is less than one complete {cavity.Value}-cavity tray.");
            if (trays > int.MaxValue)
                return (false, 0, 0, 0, $"{quantityLabel} is too large.");
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
        public const string SeedQuantityLabel = "Seed Quantity";
        public const string CuttingQuantityLabel = "Cutting Quantity";

        public static (bool Ok, int Trays, decimal SeedsUsed, decimal RemainingSeeds, decimal AvailableAfter, string? Error) PlanSowing(
            decimal seedQuantity, string? cavityType, decimal physical, decimal inTransit,
            string quantityLabel = SeedQuantityLabel, string stockLabel = "Main Office seed stock")
        {
            var available = physical - inTransit;
            if (seedQuantity <= 0)
                return (false, 0, 0, 0, available, $"{quantityLabel} must be greater than zero.");
            if (!IsWholeNumber(seedQuantity))
                return (false, 0, 0, 0, available, $"{quantityLabel} must be a whole number.");
            var (traysOk, trays, seedsUsed, remaining, trayError) = CalculateTrays(seedQuantity, cavityType, quantityLabel);
            if (!traysOk)
                return (false, 0, 0, 0, available, trayError);
            var (enough, _, stockError) = CheckSeedAvailability(physical, inTransit, seedQuantity, quantityLabel, stockLabel);
            if (!enough)
                return (false, 0, 0, 0, available, stockError);
            return (true, trays, seedsUsed, remaining, available - seedsUsed, null);
        }

        // ---- Approval authority --------------------------------------------
        // A sowing is approved ONLY by the supervisor assigned to it
        // (SeedSowings.SupervisorId). Holding ReadyStock.Confirm is necessary
        // (page permission) but not sufficient. A SEED sowing (sourceType null
        // or 'Seed') is never approved by the person who recorded it. A CUTTING
        // TRAY sowing may be: its recorder can be chosen as the supervisor
        // (SowingSupervisorRules) and then approves it as that supervisor -- the
        // assigned-supervisor check below still applies to them.
        // Applied by ReadyConfirmationRepository.ConfirmAsync under the sowing's
        // row lock, and by the pages for their messages.
        public static (bool Ok, string? Error) CanApprove(
            int? assignedSupervisorId, int? createdById, string? createdBy, int? approverId, string? approverName,
            string? sourceType = null)
        {
            if (!approverId.HasValue)
                return (false, "Your user could not be identified.");
            var recorderMayApprove = sourceType == Models.SeedSowing.SourceCutting;
            if (!recorderMayApprove && IsOwnSowing(createdById, createdBy, approverId, approverName))
                return (false, OwnSowingMessage);
            if (!assignedSupervisorId.HasValue)
                return (false, NoSupervisorMessage);
            if (assignedSupervisorId.Value != approverId.Value)
                return (false, NotAssignedMessage);
            return (true, null);
        }

        public const string NoSupervisorMessage =
            "No supervisor is assigned to this sowing, so it cannot be approved. Cancel it and record it again with its Sowing Supervisor.";
        public const string NotAssignedMessage =
            "Only the supervisor assigned to this sowing can approve it.";

        // Who may be ASSIGNED as a sowing's supervisor: an active Sowing
        // Supervisor (eligible list from UserRoleRepository.GetUsersInRoleAsync),
        // and never the person who records the sowing (they could not approve
        // it). The assignment is final: it cannot be changed after the sowing
        // is saved (TR_SeedSowings_ImmutableTrayData), so nobody can re-assign a
        // sowing to themselves to approve it.
        public static (bool Ok, string? Error) ValidateSupervisorAssignment(
            int? supervisorId, int? createdById, IReadOnlyCollection<int> eligibleSupervisorIds)
        {
            if (!supervisorId.HasValue || supervisorId.Value <= 0)
                return (false, "Supervisor is required: choose the Sowing Supervisor who will approve this batch.");
            if (createdById.HasValue && supervisorId.Value == createdById.Value)
                return (false, "You cannot assign yourself as the supervisor of a sowing you record (you could not approve it).");
            if (!eligibleSupervisorIds.Contains(supervisorId.Value))
                return (false, "The selected supervisor is not an active Sowing Supervisor.");
            return (true, null);
        }

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
            "You recorded this sowing, so you cannot approve it. Only the Sowing Supervisor assigned to this sowing can approve it.";

        // Available Main Office seed after a sowing, or an error when the
        // sowing would make stock negative.
        public static (bool Ok, decimal Remaining, string? Error) CheckSeedAvailability(decimal physical, decimal inTransit, decimal quantitySown,
            string quantityLabel = SeedQuantityLabel, string stockLabel = "Main Office seed stock")
        {
            if (quantitySown <= 0)
                return (false, physical - inTransit, $"{quantityLabel} must be greater than zero.");
            if (!IsWholeNumber(quantitySown))
                return (false, physical - inTransit, $"{quantityLabel} must be a whole number.");
            var available = physical - inTransit;
            if (quantitySown > available)
                return (false, available, $"Insufficient {stockLabel} (available {QuantityFormat.Qty(available)}, requested {QuantityFormat.Qty(quantitySown)}).");
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
            // A higher actual count than originally sown/estimated is a
            // legitimate business outcome (e.g. germination undercounted) and
            // is allowed -- ComputeApproval below closes the batch with zero
            // wastage in that case rather than a negative one.

            var trays = (int)actualTrays;
            var seedlings = trays * (decimal)cavity.Value;
            // Same balance rule as before (Ready + Wastage = Sown, no double
            // approval, reason required when wastage > 0), on the derived seedlings.
            var (ok, wastage, error) = ComputeApproval(seedsUsed, alreadyReady, alreadyWasted, seedlings, wastageReason);
            if (!ok)
                return (false, trays, seedlings, wastage, WastagePercent(wastage, seedsUsed), error);
            return (true, trays, seedlings, wastage, WastagePercent(wastage, seedsUsed), null);
        }

        // ---- Cutting Tray Sowing: more ready trays than were sown -------------
        // UNITS -- read this first. The supervisor ALWAYS enters TRAYS
        // (ActualReadyTrays). Every stock comparison is made in CUTTINGS:
        //   ReadyCuttings = ActualReadyTrays x cavity          (cavity = cuttings per tray)
        //   Available     = Physical - In-Transit               (cuttings)
        // Example: cavity 24, 500 trays = 12,000 cuttings; "600" in the approval
        // field means 600 TRAYS = 14,400 cuttings.
        //
        // Cutting sowings only. The sowing's own cuttings (QuantitySown, in
        // cuttings = SownTrays x cavity) were already taken out of Cutting Stock
        // when it was recorded and are never taken again. When ActualReadyTrays is
        // more than the trays the sowing still expects, only the EXTRA is taken
        // from the same Cutting Stock pool at approval:
        //   ExtraTrays    = ActualReadyTrays - SownTrays                (first approval)
        //   ExtraCuttings = ExtraTrays x cavity
        //                 = ReadyCuttings - (QuantitySown - already approved)
        // ActualReadyTrays <= SownTrays: nothing extra, no stock check.
        // Seed sowings (and any other source) never take anything extra: 0.
        // stillExpectedCuttings = QuantitySown - ConfirmedReadyQuantity - WastageQuantity (cuttings).
        public static decimal ExtraCuttingsNeeded(string? sourceType, decimal actualReadyTrays, string? cavityType, decimal stillExpectedCuttings)
        {
            if (sourceType != Models.SeedSowing.SourceCutting || actualReadyTrays <= 0)
                return 0;
            var cavity = CavityCount(cavityType);
            if (cavity == null)
                return 0;
            var readyCuttings = actualReadyTrays * cavity.Value;
            return readyCuttings > stillExpectedCuttings ? readyCuttings - stillExpectedCuttings : 0;
        }

        // The same extra, counted in TRAYS (extra cuttings / cavity): for display and messages.
        public static decimal ExtraTrays(decimal extraCuttings, string? cavityType)
        {
            var cavity = CavityCount(cavityType);
            return cavity == null || extraCuttings <= 0 ? 0 : extraCuttings / cavity.Value;
        }

        // The stock rule for that extra, entirely in CUTTINGS: the TOTAL ready
        // cuttings (ActualReadyTrays x cavity) may not be more than the pool's
        // AVAILABLE Cutting Stock (Physical - In-Transit; in-transit cuttings can
        // never be used), and the extra cuttings themselves must obviously be
        // covered too (never negative stock). Nothing to check when there is no
        // extra. actualReadyTrays / cavity are optional and only make the message
        // spell out the tray -> cutting conversion.
        public static (bool Ok, decimal Available, string? Error) CheckCuttingOverage(
            decimal actualReadyCuttings, decimal extraCuttings, decimal physical, decimal inTransit,
            decimal? actualReadyTrays = null, int? cavity = null)
        {
            var available = physical - inTransit;
            if (extraCuttings <= 0)
                return (true, available, null);
            if (actualReadyCuttings > available || extraCuttings > available)
            {
                var ready = actualReadyTrays.HasValue && cavity.HasValue
                    ? $"{QuantityFormat.Qty(actualReadyTrays.Value)} Actual Ready Trays x {cavity.Value}-cavity = {QuantityFormat.Qty(actualReadyCuttings)} cuttings"
                    : $"The Actual Ready quantity ({QuantityFormat.Qty(actualReadyCuttings)} cuttings)";
                var extraTrays = cavity.HasValue && cavity.Value > 0 ? $" ({extraCuttings / cavity.Value:N0} extra trays)" : "";
                return (false, available,
                    $"{ready} is more than the available Cutting Stock ({QuantityFormat.Qty(available)} cuttings, in-transit excluded). "
                    + $"It would need {QuantityFormat.Qty(extraCuttings)} extra cuttings{extraTrays} beyond the cuttings already sown, and only available stock can be used.");
            }
            return (true, available, null);
        }

        // The most TRAYS the supervisor can enter right now: never fewer than the
        // trays the sowing still expects (those need no extra stock), otherwise as
        // many WHOLE trays as the available stock covers in total
        // (floor(Available cuttings / cavity)). Always consistent with
        // ExtraCuttingsNeeded + CheckCuttingOverage.
        public static decimal MaxReadyTrays(decimal stillExpectedCuttings, string? cavityType, decimal availableCuttings)
        {
            var cavity = CavityCount(cavityType);
            if (cavity == null)
                return 0;
            var expectedTrays = decimal.Floor(Math.Max(stillExpectedCuttings, 0) / cavity.Value);
            var stockTrays = decimal.Floor(Math.Max(availableCuttings, 0) / cavity.Value);
            return Math.Max(expectedTrays, stockTrays);
        }

        public static decimal WastagePercent(decimal wastage, decimal seedsUsed)
            => seedsUsed > 0 ? Math.Round(wastage / seedsUsed * 100m, 2, MidpointRounding.AwayFromZero) : 0m;

        // ---- Survivorship checkpoints (Phase H) -----------------------------
        // Trays Alive / Hardening Alive: optional, informational counts
        // recorded after sowing (the useful part of the retired legacy
        // dbo.SeedEntries pipeline). They never gate stock, wastage or Ready
        // Confirmation -- only a sanity check that a provided value is a
        // non-negative whole number and cannot exceed what was actually sown
        // (a checkpoint can only report on the batch it was taken from).
        public static (bool Ok, string? Error) ValidateSurvivorshipCheckpoint(decimal? quantity, decimal quantitySown, string label)
        {
            if (!quantity.HasValue)
                return (true, null);
            if (quantity.Value < 0)
                return (false, $"{label} cannot be negative.");
            if (!IsWholeNumber(quantity.Value))
                return (false, $"{label} must be a whole number.");
            if (quantity.Value > quantitySown)
                return (false, $"{label} cannot exceed the Sowing's Quantity Used ({QuantityFormat.Qty(quantitySown)}).");
            return (true, null);
        }

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
            // Actual Ready may legitimately exceed what was still expected
            // (e.g. the original sowing count under-estimated the batch) --
            // that closes the batch with zero wastage, never negative.
            // Only an actual count BELOW what was expected is still wastage,
            // and still needs the same Wastage Reason it always did.
            var wastage = readyNow >= remaining ? 0 : remaining - readyNow;
            if (wastage > 0 && !IsValidWastageReason(wastageReason))
                return (false, wastage, $"A Wastage Reason is required when wastage is {QuantityFormat.Qty(wastage)} (one of: {string.Join(", ", WastageReasons)}).");
            if (wastage == 0 && !string.IsNullOrWhiteSpace(wastageReason) && !IsValidWastageReason(wastageReason))
                return (false, 0, "Invalid Wastage Reason.");
            if (alreadyReady + readyNow + alreadyWasted + wastage < quantitySown)
                return (false, wastage, "Ready + Wastage must account for at least the Sown Quantity.");
            return (true, wastage, null);
        }
    }
}
