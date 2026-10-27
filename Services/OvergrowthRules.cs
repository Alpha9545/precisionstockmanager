using Microsoft.AspNetCore.Html;

namespace PlantStockManager.Services
{
    // 2026-10-22: Overgrowth ("⚠ OG -- Overgrowth") on Ready Stock batches (2026-10-24_ReadyStockOvergrowth.sql).
    //   * A deliberate mark on ONE dbo.ReadyStock batch row by an authorised user -- never automatic (no age / variety rule).
    //   * Who: ReadyStock.Overgrowth held through a role assignment in the BATCH'S OWN Area, or a System Administrator.
    //     ReadyStockRepository.SetOvergrowthAsync re-checks it inside the transaction, under the batch's lock.
    //   * Stays with the batch through reservation, partial fulfilment and dispatch (they reference the batch row), through
    //     Send to Main Office / Outlet (the same row), and is copied onto the batch received in a Mother Plant Area. A
    //     transplanted output is a new batch of new trays and starts unmarked (the Transplanting page says so).
    //   * Overgrowth stock stays eligible for booking fulfilment and customer dispatch -- no quantity, price or booking
    //     rule changes. Dispatch lines keep whether the batch was Overgrowth AT DISPATCH.
    public static class OvergrowthRules
    {
        public const string Permission = "ReadyStock.Overgrowth";
        public const int MaxReasonLength = 200;

        public const string Label = "⚠ OG — Overgrowth";
        public const string ShortLabel = "⚠ OG";

        public const string ActionMarked = "Marked";
        public const string ActionUnmarked = "Unmarked";
        public const string ActionInherited = "Inherited";

        public const string ReasonRequiredMessage = "Give the reason the seedlings are overgrown (what you observed).";
        public const string NothingLeftMessage = "Only a batch that still has plants can be marked as Overgrowth.";
        public const string AlreadyMarkedMessage = "This batch is already marked as Overgrowth -- nothing changed.";
        public const string NotMarkedMessage = "This batch is not marked as Overgrowth -- nothing changed.";
        public const string NotAuthorisedMessage = "Only a user holding Overgrowth marking in this batch's Area (or a System Administrator) can change the Overgrowth marker.";
        public const string NotFoundMessage = "Ready Stock batch not found.";
        // 2026-10-22 (owner decision): a transplanted batch starts unmarked, but the transplant's existing trace records whether its
        // SOURCE was Overgrowth at that moment -- the source batch's TransplantOut ledger row and the new tray batch's remarks.
        public static string TransplantSourceLedgerNote(string? reason)
            => $"; source batch was {Label} when transplanted" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason.Trim());
        public const string TransplantOutputNote = "[source batch " + ShortLabel + " Overgrowth at transplant; this batch starts unmarked]";

        public const string MarkedMessage = "The batch is now marked ⚠ OG — Overgrowth. It stays available for booking and dispatch.";
        public const string UnmarkedMessage = "The Overgrowth marker was removed -- the batch is normal Ready Stock again.";

        // Before the save: a mark needs a reason (trimmed, at most MaxReasonLength) and plants still in the batch;
        // an unmark only an existing mark (its reason is optional). null = fine, else why not.
        public static string? Validate(bool mark, bool currentlyMarked, decimal physicalQuantity, string? reason)
        {
            var r = reason?.Trim();
            if (r != null && r.Length > MaxReasonLength)
                return $"The reason can be at most {MaxReasonLength} characters.";
            if (mark)
            {
                if (currentlyMarked) return AlreadyMarkedMessage;
                if (physicalQuantity <= 0) return NothingLeftMessage;
                if (string.IsNullOrEmpty(r)) return ReasonRequiredMessage;
                return null;
            }
            return currentlyMarked ? null : NotMarkedMessage;
        }

        // The badge, identical everywhere: amber outline, not a filled colour block. Empty when not Overgrowth.
        public static IHtmlContent Badge(bool isOvergrowth, bool compact = false)
            => isOvergrowth
                ? new HtmlString("<span class=\"badge rounded-pill border border-warning text-warning-emphasis bg-warning-subtle fw-semibold og-badge\" "
                    + "title=\"Overgrowth -- seedlings in this batch are overgrown. Still available for booking and dispatch.\" data-og=\"1\">"
                    + System.Net.WebUtility.HtmlEncode(compact ? ShortLabel : Label) + "</span>")
                : HtmlString.Empty;
    }
}
