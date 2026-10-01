namespace PlantStockManager.Models
{
    // Step 3B: one row per SUCCESSFUL login (Pages/Account/Login.cshtml.cs).
    // Append-only, insert-only -- never updated, never deleted by the app.
    // Deliberately minimal: no Status/Success column (a row's mere
    // existence IS "a successful login happened"), no IP address, no user
    // agent -- none of the approved report's 5 questions need them; adding
    // failed-attempt tracking or request metadata later is a separate,
    // separately-approved feature, not bundled in here.
    public class UserLoginHistory
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public DateTime LoginAt { get; set; } // UTC
    }

    // The per-employee row the eventual "Employee Software Activity"
    // report section needs -- combines dbo.IMSUsers (active employees)
    // with dbo.UserLoginHistory (today's first login, if any) in one query
    // (UserLoginHistoryRepository.GetTodaysActivityAsync), so no caller
    // needs to separately load employees and logins and join them in C#.
    public class EmployeeLoginActivity
    {
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? DesignationName { get; set; }
        public bool LoggedInToday => FirstLoginAtToday.HasValue;
        public DateTime? FirstLoginAtToday { get; set; } // UTC; MIN(LoginAt) for the requested day

        // Step 7: at least one reliable, recorded operational business
        // action attributable to this user on the requested day -- see
        // UserLoginHistoryRepository.GetTodaysActivityAsync for the exact,
        // approved list of qualifying sources. Never inferred from login/
        // session/cookie presence -- only real rows in real tables.
        public bool HasActivityToday { get; set; }

        // "Active User" (Step 7, approved definition): logged in AND has
        // at least one qualifying activity, both on the requested day.
        public bool IsActiveUser => LoggedInToday && HasActivityToday;
    }
}
