namespace PlantStockManager.Models
{
    // A candidate empty-tray source of a sowing page: an Area, and the Polyhouse the page offers (null = the Area
    // itself, e.g. Mother Plant transplanting, which never names a Polyhouse). Built by the page from ITS OWN list of
    // eligible, authorized locations -- never from request values.
    public sealed record TrayLocation(int AreaId, int? PolyhouseId);

    // Empty trays of one cavity in one Tray Stock pool (TrayStockRepository.GetCavityAvailabilityAsync).
    // Key "p{PolyhouseId}" = a Polyhouse pool (Main Office and every other Area), "a{AreaId}" = the Area pool of a
    // Mother Plant Area (2026-10-14) -- several offered Polyhouses of one Mother Plant Area share that ONE row, so the
    // same trays are never listed (or counted) twice.
    public sealed record TrayAvailabilityRow(string Key, int AreaId, string AreaName, int? PolyhouseId, string? PolyhouseName,
        bool AreaPool, string TraySize, decimal Available)
    {
        public string Location => AreaPool ? $"{AreaName} (Area)" : $"{PolyhouseName} ({AreaName})";
    }
}
