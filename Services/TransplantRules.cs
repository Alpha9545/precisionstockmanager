namespace PlantStockManager.Services
{
    // Transplanting Ready Stock in a Mother Plant Area into (larger) cavity trays
    // (dbo.ReadyStockTransplants). A TRANSFORMATION, never a multiplication:
    //   source batch   - (transplanted + lost)          'TransplantOut' + 'TransplantLoss'
    //   output batch   + transplanted (target cavity)   'TransplantIn'  (a NEW Ready Stock batch)
    //   Tray Stock     - output trays of the target cavity, from the TARGET polyhouse ('Transplant')
    // Output trays = CEILING(transplanted / target cavity): exact plant counts, the last tray may be
    // partly filled (business decision 2026-10-09); no plant is invented to fill a tray.
    // No growing material is deducted: Material Stock exists only for Main Office Areas and no
    // rule says transplanting uses cocopeat / pitmos.
    public static class TransplantRules
    {
        public const string MotherPlantAreaType = "MotherPlant";
        public const string ReferenceType = "ReadyStockTransplant";
        public const string StatusCompleted = "Completed";
        public const string StatusReversed = "Reversed";

        // The cavity list is the nursery's existing closed list (DirectSowingRules.CavityTypes,
        // also the Tray Stock tray sizes) -- not a second list.
        public static IReadOnlyList<string> TargetCavityTypes => DirectSowingRules.CavityTypes;

        public record Plan(int OutputTrays, int FullTrays, decimal PlantsInLastTray, int Cavity);

        private static string PartialTrayMessage(int batchQuantity, string action)
            => $"This batch is one partial tray of {QuantityFormat.Qty(batchQuantity)} seedlings and is taken only as a whole: {action} ({QuantityFormat.Qty(batchQuantity)}).";

        // sourceWholeTrayCavity: set when the SOURCE is a sowing batch (whole trays only -- e.g. a
        // Mother Plant Area's own cutting sowing): the plants taken (selected) must then be whole
        // source trays. NULL for a received / transplanted batch (exact plant counts).
        // sourcePartialTray (2026-10-31): the source is a partial-tray batch (PartialTrayRules) and sourceWholeTrayCavity is its
        // whole quantity -- it is taken only as a whole.
        public static (bool Ok, Plan? Plan, string? Error) PlanTransplant(
            decimal selected, decimal transplanted, decimal lost, decimal sourceAvailable, string? targetCavityType,
            int? sourceWholeTrayCavity = null, bool sourcePartialTray = false)
        {
            var cavity = DirectSowingRules.CavityCount(targetCavityType);
            if (cavity is null or <= 0)
                return (false, null, $"Target tray must be one of: {string.Join(", ", TargetCavityTypes)}.");
            if (!DirectSowingRules.IsWholeNumber(selected) || !DirectSowingRules.IsWholeNumber(transplanted) || !DirectSowingRules.IsWholeNumber(lost))
                return (false, null, "Quantities must be whole numbers of plants.");
            if (transplanted <= 0)
                return (false, null, "Successfully transplanted must be greater than zero.");
            if (lost < 0)
                return (false, null, "Lost / damaged cannot be negative.");
            if (selected != transplanted + lost)
                return (false, null, $"Selected ({QuantityFormat.Qty(selected)}) must equal transplanted ({QuantityFormat.Qty(transplanted)}) + lost / damaged ({QuantityFormat.Qty(lost)}).");
            if (selected > sourceAvailable)
                return (false, null, $"Only {QuantityFormat.Qty(sourceAvailable)} plants of this batch are available.");
            if (sourceWholeTrayCavity is > 0 && selected % sourceWholeTrayCavity.Value != 0)
                return (false, null, sourcePartialTray
                    ? PartialTrayMessage(sourceWholeTrayCavity.Value, "select all of them (transplanted + lost)")
                    : $"This batch is a sowing batch kept in whole {sourceWholeTrayCavity.Value}-cavity trays: select a multiple of {sourceWholeTrayCavity.Value} plants (transplanted + lost).");
            var trays = (int)Math.Ceiling(transplanted / cavity.Value);
            var full = (int)Math.Floor(transplanted / cavity.Value);
            return (true, new Plan(trays, full, transplanted - full * cavity.Value, cavity.Value), null);
        }

        // 2026-10-16 (owner): Mother Plant transplanting works like Main Office bed transplanting. The user enters the
        // seedlings to PROCESS (default: all available) and the cavity; C = cavity, Q = processed:
        //   complete trays = floor(Q / C), transplanted = trays x C, WASTE = Q - transplanted (reason required),
        //   left in the batch = available - Q (the unprocessed part is NOT waste and stays available).
        // Example: 170 received, 150 cavity, process 170 -> 1 tray, 150 transplanted, 20 waste, 0 left.
        // Fewer seedlings than one tray make no tray batch: the user may only close them as waste, explicitly
        // confirmed (discardAll) and with a reason -- never an empty / zero tray batch.
        public const string WasteOnlyConfirmation =
            "These seedlings are dead / unusable: record all of them as waste (no tray batch is made).";
        public const int MaxWasteReasonLength = 500;

        public record ProcessPlan(int Trays, decimal Transplanted, decimal Waste, decimal Processed, decimal LeftInBatch, int Cavity)
        {
            public bool MakesTrayBatch => Trays > 0;
            public string Summary(string? cavityType, bool done = false)
            {
                var willBe = done ? "were" : "will be";
                var left = LeftInBatch > 0 ? $" {QuantityFormat.Qty(LeftInBatch)} seedlings {(done ? "stayed" : "stay")} in the batch." : "";
                return Trays > 0
                    ? $"{QuantityFormat.Qty(Transplanted)} seedlings {willBe} transplanted into {Trays} {cavityType} trays"
                      + (Waste > 0 ? $"; the remaining {QuantityFormat.Qty(Waste)} {willBe} recorded as waste." : ".") + left
                    : $"No tray batch: all {QuantityFormat.Qty(Waste)} seedlings {willBe} recorded as waste." + left;
            }
        }

        // sourceWholeTrayCavity: the source is a sowing batch kept in whole trays -- Q must be whole source trays.
        // sourcePartialTray: as PlanTransplant -- a partial-tray batch is processed only as a whole.
        public static (bool Ok, ProcessPlan? Plan, string? Error) PlanProcessing(
            decimal available, decimal processQuantity, string? targetCavityType, bool discardAll, string? wasteReason,
            int? sourceWholeTrayCavity = null, bool sourcePartialTray = false)
        {
            if (processQuantity <= 0 || !DirectSowingRules.IsWholeNumber(processQuantity))
                return (false, null, "Seedlings to process must be a whole number greater than zero.");
            if (processQuantity > available)
                return (false, null, $"Only {QuantityFormat.Qty(Math.Max(available, 0))} seedlings of this batch are available.");
            if (sourceWholeTrayCavity is > 0 && processQuantity % sourceWholeTrayCavity.Value != 0)
                return (false, null, sourcePartialTray
                    ? PartialTrayMessage(sourceWholeTrayCavity.Value, "process all of them")
                    : $"This batch is a sowing batch kept in whole {sourceWholeTrayCavity.Value}-cavity trays: process a multiple of {sourceWholeTrayCavity.Value} seedlings.");
            var reason = wasteReason?.Trim();
            if (reason?.Length > MaxWasteReasonLength)
                return (false, null, $"The waste reason is too long ({MaxWasteReasonLength} characters at most).");
            if (discardAll)
            {
                if (string.IsNullOrEmpty(reason))
                    return (false, null, "Enter the reason the seedlings are discarded as waste.");
                return (true, new ProcessPlan(0, 0, processQuantity, processQuantity, available - processQuantity, 0), null);
            }
            var cavity = DirectSowingRules.CavityCount(targetCavityType);
            if (cavity is null or <= 0)
                return (false, null, $"Target tray must be one of: {string.Join(", ", TargetCavityTypes)}.");
            var trays = (int)decimal.Floor(processQuantity / cavity.Value);
            if (trays == 0)
                return (false, null, $"{QuantityFormat.Qty(processQuantity)} seedlings do not fill one {targetCavityType} tray ({cavity.Value}). "
                    + "Choose a smaller cavity, or confirm that these seedlings are discarded as waste.");
            var transplanted = trays * (decimal)cavity.Value;
            var waste = processQuantity - transplanted;
            if (waste > 0 && string.IsNullOrEmpty(reason))
                return (false, null, $"{QuantityFormat.Qty(waste)} seedlings do not fill a complete tray and will be recorded as waste: enter the reason.");
            return (true, new ProcessPlan(trays, transplanted, waste, processQuantity, available - processQuantity, cavity.Value), null);
        }

        public record Location(string? AreaType, bool AreaActive, int AreaId);

        public static (bool Ok, string? Error) ValidateLocations(Location source, Location target, int? targetPolyhouseAreaId)
        {
            if (source.AreaType != MotherPlantAreaType || !source.AreaActive)
                return (false, "Only Ready Stock in an active Mother Plant Area can be transplanted.");
            if (target.AreaType != MotherPlantAreaType || !target.AreaActive)
                return (false, "The target must be an active Mother Plant Area.");
            if (targetPolyhouseAreaId != target.AreaId)
                return (false, "The target Polyhouse does not belong to the target Mother Plant Area.");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateDate(DateTime transplantDate, DateTime todayIndia, DateTime sourceAvailableFromIndia)
        {
            if (transplantDate.Date > todayIndia.Date)
                return (false, "The transplanting date cannot be in the future.");
            if (transplantDate.Date < sourceAvailableFromIndia.Date)
                return (false, "The transplanting date cannot be before the stock was available here.");
            return (true, null);
        }

        // availableAsOfIndia (2026-10-10): for a back-dated transplanting, the trays counted are those
        // available from that date onward (TrayStockRepository.AvailableFromAsync).
        // where (2026-10-14): "the target Polyhouse", or "this Mother Plant Area" (its Area tray pool).
        public static (bool Ok, string? Error) ValidateTrays(int required, decimal available, string targetCavityType, DateTime? availableAsOfIndia = null,
            string where = "the target Polyhouse")
            => available >= required
                ? (true, null)
                : (false, availableAsOfIndia.HasValue
                    ? $"Not enough empty {targetCavityType} trays in {where} on {availableAsOfIndia:dd/MM/yyyy}: {required} needed, "
                      + $"{QuantityFormat.Qty(Math.Max(available, 0))} available from that date onward (counting every later movement). Record the trays' arrival first, or use a later date."
                    : $"Not enough empty {targetCavityType} trays in {where}: {required} needed, {QuantityFormat.Qty(available)} in Tray Stock. Add Tray Stock first.");

        // A transplanting can be reversed only while its output batch is untouched.
        public record Output(decimal Quantity, decimal Reserved, decimal Dispatched, decimal InTransit, int Children);

        public static (bool Ok, string? Error) ValidateReverse(string status, decimal transplanted, Output output, string? reason)
        {
            if (status != StatusCompleted)
                return (false, "This transplanting is already reversed.");
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "Enter the reason for the reversal.");
            if (reason.Trim().Length > 500)
                return (false, "The reason is too long (500 characters at most).");
            if (output.Children > 0 || output.Quantity != transplanted || output.Reserved > 0 || output.Dispatched > 0 || output.InTransit > 0)
                return (false, "The transplanted stock has already been used (booked, dispatched, moved, transplanted again or changed), so this transplanting cannot be reversed.");
            return (true, null);
        }
    }
}
