namespace PlantStockManager.Models
{
    // New, standalone entity introduced in Phase 17 -- deliberately separate
    // from dbo.Vendors (Phase 11), which remains a narrow procurement-only
    // master (fertilizer/empty-pot Purchase Orders) with no relationship to
    // Area, production, or stock. A GrowingPartner is an external/internal
    // party who can be assigned ownership of one or more Areas
    // (Area.GrowingPartnerId), letting existing Area-scoped stock
    // (CuttingStock, PottedPlantStock, EmptyPotInventory) separate by
    // partner with no new stock tables. Capability flags (Mother Plant /
    // Cutting / Pot Production / Direct Sale) and access-control scoping
    // are intentionally NOT part of this phase -- see Phase B.
    public class GrowingPartner
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ContactPerson { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Address { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }
}
