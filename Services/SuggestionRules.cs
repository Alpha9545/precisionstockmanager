namespace PlantStockManager.Services
{
    // Suggestions & Problems (Database/Migrations/2026-10-07_SuggestionsProblems.sql).
    //
    // Three separate concepts, never mixed:
    //   Lifetime   a post lives 48 hours from CreatedAt (ExpiresAt is a computed database column);
    //              expired = !IsPermanent && ExpiresAt <= now. Nothing "expires" a post -- it is
    //              decided when read, with the database clock.
    //   Permanent  an administrator's decision: the post never expires. Not a permission bypass:
    //              who may SEE it is still decided by the Area (SuggestionAuthorization).
    //   Status     the business state of the issue (Open -> In Discussion -> In Progress ->
    //              Resolved / Closed). An expired post can be In Progress; a permanent one Open.
    // There are no likes, dislikes or reactions in this module by design.
    public static class SuggestionRules
    {
        public const int LifetimeHours = 48;
        public const int MaxTitleLength = 150;
        public const int MaxDescriptionLength = 2000;
        public const int MaxCommentLength = 1000;
        public const int MaxReasonLength = 500;
        public const int CommentEditWindowMinutes = 30;
        public const int PageSize = 20;

        public const string Open = "Open";
        public const string InDiscussion = "InDiscussion";
        public const string InProgress = "InProgress";
        public const string Resolved = "Resolved";
        public const string Closed = "Closed";
        public static readonly IReadOnlyList<string> Statuses = new[] { Open, InDiscussion, InProgress, Resolved, Closed };

        public const string Problem = "Problem";
        public const string Suggestion = "Suggestion";

        // Lifetime filter values (the administrator's "All / Active / Expired / Permanent / Deleted").
        public const string LifetimeAll = "All";
        public const string LifetimeActive = "Active";
        public const string LifetimeExpired = "Expired";
        public const string LifetimePermanent = "Permanent";
        public const string LifetimeDeleted = "Deleted";
        public static readonly IReadOnlyList<string> Lifetimes = new[] { LifetimeAll, LifetimeActive, LifetimeExpired, LifetimePermanent, LifetimeDeleted };
        public static readonly IReadOnlyList<string> EmployeeLifetimes = new[] { LifetimeActive, LifetimePermanent };

        public const string ChooseAreaMessage = "Choose an Area.";
        public const string PolyhouseNotInAreaMessage = "This Polyhouse does not belong to the selected Area.";
        public const string PolyhouseRequiredMessage = "Please select a Polyhouse for this Area.";
        public const string AlreadySubmittedMessage = "This form was already submitted.";
        public const string ChangedMessage = "This post was changed by someone else in the meantime. Nothing was saved -- please review it again.";

        public static string StatusLabel(string? status) => status switch
        {
            InDiscussion => "In Discussion",
            InProgress => "In Progress",
            null => "",
            _ => status
        };

        public static string StatusBadge(string? status) => status switch
        {
            Open => "bg-primary",
            InDiscussion => "bg-info text-dark",
            InProgress => "bg-warning text-dark",
            Resolved => "bg-success",
            Closed => "bg-secondary",
            _ => "bg-light text-dark"
        };

        public static string TypeBadge(string? type) => type == Problem ? "bg-danger" : type == Suggestion ? "bg-success" : "bg-secondary";

        public static string TypeIcon(string? type) => type == Problem ? "⚠" : type == Suggestion ? "💡" : "•";

        // The same rule as the database: ExpiresAt = CreatedAt + 48 h.
        public static DateTime ExpiresAt(DateTime createdAtUtc) => createdAtUtc.AddHours(LifetimeHours);

        public static bool IsExpired(bool isPermanent, DateTime expiresAtUtc, DateTime nowUtc) => !isPermanent && expiresAtUtc <= nowUtc;

        // "1 day 4 hours", "3 hours 20 minutes", "12 minutes", "less than a minute".
        public static string Remaining(DateTime expiresAtUtc, DateTime nowUtc)
        {
            var left = expiresAtUtc - nowUtc;
            if (left <= TimeSpan.Zero) return "expired";
            static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
            if (left.TotalDays >= 1) return left.Hours > 0 ? $"{Unit(left.Days, "day")} {Unit(left.Hours, "hour")}" : Unit(left.Days, "day");
            if (left.TotalHours >= 1) return left.Minutes > 0 ? $"{Unit(left.Hours, "hour")} {Unit(left.Minutes, "minute")}" : Unit(left.Hours, "hour");
            return left.TotalMinutes >= 1 ? Unit(left.Minutes, "minute") : "less than a minute";
        }

        // The MainOffice Area is organised by Polyhouse (the Daily Labour convention): a post there
        // names its Polyhouse. Other Areas MAY name one of their Polyhouses.
        public static bool RequiresPolyhouse(string? areaType) => DailyLabourRules.RequiresPolyhouse(areaType);

        public static bool CanEditComment(DateTime createdAtUtc, DateTime nowUtc)
            => nowUtc - createdAtUtc <= TimeSpan.FromMinutes(CommentEditWindowMinutes);

        public static string? Required(string? value, string label, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return $"{label} is required.";
            return value.Trim().Length > max ? $"{label} cannot be longer than {max} characters." : null;
        }

        public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
