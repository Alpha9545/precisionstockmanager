using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    public class StoreCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    // dbo.StoreItems. AvailableQuantity = Main Office stock (changed only through the ledger).
    public class StoreItem
    {
        public int Id { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int CategoryId { get; set; }
        public string? CategoryName { get; set; }
        public string? Description { get; set; }
        public int UnitId { get; set; }
        public string? UnitName { get; set; }
        public string ItemType { get; set; } = StoreRules.Consumable;
        public decimal MinimumStock { get; set; }
        public decimal AvailableQuantity { get; set; }
        public string? Location { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Remarks { get; set; }
        public bool HasMovements { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedByName { get; set; }

        public bool IsLow => IsActive && AvailableQuantity < MinimumStock;
        public string Display => $"{ItemCode} -- {Name}";
    }

    public class StoreTransaction
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string? UnitName { get; set; }
        public string TransactionType { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal BalanceBefore { get; set; }
        public decimal BalanceAfter { get; set; }
        public DateTime TransactionDate { get; set; }
        public int? RequestId { get; set; }
        public string? RequestNo { get; set; }
        public int? AreaId { get; set; }
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public string? VendorName { get; set; }
        public string? Reference { get; set; }
        public decimal? UnitCost { get; set; }
        public string? Remarks { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }    // UTC

        public string Location => AreaName == null ? "Main Office store" : PolyhouseId.HasValue ? $"{AreaName} / {PolyhouseName}" : AreaName;
    }

    public class StoreRequest
    {
        public int Id { get; set; }
        public string RequestNo { get; set; } = string.Empty;
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public string Status { get; set; } = StoreRules.Pending;
        public int RequestedById { get; set; }
        public string? RequestedByName { get; set; }
        public DateTime RequestedAt { get; set; }
        public string? ReviewedByName { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewRemarks { get; set; }
        public string? IssuedByName { get; set; }
        public DateTime? IssuedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? CancelledByName { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string? CancelReason { get; set; }
        public int LineCount { get; set; }
        public int PendingReturns { get; set; }
        public int UnsettledLines { get; set; }
        public List<StoreRequestLine> Lines { get; set; } = new();

        public string Destination => PolyhouseId.HasValue ? $"{AreaName} / {PolyhouseName}" : AreaName ?? string.Empty;
    }

    public class StoreRequestLine
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public int ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string? UnitName { get; set; }
        public string ItemType { get; set; } = StoreRules.Consumable;
        public decimal ItemAvailable { get; set; }
        public decimal RequestedQty { get; set; }
        public decimal? ApprovedQty { get; set; }
        public string? ReviewRemarks { get; set; }
        public decimal IssuedQty { get; set; }
        public decimal ConsumedQty { get; set; }
        public decimal KeptQty { get; set; }
        public decimal ReturnDeclaredQty { get; set; }
        public decimal ReturnedQty { get; set; }
        public string? ReturnCondition { get; set; }
        public string? ReturnRemarks { get; set; }
        public string? SettledByName { get; set; }
        public DateTime? SettledAt { get; set; }
        public string? ReturnConfirmedByName { get; set; }
        public DateTime? ReturnConfirmedAt { get; set; }
        public string? ConfirmRemarks { get; set; }

        public bool NeedsSettlement => IssuedQty > 0 && !SettledAt.HasValue;
        public bool ReturnPending => ReturnDeclaredQty > 0;
        public string Outcome => !ApprovedQty.HasValue ? "Awaiting review"
            : ApprovedQty == 0 ? "Rejected"
            : IssuedQty == 0 && !SettledAt.HasValue ? "Approved"
            : NeedsSettlement ? "Issued -- to settle"
            : ReturnPending ? "Return to confirm"
            : "Settled";
    }

    public sealed record StoreRequestLineInput(int ItemId, decimal Quantity);

    // Where store items are, per item and location (Area / Polyhouse): out on issue (not settled
    // yet), returned but not yet confirmed, kept, permanently transferred, consumed.
    public class StoreHolding
    {
        public int ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string? UnitName { get; set; }
        public string ItemType { get; set; } = string.Empty;
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public decimal OutOnIssue { get; set; }
        public decimal PendingReturn { get; set; }
        public decimal Kept { get; set; }
        public decimal Transferred { get; set; }
        public decimal Consumed { get; set; }

        public decimal Assigned => Kept + Transferred;
        // Physically at the location now (consumed items are used up).
        public decimal AtLocation => OutOnIssue + PendingReturn + Assigned;
        public string Location => PolyhouseId.HasValue ? $"{AreaName} / {PolyhouseName}" : AreaName ?? string.Empty;
    }

    public class StoreTransactionFilter
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string? TransactionType { get; set; }
        public int? ItemId { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public int? RequestId { get; set; }
    }
}
