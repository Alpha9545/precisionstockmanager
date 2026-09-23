namespace PlantStockManager.Models
{
    // Phase 22/Phase H: a Species+Area+Lot pool of seed stock. Main
    // Office holds one or more of these (AreaId pointing at a
    // MainOffice-type Area); once a Seed Issue is confirmed, the same
    // table gains a matching row at the destination Polyhouse/Growing
    // Area (AreaId pointing at that Area instead) -- exactly the same
    // "one dedicated stock table, many Areas" shape PottedPlantStock/
    // EmptyPotInventory already use since Phase 8.
    public class SeedStock
    {
        public int Id { get; set; }
        public int SpeciesId { get; set; }
        public int AreaId { get; set; }

        // Optional lot/batch identity -- empty string means "no
        // specific lot tracked" rather than NULL, so (SpeciesId,
        // AreaId, BatchNo) can be a real, simple UNIQUE key (see
        // Database/Phase22_MainOfficeSeedIssue.sql for why).
        public string BatchNo { get; set; } = string.Empty;

        public int? SeedSourceId { get; set; }
        public string Unit { get; set; } = "pcs";

        public decimal PhysicalQuantity { get; set; }
        public decimal InTransitQuantity { get; set; }
        public decimal AvailableQuantity { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in SeedStockRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? AreaType { get; set; }
        public string? SeedSourceName { get; set; }
    }
}
