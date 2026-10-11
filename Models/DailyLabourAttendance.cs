using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // One row of dbo.DailyLabourAttendance: the headcount of one location at
    // one attendance time (DailyLabourRules.Morning = 9:00 AM, Afternoon = 2:00 PM).
    public class DailyLabourAttendanceEntry
    {
        public int Id { get; set; }
        public DateTime LabourDate { get; set; }
        public int AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public string AttendanceTime { get; set; } = string.Empty;
        public int MaleCount { get; set; }
        public int FemaleCount { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedDate { get; set; }      // UTC
        public int? CreatedById { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime? ModifiedDate { get; set; }    // UTC
        public int? ModifiedById { get; set; }
        public string? ModifiedByName { get; set; }
    }

    // One day of one location in Attendance History: either the two attendance
    // times (Source = Attendance) or an older manual record (Source = Manual,
    // dbo.DailyLabourCounts -- its Full/Half Day were entered, not estimated).
    public class DailyLabourDay
    {
        public const string AttendanceSource = "Attendance";
        public const string ManualSource = "Manual";

        public string Source { get; set; } = AttendanceSource;
        public int? ManualId { get; set; }
        public DateTime LabourDate { get; set; }
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }

        public int? Male9 { get; set; }
        public int? Female9 { get; set; }
        public int? Male2 { get; set; }
        public int? Female2 { get; set; }
        public bool Corrected { get; set; }

        // Manual records only.
        public int ManualMaleFullDay { get; set; }
        public int ManualMaleHalfDay { get; set; }
        public int ManualFemaleFullDay { get; set; }
        public int ManualFemaleHalfDay { get; set; }

        public bool IsManual => Source == ManualSource;
        public bool Has9 => Male9.HasValue && Female9.HasValue;
        public bool Has2 => Male2.HasValue && Female2.HasValue;
        // Incomplete = only one attendance time recorded: no Full/Half Day estimate.
        public bool IsComplete => IsManual || (Has9 && Has2);

        public int? MaleFullDay => IsManual ? ManualMaleFullDay : IsComplete ? DailyLabourRules.Classify(Male9!.Value, Male2!.Value).FullDay : null;
        public int? MaleHalfDay => IsManual ? ManualMaleHalfDay : IsComplete ? DailyLabourRules.Classify(Male9!.Value, Male2!.Value).HalfDay : null;
        public int? FemaleFullDay => IsManual ? ManualFemaleFullDay : IsComplete ? DailyLabourRules.Classify(Female9!.Value, Female2!.Value).FullDay : null;
        public int? FemaleHalfDay => IsManual ? ManualFemaleHalfDay : IsComplete ? DailyLabourRules.Classify(Female9!.Value, Female2!.Value).HalfDay : null;
        public int? TotalFullDay => IsComplete ? MaleFullDay + FemaleFullDay : null;
        public int? TotalHalfDay => IsComplete ? MaleHalfDay + FemaleHalfDay : null;
        public decimal? LabourDays => IsComplete ? TotalFullDay + TotalHalfDay * 0.5m : null;
    }

    // Totals over every matching day. Only complete days (both times, or a manual
    // record) are counted in Full/Half Day; Incomplete days are only counted.
    public class DailyLabourDaySummary
    {
        public int Days { get; set; }
        public int CompleteDays { get; set; }
        public int IncompleteDays { get; set; }
        public int ManualDays { get; set; }
        public int MaleFullDay { get; set; }
        public int MaleHalfDay { get; set; }
        public int FemaleFullDay { get; set; }
        public int FemaleHalfDay { get; set; }

        public int TotalFullDay => MaleFullDay + FemaleFullDay;
        public int TotalHalfDay => MaleHalfDay + FemaleHalfDay;
        public decimal LabourDays => TotalFullDay + TotalHalfDay * 0.5m;

        public void Add(DailyLabourDay d)
        {
            Days++;
            if (d.IsManual) ManualDays++;
            if (!d.IsComplete)
            {
                IncompleteDays++;
                return;
            }
            CompleteDays++;
            MaleFullDay += d.MaleFullDay ?? 0;
            MaleHalfDay += d.MaleHalfDay ?? 0;
            FemaleFullDay += d.FemaleFullDay ?? 0;
            FemaleHalfDay += d.FemaleHalfDay ?? 0;
        }
    }

    // One audit row of dbo.DailyLabourAttendanceHistory (written by a trigger).
    public class DailyLabourAttendanceChange
    {
        public int Id { get; set; }
        public string AttendanceTime { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public int? OldMaleCount { get; set; }
        public int? OldFemaleCount { get; set; }
        public string? OldRemarks { get; set; }
        public int? NewMaleCount { get; set; }
        public int? NewFemaleCount { get; set; }
        public string? NewRemarks { get; set; }
        public string? ChangedByName { get; set; }
        public DateTime ChangedDate { get; set; }      // UTC
    }

    // What to save for one attendance time (null = not entered on the form).
    public sealed record DailyLabourAttendanceInput(string AttendanceTime, int MaleCount, int FemaleCount, string? Remarks);
}
