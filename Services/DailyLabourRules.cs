using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Daily Labour Count (Database/Migrations/2026-10-04_DailyLabourCounts.sql):
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

        public static bool RequiresPolyhouse(string? areaType)
            => string.Equals(areaType, DirectSowingRules.MainOfficeAreaType, StringComparison.Ordinal);

        // Every Area except Outlet takes part in Daily Labour.
        public static bool IsLabourArea(string? areaType)
            => !string.Equals(areaType, OutletRules.AreaType, StringComparison.Ordinal);

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
        public static (string Field, string Message)? ValidateLocation(Area? area, bool isAuthorized, int? polyhouseId, Polyhouse? polyhouse)
        {
            if (area == null)
                return ("AreaId", "Area is required.");
            if (!area.IsActive)
                return ("AreaId", "The selected Area is not active.");
            if (!IsLabourArea(area.AreaType))
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
