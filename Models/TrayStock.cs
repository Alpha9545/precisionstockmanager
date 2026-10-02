namespace PlantStockManager.Models
{
    // Tray Stock: inventory of empty trays held at a specific Main Office
    // Polyhouse, one row per (PolyhouseId, TraySize). Mirrors
    // EmptyPotInventory's own (PotSize, AreaId) pattern exactly, except the
    // location key is a POLYHOUSE, never an Area -- "Facility-1 / 24 Cavity"
    // and "Facility-3 / 24 Cavity" are completely independent pools, even
    // though both Polyhouses sit under the same Main Office Area.
    public class TrayStock
    {
        public int Id { get; set; }
        public int PolyhouseId { get; set; }
        // Closed set -- the exact same domain as SeedSowings.CavityType
        // (DirectSowingRules.CavityTypes); no separate Tray Size master.
        public string TraySize { get; set; } = string.Empty;
        public decimal PhysicalQuantity { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by the join in TrayStockRepository.
        public string? PolyhouseName { get; set; }
        public int? AreaId { get; set; }
        public string? AreaName { get; set; }
    }

    public class TrayStockTransaction
    {
        public int Id { get; set; }
        public int TrayStockId { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

        // 'Allocation' (Main Office Officer gives trays) | 'Sowing' (consumed
        // by a Confirm Sowing) | 'ReversalReturn' | 'Adjustment'.
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
        public string? PolyhouseName { get; set; }
        public string? TraySize { get; set; }
        public string? UserName { get; set; }
    }
}
