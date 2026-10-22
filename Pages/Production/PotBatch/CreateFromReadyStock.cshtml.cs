using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.PotBatch
{
    // 2026-10-15: Pot Production from received Ready Stock -- Ready Stock sent to a Mother Plant Area for the
    // purpose "Pot Production" is potted there: the seedlings of the received batch start a pot batch with the
    // Area's own empty pots, then follow the existing pot flow (daily production, READY -> Potted Plant Stock).
    // The Area is the batch's own (never posted); the repository re-reads and re-checks everything under lock.
    // The Cutting -> Pots page (Create) is unchanged.
    // 2026-10-16: reached from Start Pot Batch > Source "Main Office Seedlings"; only seedlings received from MAIN OFFICE
    // Ready Stock (PotBatchRepository.GetMainOfficeSeedlingSourcesAsync), and one batch per form (SubmissionToken).
    public class CreateFromReadyStockModel : PageModel
    {
        private readonly PotBatchRepository _batchRepo;
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly PotSizeRepository _potSizeRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly DatabaseHelper _db;
        private readonly AreaAccessService _areaAccess;   // Mother Plant Area work: always Area-scoped

        public CreateFromReadyStockModel(PotBatchRepository batchRepo, ReadyStockRepository readyStockRepo, EmptyPotInventoryRepository emptyPotRepo,
            PotSizeRepository potSizeRepo, UserRoleRepository userRoleRepo, DatabaseHelper db, AreaAccessService areaAccess)
        {
            _batchRepo = batchRepo;
            _readyStockRepo = readyStockRepo;
            _emptyPotRepo = emptyPotRepo;
            _potSizeRepo = potSizeRepo;
            _userRoleRepo = userRoleRepo;
            _db = db;
            _areaAccess = areaAccess;
        }

        public ReadyStockModel Source { get; private set; } = new();
        public List<PotSize> PotSizes { get; private set; } = new();
        // empty pots of the batch's Area per pot size (shown; the daily production uses them)
        public Dictionary<string, decimal> EmptyPots { get; private set; } = new();
        public List<(int Id, string Name)> ReadyConfirmers { get; private set; } = new();

        [BindProperty] public string? PotSize { get; set; }
        [BindProperty] public decimal? SeedlingsAllocated { get; set; }
        [BindProperty] public DateTime ProductionStartDate { get; set; } = AppClock.Today;
        [BindProperty] public DateTime ExpectedReadyDate { get; set; } = AppClock.Today.AddDays(30);
        [BindProperty] public int ReadyConfirmerId { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int readyStockId)
        {
            var refusal = await LoadAsync(readyStockId);
            if (refusal != null) return NotHere(refusal);
            SeedlingsAllocated = Source.AvailableQuantity;
            SubmissionToken = Guid.NewGuid();
            if (ReadyConfirmers.Count == 1) ReadyConfirmerId = ReadyConfirmers[0].Id;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int readyStockId)
        {
            var refusal = await LoadAsync(readyStockId);
            if (refusal != null) return NotHere(refusal);
            var me = User.GetUserId();
            if (!me.HasValue)
                ModelState.AddModelError(string.Empty, "Your user could not be identified.");
            if (string.IsNullOrWhiteSpace(PotSize) || !PotSizes.Any(p => p.Name == PotSize))
                ModelState.AddModelError(nameof(PotSize), "Choose a pot size.");
            if (SeedlingsAllocated is null)
                ModelState.AddModelError(nameof(SeedlingsAllocated), "Enter the seedlings to pot.");
            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "This form has expired. Open it again.");
            if (!ModelState.IsValid) return Page();

            var entry = new PotProductionBatch
            {
                SourceReadyStockId = readyStockId,
                CuttingAllocated = SeedlingsAllocated!.Value,
                PotSize = PotSize!,
                ProductionStartDate = ProductionStartDate,
                ExpectedReadyDate = ExpectedReadyDate,
                SupervisorId = ReadyConfirmerId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System",
                SubmissionToken = SubmissionToken
            };
            var (ok, msg, id) = await _batchRepo.CreateFromReadyStockAsync(entry, me!.Value, areaId => _areaAccess.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, msg ?? "The pot batch could not be started.");
                return Page();
            }
            TempData["Success"] = msg;
            return RedirectToPage("/Production/PotBatch/Details", new { id });
        }

        // null = fine; otherwise why this batch cannot be potted here.
        private async Task<string?> LoadAsync(int readyStockId)
        {
            using (var conn = _db.GetConnection())
            {
                await conn.OpenAsync();
                if (!await PotBatchSchema.ReadyStockSourceAsync(conn))
                    return PotBatchSchema.MigrationMessage;
                var source = await _readyStockRepo.GetByIdAsync(readyStockId);
                if (source == null || !source.InMotherPlantArea || !_areaAccess.CanAccessArea(User, source.AreaId))
                    return "That Ready Stock batch is not in one of your Mother Plant Areas.";
                if (await MotherPlantTransferRepository.ReceivedBatchPurposeAsync(conn, null, readyStockId) != MotherPlantTransferRules.PurposePotProduction)
                    return PotBatchRepository.NotForPotProductionMessage;
                Source = source;
            }
            if (!(await _batchRepo.GetMainOfficeSeedlingSourcesAsync(new[] { Source.AreaId })).Any(x => x.ReadyStockId == readyStockId)
                && Source.AvailableQuantity > 0)
                return PotBatchRepository.NotMainOfficeSeedlingsMessage;
            PotSizes = await _potSizeRepo.GetAllAsync(activeOnly: true);
            EmptyPots = (await _emptyPotRepo.GetAllAsync(activeOnly: true))
                .Where(p => p.AreaId == Source.AreaId)
                .GroupBy(p => p.PotSize)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.PhysicalQuantity));
            ReadyConfirmers = (await _userRoleRepo.GetReadyConfirmersAsync(Source.AreaId)).Select(u => (u.EmployeeID, u.Name ?? "")).ToList();
            return null;
        }

        private IActionResult NotHere(string message)
        {
            TempData["Error"] = message;
            return RedirectToPage("/Production/PotBatch/Create", new { source = CreateModel.SourceMainOfficeSeedlings });
        }
    }
}
