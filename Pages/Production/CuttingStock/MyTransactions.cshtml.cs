using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // CUTTING DELIVERY HISTORY (Correction #7; the page was "My Cutting Transactions" and only worked after picking one Area).
    // Every cutting delivery -- and every Cutting Entry that was not delivered -- with where it came from (Area, Polyhouse,
    // Mother Plant, Cutting Supervisor), where it went (Main Office Area / kept for Pot Production), who entered it, who
    // received or rejected it, quantities (sent, received, transit loss) and status. Read-only: nothing is written here.
    //
    // Filters are GET query-string values, all applied by the database together (AND) in
    // CuttingDeliveryHistoryRepository.SearchAsync. "Clear filters" is a plain link back to the page (no query string).
    //
    // AREA SECURITY: the user's Areas (AreaAccessService: UserRoles.AreaId claims; null = full access) are part of the query
    // and of the dropdown-option queries -- a row is visible when the user may access its source Area, its destination Area, or
    // the Main Office Area it is waiting at -- so choosing another Area in a filter can never reveal anything. The C# check below
    // (CanAccessAnyArea) is a second guard. Page permission (FeatureAuthorizationConventions) is unchanged.
    public class MyTransactionsModel : PageModel
    {
        private readonly CuttingDeliveryHistoryRepository _historyRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public MyTransactionsModel(CuttingDeliveryHistoryRepository historyRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
        {
            _historyRepo = historyRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        // the Areas the user may access (kept from the previous page)
        public List<Area> Areas { get; set; } = new();
        public List<CuttingDeliveryHistoryRow> Rows { get; set; } = new();
        public CuttingDeliveryHistoryRepository.FilterOptions Options { get; set; } = new();
        public List<string> Notices { get; set; } = new();
        public bool FilterActive { get; set; }

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public int? SourceAreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SourcePolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public int? MotherPlantId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SupervisorId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Destination { get; set; }
        [BindProperty(SupportsGet = true)] public int? DestinationAreaId { get; set; }
        [BindProperty(SupportsGet = true)] public string? DeliveryStatus { get; set; }
        [BindProperty(SupportsGet = true)] public string? EnteredBy { get; set; }
        [BindProperty(SupportsGet = true)] public int? ReceivedById { get; set; }

        public decimal TotalQuantity => Rows.Sum(r => r.Quantity);
        public decimal TotalReceived => Rows.Where(r => r.TransferStatus == "Completed").Sum(r => r.ConfirmedQuantity ?? 0);
        public decimal TotalTransitLoss => Rows.Sum(r => r.TransitLoss ?? 0);

        // areaId: kept so old links still open the page; the Area picker it belonged to is now the Source / Destination Area filters.
        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            var filter = new CuttingDeliveryHistoryFilter
            {
                From = From, To = To, SourceAreaId = SourceAreaId, SourcePolyhouseId = SourcePolyhouseId, MotherPlantId = MotherPlantId,
                SupervisorId = SupervisorId, Destination = Destination, DestinationAreaId = DestinationAreaId, DeliveryStatus = DeliveryStatus,
                EnteredBy = EnteredBy, ReceivedById = ReceivedById
            };
            Notices = filter.Normalize();

            // what will be searched is what stays on screen
            From = filter.From; To = filter.To; SourceAreaId = filter.SourceAreaId; SourcePolyhouseId = filter.SourcePolyhouseId;
            MotherPlantId = filter.MotherPlantId; SupervisorId = filter.SupervisorId; Destination = filter.Destination;
            DestinationAreaId = filter.DestinationAreaId; DeliveryStatus = filter.DeliveryStatus; EnteredBy = filter.EnteredBy; ReceivedById = filter.ReceivedById;
            FilterActive = filter.HasAnyFilter;

            Areas = _areaAccessService.FilterByArea(User, await _areaRepo.GetAllAreas(), a => (int?)a.Id);

            IReadOnlyCollection<int>? allowedAreas = _areaAccessService.HasFullAreaAccess(User) ? null : _areaAccessService.GetAccessibleAreaIds(User);
            Options = await _historyRepo.GetFilterOptionsAsync(allowedAreas);
            Rows = (await _historyRepo.SearchAsync(filter, allowedAreas))
                .Where(r => _areaAccessService.CanAccessAnyArea(User, r.SourceAreaId, r.DestinationAreaId, r.PendingAreaId))
                .ToList();
            return Page();
        }
    }
}
