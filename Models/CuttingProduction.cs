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

        // Where these cuttings went (see CuttingDestination). Null = recorded before
        // destinations existed. DestinationAreaId: the Main Office Area for MainOffice,
        // the Mother Plant's own Area for PotProduction.
        public string? DestinationType { get; set; }
        public int? DestinationAreaId { get; set; }
        // One-time key of the entry form: a repeated POST of the same form (double
        // click, refresh, retry) finds the row already saved and creates nothing new.
        public Guid? SubmissionToken { get; set; }

        // Set by CuttingProductionRepository.InsertAsync
        public bool IsDuplicateSubmission { get; set; }
        public int? TransferId { get; set; }
        public string? TransferCode { get; set; }

        // display
        public string? DestinationAreaName { get; set; }
        public string? TransferStatus { get; set; }
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? Color { get; set; }
        public string? AreaName { get; set; }
        // The Mother Plant's Polyhouse (dbo.MotherPlants.PolyhouseId); display / filter only.
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public string? SupervisorName { get; set; }
    }
}
