namespace PlantStockManager.Models
{
    public class PlantSpecies
    {
        public int Id { get; set; }
        public int PlantTypeId { get; set; } // FK to PlantType
        public string Name { get; set; }
        public string? ScientificName { get; set; } // Nullable

        // Phase 24 (Phase J: Ready Alerts). The variety-specific number of
        // days from Sowing to expected readiness -- reused as the single
        // source of truth for SeedSowingRepository.InsertAsync's
        // ExpectedReadyDate computation (SowingDate + ReadyStockDays).
        // Nullable/optional: a species with no configured value here
        // simply falls back to Phase I's original manual-entry behavior
        // for ExpectedReadyDate on new Sowings. Editing this value later
        // never rewrites any existing SeedSowings row -- see
        // Models/SeedSowing.cs's own ReadyStockDays column, which stores
        // the value that was actually applied at sowing time.
        public int? ReadyStockDays { get; set; }

        // Navigation Property (Not stored in DB, used for reference)
        public PlantType? PlantType { get; set; }
    }
}
