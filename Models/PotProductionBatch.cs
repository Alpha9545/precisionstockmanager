using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // Phase D: Cutting -> Pot Production Batch -> daily production -> READY
    // -> Potted Plant Stock (dbo.PotProductionBatches / PotProductionEntries).
    public class PotProductionBatch
    {
        public int Id { get; set; }
        public string BatchCode { get; set; } = string.Empty;
        public int SourceCuttingStockId { get; set; }
        public int SpeciesId { get; set; }
        public int AreaId { get; set; }
        public string PotSize { get; set; } = string.Empty;
        public int EmptyPotInventoryId { get; set; }
        public decimal CuttingAllocated { get; set; }
        public DateTime ProductionStartDate { get; set; } = DateTime.Today;
        public DateTime ExpectedReadyDate { get; set; } = DateTime.Today;
        public int SupervisorId { get; set; }
        public string Status { get; set; } = PotBatchRules.InProduction;
        public decimal? ReadyQuantity { get; set; }
        public string? WastageReason { get; set; }
        public string? UnusedCuttingAction { get; set; }
        public int? ReadyConfirmedById { get; set; }
        public DateTime? ReadyDate { get; set; }
        public string? ReadyRemarks { get; set; }
        public int? PottedPlantStockId { get; set; }
        public string? Remarks { get; set; }
        public int CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }

        // display / derived
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? Color { get; set; }
        public string? AreaName { get; set; }
        public string? SourceAreaName { get; set; }
        public string? SupervisorName { get; set; }
        public string? CreatedByName { get; set; }
        public string? ReadyConfirmedByName { get; set; }
        public decimal PottedQuantity { get; set; }            // sum of daily entries
        public decimal EmptyPotsAvailable { get; set; }        // the batch Area's pool, now

        public decimal CuttingsLeft => CuttingAllocated - PottedQuantity;
        public bool IsConfirmed => Status == PotBatchRules.Ready || Status == PotBatchRules.Lost;
        public decimal PotWastage => IsConfirmed && ReadyQuantity.HasValue ? PottedQuantity - ReadyQuantity.Value : 0;
        public decimal UnusedCuttings => IsConfirmed ? CuttingAllocated - PottedQuantity : 0;
        public string Readiness(DateTime today) => PotBatchRules.ReadinessStatus(Status, ExpectedReadyDate, today);
    }

    public class PotProductionEntry
    {
        public int Id { get; set; }
        public int BatchId { get; set; }
        public DateTime ProductionDate { get; set; } = DateTime.Today;
        public decimal Quantity { get; set; }
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
