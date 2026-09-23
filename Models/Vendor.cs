namespace PlantStockManager.Models
{
    // General-purpose vendor master for Phase 11 Purchase Orders. Not
    // related to the untouched dbo.VendorPurchases/VendorPurchase.cs
    // stub (Decision 2), and not the same thing as dbo.FertilizerSource
    // (kept separate and still used as its own dropdown for Fertilizer
    // Purchase Order lines, since dbo.FertilizerStock.SourceId points
    // at FertilizerSource specifically).
    public class Vendor
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ContactPerson { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Address { get; set; }
        public string? GSTIN { get; set; }
        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }
}
