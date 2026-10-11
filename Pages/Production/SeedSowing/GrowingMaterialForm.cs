using Microsoft.AspNetCore.Http;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // The "Growing Material" section shared by both sowing entry pages (Direct Seed
    // Sowing and Confirm Cutting Sowing): the radio choices, the live stock preview
    // and the "exactly one material" check of the posted form. Everything the browser
    // shows is a preview -- SeedSowingRepository re-checks and deducts under lock.
    public sealed class GrowingMaterialViewModel
    {
        public List<MaterialType> Materials { get; set; } = new();
        public int? SelectedId { get; set; }
        public string FieldName { get; set; } = GrowingMaterialForm.FieldName;
    }

    public static class GrowingMaterialForm
    {
        public const string FieldName = "SeedSowing.GrowingMaterialTypeId";

        // A radio group posts one value; two values for the field can only come from a
        // tampered form -- refused rather than silently taking the first.
        public static string? ValidatePosted(HttpRequest request, string fieldName = FieldName)
            => request.HasFormContentType && request.Form.TryGetValue(fieldName, out var values) && values.Count(v => !string.IsNullOrWhiteSpace(v)) > 1
                ? MaterialStockRules.OneMaterialOnlyMessage
                : null;

        // The AJAX preview: whether the Polyhouse uses growing material at all, the
        // physical trays (the same CEILING(quantity / cavity) the save uses), and
        // per material: available, kg per tray, required, remaining, enough.
        public static async Task<object> PreviewAsync(MaterialStockRepository repo, int areaId, int polyhouseId, string? cavityType, decimal quantity)
        {
            var trays = DirectSowingRules.PhysicalTraysRequired(quantity, cavityType);
            var (applicable, materials) = areaId > 0 && polyhouseId > 0
                ? await repo.GetSowingPreviewAsync(areaId, polyhouseId, cavityType, trays)
                : (false, new List<MaterialSowingPreview>());
            return new
            {
                applicable,
                trays,
                materials = materials.Select(m => new
                {
                    id = m.MaterialTypeId,
                    name = m.MaterialName,
                    available = m.Available,
                    perTray = m.QuantityPerTray,
                    required = m.Required,
                    remaining = m.RemainingAfter,
                    enough = m.Enough,
                    unit = m.Unit.ToLowerInvariant(),
                }),
            };
        }
    }
}
