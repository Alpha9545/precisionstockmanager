namespace PlantStockManager.Models
{
    // Phase D: one cutting harvest from a Mother Plant (dbo.CuttingProductions).
    // Saving it credits the Mother Plant Area's Cutting Stock ('Harvest').
    public class CuttingProduction
    {
        public int Id { get; set; }
        public string ProductionCode { get; set; } = string.Empty;
        public int MotherPlantId { get; set; }
        public int SpeciesId { get; set; }
        public int AreaId { get; set; }
        public int CuttingStockId { get; set; }
        public DateTime CuttingDate { get; set; } = DateTime.Today;
        public decimal Quantity { get; set; }
        public int SupervisorId { get; set; }
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }

        // display
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? Color { get; set; }
        public string? AreaName { get; set; }
        public string? SupervisorName { get; set; }
    }
}
