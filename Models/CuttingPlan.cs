namespace PlantStockManager.Models
{
    public class CuttingPlan
    {
        public int Id { get; set; }
        public string PlanNumber { get; set; } = string.Empty;

        // The Mother Plant this plan draws cuttings from. SpeciesId is
        // always copied from the selected Mother Plant by the repository
        // (never independently chosen by the user) -- see
        // Phase3_CuttingPlan.sql's CK_CuttingPlans_SpeciesMatchesMotherPlant
        // for the DB-level guarantee of the same rule.
        public int MotherPlantId { get; set; }
        public int SpeciesId { get; set; }

        public DateTime PlannedCuttingDate { get; set; } = DateTime.Today;
        public decimal PlannedQuantity { get; set; }
        public decimal CuttingRate { get; set; }

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }

        public string Status { get; set; } = "Planned";
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in CuttingPlanRepository. Not persisted.
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? PolyhouseName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }

        // How much of this plan's PlannedQuantity has already been consumed
        // by Actual Cutting records against it (populated by the
        // repository for the Details page's traceability view -- not a
        // stored column, since Actual Cutting is the source of truth for
        // "how much cutting actually happened").
        public decimal ActualCuttingQuantityRecorded { get; set; }
    }
}
