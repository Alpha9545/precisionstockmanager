namespace PlantStockManager.Services
{
    // 2026-10-22: the Variety and Sowing Date filters of Bed Transplanting (step 1, the list of beds) and Sowing Approvals
    // (Waiting for approval). A LIST aid only: the filters narrow which rows are shown, AND-ed onto the page's own Area /
    // approval scope inside the repository query -- never widening it, and never deciding which bed is transplanted or which
    // batch is approved (those still go by the bed / batch id, re-checked by the save).
    //   SpeciesId   dbo.BedSowings.SpeciesId / dbo.SeedSowings.SpeciesId (the variety, dbo.PlantSpecies.Id)
    //   SowingDate  the calendar day of dbo.BedSowings.SowingDate (DATE) / dbo.SeedSowings.SowingDate (DATETIME2: the whole
    //               day, [day 00:00, next day 00:00) -- the same calendar date the pages display with ToString("dd/MM/yyyy"))
    // Unset = no restriction. Values come from the query string / form, so Normalize validates them on the server.
    public sealed record SowingListFilter(int? SpeciesId, DateTime? SowingDate)
    {
        public static readonly SowingListFilter None = new(null, null);
        public static readonly DateTime MinDate = new(2000, 1, 1);
        public static readonly DateTime MaxDate = new(2100, 12, 31);

        public const string VarietyClearedMessage = "The selected variety has nothing listed here any more -- the Variety filter was cleared.";
        public const string InvalidDateMessage = "Sowing Date was not a valid date and was ignored.";

        public bool IsActive => SpeciesId.HasValue || SowingDate.HasValue;
        public DateTime? DayStart => SowingDate?.Date;
        public DateTime? DayEnd => SowingDate?.Date.AddDays(1);
        // yyyy-MM-dd for <input type="date"> and route values.
        public string? SowingDateValue => SowingDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // speciesId: kept only when it is one of the page's own options (built from the user's scope on the server) -- an id the
        // user cannot see is dropped (Note says so). sowingDate: the date part only, inside MinDate..MaxDate.
        public static (SowingListFilter Filter, string? Note) Normalize(int? speciesId, DateTime? sowingDate, IEnumerable<int> allowedSpeciesIds)
        {
            string? note = null;
            int? species = null;
            if (speciesId is > 0)
            {
                if (allowedSpeciesIds.Contains(speciesId.Value))
                    species = speciesId;
                else
                    note = VarietyClearedMessage;
            }
            DateTime? date = null;
            if (sowingDate.HasValue)
            {
                if (sowingDate.Value.Date >= MinDate && sowingDate.Value.Date <= MaxDate)
                    date = sowingDate.Value.Date;
                else
                    note = note == null ? InvalidDateMessage : note + " " + InvalidDateMessage;
            }
            return (new SowingListFilter(species, date), note);
        }
    }

    // One Variety dropdown option (dbo.PlantSpecies).
    public sealed record VarietyOption(int Id, string Name, string? PlantTypeName)
    {
        public string Label => string.IsNullOrWhiteSpace(PlantTypeName) ? Name : $"{Name} ({PlantTypeName})";
    }

    // Pages/Shared/_SowingListFilter.cshtml: the Variety / Sowing Date filter bar (a GET form on the page itself).
    //   Page    the page to filter (asp-page); Hidden: the page's other query values to keep (e.g. Sowing Approvals' category)
    //   Shown   the rows listed after filtering (for the "n shown" line)
    public sealed record SowingListFilterView(string Page, IReadOnlyList<VarietyOption> Options, SowingListFilter Filter, string? Note,
        IReadOnlyDictionary<string, string?> Hidden, int Shown, string IdPrefix);
}
