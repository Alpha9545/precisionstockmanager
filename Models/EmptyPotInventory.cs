namespace PlantStockManager.Models
{
    public class EmptyPotInventory
    {
        public int Id { get; set; }
        public string PotSize { get; set; } = string.Empty;

        // Which Area physically holds this pool of empty pots. Nullable
        // for legacy/"unassigned location" rows created before Phase 8
        // added location tracking -- every NEW row going forward always
        // has a real AreaId. The business key is (PotSize, AreaId).
        public int? AreaId { get; set; }

        public decimal PhysicalQuantity { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by the join in EmptyPotInventoryRepository.
        public string? AreaName { get; set; }
        public string? AreaType { get; set; }

        // Phase E: derived via Area.GrowingPartnerId, same as
        // PottedPlantStock.GrowingPartnerName -- lets a Growing Partner
        // Supervisor's Empty Pot Stock page identify whose pool this is,
        // while keeping Empty Pot Inventory and Potted Plant Stock as two
        // clearly separate inventories (spec item 16).
        public string? GrowingPartnerName { get; set; }
    }

    public class EmptyPotInventoryTransaction
    {
        public int Id { get; set; }
        public int EmptyPotInventoryId { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

        // 'StockIn' | 'Consumption' | 'Adjustment' | 'ReversalReturn'
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

        // Display-only, populated by joins in EmptyPotInventoryRepository.
        public string? PotSize { get; set; }
        public string? UserName { get; set; }
    }
}
