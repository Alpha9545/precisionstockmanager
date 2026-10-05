using System.Globalization;

namespace PlantStockManager.Services
{
    // Single display rule for quantities/counts (plants, pots, trays,
    // cuttings, seeds, fertilizer...). Quantity columns are DECIMAL(18,2),
    // so a value read from SQL carries two decimal places and would print
    // as "100.00". This drops trailing zeroes but keeps any real fraction
    // and the thousands separator: 100.00 -> "100", 4762 -> "4,762",
    // 100.50 -> "100.5", 12.25 -> "12.25". Display only -- never use the
    // returned string for calculations, and never use it for money,
    // rates, percentages or measurements (those keep their own formats).
    public static class QuantityFormat
    {
        public const string DisplayPattern = "#,##0.##";

        // For <input> value attributes: no thousands separator, so the
        // browser/model binder can parse it back. Use via asp-format.
        public const string InputPattern = "0.##";
        public const string InputFormat = "{0:0.##}";

        public static string Qty(decimal value) =>
            value.ToString(DisplayPattern, CultureInfo.CurrentCulture);

        public static string Qty(decimal? value, string nullText = "-") =>
            value.HasValue ? Qty(value.Value) : nullText;

        public static string Qty(int value) => Qty((decimal)value);

        public static string Qty(int? value, string nullText = "-") =>
            value.HasValue ? Qty((decimal)value.Value) : nullText;

        // Fertilizer quantities are DECIMAL(18,3) (e.g. 12.125 kg), so they
        // keep up to three places instead of being rounded to two.
        public const string Display3Pattern = "#,##0.###";
        public const string Input3Format = "{0:0.###}";

        public static string Qty3(decimal value) =>
            value.ToString(Display3Pattern, CultureInfo.CurrentCulture);

        public static string Qty3(decimal? value, string nullText = "-") =>
            value.HasValue ? Qty3(value.Value) : nullText;

        public static string QtyInput(decimal value) =>
            value.ToString(InputPattern, CultureInfo.InvariantCulture);

        public static string QtyInput(decimal? value) =>
            value.HasValue ? QtyInput(value.Value) : string.Empty;

        public static string QtyInput3(decimal value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
