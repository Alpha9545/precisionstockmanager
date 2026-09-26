using System.Globalization;
using System.Text.RegularExpressions;

namespace PlantStockManager.Services
{
    // Controlled pot sizes (dbo.PotSizes). One standard spelling per size so
    // "5 inch", "5 Inch", "5\"", "5-inch" and "5in" can never become separate
    // stock pools. Sizes that are not inches ("Hanging basket", "10 L bag")
    // are kept as typed (trimmed, single-spaced) and compared case-insensitively.
    public static class PotSizeRules
    {
        private static readonly Regex InchPattern = new(
            @"^\s*(?<n>\d+(?:\.\d+)?)\s*(?:-|\s)*(?:""|''|in\.?|inch(?:es)?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Canonical display value: "5 inch", "4.5 inch", or the trimmed text.
        public static string Normalize(string? value)
        {
            var text = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
            var m = InchPattern.Match(text);
            if (!m.Success)
                return text;
            var n = decimal.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            return $"{n.ToString("0.##", CultureInfo.InvariantCulture)} inch";
        }

        // Duplicate-detection key: two sizes with the same key are the same size.
        public static string Key(string? value) => Normalize(value).ToLowerInvariant();

        public static (bool Ok, string Name, string? Error) Validate(string? value, IEnumerable<string> existingNames)
        {
            var name = Normalize(value);
            if (name.Length == 0)
                return (false, name, "Pot size is required.");
            if (name.Length > 50)
                return (false, name, "Pot size must be 50 characters or fewer.");
            var key = Key(name);
            var clash = existingNames.FirstOrDefault(e => Key(e) == key);
            if (clash != null)
                return (false, name, $"Pot size '{clash}' already exists.");
            return (true, name, null);
        }
    }
}
