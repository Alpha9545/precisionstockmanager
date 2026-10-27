using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // 2026-10-27: the Category and Source filters of the Sowings list (Pages/Production/SeedSowing/Index). A LIST aid only:
    // the filters narrow which rows are shown, AND-ed onto the page's own Area scope -- never widening it.
    //   PlantTypeId  the variety's category, dbo.PlantSpecies.PlantTypeId (dbo.PlantTypes master: the same "Plant Type" the
    //                Monthly Sowing report filters on)
    //   SourceType   dbo.SeedSowings.SourceType -- exactly the four values the table's CHECK constraints allow
    // Unset = no restriction. Values come from the query string, so Normalize validates them on the server.
    public sealed record SeedSowingListFilter(int? PlantTypeId, string? SourceType)
    {
        public static readonly SeedSowingListFilter None = new(null, null);

        public const string CategoryClearedMessage = "The selected category does not exist any more -- the Category filter was cleared.";
        public const string SourceClearedMessage = "Source was not a valid choice and was ignored.";

        // The Source dropdown: value = SeedSowings.SourceType, label = the model's own SourceLabel for that type.
        public static readonly IReadOnlyList<(string Value, string Label)> SourceOptions = new[]
        {
            (SeedSowing.SourceSeed, new SeedSowing { SourceType = SeedSowing.SourceSeed }.SourceLabel),
            (SeedSowing.SourceCutting, new SeedSowing { SourceType = SeedSowing.SourceCutting }.SourceLabel),
            (SeedSowing.SourceBedSowing, new SeedSowing { SourceType = SeedSowing.SourceBedSowing }.SourceLabel),
            (SeedSowing.SourceTransplant, new SeedSowing { SourceType = SeedSowing.SourceTransplant }.SourceLabel),
        };

        public bool IsActive => PlantTypeId.HasValue || SourceType != null;

        // plantTypeId: kept only when it is one of the dbo.PlantTypes ids (allowedPlantTypeIds). sourceType: exact (ordinal)
        // match to one of SourceOptions -- anything else is dropped, and Note says so.
        public static (SeedSowingListFilter Filter, string? Note) Normalize(int? plantTypeId, string? sourceType, IEnumerable<int> allowedPlantTypeIds)
        {
            string? note = null;
            int? category = null;
            if (plantTypeId.HasValue)
            {
                if (plantTypeId > 0 && allowedPlantTypeIds.Contains(plantTypeId.Value))
                    category = plantTypeId;
                else
                    note = CategoryClearedMessage;
            }
            string? source = null;
            if (!string.IsNullOrEmpty(sourceType))
            {
                var match = SourceOptions.FirstOrDefault(o => string.Equals(o.Value, sourceType, StringComparison.Ordinal));
                if (match.Value != null)
                    source = match.Value;
                else
                    note = note == null ? SourceClearedMessage : note + " " + SourceClearedMessage;
            }
            return (new SeedSowingListFilter(category, source), note);
        }
    }
}
