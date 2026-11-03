using System.Globalization;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 4 (2026-11-03): the pure rules of an expense -- no database, no Razor (unit-tested). The database
    // re-checks the same rules (CHECKs and TR_FieldExpenses_Guard in 2026-11-03_FieldWorkExpenses.sql).
    public static class FieldExpenseRules
    {
        public const string StatusSubmitted = "Submitted";
        public const string StatusApproved = "Approved";
        public const string StatusRejected = "Rejected";
        public const string StatusCancelled = "Cancelled";
        public static readonly IReadOnlyList<string> Statuses = new[] { StatusSubmitted, StatusApproved, StatusRejected, StatusCancelled };

        public const string CategoryOther = "Other";
        public static readonly IReadOnlyList<string> Categories = new[] { "Petrol", "Food", "Lodging", "Toll", "Parking", CategoryOther };

        public const decimal MaxAmount = 100_000m;
        public const int MaxNoteLength = 200;
        public const int MaxReasonLength = 200;
        public const int MaxCommentLength = 300;
        public const int MaxRangeDays = 366;

        public static string StatusLabel(string status) => status == StatusSubmitted ? "Pending" : status;

        // "1,250.50" / "1250" -> 1250.50; null when it is not a plain number.
        public static decimal? ParseAmount(string? text)
        {
            var t = (text ?? "").Trim().Replace(",", "");
            return decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        public sealed record Input(string? Category, string? Amount, DateTime? Date, string? Note);
        public sealed record Clean(string Category, decimal Amount, DateTime Date, string? Note);

        // The earliest date an expense may carry: today (India) minus backdateDays (7 = today and the 7 dates before it).
        public static DateTime EarliestDate(DateTime todayIst, int backdateDays) => todayIst.Date.AddDays(-Math.Max(0, backdateDays));

        // Everything about the entered values. No date = today (the server's India date).
        public static IReadOnlyList<string> Validate(Input input, DateTime todayIst, int backdateDays, out Clean? clean)
        {
            var errors = new List<string>();
            clean = null;
            var category = FieldCustomerRules.Clean(input.Category);
            if (category == null || !Categories.Contains(category))
                errors.Add("Choose the category.");
            var amount = ParseAmount(input.Amount);
            if (FieldCustomerRules.Clean(input.Amount) == null)
                errors.Add("Enter the amount.");
            else if (amount == null)
                errors.Add("The amount must be a number (for example 250 or 250.50).");
            else if (amount <= 0)
                errors.Add("The amount must be more than zero.");
            else if (amount > MaxAmount)
                errors.Add($"The amount can be at most Rs {MaxAmount:#,0}.");
            else if (decimal.Round(amount.Value, 2) != amount.Value)
                errors.Add("The amount can have at most 2 decimals (paise).");
            var date = (input.Date ?? todayIst).Date;
            if (date > todayIst.Date)
                errors.Add("The date cannot be in the future.");
            else if (date < EarliestDate(todayIst, backdateDays))
                errors.Add($"The date can be at most {Math.Max(0, backdateDays)} days back ({EarliestDate(todayIst, backdateDays):dd MMM yyyy}).");
            var note = FieldCustomerRules.Clean(input.Note);
            if (note != null && note.Length > MaxNoteLength)
                errors.Add($"The note can be at most {MaxNoteLength} characters.");
            else if (note == null && category == CategoryOther)
                errors.Add("Write a note for an \"Other\" expense.");
            if (errors.Count == 0)
                clean = new Clean(category!, amount!.Value, date, note);
            return errors;
        }

        // null = the bill photo is acceptable, otherwise the message.
        public static string? CheckBill(bool hasPhoto, int? reminderVersion, int currentReminderVersion)
            => !hasPhoto ? "Take a photo of the bill."
               : reminderVersion != currentReminderVersion ? "The photo reminder has changed. Reload the page and take the bill photo again."
               : null;

        public static string? ValidateReason(string? reason, string what)
        {
            var r = FieldCustomerRules.Clean(reason);
            return r == null ? $"Give a reason for {what}."
                 : r.Length > MaxReasonLength ? $"The reason can be at most {MaxReasonLength} characters."
                 : null;
        }

        // The owner cancels their own expense, only while it is pending.
        public static string? CheckCancel(string status, int ownerId, int userId)
            => ownerId != userId ? "Only the employee who added the expense can cancel it."
               : status != StatusSubmitted ? $"This expense is already {StatusLabel(status).ToLowerInvariant()} and cannot be cancelled."
               : null;

        // A field manager decides a pending expense -- never their own, unless they are an administrator.
        public static string? CheckDecision(string status, int ownerId, int deciderId, bool mayManage, bool isFullAccess)
            => !mayManage ? "Only a field manager can approve or reject expenses."
               : ownerId == deciderId && !isFullAccess ? "You cannot approve or reject your own expense. Another manager or an administrator must do it."
               : status != StatusSubmitted ? $"This expense is already {StatusLabel(status).ToLowerInvariant()}."
               : null;

        public static string? ValidateComment(bool reject, string? comment)
        {
            var c = FieldCustomerRules.Clean(comment);
            return reject && c == null ? "Write why the expense is rejected."
                 : c != null && c.Length > MaxCommentLength ? $"The comment can be at most {MaxCommentLength} characters."
                 : null;
        }

        // "yyyy-MM" -> the first and last date of that month; an empty / invalid / future month = the current month.
        public static (DateTime From, DateTime To) MonthRange(string? month, DateTime todayIst)
        {
            var current = new DateTime(todayIst.Year, todayIst.Month, 1);
            var first = DateTime.TryParseExact((month ?? "").Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)
                        && m <= current && m.Year >= 2000 ? m : current;
            return (first, first.AddMonths(1).AddDays(-1));
        }

        public sealed record Filter(DateTime From, DateTime To, int? UserId, string? Category, string? Status);

        // Manager list: dates default to the current month (1st -> today); unknown employee / category / status values are
        // dropped (never widen anything); the range is capped.
        public static Filter NormalizeFilter(DateTime? from, DateTime? to, int? userId, string? category, string? status,
            IReadOnlyCollection<int> knownEmployeeIds, DateTime todayIst)
        {
            var t = (to ?? todayIst).Date;
            var f = (from ?? new DateTime(t.Year, t.Month, 1)).Date;
            if (f > t)
                (f, t) = (t, f);
            if ((t - f).TotalDays > MaxRangeDays)
                f = t.AddDays(-MaxRangeDays);
            return new Filter(f, t,
                userId.HasValue && knownEmployeeIds.Contains(userId.Value) ? userId : null,
                category != null && Categories.Contains(category) ? category : null,
                status != null && Statuses.Contains(status) ? status : null);
        }
    }
}
