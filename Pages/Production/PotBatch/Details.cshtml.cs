using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.PotBatch
{
    // One pot batch: daily production entries (running total shown), the
    // READY confirmation (any authorized user assigned to the batch Area --
    // PotBatchRules.CanConfirmReady), cancel before any production, and the
    // expected ready date.
    public class DetailsModel : PageModel
    {
        private readonly PotBatchRepository _batchRepo;
        private readonly AreaAccessService _areaAccess;

        public DetailsModel(PotBatchRepository batchRepo, AreaAccessService areaAccess)
        {
            _batchRepo = batchRepo;
            _areaAccess = areaAccess;
        }

        public PotProductionBatch Batch { get; set; } = new();
        public List<PotProductionEntry> Entries { get; set; } = new();
        // Pot Production permission + assignment to the batch Area.
        public bool CanConfirmReady { get; set; }
        // The "Ready Confirmation By" user named on the batch (may confirm, like
        // any other authorized Area user; also keeps the cancel / expected-date rights).
        public bool IsDesignatedConfirmer { get; set; }
        public bool IsCreator { get; set; }
        public IReadOnlyList<string> WastageReasons => DirectSowingRules.WastageReasons;

        [BindProperty] public DateTime EntryDate { get; set; } = DateTime.Today;
        [BindProperty] public decimal EntryQuantity { get; set; }
        [BindProperty] public string? EntryRemarks { get; set; }

        [BindProperty] public decimal ReadyQuantity { get; set; }
        [BindProperty] public string? WastageReason { get; set; }
        [BindProperty] public string? UnusedCuttingAction { get; set; }
        [BindProperty] public string? ReadyRemarks { get; set; }

        [BindProperty] public DateTime NewExpectedReadyDate { get; set; }

        public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : Denied();

        public async Task<IActionResult> OnPostEntryAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            var (ok, message) = await _batchRepo.AddEntryAsync(id, EntryDate, EntryQuantity, EntryRemarks, User.GetUserId(), User.Identity?.Name);
            TempData[ok ? "Success" : "Error"] = ok ? $"{EntryQuantity:N0} pots recorded for {EntryDate:dd/MM/yyyy}." : message;
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostReadyAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            var me = User.GetUserId();
            if (!me.HasValue || !CanConfirmReady)
            {
                TempData["Error"] = "You cannot confirm this batch READY: it needs Pot Production permission and assignment to this batch's Area.";
                return RedirectToPage(new { id });
            }
            var (ok, message) = await _batchRepo.ConfirmReadyAsync(id, ReadyQuantity, WastageReason, UnusedCuttingAction, ReadyRemarks, me.Value, User.Identity?.Name);
            TempData[ok ? "Success" : "Error"] = !ok ? message
                : ReadyQuantity == 0
                    ? $"Batch {Batch.BatchCode} closed as a complete loss ({WastageReason}). Nothing was added to stock."
                    : $"Batch {Batch.BatchCode} READY: {ReadyQuantity:N0} pots added to Potted Plant Stock ({Batch.AreaName}, {Batch.PotSize}).";
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            var me = User.GetUserId();
            var (ok, message) = me.HasValue ? await _batchRepo.CancelAsync(id, me.Value, User.Identity?.Name) : (false, "Your user could not be identified.");
            TempData[ok ? "Success" : "Error"] = ok ? $"Batch {Batch.BatchCode} cancelled; {Batch.CuttingAllocated:N0} cuttings returned to Cutting Stock." : message;
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostExpectedDateAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            var me = User.GetUserId();
            var (ok, message) = me.HasValue ? await _batchRepo.UpdateExpectedReadyDateAsync(id, NewExpectedReadyDate, me.Value, User.Identity?.Name) : (false, "Your user could not be identified.");
            TempData[ok ? "Success" : "Error"] = ok ? $"Expected ready date changed to {NewExpectedReadyDate:dd/MM/yyyy}." : message;
            return RedirectToPage(new { id });
        }

        private async Task<bool> LoadAsync(int id)
        {
            var batch = await _batchRepo.GetByIdAsync(id);
            if (batch == null || !_areaAccess.CanAccessArea(User, batch.AreaId))
                return false;
            Batch = batch;
            Entries = await _batchRepo.GetEntriesAsync(id);
            var me = User.GetUserId();
            CanConfirmReady = PotBatchRules.CanConfirmReady(User, batch.AreaId);
            IsDesignatedConfirmer = me.HasValue && me.Value == batch.SupervisorId;
            IsCreator = me.HasValue && me.Value == batch.CreatedById;
            NewExpectedReadyDate = batch.ExpectedReadyDate;
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Pot batch not found, or it belongs to an Area you cannot access.";
            return RedirectToPage("/Production/PotBatch/Index");
        }
    }
}
