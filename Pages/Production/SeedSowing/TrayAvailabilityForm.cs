using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // The "Empty trays available" panel shared by the tray-consuming sowing pages (Direct Seed Sowing, Confirm Cutting
    // Sowing, Bed Transplanting, Mother Plant Transplant into Trays): for the chosen cavity, the empty trays of every
    // location the page itself offers the user (its own eligible + authorized list -- never a request value), the
    // chosen location's balance and whether it covers the trays this entry needs (`needed`, computed by the page with
    // the same rule its save uses). A PREVIEW only: the repositories re-check and deduct under the pool lock on save.
    public static class TrayAvailabilityForm
    {
        public static async Task<object> BuildAsync(TrayStockRepository repo, string? cavityType, IReadOnlyList<TrayLocation> eligible,
            TrayLocation? selected, decimal needed)
        {
            if (!DirectSowingRules.IsValidCavityType(cavityType))
                return new { ok = false, cavity = (string?)null, rows = Array.Empty<object>(), selectedKey = (string?)null, needed = 0m, message = "Choose a cavity to see the empty trays available." };
            var rows = await repo.GetCavityAvailabilityAsync(cavityType!, eligible);
            // The chosen location counts only when it is one the page offered.
            var chosen = selected != null && eligible.Any(e => e.AreaId == selected.AreaId && e.PolyhouseId == selected.PolyhouseId)
                ? rows.FirstOrDefault(r => r.AreaPool ? r.AreaId == selected.AreaId : r.PolyhouseId == selected.PolyhouseId)
                : null;
            return new
            {
                ok = true,
                cavity = cavityType,
                rows = rows.Select(r => new { key = r.Key, location = r.Location, area = r.AreaName, polyhouse = r.PolyhouseName, areaPool = r.AreaPool, available = r.Available }),
                selectedKey = chosen?.Key,
                selectedLocation = chosen?.Location,
                selectedAvailable = chosen?.Available,
                needed = needed > 0 ? needed : 0,
                enough = chosen == null || needed <= 0 || needed <= chosen.Available,
                message = rows.Count == 0 ? "No location is available to you for this entry."
                        : rows.All(r => r.Available <= 0) ? $"No empty {cavityType} trays in any of your locations. Add empty trays first, or choose another cavity."
                        : null
            };
        }

        // Trays a whole-tray transplant needs: complete trays of the cavity only (FLOOR) -- the remainder is waste and
        // takes no tray (TransplantRules / BedSowingRules). Seed and Cutting Sowing use DirectSowingRules.PhysicalTraysRequired.
        public static decimal CompleteTrays(decimal quantity, string? cavityType)
            => DirectSowingRules.CavityCount(cavityType) is int c && c > 0 && quantity > 0 ? Math.Floor(quantity / c) : 0;
    }
}
