using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Step 8A/8B/8C/8D/8E: DailyReport -> the 16 positional Sendvise template
    // variables, in the EXACT approved order. Pure (no HTTP, no IOptions, no
    // clock of its own -- reportTime is passed in, never DateTime.Now read
    // internally) so this is fully unit-testable and deterministic. Never
    // touches SendviseOptions/ApiKey -- this class cannot leak the API key
    // because it never has access to it.
    //
    // Step 8E (this change, 20 -> 16): the approved template now has room for
    // only 2 of the database's 4 Ready Stock Plant Types. There was no
    // existing rule anywhere in the codebase for "which 2" (dbo.PlantTypes
    // has only Id/Name -- no priority/featured/display-order column, and
    // nothing in the app ever picked a subset of Plant Types before this).
    // Explicitly approved rule (user decision, this step): the TOP 2 Plant
    // Types BY CURRENT READY STOCK QUANTITY, highest first -- recalculated
    // from DailyReport.ReadyStockByPlantType every time a report is built,
    // never a fixed/hard-coded pair. Ties are broken by PlantTypeId ascending
    // (LINQ OrderByDescending is a stable sort, and ReadyStockByPlantType
    // already arrives in PlantTypeId order from
    // ManagementDashboardRepository.GetReadyStockByPlantTypeAsync's own
    // `ORDER BY pt.Id`, so a tie simply keeps that existing order -- no
    // separate tiebreak code needed). Both the NAME and the quantity for
    // these 2 entries come from DailyReport.ReadyStockByPlantType -- never a
    // literal Plant Type name anywhere in this class. The other 2 Plant
    // Types' quantities are still fully computed by
    // ManagementDashboardRepository/DailyReportService exactly as before
    // (DailyReport.ReadyStockByPlantType still carries all 4); this class
    // simply no longer prints 2 of them in this specific 16-variable
    // template. Report Time (the old 17-variable template's {{2}}) remains
    // absent, as it was in the 20-variable template -- the reportTime
    // parameter is kept on FormatVariables' signature for compatibility but
    // still unused in the output.
    //
    // Step 8F (this change): {{15}}/{{16}} switched from counts back to
    // comma-separated employee NAMES (report.NoActivityRecordedCount/
    // NotLoggedInEmployeeCount remain on DailyReport, still populated, still
    // used for {{12}}-{{14}} and everywhere else -- only this template
    // mapping changed). Names come from DailyReportCalculations.
    // GetNoActivityRecordedNames/GetNotLoggedInNames -- pure functions that
    // already existed (added in Step 8B, left in place but unused by the
    // formatter since Step 8C) operating on DailyReport.EmployeesLoggedInToday/
    // EmployeesNotLoggedInToday, which DailyReportService already populates
    // from UserLoginHistoryRepository.GetTodaysActivityAsync. No new query,
    // no model/service change: this is the only file this step touches.
    public static class DailyReportWhatsAppFormatter
    {
        public const int ExpectedVariableCount = 16;
        private const string NoNames = "None";

        public static List<string> FormatVariables(DailyReport report, DateTime reportTime)
        {
            var top2 = report.ReadyStockByPlantType
                .OrderByDescending(p => p.Quantity)
                .Take(2)
                .ToList();
            var pt0 = PlantTypeAt(top2, 0);
            var pt1 = PlantTypeAt(top2, 1);

            return new List<string>
            {
                /* {{1}}  Date                    */ report.ReportDate.ToString("yyyy-MM-dd"),
                /* {{2}}  Sowing                  */ QuantityFormat.Qty(report.SowingQuantity),
                /* {{3}}  Ready Stock name #1     */ pt0.PlantTypeName,
                /* {{4}}  Ready Stock qty #1      */ QuantityFormat.Qty(pt0.Quantity),
                /* {{5}}  Ready Stock name #2     */ pt1.PlantTypeName,
                /* {{6}}  Ready Stock qty #2      */ QuantityFormat.Qty(pt1.Quantity),
                /* {{7}}  Bookings                */ $"Seedling: {report.SeedlingBookingsCount}, Potted: {report.PottedPlantBookingsCount}",
                /* {{8}}  Dispatch                */ $"Seedling: {report.SeedlingDispatchCount}, Potted: {report.PottedPlantDispatchCount}",
                /* {{9}}  Outlet Sales            */ report.OutletSalesCount.ToString(),
                /* {{10}} Fertilizer Usage        */ QuantityFormat.Qty3(report.FertilizerUsageQuantity),
                /* {{11}} Wastage                 */ QuantityFormat.Qty(report.WastageQuantity),
                /* {{12}} Total Employees         */ report.TotalActiveEmployees.ToString(),
                /* {{13}} Logged In               */ report.LoggedInEmployeeCount.ToString(),
                /* {{14}} Active Users            */ report.ActiveUserCount.ToString(),
                /* {{15}} No Activity Recorded    */ FormatNames(DailyReportCalculations.GetNoActivityRecordedNames(report.EmployeesLoggedInToday)),
                /* {{16}} Not Logged In           */ FormatNames(DailyReportCalculations.GetNotLoggedInNames(report.EmployeesNotLoggedInToday)),
            };
        }

        private static string FormatNames(List<string> names)
            => names.Count == 0 ? NoNames : string.Join(", ", names);

        // Defensive: a DailyReport with fewer than 2 ReadyStockByPlantType
        // entries (e.g. a hand-built report in a test that never populated
        // it) still produces exactly 16 variables -- a missing Plant Type
        // becomes an empty name and a 0 quantity, never an exception.
        private static ReadyStockPlantTypeQuantity PlantTypeAt(List<ReadyStockPlantTypeQuantity> ordered, int index)
        {
            if (index >= ordered.Count)
                return new ReadyStockPlantTypeQuantity(string.Empty, 0m);
            var entry = ordered[index];
            return entry.PlantTypeName == null ? entry with { PlantTypeName = string.Empty } : entry;
        }
    }
}
