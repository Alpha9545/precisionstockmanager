using System.Globalization;

namespace PlantStockManager.Pages.FieldWork
{
    // Small form helpers shared by the Phase 2 pages: invariant-culture numbers (the browser always sends "12.3456") and
    // uploaded live photos read into memory with their count and size checked BEFORE they are read.
    public static class FieldWorkForm
    {
        public static decimal? ParseDecimal(string? text)
            => decimal.TryParse((text ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

        public static async Task<List<byte[]>> ReadPhotosAsync(IEnumerable<IFormFile>? files, int maxCount, long maxBytes, List<string> errors)
        {
            var list = new List<byte[]>();
            var uploads = (files ?? Enumerable.Empty<IFormFile>()).Where(f => f.Length > 0).ToList();
            if (uploads.Count > maxCount)
            {
                errors.Add(maxCount == 0 ? "Photos are not enabled here." : $"At most {maxCount} photo(s).");
                return list;
            }
            foreach (var f in uploads)
            {
                if (f.Length > maxBytes)
                {
                    errors.Add($"A photo is too large (at most {maxBytes / 1024 / 1024.0:0.#} MB).");
                    continue;
                }
                using var ms = new MemoryStream((int)f.Length);
                await f.CopyToAsync(ms);
                list.Add(ms.ToArray());
            }
            return list;
        }
    }
}
