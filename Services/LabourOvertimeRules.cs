namespace PlantStockManager.Services
{
    // Labour Overtime (Database/Migrations/2026-10-06_LabourOvertime.sql): one entry per
    // work period of a worker group at one location (same location rule as Daily Labour --
    // DailyLabourRules). Several entries on the same date and location are kept separate,
    // overlapping times included. Pure rules shared by the pages and the tests; the database
    // repeats every rule (CHECK constraints, TR_LabourOvertime_Location) and itself computes
    // DurationMinutes / MaleWorkerMinutes / FemaleWorkerMinutes from the stored times.
    public static class LabourOvertimeRules
    {
        public const int MaxDescriptionLength = 500;
        public const int MaxRemarksLength = 500;

        public const string TimesRequiredMessage = "Start time and end time are required.";
        public const string EndAfterStartMessage = "End time must be later than start time (overtime cannot continue past midnight -- enter it as two entries).";
        public const string AtLeastOneWorkerMessage = "Enter at least one worker (male or female).";
        public const string DescriptionRequiredMessage = "Work description is required.";
        public const string AlreadySubmittedMessage = "This overtime form was already submitted. Check Overtime History before adding it again.";

        // Minutes from start to end on the same day; never wraps past midnight
        // (TimeOnly subtraction would), so an end before the start is <= 0.
        public static int DurationMinutes(TimeOnly start, TimeOnly end)
            => (int)(end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;

        public static decimal WorkerHours(int workers, int durationMinutes)
            => workers * durationMinutes / 60m;

        public static decimal Hours(int minutes) => minutes / 60m;

        // 150 -> "2h 30m", 180 -> "3h", 45 -> "45m", 3680 -> "61h 20m".
        public static string FormatDuration(int minutes)
        {
            if (minutes <= 0) return "0m";
            var h = minutes / 60;
            var m = minutes % 60;
            return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m}m";
        }

        // 10 -> "10", 145.5 -> "145.5", 2.3333 -> "2.33".
        public static string FormatHours(decimal hours) => hours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // Times are kept to the minute (the database rejects seconds).
        public static TimeOnly? ToMinute(TimeOnly? time) => time.HasValue ? new TimeOnly(time.Value.Hour, time.Value.Minute) : null;

        // Everything except date and location (DailyLabourRules.ValidateDate / ValidateLocation).
        // Returns (field, message) pairs; empty = valid.
        public static List<(string Field, string Message)> Validate(int? maleWorkers, int? femaleWorkers, TimeOnly? start, TimeOnly? end, string? workDescription, string? remarks)
        {
            var errors = new List<(string, string)>();
            var maleError = DailyLabourRules.ValidateCount(maleWorkers, "Male Workers");
            if (maleError != null) errors.Add(("MaleWorkers", maleError));
            var femaleError = DailyLabourRules.ValidateCount(femaleWorkers, "Female Workers");
            if (femaleError != null) errors.Add(("FemaleWorkers", femaleError));
            if (maleError == null && femaleError == null && maleWorkers + femaleWorkers <= 0)
                errors.Add(("MaleWorkers", AtLeastOneWorkerMessage));

            if (!start.HasValue || !end.HasValue)
                errors.Add(("StartTime", TimesRequiredMessage));
            else if (DurationMinutes(ToMinute(start)!.Value, ToMinute(end)!.Value) <= 0)
                errors.Add(("EndTime", EndAfterStartMessage));

            if (string.IsNullOrWhiteSpace(workDescription))
                errors.Add(("WorkDescription", DescriptionRequiredMessage));
            else if (workDescription.Trim().Length > MaxDescriptionLength)
                errors.Add(("WorkDescription", $"Work description cannot be longer than {MaxDescriptionLength} characters."));
            if (remarks != null && remarks.Trim().Length > MaxRemarksLength)
                errors.Add(("Remarks", $"Remark cannot be longer than {MaxRemarksLength} characters."));
            return errors;
        }
    }
}
