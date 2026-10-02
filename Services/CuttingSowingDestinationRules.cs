namespace PlantStockManager.Services
{
    // Cutting Tray Sowing destination (2026-10-01 correction): the destination
    // growing Main Area / Polyhouse is chosen by the Sowing Supervisor on the
    // SAME screen as the Cutting Quantity / Tray Cavity (Pages/Production/
    // SeedSowing/CreateFromCutting) -- never by the Mother Plant Supervisor,
    // and never deferred to a later confirmation step. Outlet is a
    // customer-facing stock location, never a valid sowing destination.
    //
    // 2026-10-02 correction: a real, actual Polyhouse (a named physical
    // facility, e.g. "Facility-5") IS a valid destination even when it is
    // organisationally filed under a Main Office-type Area -- that Area's
    // own Cutting Stock DEPOT concept is what is never a destination (there
    // is no blank/fallback path that could silently default a sowing to it;
    // PolyhouseId is always mandatory), not every Polyhouse physically
    // located there.
    public static class CuttingSowingDestinationRules
    {
        public const string PolyhouseRequiredMessage = "Please select a Main Area / Polyhouse.";
        public const string SupervisorRequiredMessage = "Please select a Supervisor.";
        public const string InvalidDestinationMessage =
            "Choose the Main Area / Polyhouse where this cutting is actually sown. Outlet and inactive Areas are not valid sowing destinations.";

        public static (bool Ok, string? Error) ValidateDestination(int? areaId, bool areaIsActive, string? areaType)
        {
            if (areaId is not > 0 || !areaIsActive
                || string.Equals(areaType, OutletRules.AreaType, StringComparison.Ordinal))
                return (false, InvalidDestinationMessage);
            return (true, null);
        }
    }
}
