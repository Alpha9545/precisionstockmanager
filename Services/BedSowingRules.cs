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
    //                     tray sowing): Quantity = Output Trays x cavity. 2026-10-14: the rest is waste, the bed closes; was: a remainder stays in the
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
        // 2026-10-17: shown on a Bed Sowing batch whose (calculated) Ready Date has not come yet -- approval is allowed anyway.
        public const string BedAnyDateNoteFormat =
            "Calculated Ready Date {0:dd/MM/yyyy}. A tray batch from a Bed Sowing may be approved on any date by the Bed Sowing approver; the Ready Date stays recorded as it is.";
        // 2026-10-20: shown on a Mother Plant transplant batch approved before its (calculated) Ready Date.
        public const string TransplantAnyDateNoteFormat =
            "Calculated Ready Date {0:dd/MM/yyyy}. A tray batch transplanted in a Mother Plant Area may be approved on any date by an Area Supervisor of its Area; the Ready Date stays recorded as it is.";
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

        // Transplant quantity T: whole number, at most the bed's remaining seedlings, COMPLETE trays
        // of a listed cavity, the trays on the form (if any) equal to T / cavity, a date from the bed
        // sowing date up to today (India). Returns the output trays.
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
                return (false, 0, "Quantity to Transplant must be greater than zero.");
            if (!DirectSowingRules.IsWholeNumber(quantity))
                return (false, 0, "Quantity to Transplant must be a whole number.");
            if (quantity > remaining)
                return (false, 0, $"Only {QuantityFormat.Qty(Math.Max(remaining, 0))} seedlings remain in this bed; {QuantityFormat.Qty(quantity)} cannot be transplanted.");
            if (quantity % cavity.Value != 0)
            {
                var whole = decimal.Floor(quantity / cavity.Value);
                return (false, 0, $"{QuantityFormat.Qty(quantity)} seedlings do not fill complete {cavityType} trays: the Quantity to Transplant must be a multiple of {cavity.Value}. "
                    + (whole > 0 ? $"For example {QuantityFormat.Qty(whole * cavity.Value)} ({QuantityFormat.Qty(whole)} trays) -- the remaining seedlings are then recorded as waste." : ""));
            }
            var trays = (int)(quantity / cavity.Value);
            if (postedTrays.HasValue && postedTrays.Value != trays)
                return (false, 0, $"Output Trays must be {trays} for {QuantityFormat.Qty(quantity)} seedlings in {cavityType} trays.");
            return (true, trays, null);
        }

        // 2026-10-14 (owner): the Transplanter ENTERS the Quantity to Transplant T and every save closes the
        // bed. N = remaining, C = cavity: T must be a positive multiple of C and at most N; TrayCount = T / C;
        // the rest, WasteQuantity = N - T, is recorded at once as a system-generated bed loss in the same
        // transaction -- no seedling is left unaccounted for in the bed, and there is no separate loss form.
        // Waste-only closure (closeAsWaste, 2026-10-14 owner decision 2): the seedlings left are all dead / unusable --
        // whether fewer or more than one tray holds -- the Transplanter explicitly confirms it and gives a reason; ALL N
        // are recorded as one bed loss, no tray batch, no trays or material, and the bed closes.
        public const string RemainderLossReason = "Untransplanted remainder after complete-tray transplant";
        public const string WasteOnlyConfirmation =
            "All remaining seedlings are dead/unusable; record the entire remaining quantity as waste and close this bed.";
        public const int MaxWasteReasonLength = 500;

        public record TransplantPlan(int Trays, decimal Transplanted, decimal Waste)
        {
            public bool MakesTrayBatch => Trays > 0;
            // done = false: the preview before saving; true: what was recorded.
            public string Summary(string? cavityType, bool done = false)
            {
                var (willBe, willBeCreated) = done ? ("were", "was created") : ("will be", "will be created");
                return Trays > 0
                    ? $"{QuantityFormat.Qty(Transplanted)} seedlings {willBe} transplanted into {Trays} {cavityType} trays"
                      + (Waste > 0 ? $", and the remaining {QuantityFormat.Qty(Waste)} seedlings {willBe} recorded as waste." : ".")
                    : $"No tray batch {willBeCreated}: all {QuantityFormat.Qty(Waste)} remaining seedlings {willBe} recorded as waste and the bed {(done ? "was" : "will be")} closed.";
            }
        }

        public static (bool Ok, TransplantPlan? Plan, string? Error) PlanTransplant(
            decimal remaining, decimal quantity, string? cavityType, int? postedTrays,
            DateTime transplantDate, DateTime bedSowingDate, DateTime todayIndia, bool closeAsWaste = false, string? wasteReason = null)
        {
            if (remaining <= 0)
                return (false, null, "No seedlings remain in this bed.");
            var cavity = DirectSowingRules.CavityCount(cavityType);
            if (closeAsWaste)
            {
                // No cavity, trays or material: only the date, the reason and an empty Quantity to Transplant.
                var (dateOk, dateError) = ValidateBusinessDate(transplantDate, todayIndia, "Transplant Date", bedSowingDate, "bed sowing date");
                if (!dateOk)
                    return (false, null, dateError);
                if (quantity != 0)
                    return (false, null, "To record all the remaining seedlings as waste, leave the Quantity to Transplant at 0.");
                if (string.IsNullOrWhiteSpace(wasteReason))
                    return (false, null, "Enter the reason the remaining seedlings are dead or unusable.");
                if (wasteReason.Trim().Length > MaxWasteReasonLength)
                    return (false, null, $"The reason is too long ({MaxWasteReasonLength} characters at most).");
                return (true, new TransplantPlan(0, 0, remaining), null);
            }
            if (quantity <= 0 && cavity is > 0 && remaining < cavity.Value)
                return (false, null, $"Only {QuantityFormat.Qty(remaining)} seedlings remain -- fewer than one {cavityType} tray holds. "
                    + "No tray batch can be made: if they are dead or unusable, confirm the waste-only closure with a reason instead.");
            var (ok, trays, error) = ValidateTransplant(remaining, quantity, cavityType, postedTrays, transplantDate, bedSowingDate, todayIndia);
            return ok ? (true, new TransplantPlan(trays, quantity, remaining - quantity), null) : (false, null, error);
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
        // bedAnyDate (2026-10-17_BedSowingApprovalAnyDate.sql applied): a BED SOWING batch is approved on any date --
        // before, on or after its Ready Date, which stays recorded as it is.
        // transplantAnyDate (2026-10-20_TransplantApprovalAnyDate.sql applied): a Mother Plant Area TRANSPLANT batch is
        // approved on any date too; it must still HAVE a Ready Date (eligibility, not timing). Without it: from its Ready Date.
        // Seed and Cutting sowings have no date rule; nothing here changes them.
        public static (bool Ok, string? Error) ValidateApprovalReadyDate(string? sourceType, DateTime? expectedReadyDate, DateTime todayIndia,
            bool bedAnyDate = false, bool transplantAnyDate = false)
        {
            if (!TrayBatchRules.IsTransplantedBatch(sourceType))
                return (true, null);
            var bed = sourceType == SeedSowing.SourceBedSowing;
            if (bed && bedAnyDate)
                return (true, null);
            if (!expectedReadyDate.HasValue)
                return (false, $"This tray batch from {(bed ? "a Bed Sowing" : "a transplant")} has no Ready Date, so it cannot be approved.");
            if (!bed && transplantAnyDate)
                return (true, null);
            return todayIndia.Date < expectedReadyDate.Value.Date
                ? (false, string.Format(bed ? NotReadyYetMessageFormat : TransplantNotReadyYetMessageFormat, expectedReadyDate.Value))
                : (true, null);
        }

        public static (bool Ok, string? Error) ValidateReason(string? reason, string what)
            => string.IsNullOrWhiteSpace(reason) ? (false, $"A reason is required to {what}.") : (true, null);

        // ---- Edit (2026-10-22, 2026-10-22_BedSowingEdit.sql) ------------------------------------------------------
        // Only an UNTOUCHED bed may be edited: pending transplant, nothing transplanted or lost, no transplant / loss
        // operation ever recorded (not even a reversed one -- its tray batch history names this bed's variety and date),
        // not cancelled, and no legacy Ready Date. TR_BedSowings_Guard repeats this in the database (52801).
        public const string EditNotUntouchedMessage =
            "Only an untouched bed sowing can be edited: nothing transplanted or lost, and no transplant or loss ever recorded. Reverse those first, or cancel the bed and record it again.";
        public const string EditCancelledMessage = "A cancelled bed sowing cannot be edited.";
        public const string EditLegacyReadyDateMessage =
            "This bed was recorded with a Ready Date (before 2026-10-13) and cannot be edited. Cancel it and record it again.";
        public const string EditStaleMessage =
            "This bed sowing was changed after you opened it (or this form was already saved). Nothing was changed -- reload the bed and edit it again.";
        public const string EditNoChangesMessage = "No changes to save.";

        public static (bool Ok, string? Error) ValidateEditable(string status, decimal transplanted, decimal lost, int operationCount, int? readyStockDays)
        {
            if (status == StatusCancelledValue)
                return (false, EditCancelledMessage);
            if (status != StatusPendingValue || transplanted != 0 || lost != 0 || operationCount > 0)
                return (false, EditNotUntouchedMessage);
            if (readyStockDays.HasValue)
                return (false, EditLegacyReadyDateMessage);
            return (true, null);
        }

        private const string StatusPendingValue = BedSowing.StatusPending;
        private const string StatusCancelledValue = BedSowing.StatusCancelled;

        // What an edit changes. "Sown" = the four fields that move seed between lots / dates and need the ledger reconciled.
        public sealed record BedEditValues(DateTime SowingDate, int SpeciesId, int SeedStockId, decimal Quantity, string? Remarks);

        public sealed record BedEditChanges(bool Date, bool Variety, bool Lot, bool Quantity, bool Remarks)
        {
            public bool Sown => Date || Variety || Lot || Quantity;
            public bool Any => Sown || Remarks;
        }

        public static string? NormaliseRemarks(string? remarks) => string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();

        public static BedEditChanges Compare(BedEditValues current, BedEditValues revised) => new(
            current.SowingDate.Date != revised.SowingDate.Date,
            current.SpeciesId != revised.SpeciesId,
            current.SeedStockId != revised.SeedStockId,
            current.Quantity != revised.Quantity,
            NormaliseRemarks(current.Remarks) != NormaliseRemarks(revised.Remarks));

        // The form re-posts the values it was opened with; any difference from the stored bed means someone else saved in
        // between (or this same form was already submitted) -- refused, so an edit is never applied twice.
        public static bool MatchesStored(BedEditValues stored, BedEditValues original)
            => !Compare(stored, original).Any;

        // Validation of the revised values (server side; the lot / variety / Area / stock rules are checked under lock).
        public static (bool Ok, string? Error) ValidateEdit(BedEditValues revised, DateTime todayIndia, BedEditChanges changes, string? reason)
        {
            if (revised.SpeciesId <= 0)
                return (false, "Variety is required.");
            if (revised.SeedStockId <= 0)
                return (false, "Main Office seed lot is required.");
            var (qtyOk, qtyError) = ValidateBedQuantity(revised.Quantity);
            if (!qtyOk)
                return (false, qtyError);
            var (dateOk, dateError) = ValidateBusinessDate(revised.SowingDate, todayIndia, "Sowing Date");
            if (!dateOk)
                return (false, dateError);
            if (revised.Remarks?.Trim().Length > 500)
                return (false, "Remarks can be at most 500 characters.");
            if (changes.Sown)
            {
                var (reasonOk, reasonError) = ValidateReason(reason, "change the sowing date, variety, seed lot or quantity");
                if (!reasonOk)
                    return (false, reasonError);
                if (reason!.Trim().Length > 500)
                    return (false, "The reason can be at most 500 characters.");
            }
            return (true, null);
        }

        // "Sowing Date 01/10/2026 -> 02/10/2026; Quantity 25,000 -> 24,000; ..." for the audit log and the success message.
        public static string DescribeChanges(BedEditChanges c, BedEditValues from, BedEditValues to, string? fromVariety, string? toVariety,
            string? fromLot, string? toLot)
        {
            var parts = new List<string>();
            if (c.Date) parts.Add($"Sowing Date {from.SowingDate.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} -> {to.SowingDate.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)}");
            if (c.Variety) parts.Add($"Variety {fromVariety ?? "#" + from.SpeciesId} -> {toVariety ?? "#" + to.SpeciesId}");
            if (c.Lot) parts.Add($"Seed lot {fromLot ?? "#" + from.SeedStockId} -> {toLot ?? "#" + to.SeedStockId}");
            if (c.Quantity) parts.Add($"Quantity {QuantityFormat.Qty(from.Quantity)} -> {QuantityFormat.Qty(to.Quantity)}");
            if (c.Remarks) parts.Add("Remarks changed");
            return string.Join("; ", parts);
        }
    }
}
