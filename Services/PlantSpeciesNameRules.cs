using System.Text;

namespace PlantStockManager.Services
{
    // When two variety (PlantSpecies) names count as the same name: capital letters, surrounding spaces and runs of
    // whitespace inside the name (spaces, tabs, line breaks, non-breaking spaces) are ignored. Punctuation and spelling
    // are NOT: "Petunia S.C. White" and "Petunia SC White" stay different names, as do "Pavnee" and "Pawnee" -- deciding
    // that two spellings are one variety is an owner decision, never automatic. No database needed.
    public static class PlantSpeciesNameRules
    {
        public static string Key(string? name)
        {
            var sb = new StringBuilder();
            var pendingSpace = false;
            foreach (var ch in (name ?? "").Trim())
            {
                if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        public static bool SameName(string? a, string? b) => Key(a) == Key(b);

        // 2026-10-15 duplicate-variety cleanup: a merged duplicate is retired as "DUP-<its Id>-<its old name>".
        // The prefix is reserved -- no variety may be added or renamed to it, and a merged duplicate never comes back.
        public const string MergedPrefix = "DUP-";

        public static bool IsMergedDuplicate(string? name)
            => (name ?? "").TrimStart().StartsWith(MergedPrefix, StringComparison.OrdinalIgnoreCase);

        // "DUP-3926-Lantana Beach" -> "Lantana Beach" (the name it had when it was merged); null when not a merged name.
        public static string? MergedFromName(string? name)
        {
            if (!IsMergedDuplicate(name)) return null;
            var rest = name!.TrimStart()[MergedPrefix.Length..];
            var dash = rest.IndexOf('-');
            if (dash > 0 && rest[..dash].All(char.IsDigit)) rest = rest[(dash + 1)..];
            return rest.Trim();
        }
    }
}
