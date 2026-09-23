namespace PlantStockManager.Models
{
    public class MotherPlant
    {
        public int Id { get; set; }

        // Business identifier (spec: "MotherPlantCode"). Renamed from the
        // original "BatchNumber" before Phase 3 started, since Phase 3+
        // needed the naming to be unambiguous across modules.
        public string MotherPlantCode { get; set; } = string.Empty;

        // Reuses the existing Polyhouses and PlantSpecies masters --
        // no new Polyhouse/Variety tables.
        public int PolyhouseId { get; set; }
        public int SpeciesId { get; set; }

        public int? AreaId { get; set; }

        // Both FK to the existing IMSUsers table -- ResponsiblePersonId and
        // SupervisorId are deliberately separate fields (a Mother Plant can
        // have a day-to-day responsible person and a different supervisor
        // signing off on it), matching how Cutting Plan/Actual Cutting also
        // carry both.
        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }

        public DateTime PlantingDate { get; set; } = DateTime.Today;

        public decimal MotherPlantQuantity { get; set; }
        public int CuttingPeriodDays { get; set; }
        public decimal CuttingRate { get; set; }

        // Calculated fields (spec section 7). The repository recomputes
        // these on every insert/update -- they are never taken as-is from
        // user input, even though they round-trip through the form for
        // display.
        public decimal ExpectedCuttingQuantity { get; set; }
        public decimal ExpectedMonthlyCuttingQuantity { get; set; }

        public string Status { get; set; } = "Active";
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in MotherPlantRepository. Not persisted.
        public string? PolyhouseName { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }

        public static decimal CalculateExpectedCuttingQuantity(decimal motherPlantQuantity, decimal cuttingRate)
            => motherPlantQuantity * cuttingRate;

        public static decimal CalculateExpectedMonthlyCuttingQuantity(decimal expectedCuttingQuantity, int cuttingPeriodDays)
            => cuttingPeriodDays > 0 ? expectedCuttingQuantity * 30 / cuttingPeriodDays : 0;
    }
}
