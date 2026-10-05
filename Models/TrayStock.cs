namespace PlantStockManager.Models
{
    // Tray Stock: inventory of empty trays, one pool per
    // AREA + POLYHOUSE + CAVITY -- added by the Sowing Supervisor for a
    // Polyhouse of an Area they are authorized for, and consumed by Seed/
    // Cutting Sowing into that same Polyhouse
    // (Database/Migrations/2026-10-04_TrayStockPolyhouse.sql).
    //
    // Area-level rows (PolyhouseId NULL) existed only between the two
    // 2026-10-04 migrations: they are retired at 0, kept for history, and
    // can never be active again (CK_TrayStock_ActivePoolHasPolyhouse).
    public class TrayStock
    {
        public int Id { get; set; }
        public int AreaId { get; set; }
        // Always set on an active pool; NULL only on the retired Area-level rows.
        public int? PolyhouseId { get; set; }
        // Closed set -- the exact same domain as SeedSowings.CavityType
        // (DirectSowingRules.CavityTypes); no separate Tray Size master.
        public string TraySize { get; set; } = string.Empty;
        public decimal PhysicalQuantity { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by the joins in TrayStockRepository.
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
        public bool IsAreaLevelPool => !PolyhouseId.HasValue;
    }

    public class TrayStockTransaction
    {
        public int Id { get; set; }
        public int TrayStockId { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

        // 'Allocation' (trays added: Add Tray Stock, or the retired Main Office
        // allocation) | 'Sowing' (consumed by a sowing / overage approval) |
        // 'ReversalReturn' | 'Adjustment' (the 2026-10-04 migrations/reversal).
        public string TransactionType { get; set; } = string.Empty;
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }

        // Signed delta: positive = added, negative = consumed.
        public decimal Quantity { get; set; }
        public decimal BeforeQuantity { get; set; }
        public decimal AfterQuantity => BeforeQuantity + Quantity;

        public int? UserId { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Display-only, populated by joins in TrayStockRepository.
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }   // NULL only on the retired Area-level rows
        public string? TraySize { get; set; }
        public string? UserName { get; set; }

        // Plain-language label for the history screen.
        public string TypeLabel => TransactionType switch
        {
            "Allocation" => "Tray Stock Added",
            "Sowing" => "Used for Sowing",
            "ReversalReturn" => "Returned (cancellation)",
            "Adjustment" when ReferenceType == "TrayStockMigration" && Quantity < 0 => "Moved to Area stock (2026-10-04)",
            "Adjustment" when ReferenceType == "TrayStockMigration" => "Carried over from Polyhouse stock (2026-10-04)",
            "Adjustment" when ReferenceType == "TrayStockPolyhouseSplit" && Quantity < 0 => "Moved back to Polyhouse stock (2026-10-04)",
            "Adjustment" when ReferenceType == "TrayStockPolyhouseSplit" => "Restored to Polyhouse stock (2026-10-04)",
            "Adjustment" when ReferenceType == "TrayStockReversal" => "Reversed entry (no Polyhouse recorded)",
            _ => TransactionType
        };
    }
}
