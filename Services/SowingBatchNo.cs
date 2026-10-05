using System.Globalization;
using System.Text.RegularExpressions;
using PlantStockManager.Data;

namespace PlantStockManager.Services
{
    // Short business batch number shown in the Booking / Allocation workflow:
    // MONTH LETTER + "-" + TWO-DIGIT SOWING DAY, calculated from the sowing
    // date at display time (nothing is stored). The month letter is the same
    // one the full SowingCode already uses (BatchNumberRepository.MonthLetter:
    // January = A ... September = I, October = J ... December = L).
    //   2026-10-01 -> J-01    2026-10-10 -> J-10    2026-09-29 -> I-29
    //
    // It is a LABEL, not an identity: every sowing on the same date has the
    // same batch number (several "J-10" batches are expected). Records are
    // always identified by their own Id (SeedSowings.Id / ReadyStock.Id); the
    // full, unique SowingCode stays available as the reference.
    public static class SowingBatchNo
    {
        public static string Format(DateTime sowingDate)
            => $"{BatchNumberRepository.MonthLetter(sowingDate.Month)}-{sowingDate.Day.ToString("00", CultureInfo.InvariantCulture)}";

        private static readonly Regex Pattern = new(@"^\s*([A-La-l])\s*-?\s*(\d{1,2})\s*$", RegexOptions.Compiled);

        // Recognises a typed batch number for searching: "J-10", "j-10", "J10",
        // "J-1" (all -> October, day 10 / 1). Anything else is not a batch number.
        public static bool TryParse(string? text, out int month, out int day)
        {
            month = 0; day = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var m = Pattern.Match(text);
            if (!m.Success)
                return false;
            var d = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (d < 1 || d > 31)
                return false;
            month = char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A' + 1;
            day = d;
            return true;
        }
    }
}
