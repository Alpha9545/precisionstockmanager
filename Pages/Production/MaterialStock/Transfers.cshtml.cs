using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Material Transfers (2026-10-23): move Cocopeat / Pitmos between Polyhouses of the Main Office and Mother Plant Areas,
    // and the transfer history. Page: MaterialStock.Transfer or MaterialStock.History (FeatureAuthorizationConventions; every
    // POST also needs MaterialStock.Transfer there and here).
    //   send     MaterialStock.Transfer, FROM an Area the user is assigned to (AreaAccessService.CanAccessRequiredArea),
    //            TO a Polyhouse in an Area where someone can see material (administrators: any eligible Polyhouse).
    //   history  transfers whose source OR destination is in the user's Areas (administrators: all).
    // One atomic move (MaterialTransferRepository): the source loses and the destination gains the quantity together.
    public class TransfersModel : PageModel
    {
        private readonly MaterialTransferRepository _repo;
        private readonly MaterialStockRepository _stockRepo;
        private readonly AreaAccessService _areaAccess;

        public TransfersModel(MaterialTransferRepository repo, MaterialStockRepository stockRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _stockRepo = stockRepo;
            _areaAccess = areaAccess;
        }

        public const int PageSize = 50;

        [BindProperty] public MaterialTransferRequest Input { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? MaterialTypeId { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true, Name = "pg")] public int PageNo { get; set; } = 1;

        public bool SchemaReady { get; private set; }
        public bool CanTransfer { get; private set; }
        public List<MaterialType> Materials { get; private set; } = new();
        public List<MaterialTransferLocation> SourceLocations { get; private set; } = new();
        public List<MaterialTransferLocation> DestinationLocations { get; private set; } = new();
        public List<MaterialStockBalance> SourceBalances { get; private set; } = new();
        public List<(int Id, string Name)> AreaOptions { get; private set; } = new();
        public List<MaterialTransfer> Transfers { get; private set; } = new();
        public int TotalRows { get; private set; }
        public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));
        public DateTime Today => AppClock.Today;

        public async Task OnGetAsync()
        {
            await LoadAsync();
            Input = new MaterialTransferRequest { TransferDate = Today, SubmissionToken = Guid.NewGuid() };
            if (Materials.Count == 1) Input.MaterialTypeId = Materials[0].Id;
            var sourceAreas = SourceLocations.Select(l => l.AreaId).Distinct().ToList();
            if (sourceAreas.Count == 1) Input.SourceAreaId = sourceAreas[0];
        }

        public async Task<IActionResult> OnPostSendAsync()
        {
            if (!User.CanTransferMaterials()) return Forbid();
            ModelState.Clear();
            await LoadAsync();
            if (!SchemaReady)
                return Page();
            if (Input.MaterialTypeId <= 0)
                ModelState.AddModelError("Input.MaterialTypeId", "Choose Cocopeat or Pitmos.");
            if (Input.SourceAreaId <= 0 || !SourceLocations.Any(l => l.AreaId == Input.SourceAreaId))
                ModelState.AddModelError("Input.SourceAreaId", "Choose a source Area you may transfer material from.");
            if (Input.SourcePolyhouseId <= 0)
                ModelState.AddModelError("Input.SourcePolyhouseId", "Source Polyhouse is required.");
            if (Input.DestinationAreaId <= 0)
                ModelState.AddModelError("Input.DestinationAreaId", "Destination Area is required.");
            if (Input.DestinationPolyhouseId <= 0)
                ModelState.AddModelError("Input.DestinationPolyhouseId", "Destination Polyhouse is required.");
            if (!ModelState.IsValid)
                return Page();

            var result = await _repo.CreateAsync(Input,
                new MaterialTransferRepository.Actor(User.GetUserId(), User.Identity?.Name, _areaAccess.HasFullAreaAccess(User)),
                id => _areaAccess.CanAccessRequiredArea(User, id));
            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Message ?? "The transfer could not be saved.");
                return Page();
            }
            TempData["Success"] = result.Duplicate
                ? $"Transfer {result.Code} was already saved -- nothing was moved twice."
                : $"Transfer {result.Code}: {MaterialStockRules.Kg(result.Quantity, result.Unit)} of {result.MaterialName} moved. "
                  + $"Source now {MaterialStockRules.Kg(result.SourceBalance, result.Unit)}, destination now {MaterialStockRules.Kg(result.DestinationBalance, result.Unit)}.";
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            SchemaReady = await _repo.SchemaReadyAsync();
            CanTransfer = User.CanTransferMaterials();
            var full = _areaAccess.HasFullAreaAccess(User);
            var scope = full ? null : _areaAccess.GetAccessibleAreaIds(User);
            Materials = await _stockRepo.GetTypesAsync();
            if (!SchemaReady)
                return;

            var locations = await _repo.GetLocationsAsync();
            if (CanTransfer)
            {
                SourceLocations = locations.Where(l => _areaAccess.CanAccessRequiredArea(User, l.AreaId)).ToList();
                var viewable = full ? null : await _repo.GetViewableAreaIdsAsync();
                DestinationLocations = locations.Where(l => viewable == null || viewable.Contains(l.AreaId)).ToList();
                var sourceAreas = SourceLocations.Select(l => l.AreaId).ToHashSet();
                SourceBalances = (await _stockRepo.GetBalancesAsync(new MaterialStockFilter(), scope))
                    .Where(b => sourceAreas.Contains(b.AreaId) && b.CurrentQuantity > 0).ToList();
            }

            AreaOptions = locations.Where(l => full || _areaAccess.CanAccessRequiredArea(User, l.AreaId))
                .Select(l => (l.AreaId, l.AreaName)).Distinct().OrderBy(a => a.AreaName).ToList();
            if (FromDate.HasValue && ToDate.HasValue && FromDate > ToDate) (FromDate, ToDate) = (ToDate, FromDate);
            var filter = new MaterialTransferFilter
            {
                MaterialTypeId = MaterialTypeId, AreaId = AreaId, FromDate = FromDate, ToDate = ToDate, Page = Math.Max(1, PageNo), PageSize = PageSize,
            };
            (Transfers, TotalRows) = await _repo.GetTransfersAsync(filter, scope);
            if (PageNo > PageCount)
            {
                PageNo = PageCount;
                filter.Page = PageNo;
                (Transfers, TotalRows) = await _repo.GetTransfersAsync(filter, scope);
            }
        }

        public Dictionary<string, string?> Route(int page) => new()
        {
            ["MaterialTypeId"] = MaterialTypeId?.ToString(),
            ["AreaId"] = AreaId?.ToString(),
            ["FromDate"] = FromDate?.ToString("yyyy-MM-dd"),
            ["ToDate"] = ToDate?.ToString("yyyy-MM-dd"),
            ["pg"] = page.ToString(),
        };
    }
}
