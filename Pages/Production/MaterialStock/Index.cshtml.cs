using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using TrayStockModel = PlantStockManager.Models.TrayStock;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Material Stock -- ONE page over THREE separate stocks, never added together:
    //   Cocopeat, Pitmos   kg, per Main Office Area + Polyhouse  (MaterialStockRepository)
    //   Trays              tray count, per Area + Polyhouse + Cavity (TrayStockRepository;
    //                      the former Tray Stock page)
    // Each section is shown only to users holding that domain's existing permission
    // (MaterialStockAccess). Area scope is AreaAccessService's regular rule for both
    // (SQL scope for material, FilterByArea for trays, exactly as the two old pages did).
    public class IndexModel : PageModel
    {
        private readonly MaterialStockRepository _repo;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaAccessService _areaAccess;

        private readonly TrayAreaScope _trayScope;
        private readonly TrayTransferRepository _trayTransferRepo;

        public IndexModel(MaterialStockRepository repo, TrayStockRepository trayStockRepo, AreaAccessService areaAccess, AreaRepository areaRepo,
            TrayTransferRepository trayTransferRepo)
        {
            _trayTransferRepo = trayTransferRepo;
            _trayScope = new TrayAreaScope(areaRepo, areaAccess);
            _repo = repo;
            _trayStockRepo = trayStockRepo;
            _areaAccess = areaAccess;
        }

        // "" = all, a material Code (COCOPEAT / PITMOS), or TRAYS.
        [BindProperty(SupportsGet = true)] public string? Material { get; set; }
        [BindProperty(SupportsGet = true)] public int? MaterialTypeId { get; set; }   // older links
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Cavity { get; set; }
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }

        public bool ShowMaterials { get; private set; }
        public bool ShowTrays { get; private set; }
        public bool CanSeeMaterials { get; private set; }
        public bool CanSeeTrays { get; private set; }

        public List<MaterialType> Materials { get; set; } = new();
        public List<MaterialKpi> Kpis { get; set; } = new();
        public List<MaterialStockBalance> MaterialRows { get; set; } = new();
        public List<TrayStockModel> TrayRows { get; set; } = new();
        // Tray totals per cavity (a 150-cavity tray and a 9-cavity tray are different
        // stock, so there is deliberately no grand total across cavities).
        public List<(string Cavity, decimal Trays)> TraySummary { get; set; } = new();
        // 2026-10-10: trays sent between Polyhouses and not yet received -- in neither pool above.
        public (decimal Trays, int Transfers) TraysInTransit { get; set; }
        public List<(int Id, string Name)> AreaOptions { get; set; } = new();
        public List<(int Id, string Name, int AreaId)> PolyhouseOptions { get; set; } = new();
        public IReadOnlyList<string> Cavities => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync()
        {
            CanSeeMaterials = User.CanViewMaterials();
            CanSeeTrays = User.CanViewTrays();
            Materials = CanSeeMaterials ? await _repo.GetTypesAsync(activeOnly: false) : new();
            if (MaterialTypeId.HasValue && string.IsNullOrEmpty(Material))
                Material = Materials.FirstOrDefault(m => m.Id == MaterialTypeId)?.Code;
            Material = MaterialStockRules.NormalizeKind(Material, Materials, CanSeeMaterials, CanSeeTrays);
            Cavity = DirectSowingRules.IsValidCavityType(Cavity) ? Cavity : null;
            var search = MaterialStockRules.Trimmed(Search);

            var selected = Materials.FirstOrDefault(m => m.Code == Material);
            ShowMaterials = CanSeeMaterials && Material != MaterialStockRules.TraysKind;
            ShowTrays = CanSeeTrays && (Material == "" || Material == MaterialStockRules.TraysKind);

            // locations for the filters: union of both domains, each within the user's Areas
            var locations = new List<(int AreaId, string AreaName, int PolyhouseId, string PolyhouseName)>();
            if (CanSeeMaterials)
            {
                var scope = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
                locations.AddRange(await _repo.GetLocationsAsync(a => _areaAccess.CanAccessRequiredArea(User, a)));
                Kpis = await _repo.GetKpisAsync(scope, AppClock.Today);
                if (ShowMaterials)
                    MaterialRows = await _repo.GetBalancesAsync(new MaterialStockFilter
                    {
                        MaterialTypeId = selected?.Id, AreaId = AreaId, PolyhouseId = PolyhouseId, Search = search,
                    }, scope);
            }
            if (CanSeeTrays)
            {
                var canSeeTrayArea = await _trayScope.CanViewPredicateAsync(User);   // own Areas (+ Mother Plant Areas for TrayStock.EnterMotherPlant)
                var trays = (await _trayStockRepo.GetAllAsync(activeOnly: true)).Where(s => canSeeTrayArea(s.AreaId)).ToList();
                locations.AddRange(trays.Where(t => t.PolyhouseId.HasValue)
                    .Select(t => (t.AreaId, t.AreaName ?? "", t.PolyhouseId!.Value, t.PolyhouseName ?? "")));
                if (ShowTrays)
                {
                    TrayRows = trays
                        .Where(s => !AreaId.HasValue || s.AreaId == AreaId)
                        .Where(s => !PolyhouseId.HasValue || s.PolyhouseId == PolyhouseId)
                        .Where(s => Cavity == null || s.TraySize == Cavity)
                        .Where(s => search == null || Contains(s.AreaName, search) || Contains(s.PolyhouseName, search) || Contains(s.TraySize, search))
                        .OrderBy(s => s.AreaName).ThenBy(s => s.PolyhouseName).ThenBy(s => DirectSowingRules.CavityCount(s.TraySize) ?? 0)
                        .ToList();
                    TraysInTransit = await _trayTransferRepo.GetInTransitAsync(canSeeTrayArea);
                    TraySummary = Cavities
                        .Select(c => (c, TrayRows.Where(r => r.TraySize == c).Sum(r => r.PhysicalQuantity)))
                        .ToList();
                }
            }
            AreaOptions = locations.Select(l => (l.AreaId, l.AreaName.Trim())).Distinct().OrderBy(a => a.Item2).ToList();
            PolyhouseOptions = locations.Where(l => !AreaId.HasValue || l.AreaId == AreaId)
                .Select(l => (l.PolyhouseId, l.PolyhouseName.Trim(), l.AreaId)).Distinct().OrderBy(p => p.Item2).ToList();
        }

        private static bool Contains(string? value, string search) => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

        public IEnumerable<MaterialType> VisibleMaterials
            => Materials.Where(m => m.IsActive || MaterialRows.Any(r => r.MaterialTypeId == m.Id))
                        .Where(m => Material == "" || m.Code == Material);

        public Dictionary<string, string?> Route(string? material) => new()
        {
            ["Material"] = material,
            ["AreaId"] = AreaId?.ToString(),
            ["PolyhouseId"] = PolyhouseId?.ToString(),
            ["Cavity"] = material == MaterialStockRules.TraysKind ? Cavity : null,
            ["Search"] = Search,
        };
    }
}
