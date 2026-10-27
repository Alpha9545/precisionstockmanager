using Microsoft.AspNetCore.Http;
using ReadyBatch = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Services
{
    // 2026-10-27: the option-list filters of the Ready Stock list (Pages/Production/ReadyStock/Index), next to its existing
    // Search / Stock (All, Overgrowth only, Normal only) / Include-empty filters. A LIST aid only: they narrow which rows are
    // shown and exported, AND-ed onto the page's Area scope -- never widening it, never changing a batch or its balances.
    //   Species    ReadyStock.PlantTypeName (dbo.PlantTypes.Name of the variety -- the list's "Species" column)
    //   VarietyId  ReadyStock.SpeciesId (dbo.PlantSpecies.Id -- the list's "Variety" column)
    //   AreaId     ReadyStock.AreaId;  PolyhouseId  ReadyStock.PolyhouseId
    //   Origin     ReadyStock.OriginType (Sowing / Transfer / Transplant)
    //   Status     the derived ReadyStock.Status text (StatusValues)
    // Every option is built from the rows the user may already see (OptionsFor over the Area-scoped list), so the dropdowns
    // never name another Area's Area, Polyhouse or variety; Normalize keeps a value only when it is one of those options.
    public sealed record ReadyStockListFilter(string? Species, int? VarietyId, int? AreaId, int? PolyhouseId, string? Origin, string? Status)
    {
        public static readonly ReadyStockListFilter None = new(null, null, null, null, null, null);

        // Query-string names (PascalCase, like the page's existing Stock / Search / IncludeEmpty).
        public const string SpeciesKey = "Species", VarietyKey = "Variety", AreaKey = "Area", PolyhouseKey = "Polyhouse",
            OriginKey = "Origin", StatusKey = "Status";
        public static readonly IReadOnlyList<string> Keys = new[] { SpeciesKey, VarietyKey, AreaKey, PolyhouseKey, OriginKey, StatusKey };

        public const string ClearedMessage = "A selected filter is not available in your Ready Stock list any more and was cleared:";
        public const int MaxTextLength = 100;

        // Every value ReadyStock.Status can take, in the order the list shows them.
        public static readonly IReadOnlyList<string> StatusValues = new[]
            { "Available", "Partly reserved/dispatched", "Fully reserved", "In transit", "Fully dispatched", "Empty" };

        public static readonly IReadOnlyList<string> OriginValues = new[] { ReadyBatch.OriginSowing, ReadyBatch.OriginTransfer, ReadyBatch.OriginTransplant };
        public static string OriginLabel(string origin) => new ReadyBatch { OriginType = origin }.OriginLabel;

        public bool IsActive => Species != null || VarietyId.HasValue || AreaId.HasValue || PolyhouseId.HasValue || Origin != null || Status != null;

        public sealed record IdOption(int Id, string Label);
        public sealed record ListOptions(IReadOnlyList<string> Species, IReadOnlyList<VarietyOption> Varieties, IReadOnlyList<IdOption> Areas,
            IReadOnlyList<IdOption> Polyhouses, IReadOnlyList<string> Origins, IReadOnlyList<string> Statuses);

        // The dropdowns: only values present in rows (the caller passes its Area-scoped rows).
        public static ListOptions OptionsFor(IEnumerable<ReadyBatch> rows)
        {
            var list = rows.ToList();
            var polyhouses = list.Where(r => r.PolyhouseId.HasValue)
                .GroupBy(r => r.PolyhouseId!.Value)
                .Select(g => (Id: g.Key, Name: g.First().PolyhouseName ?? $"Polyhouse {g.Key}", Areas: g.Select(r => r.AreaName).Distinct().ToList()))
                .ToList();
            // A Polyhouse name used in more than one listed Area gets its Area in the label.
            var repeated = polyhouses.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ListOptions(
                list.Select(r => r.PlantTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
                    .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                list.GroupBy(r => r.SpeciesId)
                    .Select(g => new VarietyOption(g.Key, g.First().SpeciesName ?? $"Variety {g.Key}", g.First().PlantTypeName))
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Id).ToList(),
                list.GroupBy(r => r.AreaId)
                    .Select(g => new IdOption(g.Key, g.First().AreaName ?? $"Area {g.Key}"))
                    .OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList(),
                polyhouses
                    .Select(p => new IdOption(p.Id, repeated.Contains(p.Name) ? $"{p.Name} ({string.Join(", ", p.Areas)})" : p.Name))
                    .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList(),
                OriginValues.Where(o => list.Any(r => r.OriginType == o)).ToList(),
                StatusValues.Where(s => list.Any(r => r.Status == s)).ToList());
        }

        // Keeps each value only when it is one of the options (exact, ordinal match for text); the rest are dropped and Note
        // names them. An unset / empty value is simply no restriction.
        public static (ReadyStockListFilter Filter, string? Note) Normalize(string? species, int? varietyId, int? areaId, int? polyhouseId,
            string? origin, string? status, ListOptions options)
        {
            var cleared = new List<string>();
            string? Text(string? value, IReadOnlyList<string> allowed, string name)
            {
                if (string.IsNullOrEmpty(value)) return null;
                var match = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.Ordinal));
                if (match == null) cleared.Add(name);
                return match;
            }
            int? Id(int? value, IEnumerable<int> allowed, string name)
            {
                if (!value.HasValue) return null;
                if (value > 0 && allowed.Contains(value.Value)) return value;
                cleared.Add(name);
                return null;
            }
            var filter = new ReadyStockListFilter(
                Text(species, options.Species, "Species"),
                Id(varietyId, options.Varieties.Select(v => v.Id), "Variety"),
                Id(areaId, options.Areas.Select(a => a.Id), "Area"),
                Id(polyhouseId, options.Polyhouses.Select(p => p.Id), "Polyhouse"),
                Text(origin, options.Origins, "Origin"),
                Text(status, options.Statuses, "Status"));
            return (filter, cleared.Count == 0 ? null : $"{ClearedMessage} {string.Join(", ", cleared)}.");
        }

        public bool Matches(ReadyBatch r)
            => (Species == null || string.Equals(r.PlantTypeName, Species, StringComparison.Ordinal))
            && (!VarietyId.HasValue || r.SpeciesId == VarietyId)
            && (!AreaId.HasValue || r.AreaId == AreaId)
            && (!PolyhouseId.HasValue || r.PolyhouseId == PolyhouseId)
            && (Origin == null || r.OriginType == Origin)
            && (Status == null || r.Status == Status);

        // Route values of the SET filters only (an unset filter adds no key).
        public IEnumerable<KeyValuePair<string, string?>> RouteValues()
        {
            if (Species != null) yield return new(SpeciesKey, Species);
            if (VarietyId.HasValue) yield return new(VarietyKey, VarietyId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (AreaId.HasValue) yield return new(AreaKey, AreaId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (PolyhouseId.HasValue) yield return new(PolyhouseKey, PolyhouseId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Origin != null) yield return new(OriginKey, Origin);
            if (Status != null) yield return new(StatusKey, Status);
        }

        // For the Trace / Overgrowth pages, which only carry the list's filters back to it: the raw query values, shape-checked
        // only (positive ints, text capped at MaxTextLength). The Ready Stock list re-validates them against its own options.
        public static ReadyStockListFilter FromQuery(IQueryCollection query)
        {
            string? Text(string key)
            {
                var v = query[key].FirstOrDefault();
                return string.IsNullOrEmpty(v) || v.Length > MaxTextLength ? null : v;
            }
            int? Id(string key) => int.TryParse(query[key].FirstOrDefault(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
            return new ReadyStockListFilter(Text(SpeciesKey), Id(VarietyKey), Id(AreaKey), Id(PolyhouseKey), Text(OriginKey), Text(StatusKey));
        }
    }
}
