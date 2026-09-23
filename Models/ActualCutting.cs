namespace PlantStockManager.Models
{
    public class ActualCutting
    {
        public int Id { get; set; }
        public string ActualCuttingCode { get; set; } = string.Empty;

        // MotherPlantId/SpeciesId are always copied from the selected
        // Cutting Plan by the repository (never independently chosen) --
        // see Phase4_ActualCutting.sql's
        // CK_ActualCuttings_MatchesCuttingPlan for the DB-level guarantee.
        public int CuttingPlanId { get; set; }
        public int MotherPlantId { get; set; }
        public int SpeciesId { get; set; }

        public DateTime CuttingDate { get; set; } = DateTime.Today;

        // Snapshot of the plan's PlannedQuantity at the time this row was
        // recorded (so a later edit to the plan doesn't rewrite history).
        public decimal PlannedQuantity { get; set; }

        public decimal ActualQuantity { get; set; }
        public decimal GoodQuantity { get; set; }
        public decimal DamagedQuantity { get; set; }
        public decimal RejectedQuantity { get; set; }

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in ActualCuttingRepository.
        public string? CuttingPlanNumber { get; set; }
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }

        // Display-only, computed by ActualCuttingRepository from
        // CuttingDeliveries (Phase 5) -- sum of DeliveredQuantity already
        // taken from this record's GoodQuantity pool. Zero / not
        // populated until Phase 5's table exists.
        public decimal DeliveredQuantityRecorded { get; set; }

        public static bool IsReconciled(decimal good, decimal damaged, decimal rejected, decimal actual)
            => good + damaged + rejected == actual;
    }
}
