using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // Cutting Delivery to Main Office. Creates a Cutting-type
    // InternalTransfer in 'PendingConfirmation'; only InTransitQuantity is
    // reserved here. Stock moves when Main Office confirms what it received
    // (ConfirmReceipt).
    public class GiveToMainOfficeModel : PageModel
    {
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public GiveToMainOfficeModel(
            CuttingStockRepository cuttingStockRepo,
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo,
            AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _cuttingStockRepo = cuttingStockRepo;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
        }

        private static readonly string[] _sourceAreaTypes = { "MotherPlant", "Kunjir", "Kiran" };

        public List<Area> Areas { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<CuttingStockModel> StockPools { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            // F1: block browsing another Area's stock pools by URL.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view cutting stock for the selected Area.";
                return RedirectToPage("/Production/CuttingStock/GiveToMainOffice");
            }

            SelectedAreaId = areaId;
            await LoadDropdownsAsync(areaId);
            return Page();
        }

        public async Task<IActionResult> OnPostSendAsync(int cuttingStockId, decimal quantity, int pendingConfirmationAreaId, string? remarks, int? areaId)
        {
            var (quantityOk, quantityError) = PlantStockManager.Services.CuttingRules.ValidateProductionQuantity(quantity);
            if (!quantityOk)
            {
                TempData["Error"] = quantityError!.Replace("Cutting quantity", "Delivery quantity");
                return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
            }
            if (pendingConfirmationAreaId <= 0)
            {
                TempData["Error"] = "Select which Main Office Area this is being sent to.";
                return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
            }

            // F1: the posted cuttingStockId and pendingConfirmationAreaId
            // were trusted as-is -- any user could send ANY Area's cutting
            // stock. The pool is now re-loaded and its ACTUAL Area checked,
            // and the destination must be a real Main Office Area.
            var stockPool = await _cuttingStockRepo.GetByIdAsync(cuttingStockId);
            if (stockPool == null || !_areaAccessService.CanAccessArea(User, stockPool.AreaId))
            {
                TempData["Error"] = "You are not authorized to send cuttings from that stock pool.";
                return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
            }
            var mainOfficeAreaIds = (await _areaRepo.GetByAreaTypesAsync("MainOffice")).Select(a => a.Id).ToHashSet();
            if (!mainOfficeAreaIds.Contains(pendingConfirmationAreaId))
            {
                TempData["Error"] = "The selected destination is not a Main Office Area.";
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
                ? $"Sent {quantity:N0} cuttings to Main Office. Waiting for Main Office to confirm what it received."
                : (message ?? "Failed to send cutting to Main Office.");

            return RedirectToPage("/Production/CuttingStock/GiveToMainOffice", new { areaId });
        }

        private async Task LoadDropdownsAsync(int? areaId)
        {
            Areas = _areaAccessService.FilterByArea(User, await _areaRepo.GetByAreaTypesAsync(_sourceAreaTypes), a => (int?)a.Id);
            MainOfficeAreas = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            StockPools = areaId.HasValue
                ? (await _cuttingStockRepo.GetAllAsync(areaId.Value)).Where(s => s.AvailableQuantity > 0).ToList()
                : new List<CuttingStockModel>();
        }
    }
}
