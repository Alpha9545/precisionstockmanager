using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Daily Labour (Database/Migrations/2026-10-04_DailyLabourCounts.sql, and since
    // 2026-10-06 two-time attendance -- 2026-10-06_DailyLabourAttendance.sql):
    // supervisors record daily HEADCOUNTS only -- no names, worker ids or wages.
    // The location + date is the record's identity:
    //   Main Office Area (Area.AreaType = DirectSowingRules.MainOfficeAreaType)
    //       -> Date + Area + Polyhouse (Polyhouse required, must belong to the Area)
    //   any other Area -> Date + Area   (Polyhouse must be empty)
    //   Outlet Area (OutletRules.AreaType) -> not part of Daily Labour at all
    // Pure rules, shared by the Entry page and the tests; the database repeats
    // the location rule (TR_DailyLabourCounts_Location), the 0..500 range
    // (CHECK constraints) and the uniqueness (filtered unique indexes).
    public static class DailyLabourRules
    {
        public const int MaxCount = 500;
        public const int MaxRemarksLength = 500;

        public const string PolyhouseRequiredMessage = "No Polyhouse selected. Please select a Polyhouse for this Main Office Area.";
        public const string PolyhouseNotInAreaMessage = "This Polyhouse does not belong to the selected Area.";
        public const string PolyhouseNotAllowedMessage = "Labour for this Area is recorded Area-wise. Do not select a Polyhouse.";
        public const string OutletNotAllowedMessage = "Outlet is not part of Daily Labour.";
        public const string NotAuthorizedMessage = "You are not authorized to enter labour for this Area.";
        public const string FutureDateMessage = "Future dates are not allowed.";
        public const string WholeNumberMessage = "Labour counts must be whole numbers.";
        public const string AlreadyExistsMessage = "Labour record already exists for this date and location. It has been loaded for editing.";

        // ---- Two-time attendance (2026-10-06_DailyLabourAttendance.sql) ----------------------
        // Headcounts at 9:00 AM and 2:00 PM; Full/Half Day are ESTIMATED from the two counts
        // because individual workers are not identified (Classify). A day with only one
        // attendance time recorded is Incomplete and stays out of the Full/Half Day totals.
        public const string Morning = "AM09";
        public const string Afternoon = "PM14";
        public static readonly IReadOnlyList<string> AttendanceTimes = new[] { Morning, Afternoon };

        public const string EstimateNote = "Full Day / Half Day are estimated from the 9:00 AM and 2:00 PM headcounts "
            + "(Full Day = the smaller count, Half Day = the difference), separately for male and female workers. "
            + "Individual workers are not identified.";
        public const string AttendanceRequiredMessage = "Enter the 9:00 AM attendance, the 2:00 PM attendance, or both.";
        public const string LegacyRecordExistsMessage = "This date and location already has a manual labour record (entered before attendance was introduced). Attendance cannot be added for it.";
        public const string ManualEntryRetiredMessage = "Daily Labour is now recorded as 9:00 AM and 2:00 PM attendance. Manual Full Day / Half Day entry is no longer used for new records.";

        public static bool IsAttendanceTime(string? code) => code == Morning || code == Afternoon;

        public static string AttendanceTimeLabel(string? code) => code switch
        {
            Morning => "9:00 AM",
            Afternoon => "2:00 PM",
            _ => code ?? string.Empty,
        };

        // Full Day = present at both times (the smaller count); Half Day = present at only
        // one time (the difference). Applied separately to male and female counts.
        public static (int FullDay, int HalfDay) Classify(int at9, int at2)
            => (Math.Min(at9, at2), Math.Abs(at9 - at2));

        // One attendance time on the form: both counts blank = not entered (allowed only if
        // that time was never recorded -- a recorded time can be corrected, not removed);
        // otherwise both counts are required and each must be 0..MaxCount.
        public static List<string> ValidateAttendanceTime(int? male, int? female, string code, bool alreadyRecorded)
        {
            var label = AttendanceTimeLabel(code);
            var errors = new List<string>();
            if (!male.HasValue && !female.HasValue)
            {
                if (alreadyRecorded)
                    errors.Add($"{label} attendance is already recorded -- correct the counts instead of leaving them blank (enter 0 if none).");
                return errors;
            }
            foreach (var (value, who) in new[] { (male, "Male"), (female, "Female") })
            {
                var error = ValidateCount(value, $"{label} {who}");
                if (error != null)
                    errors.Add(error);
            }
            return errors;
        }

        public static bool RequiresPolyhouse(string? areaType)
            => string.Equals(areaType, DirectSowingRules.MainOfficeAreaType, StringComparison.Ordinal);

        // Every Area except Outlet takes part in Daily Labour.
        public static bool IsLabourArea(string? areaType)
            => !string.Equals(areaType, OutletRules.AreaType, StringComparison.Ordinal);

        // 2026-10-23: ATTENDANCE (9:00 AM / 2:00 PM headcounts) also covers Outlet Areas, Area-wise, once
        // 2026-10-26_LabourAttendanceOutlet.sql is applied (outletAllowed = LabourAttendanceOutletSchema). Manual
        // labour counts and overtime keep IsLabourArea.
        public static bool IsAttendanceArea(string? areaType, bool outletAllowed)
            => outletAllowed || IsLabourArea(areaType);

        public static int TotalWorkers(int maleFull, int maleHalf, int femaleFull, int femaleHalf)
            => maleFull + maleHalf + femaleFull + femaleHalf;

        // A half day counts as 0.5 of a labour day.
        public static decimal LabourDays(int maleFull, int maleHalf, int femaleFull, int femaleHalf)
            => maleFull + femaleFull + (maleHalf + femaleHalf) * 0.5m;

        public static string? ValidateDate(DateTime? labourDate, DateTime today)
        {
            if (!labourDate.HasValue || labourDate.Value == default)
                return "Date is required.";
            if (labourDate.Value.Date > today.Date)
                return FutureDateMessage;
            return null;
        }

        public static string? ValidateCount(int? value, string label)
        {
            if (!value.HasValue)
                return $"{label} is required (enter 0 if none).";
            if (value.Value < 0)
                return $"{label} cannot be negative.";
            if (value.Value > MaxCount)
                return $"{label} cannot be more than {MaxCount}.";
            return null;
        }

        public static string? ValidateRemarks(string? remarks)
            => remarks != null && remarks.Trim().Length > MaxRemarksLength
                ? $"Remark cannot be longer than {MaxRemarksLength} characters."
                : null;

        // Location check for a NEW record. area/polyhouse are what the server
        // loaded for the posted ids (null = not found); isAuthorized is
        // AreaAccessService's answer for the Area. Returns the first problem.
        public static (string Field, string Message)? ValidateLocation(Area? area, bool isAuthorized, int? polyhouseId, Polyhouse? polyhouse, bool outletAllowed = false)
        {
            if (area == null)
                return ("AreaId", "Area is required.");
            if (!area.IsActive)
                return ("AreaId", "The selected Area is not active.");
            if (!IsAttendanceArea(area.AreaType, outletAllowed))
                return ("AreaId", OutletNotAllowedMessage);
            if (!isAuthorized)
                return ("AreaId", NotAuthorizedMessage);

            if (RequiresPolyhouse(area.AreaType))
            {
                if (!polyhouseId.HasValue || polyhouseId.Value <= 0)
                    return ("PolyhouseId", PolyhouseRequiredMessage);
                if (polyhouse == null)
                    return ("PolyhouseId", "The selected Polyhouse does not exist.");
                if (polyhouse.AreaId != area.Id)
                    return ("PolyhouseId", PolyhouseNotInAreaMessage);
            }
            else if (polyhouseId.HasValue && polyhouseId.Value > 0)
            {
                return ("PolyhouseId", PolyhouseNotAllowedMessage);
            }
            return null;
        }
    }
}
