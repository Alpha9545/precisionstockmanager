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

        // Expected Ready Date = Sowing Date + the variety's growing days
        // (dbo.PlantSpecies.ReadyStockDays). Null when the variety has no
        // growing days configured (sowing is then refused).
        public static DateTime? ExpectedReadyDate(DateTime sowingDate, int? growingDays)
            => growingDays is > 0 ? sowingDate.Date.AddDays(growingDays.Value) : null;

        // Available Main Office seed after a sowing, or an error when the
        // sowing would make stock negative.
        public static (bool Ok, decimal Remaining, string? Error) CheckSeedAvailability(decimal physical, decimal inTransit, decimal quantitySown)
        {
            if (quantitySown <= 0)
                return (false, physical - inTransit, "Seed Quantity must be greater than zero.");
            var available = physical - inTransit;
            if (quantitySown > available)
                return (false, available, $"Insufficient Main Office seed stock (available {available:N2}, requested {quantitySown:N2}).");
            return (true, available - quantitySown, null);
        }

        // Supervisor Approval (closes the sowing):
        //   Wastage = Sown - (already approved Ready + Ready now)
        //   Ready + Wastage must equal Sown; nothing may be negative;
        //   a reason is required whenever Wastage > 0.
        public static (bool Ok, decimal Wastage, string? Error) ComputeApproval(
            decimal quantitySown, decimal alreadyReady, decimal alreadyWasted, decimal readyNow, string? wastageReason)
        {
            if (readyNow < 0)
                return (false, 0, "Actual Ready Quantity cannot be negative.");
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
