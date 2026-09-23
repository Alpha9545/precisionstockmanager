using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // "Give Cutting to Main Office" -- step 2 of the approved workflow.
    // Creates a Cutting-type InternalTransfer in 'PendingConfirmation'.
    // Nothing is decremented here except InTransitQuantity (reserved by
    // InternalTransferRepository.InsertAsync itself) -- PhysicalQuantity
    // and the ledger are untouched until the transfer is actually
    // transplanted.
    public class GiveToMainOfficeModel : PageModel
    {
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;

        public GiveToMainOfficeModel(
            CuttingStockRepository cuttingStockRepo,
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo)
        {
            _cuttingStockRepo = cuttingStockRepo;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
        }

        private static readonly string[] _sourceAreaTypes = { "MotherPlant", "Kunjir", "Kiran" };

        public List<Area> Areas { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<CuttingStockModel> StockPools { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task OnGetAsync(int? areaId)
        {
            SelectedAreaId = areaId;
            await LoadDropdownsAsync(areaId);
        }

        public async Task<IActionResult> OnPostSendAsync(int cuttingStockId, decimal quantity, int pendingConfirmationAreaId, string? remarks, int? areaId)
        {
            if (quantity <= 0)
            {
                TempData["Error"] = "Quantity must be greater than zero.";
                return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
            }
            if (pendingConfirmationAreaId <= 0)
            {
                TempData["Error"] = "Select which Main Office Area this is being sent to.";
                return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var entry = new InternalTransferModel
            {
                StockType = "Cutting",
                SourceCuttingStockId = cuttingStockId,
                PendingConfirmationAreaId = pendingConfirmationAreaId,
                Quantity = quantity,
                Remarks = remarks,
                CreatedBy = createdBy
            };

            var (success, message, _) = await _internalTransferRepo.InsertAsync(entry, userId);
            TempData[success ? "Success" : "Error"] = success
                ? $"Sent {quantity:N2} to Main Office. Awaiting confirmation."
                : (message ?? "Failed to send cutting to Main Office.");

            return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
        }

        private async Task LoadDropdownsAsync(int? areaId)
        {
            Areas = await _areaRepo.GetByAreaTypesAsync(_sourceAreaTypes);
            MainOfficeAreas = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            StockPools = areaId.HasValue
                ? (await _cuttingStockRepo.GetAllAsync(areaId.Value)).Where(s => s.AvailableQuantity > 0).ToList()
                : new List<CuttingStockModel>();
        }
    }
}
