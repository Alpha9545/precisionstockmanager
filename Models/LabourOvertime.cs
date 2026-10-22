using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // One row of dbo.LabourOvertime. DurationMinutes / MaleWorkerMinutes / FemaleWorkerMinutes
    // are computed by the database from the stored times (read-only here).
    public class LabourOvertimeEntry
    {
        public int Id { get; set; }
        public DateTime LabourDate { get; set; }
        public int AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public int MaleWorkers { get; set; }
        public int FemaleWorkers { get; set; }
        public TimeOnly StartTime { get; set; }
        public TimeOnly EndTime { get; set; }
        public int DurationMinutes { get; set; }
        public int MaleWorkerMinutes { get; set; }
        public int FemaleWorkerMinutes { get; set; }
        public string WorkDescription { get; set; } = string.Empty;
        public string? Remarks { get; set; }
        public DateTime CreatedDate { get; set; }      // UTC
        public int? CreatedById { get; set; }
        public DateTime? ModifiedDate { get; set; }    // UTC
        public int? ModifiedById { get; set; }

        // Display-only, populated by joins in LabourOvertimeRepository.
        public string? AreaName { get; set; }
        public string? AreaType { get; set; }
        public string? PolyhouseName { get; set; }
        public string? CreatedByName { get; set; }
        public string? ModifiedByName { get; set; }

        public int TotalWorkers => MaleWorkers + FemaleWorkers;
        public decimal MaleWorkerHours => LabourOvertimeRules.Hours(MaleWorkerMinutes);
        public decimal FemaleWorkerHours => LabourOvertimeRules.Hours(FemaleWorkerMinutes);
        public decimal TotalWorkerHours => LabourOvertimeRules.Hours(MaleWorkerMinutes + FemaleWorkerMinutes);
        public string Duration => LabourOvertimeRules.FormatDuration(DurationMinutes);
        public string Location => PolyhouseId.HasValue ? $"{AreaName} / {PolyhouseName}" : AreaName ?? string.Empty;
    }

    // Totals of a group of overtime entries (a day, a month, an Area, a Polyhouse, or all).
    public class LabourOvertimeSummary
    {
        public string Key { get; set; } = string.Empty;
        public int Entries { get; set; }
        public int DurationMinutes { get; set; }
        public int MaleWorkers { get; set; }
        public int FemaleWorkers { get; set; }
        public int MaleWorkerMinutes { get; set; }
        public int FemaleWorkerMinutes { get; set; }

        public string Duration => LabourOvertimeRules.FormatDuration(DurationMinutes);
        public decimal MaleWorkerHours => LabourOvertimeRules.Hours(MaleWorkerMinutes);
        public decimal FemaleWorkerHours => LabourOvertimeRules.Hours(FemaleWorkerMinutes);
        public decimal TotalWorkerHours => LabourOvertimeRules.Hours(MaleWorkerMinutes + FemaleWorkerMinutes);

        public void Add(LabourOvertimeEntry e)
        {
            Entries++;
            DurationMinutes += e.DurationMinutes;
            MaleWorkers += e.MaleWorkers;
            FemaleWorkers += e.FemaleWorkers;
            MaleWorkerMinutes += e.MaleWorkerMinutes;
            FemaleWorkerMinutes += e.FemaleWorkerMinutes;
        }

        public static List<LabourOvertimeSummary> GroupBy(IEnumerable<LabourOvertimeEntry> entries, Func<LabourOvertimeEntry, string> key)
            => entries.GroupBy(key)
                .Select(g =>
                {
                    var s = new LabourOvertimeSummary { Key = g.Key };
                    foreach (var e in g) s.Add(e);
                    return s;
                })
                .ToList();
    }

    public class LabourOvertimeFilter
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        // 2026-10-20 (My Overtime Entries): only entries recorded by this user -- always the signed-in user's id, never a posted value
        public int? CreatedById { get; set; }
    }

    // One audit row of dbo.LabourOvertimeHistory (written by a trigger).
    public class LabourOvertimeChange
    {
        public int Id { get; set; }
        public string Action { get; set; } = string.Empty;
        public int? OldMaleWorkers { get; set; }
        public int? OldFemaleWorkers { get; set; }
        public TimeOnly? OldStartTime { get; set; }
        public TimeOnly? OldEndTime { get; set; }
        public string? OldWorkDescription { get; set; }
        public string? OldRemarks { get; set; }
        public int? NewMaleWorkers { get; set; }
        public int? NewFemaleWorkers { get; set; }
        public TimeOnly? NewStartTime { get; set; }
        public TimeOnly? NewEndTime { get; set; }
        public string? NewWorkDescription { get; set; }
        public string? NewRemarks { get; set; }
        public string? ChangedByName { get; set; }
        public DateTime ChangedDate { get; set; }      // UTC
    }

    // What the form saves (the database computes duration and worker-hours).
    public sealed record LabourOvertimeInput(int MaleWorkers, int FemaleWorkers, TimeOnly StartTime, TimeOnly EndTime, string WorkDescription, string? Remarks);
}
