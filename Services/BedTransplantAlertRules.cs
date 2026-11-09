using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // 2026-11-07 Bed Sowing Alerts -- the pure rules (no database):
    //   Due Date      = bed Sowing Date + the variety's Bed Transplant Days (dbo.PlantSpecies.BedTransplantDays).
    //                   No days -> no Due Date: the bed is listed under "No transplant schedule", never dated by a guess.
    //   Alert         only an OPEN bed (Pending / Partially transplanted) that still has seedlings in it.
    //   Categories    Overdue / Due Today / Due Soon use SeedSowingRepository.ClassifyReadyAlert -- the SAME date rule and
    //                 the same default window as the Ready Alerts (Direct Seed Sowing), which stay exactly as they were.
    //   Read-only     an alert never changes a bed; transplanting stays on Bed Transplanting.
    public static class BedTransplantAlertRules
    {
        public const int MinDays = 1;
        public const int MaxDays = 365;   // = CK_PlantSpecies_BedTransplantDays
        public const int MaxWindowDays = 90;

        public const string Overdue = "Overdue";
        public const string DueToday = "ReadyToday";   // ClassifyReadyAlert's own value
        public const string DueSoon = "ReadySoon";
        public const string NoSchedule = "NoSchedule";
        public const string None = "None";

        public const string DaysRangeMessage = "Bed Transplant Days must be a whole number from 1 to 365, or left empty.";

        public static DateTime? DueDate(DateTime bedSowingDate, int? bedTransplantDays)
            => bedTransplantDays is >= MinDays and <= MaxDays ? bedSowingDate.Date.AddDays(bedTransplantDays.Value) : null;

        public static bool IsOpen(string? status, decimal remaining)
            => (status is BedSowing.StatusPending or BedSowing.StatusPartial) && remaining > 0;

        public static string Classify(string? status, decimal remaining, DateTime? dueDate, DateTime today, int windowDays)
        {
            if (!IsOpen(status, remaining))
                return None;
            if (!dueDate.HasValue)
                return NoSchedule;
            // "Sown" = "still open" for the shared classifier; the bed's own open check was done above.
            return SeedSowingRepository.ClassifyReadyAlert("Sown", dueDate, today.Date, windowDays);
        }

        // ?WindowDays= as on Ready Alerts: missing / zero / negative -> the shared default; capped so a typed URL cannot
        // ask for an unbounded list.
        public static int NormalizeWindow(int windowDays)
            => windowDays <= 0 ? SeedSowingRepository.DefaultReadySoonWindowDays : Math.Min(windowDays, MaxWindowDays);

        public static (bool Ok, string? Error) ValidateDays(int? days)
            => days is null or (>= MinDays and <= MaxDays) ? (true, null) : (false, DaysRangeMessage);

        // One pass, one classification per bed: the page lists and the dashboard counts both come from here.
        // Oldest due first (the most urgent on top); no-schedule beds oldest sowing first.
        public static BedTransplantAlertGroups Group(IEnumerable<BedTransplantAlert> beds, DateTime today, int windowDays)
        {
            var groups = new BedTransplantAlertGroups();
            foreach (var bed in beds.OrderBy(b => b.DueDate ?? DateTime.MaxValue).ThenBy(b => b.SowingDate).ThenBy(b => b.BedSowingId))
            {
                switch (Classify(bed.Status, bed.RemainingQuantity, bed.DueDate, today, windowDays))
                {
                    case Overdue: groups.Overdue.Add(bed); break;
                    case DueToday: groups.DueToday.Add(bed); break;
                    case DueSoon: groups.DueSoon.Add(bed); break;
                    case NoSchedule: groups.NoSchedule.Add(bed); break;
                }
            }
            return groups;
        }
    }
}
