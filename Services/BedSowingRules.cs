using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // 2026-10-12: the pure rules of Bed Sowing (Data/BedSowingRepository.cs applies them under
    // its locks; the pages use them for messages and previews).
    //
    //   Bed Sowing        seed -> bed at the Bed Sowing Polyhouse; seed lot deducted; no cavity,
    //                     no trays, no Ready Stock.
    //   Ready Date        = ORIGINAL bed sowing date + the variety's Ready Days
    //                     (dbo.PlantSpecies.ReadyStockDays, snapshotted). No Ready Days -> refused;
    //                     nothing is ever substituted.
    //   Transplant        bed -> COMPLETE cavity trays only (the existing whole-tray rule of every
    //                     tray sowing): Quantity = Output Trays x cavity. A remainder stays in the
    //                     bed, or is recorded as a bed loss.
    //   Conservation      Quantity = Remaining + Transplanted + Lost (never negative).
    public static class BedSowingRules
    {
        public const string TransplantPermission = "BedSowing.Transplant";
        public const string SourceLabel = "Bed Sowing";

        public const string NotTransplanterMessage =
            "Only the Bed Sowing Transplanter of this bed's Area can transplant bed seedlings or record bed losses. Administrators can view, cancel untouched beds and reverse operations, but not transplant.";
        public const string ProtectedRoleMessage =
            "The Bed Sowing Transplanter role (permission BedSowing.Transplant) belongs to one named person and is assigned only by a reviewed database script, never on this page.";
        public const string NotReadyYetMessageFormat =
            "This tray batch came from a Bed Sowing and is not ready yet: it can be approved from its Ready Date, {0:dd/MM/yyyy} (transplant date + Ready Days).";
        public const string TransplantNotReadyYetMessageFormat =
            "This tray batch was transplanted from received Ready Stock and is not ready yet: it can be approved from its Ready Date, {0:dd/MM/yyyy} (transplant date + Ready Days).";
        public const string NotBedLocationMessage =
            "Bed Sowing is allowed only at the Bed Sowing Polyhouse of an active Main Office Area.";
        public const string NoReadyDaysMessageFormat =
            "Ready Days are not configured for variety '{0}'. Set them in Administration > Plant Master before bed sowing -- no Ready Date is assumed.";
        public const string GenericCancelBlockedMessage =
            "This tray batch was transplanted from a Bed Sowing. Reverse the transplant on the Bed Sowing page instead -- the seedlings go back to the bed.";

        public static string StatusLabel(string? status) => status switch
        {
            BedSowing.StatusPending => "Pending transplant",
            BedSowing.StatusPartial => "Partially transplanted",
            BedSowing.StatusCompleted => "Fully transplanted / closed",
            BedSowing.StatusCancelled => "Cancelled",
            _ => status ?? ""
        };

        // The status the totals mean (CK_BedSowings_Status repeats it in the database).
        public static string StatusFor(decimal quantity, decimal transplanted, decimal lost)
        {
            var used = transplanted + lost;
            if (used <= 0) return BedSowing.StatusPending;
            return used >= quantity ? BedSowing.StatusCompleted : BedSowing.StatusPartial;
        }

        public static decimal Remaining(decimal quantity, decimal transplanted, decimal lost) => quantity - transplanted - lost;

        // Original = Remaining + Transplanted + Lost, every part >= 0.
        public static bool IsConserved(decimal quantity, decimal transplanted, decimal lost, decimal remaining)
            => transplanted >= 0 && lost >= 0 && remaining >= 0 && quantity == remaining + transplanted + lost;

        // Ready Date = bed sowing date + Ready Days; null when Ready Days are missing / not positive.
        public static DateTime? ReadyDate(DateTime bedSowingDate, int? readyDays) => DirectSowingRules.ExpectedReadyDate(bedSowingDate, readyDays);

        public static (bool Ok, string? Error) ValidateBusinessDate(DateTime date, DateTime todayIndia, string label, DateTime? notBefore = null, string? notBeforeLabel = null)
        {
            if (date == default)
                return (false, $"{label} is required.");
            if (date.Date > todayIndia.Date)
                return (false, $"{label} cannot be in the future.");
            if (notBefore.HasValue && date.Date < notBefore.Value.Date)
                return (false, $"{label} cannot be before the {notBeforeLabel} ({notBefore.Value:dd/MM/yyyy}).");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateBedQuantity(decimal quantity)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.");
            if (!DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number.");
            return (true, null);
        }

        // Transplant: whole number, at most the bed's remaining seedlings, COMPLETE trays of a
        // listed cavity, the trays on the form (if any) equal to Quantity / cavity, a date from the
        // bed sowing date up to today (India). Returns the output trays.
        public static (bool Ok, int Trays, string? Error) ValidateTransplant(
            decimal remaining, decimal quantity, string? cavityType, int? postedTrays,
            DateTime transplantDate, DateTime bedSowingDate, DateTime todayIndia)
        {
            var (dateOk, dateError) = ValidateBusinessDate(transplantDate, todayIndia, "Transplant Date", bedSowingDate, "bed sowing date");
            if (!dateOk)
                return (false, 0, dateError);
            var cavity = DirectSowingRules.CavityCount(cavityType);
            if (cavity is not > 0)
                return (false, 0, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (quantity <= 0)
                return (false, 0, "Transplant Quantity must be greater than zero.");
            if (!DirectSowingRules.IsWholeNumber(quantity))
                return (false, 0, "Transplant Quantity must be a whole number.");
            if (quantity > remaining)
                return (false, 0, $"Only {QuantityFormat.Qty(Math.Max(remaining, 0))} seedlings remain in this bed; {QuantityFormat.Qty(quantity)} cannot be transplanted.");
            if (quantity % cavity.Value != 0)
            {
                var whole = decimal.Floor(quantity / cavity.Value);
                return (false, 0, $"{QuantityFormat.Qty(quantity)} seedlings do not fill complete {cavityType} trays. "
                    + (whole > 0 ? $"Transplant {QuantityFormat.Qty(whole * cavity.Value)} ({QuantityFormat.Qty(whole)} trays); " : "")
                    + "the rest stays in the bed or is recorded as a bed loss.");
            }
            var trays = (int)(quantity / cavity.Value);
            if (postedTrays.HasValue && postedTrays.Value != trays)
                return (false, 0, $"Output Trays must be {trays} for {QuantityFormat.Qty(quantity)} seedlings in {cavityType} trays.");
            return (true, trays, null);
        }

        // 2026-10-11 (owner): transplanting ALL the seedlings left in a bed accounts for every one of them --
        // N = remaining, C = cavity: FullTrays = N / C (integer division), Transplanted = FullTrays x C, and
        // the remainder (N - Transplanted) is recorded at once as a system-generated bed loss. Fewer than one
        // tray: no tray batch, everything is the loss. A PART of the bed must still fill complete trays
        // (ValidateTransplant, unchanged); the rest stays in the bed.
        public const string RemainderLossReason = "Untransplanted remainder after complete-tray transplant";

        public record TransplantPlan(int Trays, decimal Transplanted, decimal Waste)
        {
            public bool MakesTrayBatch => Trays > 0;
            // done = false: the preview before saving; true: what was recorded.
            public string Summary(string? cavityType, bool done = false)
            {
                var (willBe, willBeCreated) = done ? ("were", "was created") : ("will be", "will be created");
                return Trays > 0
                    ? $"{QuantityFormat.Qty(Transplanted)} seedlings {willBe} transplanted into {Trays} {cavityType} trays"
                      + (Waste > 0 ? $", and {QuantityFormat.Qty(Waste)} seedlings {willBe} recorded as waste." : ".")
                    : $"Not enough seedlings for one {cavityType} tray: no tray batch {willBeCreated}, and {QuantityFormat.Qty(Waste)} seedlings {willBe} recorded as waste.";
            }
        }

        public static (bool Ok, TransplantPlan? Plan, string? Error) PlanTransplant(
            decimal remaining, decimal quantity, string? cavityType, int? postedTrays,
            DateTime transplantDate, DateTime bedSowingDate, DateTime todayIndia)
        {
            var cavity = DirectSowingRules.CavityCount(cavityType);
            var wholeBed = remaining > 0 && quantity == remaining && DirectSowingRules.IsWholeNumber(quantity) && cavity is > 0;
            if (!wholeBed || quantity % cavity!.Value == 0)
            {
                var (ok, trays, error) = ValidateTransplant(remaining, quantity, cavityType, postedTrays, transplantDate, bedSowingDate, todayIndia);
                return ok ? (true, new TransplantPlan(trays, quantity, 0), null) : (false, null, error);
            }
            var (dateOk, dateError) = ValidateBusinessDate(transplantDate, todayIndia, "Transplant Date", bedSowingDate, "bed sowing date");
            if (!dateOk)
                return (false, null, dateError);
            var full = (int)decimal.Floor(quantity / cavity.Value);
            if (postedTrays.HasValue && postedTrays.Value != full)
                return (false, null, $"Output Trays must be {full} for {QuantityFormat.Qty(quantity)} seedlings in {cavityType} trays.");
            var transplanted = full * (decimal)cavity.Value;
            return (true, new TransplantPlan(full, transplanted, quantity - transplanted), null);
        }

        public static (bool Ok, string? Error) ValidateLoss(decimal remaining, decimal quantity, string? reason,
            DateTime lossDate, DateTime bedSowingDate, DateTime todayIndia)
        {
            var (dateOk, dateError) = ValidateBusinessDate(lossDate, todayIndia, "Loss Date", bedSowingDate, "bed sowing date");
            if (!dateOk)
                return (false, dateError);
            if (quantity <= 0)
                return (false, "Loss Quantity must be greater than zero.");
            if (!DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Loss Quantity must be a whole number.");
            if (quantity > remaining)
                return (false, $"Only {QuantityFormat.Qty(Math.Max(remaining, 0))} seedlings remain in this bed; a loss of {QuantityFormat.Qty(quantity)} cannot be recorded.");
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "A reason is required for a bed loss.");
            return (true, null);
        }

        // A transplant can be reversed only while its tray batch is untouched by the Sowing
        // Approval (still 'Sown', nothing approved, no wastage) -- the plants are still in the
        // trays, no Ready Stock exists. Otherwise cancel the approval first.
        public static (bool Ok, string? Error) CanReverseTransplant(string? sowingStatus, decimal approved, decimal wastage)
        {
            if (sowingStatus != "Sown" || approved > 0 || wastage > 0)
                return (false, "This tray batch already has a Supervisor Approval. Cancel the approval first (Sowing Approvals > History).");
            return (true, null);
        }

        // Sowing Approval of a tray batch from a bed: at most the seedlings still expected in its
        // trays (no over-approval -- seed and cutting batches keep their own rules).
        public static (bool Ok, string? Error) ValidateApprovalWithinTransplanted(string? sourceType, decimal readyQuantity, decimal stillExpected, string? cavityType)
        {
            if (!TrayBatchRules.IsTransplantedBatch(sourceType) || readyQuantity <= stillExpected)
                return (true, null);
            var cavity = DirectSowingRules.CavityCount(cavityType) ?? 1;
            return (false, $"This tray batch was made by a transplant and holds {QuantityFormat.Qty(stillExpected)} seedlings ({QuantityFormat.Qty(decimal.Floor(stillExpected / cavity))} trays) still to approve. "
                + "More cannot be approved; transplant more seedlings instead.");
        }

        // Sowing Approval of a tray batch made by a transplant -- from a bed, or (2026-10-13) from received
        // Ready Stock: not before its Ready Date (transplant date + Ready Days, India date). Direct Seed
        // Sowing and Cutting Sowing are unchanged. TR_ReadyConfirmations_BedReadyDate repeats it.
        public static (bool Ok, string? Error) ValidateApprovalReadyDate(string? sourceType, DateTime? expectedReadyDate, DateTime todayIndia)
        {
            if (!TrayBatchRules.IsTransplantedBatch(sourceType))
                return (true, null);
            var bed = sourceType == SeedSowing.SourceBedSowing;
            if (!expectedReadyDate.HasValue)
                return (false, $"This tray batch from {(bed ? "a Bed Sowing" : "a transplant")} has no Ready Date, so it cannot be approved.");
            return todayIndia.Date < expectedReadyDate.Value.Date
                ? (false, string.Format(bed ? NotReadyYetMessageFormat : TransplantNotReadyYetMessageFormat, expectedReadyDate.Value))
                : (true, null);
        }

        public static (bool Ok, string? Error) ValidateReason(string? reason, string what)
            => string.IsNullOrWhiteSpace(reason) ? (false, $"A reason is required to {what}.") : (true, null);
    }
}
