using System.Globalization;

namespace PlantStockManager.Services
{
    // Inventory Store (Database/Migrations/2026-10-06_StoreInventory.sql) -- pure rules shared by
    // the pages, the repository and the tests: request statuses, item types, quantity and
    // settlement validation. WHO may act is StoreAuthorization; the database repeats every stock
    // rule (CHECK constraints, TR_StoreTransactions_Ledger, TR_StoreItems_Balance).
    public static class StoreRules
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Issued = "Issued";
        public const string Closed = "Closed";
        public const string Cancelled = "Cancelled";
        public static readonly IReadOnlyList<string> Statuses = new[] { Pending, Approved, Rejected, Issued, Closed, Cancelled };

        public const string Consumable = "Consumable";
        public const string Returnable = "Returnable";
        public static readonly IReadOnlyList<string> ItemTypes = new[] { Consumable, Returnable };

        public const string Purchase = "PURCHASE";
        public const string Issue = "ISSUE";
        public const string Return = "RETURN";
        public const string PermanentTransfer = "PERMANENT_TRANSFER";
        public const string Adjustment = "ADJUSTMENT";
        public static readonly IReadOnlyList<string> TransactionTypes = new[] { Purchase, Issue, Return, PermanentTransfer, Adjustment };

        public static readonly IReadOnlyList<string> ReturnConditions = new[] { "Good", "Damaged" };

        public const decimal MaxQuantity = 1_000_000m;
        public const int MaxLinesPerRequest = 20;
        public const int MaxTextLength = 500;

        public const string ChangedMessage = "This request was changed by someone else a moment ago. Please check its current status.";
        public const string AlreadySubmittedMessage = "This form was already submitted.";
        public const string NotAuthorizedAreaMessage = "You are not authorized to request store items for this Area.";
        public const string PolyhouseNotInAreaMessage = "This Polyhouse does not belong to the selected Area.";

        public static bool CanCancel(string? status) => status is Pending or Approved;

        public static string StatusBadge(string? status) => status switch
        {
            Pending => "bg-primary",
            Approved => "bg-info text-dark",
            Issued => "bg-warning text-dark",
            Closed => "bg-secondary",
            Rejected => "bg-danger",
            Cancelled => "bg-dark",
            _ => "bg-light text-dark",
        };

        public static string TypeLabel(string? type) => type switch
        {
            PermanentTransfer => "Permanent Transfer",
            _ => type == null ? string.Empty : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(type.ToLowerInvariant()),
        };

        // 60 -> "60", 2.5 -> "2.5", 0.125 -> "0.125".
        public static string Qty(decimal q) => q.ToString("0.###", CultureInfo.InvariantCulture);

        // A quantity of an item: > 0 (or >= 0 when allowZero), at most 3 decimals, whole for Returnable items.
        public static string? ValidateQuantity(decimal? qty, string label, string itemType, bool allowZero = false)
        {
            if (!qty.HasValue)
                return $"{label}: quantity is required.";
            if (qty.Value < 0 || (!allowZero && qty.Value == 0))
                return $"{label}: quantity must be {(allowZero ? "0 or more" : "greater than 0")}.";
            if (qty.Value > MaxQuantity)
                return $"{label}: quantity is too large.";
            if (decimal.Round(qty.Value, 3) != qty.Value)
                return $"{label}: at most 3 decimal places.";
            if (itemType == Returnable && decimal.Truncate(qty.Value) != qty.Value)
                return $"{label}: a returnable item is counted in whole units.";
            return null;
        }

        // Settlement of one issued line: every issued unit is accounted for exactly once.
        public static List<string> ValidateSettlement(string itemName, string itemType, decimal issued, decimal? consumed, decimal? kept, decimal? returned)
        {
            var errors = new List<string>();
            foreach (var (q, what) in new[] { (consumed, "consumed"), (kept, "kept"), (returned, "returned") })
            {
                var e = ValidateQuantity(q ?? 0, $"{itemName} {what}", itemType, allowZero: true);
                if (e != null) errors.Add(e);
            }
            if (errors.Count > 0)
                return errors;
            if (itemType == Returnable && (consumed ?? 0) > 0)
                errors.Add($"{itemName}: a returnable item cannot be consumed -- it is either returned or kept.");
            var total = (consumed ?? 0) + (kept ?? 0) + (returned ?? 0);
            if (total != issued)
                errors.Add($"{itemName}: consumed + kept + returned must equal the {Qty(issued)} issued (now {Qty(total)}).");
            return errors;
        }

        public static string? Required(string? value, string label, int max = MaxTextLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return $"{label} is required.";
            return value.Trim().Length > max ? $"{label} cannot be longer than {max} characters." : null;
        }

        public static string? Optional(string? value, string label, int max = MaxTextLength)
            => value != null && value.Trim().Length > max ? $"{label} cannot be longer than {max} characters." : null;

        public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // A stock operation that would take an item's stock below zero (checked under the item's row
    // lock; the database refuses it as well).
    public class StoreStockException : Exception
    {
        public StoreStockException(string message) : base(message) { }
    }
}
