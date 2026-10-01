using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Daily Report (Step 3A): the one genuinely pure (database-free)
    // calculation involved in assembling a DailyReport -- everything else
    // DailyReportService does is a direct pass-through of an existing
    // repository's own aggregate query, with nothing left to compute. This
    // is kept separate and static so it is unit-testable without a
    // database, the same reasoning as DirectSowingRules /
    // SeedSowingMonthlyReportRules.
    public static class DailyReportCalculations
    {
        // SeedlingFulfilmentRepository.GetDispatchRegisterAsync returns one
        // row per dispatch LINE (a dispatch can substitute/split across
        // several Ready Stock batches). "Today's dispatch count" means the
        // number of dispatch EVENTS (distinct SeedlingDispatchId), not the
        // number of lines; "quantity" is the sum of every line's Quantity.
        public static (int Count, decimal Quantity) SummarizeDispatchLines(IEnumerable<SeedlingDispatchLine> lines)
        {
            var list = lines as ICollection<SeedlingDispatchLine> ?? lines.ToList();
            var count = list.Select(l => l.SeedlingDispatchId).Distinct().Count();
            var quantity = list.Sum(l => l.Quantity);
            return (count, quantity);
        }

        // Step 5: UserLoginHistoryRepository.GetTodaysActivityAsync already
        // scopes to active employees only and already computes each one's
        // FirstLoginAtToday (EmployeeLoginActivity.LoggedInToday is a
        // computed property off that). This is purely a partition of that
        // one result set -- no new SQL, no re-filtering by IsActive (that
        // filtering already happened in the repository's own query).
        public static (List<EmployeeLoginActivity> LoggedIn, List<EmployeeLoginActivity> NotLoggedIn) PartitionEmployeeActivity(
            IEnumerable<EmployeeLoginActivity> activity)
        {
            var list = activity as ICollection<EmployeeLoginActivity> ?? activity.ToList();
            return (
                list.Where(e => e.LoggedInToday).ToList(),
                list.Where(e => !e.LoggedInToday).ToList());
        }

        // Step 7 (approved): takes the ALREADY-partitioned "logged in today"
        // list (from PartitionEmployeeActivity -- never re-queries or
        // re-fetches). ActiveUserCount = those also flagged HasActivityToday
        // by the repository's own EXISTS check (never re-derived here, never
        // guessed). NoActivityRecordedCount = exactly LoggedIn count minus
        // ActiveUserCount, the approved formula.
        public static (int ActiveUserCount, int NoActivityRecordedCount) SummarizeActiveUsers(
            IEnumerable<EmployeeLoginActivity> loggedInEmployees)
        {
            var list = loggedInEmployees as ICollection<EmployeeLoginActivity> ?? loggedInEmployees.ToList();
            var activeUserCount = list.Count(e => e.IsActiveUser);
            return (activeUserCount, list.Count - activeUserCount);
        }

        // Step 8B: the two WhatsApp name lists ({{13}}/{{14}}), derived
        // purely from the SAME partitioned lists Step 5/7 already produce --
        // no new SQL, no re-fetching, no re-filtering. "No Activity
        // Recorded" = logged-in employees who are NOT Active Users (exactly
        // the approved formula: LoggedIn minus ActiveUserCount, just named
        // instead of counted). Names are returned in the order the
        // repository already provides (ORDER BY u.Name), never re-sorted or
        // deduplicated here: UserLoginHistoryRepository.GetTodaysActivityAsync
        // selects FROM dbo.IMSUsers u, one row per Id, so the input already
        // guarantees one entry per employee -- two different employees who
        // happen to share a display name must both still appear.
        public static List<string> GetNoActivityRecordedNames(IEnumerable<EmployeeLoginActivity> loggedInEmployees)
            => loggedInEmployees.Where(e => !e.HasActivityToday).Select(e => e.Name).ToList();

        public static List<string> GetNotLoggedInNames(IEnumerable<EmployeeLoginActivity> notLoggedInEmployees)
            => notLoggedInEmployees.Select(e => e.Name).ToList();
    }
}
