using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Reports -> Material History: the Cocopeat / Pitmos ledger (MaterialStock.History)
    // and the Tray Stock ledger (the old Tray Stock history rule, MaterialStockAccess.TrayView)
    // in one list, newest first. Each row keeps its own unit -- kg, or trays of one cavity --
    // and its own details; the two ledgers are only READ here, never merged or rewritten.
    // A user sees only the ledger(s) they hold the permission for, and only their own Areas
    // (SQL scope for both, as on the two former history pages). Dates are India dates.
    public class HistoryModel : PageModel
    {
        private readonly MaterialStockRepository _repo;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaAccessService _areaAccess;

        private readonly TrayAreaScope _trayScope;

        public HistoryModel(MaterialStockRepository repo, TrayStockRepository trayStockRepo, AreaAccessService areaAccess, AreaRepository areaRepo)
        {
            _trayScope = new TrayAreaScope(areaRepo, areaAccess);
            _repo = repo;
            _trayStockRepo = trayStockRepo;
            _areaAccess = areaAccess;
        }

        public const int PageSize = 50;

        [BindProperty(SupportsGet = true)] public string? Material { get; set; }          // "", COCOPEAT, PITMOS, TRAYS
        [BindProperty(SupportsGet = true)] public int? MaterialTypeId { get; set; }       // older links
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Cavity { get; set; }
        [BindProperty(SupportsGet = true)] public string? Type { get; set; }              // RECEIVE / CONSUME / REVERSAL / ADJUSTMENT
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }
        [BindProperty(SupportsGet = true, Name = "pg")] public int PageNo { get; set; } = 1;

        public bool CanSeeMaterials { get; private set; }
        public bool CanSeeTrays { get; private set; }
        public bool ShowMaterials { get; private set; }
        public bool ShowTrays { get; private set; }

        public List<MaterialType> Materials { get; set; } = new();
        public List<(int Id, string Name)> AreaOptions { get; set; } = new();
        public List<(int Id, string Name)> PolyhouseOptions { get; set; } = new();
        public IReadOnlyList<string> Cavities => DirectSowingRules.CavityTypes;
        public List<MaterialHistoryRow> Rows { get; set; } = new();
        public int TotalRows { get; set; }
        public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));
        public List<MaterialConsumptionSummaryRow> Consumption { get; set; } = new();
        public List<(string Area, string Polyhouse, string Cavity, decimal Trays)> TrayUse { get; set; } = new();
        // 2026-10-10: pools whose business-date history goes below zero (older back-dated rows) -- flagged, never changed
        public List<TrayHistoryRules.NegativeStretch> TrayHistoryWarnings { get; set; } = new();
        public DateTime SummaryFrom { get; set; }
        public DateTime SummaryTo { get; set; }

        public async Task OnGetAsync()
        {
            CanSeeMaterials = User.CanSeeMaterialHistory();
            CanSeeTrays = User.CanViewTrays();
            var scope = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            Materials = CanSeeMaterials ? await _repo.GetTypesAsync(activeOnly: false) : new();
            if (MaterialTypeId.HasValue && string.IsNullOrEmpty(Material))
                Material = Materials.FirstOrDefault(m => m.Id == MaterialTypeId)?.Code;
            Material = MaterialStockRules.NormalizeKind(Material, Materials, CanSeeMaterials, CanSeeTrays);
            if (!MaterialStockRules.TransactionTypes.Contains(Type ?? "")) Type = null;
            Cavity = DirectSowingRules.IsValidCavityType(Cavity) ? Cavity : null;
            if (FromDate.HasValue && ToDate.HasValue && FromDate > ToDate) (FromDate, ToDate) = (ToDate, FromDate);
            var search = MaterialStockRules.Trimmed(Search);
            ShowMaterials = CanSeeMaterials && Material != MaterialStockRules.TraysKind && Cavity == null;
            ShowTrays = CanSeeTrays && (Material == "" || Material == MaterialStockRules.TraysKind);
            var selected = Materials.FirstOrDefault(m => m.Code == Material);

            var today = AppClock.Today;
            SummaryFrom = (FromDate ?? new DateTime(today.Year, today.Month, 1)).Date;
            SummaryTo = (ToDate ?? today).Date;

            var rows = new List<MaterialHistoryRow>();
            var locations = new List<(int AreaId, string AreaName, int PolyhouseId, string PolyhouseName)>();

            if (CanSeeMaterials)
            {
                locations.AddRange(await _repo.GetLocationsAsync(a => _areaAccess.CanAccessRequiredArea(User, a), withHeldStock: true));
                if (ShowMaterials)
                {
                    var (materialRows, _) = await _repo.GetTransactionsAsync(new MaterialTransactionFilter
                    {
                        MaterialTypeId = selected?.Id, AreaId = AreaId, PolyhouseId = PolyhouseId, TransactionType = Type,
                        FromDate = FromDate, ToDate = ToDate, Page = 1, PageSize = MaterialTransactionFilter.MaxPageSize,
                    }, scope);
                    rows.AddRange(materialRows.Select(FromMaterial));
                    Consumption = await _repo.GetConsumptionSummaryAsync(SummaryFrom, SummaryTo, scope);
                    if (selected != null) Consumption = Consumption.Where(c => c.MaterialName == selected.Name).ToList();
                    // The usage summary follows the Area / Polyhouse filter too, like the tray summary below
                    // (its rows carry names, so match the chosen location's names).
                    var areaName = AreaId.HasValue ? locations.FirstOrDefault(l => l.AreaId == AreaId).AreaName : null;
                    var polyhouse = PolyhouseId.HasValue ? locations.FirstOrDefault(l => l.PolyhouseId == PolyhouseId) : default;
                    if (AreaId.HasValue)
                        Consumption = Consumption.Where(c => c.AreaName == areaName).ToList();
                    if (PolyhouseId.HasValue)
                        Consumption = Consumption.Where(c => c.AreaName == polyhouse.AreaName && c.PolyhouseName == polyhouse.PolyhouseName).ToList();
                }
            }

            if (CanSeeTrays)
            {
                var trayRows = await _trayStockRepo.GetTransactionsAsync(null, null, null, await _trayScope.ViewScopeAsync(User));
                TrayHistoryWarnings = TrayHistoryRules.NegativeStretches(trayRows.Select(t => new TrayHistoryRules.Movement(
                    t.TrayStockId, $"{t.AreaName} / {t.PolyhouseName ?? "-"} / {t.TraySize}", t.TransactionDate, t.Id, t.Quantity)));
                locations.AddRange(trayRows.Where(t => t.PolyhouseId.HasValue)
                    .Select(t => (t.AreaId, t.AreaName ?? "", t.PolyhouseId!.Value, t.PolyhouseName ?? "")));
                if (ShowTrays)
                {
                    var fromUtc = FromDate.HasValue ? AppClock.IndiaToUtc(FromDate.Value.Date) : (DateTime?)null;
                    var toUtc = ToDate.HasValue ? AppClock.IndiaToUtc(ToDate.Value.Date.AddDays(1)) : (DateTime?)null;
                    var filtered = trayRows
                        .Where(t => !AreaId.HasValue || t.AreaId == AreaId)
                        .Where(t => !PolyhouseId.HasValue || t.PolyhouseId == PolyhouseId)
                        .Where(t => Cavity == null || t.TraySize == Cavity)
                        .Where(t => Type == null || MaterialStockRules.TrayCategory(t.TransactionType) == Type)
                        .Where(t => fromUtc == null || t.TransactionDate >= fromUtc)
                        .Where(t => toUtc == null || t.TransactionDate < toUtc)
                        .ToList();
                    rows.AddRange(filtered.Select(FromTray));

                    var useFrom = AppClock.IndiaToUtc(SummaryFrom);
                    var useTo = AppClock.IndiaToUtc(SummaryTo.AddDays(1));
                    TrayUse = trayRows
                        .Where(t => !AreaId.HasValue || t.AreaId == AreaId)
                        .Where(t => !PolyhouseId.HasValue || t.PolyhouseId == PolyhouseId)
                        .Where(t => Cavity == null || t.TraySize == Cavity)
                        .Where(t => t.TransactionDate >= useFrom && t.TransactionDate < useTo)
                        .Where(t => MaterialStockRules.TrayCategory(t.TransactionType) is MaterialStockRules.Consume or MaterialStockRules.Reversal)
                        .GroupBy(t => (Area: t.AreaName ?? "", Polyhouse: t.PolyhouseName ?? "-", Cavity: t.TraySize ?? ""))
                        .Select(g => (g.Key.Area, g.Key.Polyhouse, g.Key.Cavity, -g.Sum(t => t.Quantity)))
                        .Where(g => g.Item4 != 0)
                        .OrderByDescending(g => g.Item4)
                        .ToList();
                }
            }

            if (search != null)
                rows = rows.Where(r => Has(r.MaterialName, search) || Has(r.AreaName, search) || Has(r.PolyhouseName, search) || Has(r.Cavity, search)
                                       || Has(r.TypeLabel, search) || Has(r.ReferenceText, search) || Has(r.UserName, search) || Has(r.Remarks, search)).ToList();

            AreaOptions = locations.Select(l => (l.AreaId, l.AreaName.Trim())).Distinct().OrderBy(a => a.Item2).ToList();
            PolyhouseOptions = locations.Where(l => !AreaId.HasValue || l.AreaId == AreaId)
                .Select(l => (l.PolyhouseId, l.PolyhouseName.Trim())).Distinct().OrderBy(p => p.Item2).ToList();

            TotalRows = rows.Count;
            PageNo = Math.Clamp(PageNo, 1, PageCount);
            Rows = rows.OrderByDescending(r => r.AtUtc).Skip((PageNo - 1) * PageSize).Take(PageSize).ToList();
        }

        private static bool Has(string? value, string search) => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

        private static MaterialHistoryRow FromMaterial(MaterialTransaction t)
        {
            var unit = t.Unit.ToLowerInvariant();
            var sowingId = t.ReferenceType == MaterialStockRules.SowingReference ? t.ReferenceId : t.SeedSowingId;
            return new MaterialHistoryRow
            {
                IsTray = false, AtUtc = t.TransactionDate, RecordedUtc = t.CreatedAt, MaterialName = t.MaterialName, Unit = unit,
                AreaId = t.AreaId, AreaName = t.AreaName, PolyhouseId = t.PolyhouseId, PolyhouseName = t.PolyhouseName,
                Category = MaterialStockRules.MaterialCategory(t.TransactionType),
                TypeLabel = t.IsApprovalOverage
                    ? (t.TransactionType == MaterialStockRules.Consume ? "Used (extra trays at approval)" : "Returned (approval cancelled)")
                    : MaterialStockRules.TypeLabel(t.TransactionType),
                SignedQuantity = t.SignedQuantity, BalanceAfter = t.BalanceAfter,
                Detail = t.InputQuantity.HasValue ? $"{MaterialStockRules.Kg(t.InputQuantity.Value, t.Unit)} entered x {t.ConversionFactor:0.####}"
                       : t.Trays.HasValue ? $"{t.Trays} trays x {MaterialStockRules.Kg(t.QuantityPerTray ?? 0, t.Unit)}"
                       : null,
                ReferenceText = sowingId.HasValue ? $"Sowing {t.SowingCode ?? sowingId.ToString()}" + (t.IsApprovalOverage ? " (approval)" : "")
                    : t.ReferenceType == MaterialTransferRules.ReferenceType ? $"Transfer #{t.ReferenceId}" : null,
                SowingId = sowingId,
                UserName = t.CreatedByName, Remarks = t.Remarks,
            };
        }

        private static MaterialHistoryRow FromTray(TrayStockTransaction t) => new()
        {
            IsTray = true, AtUtc = t.TransactionDate, RecordedUtc = t.CreatedAt, MaterialName = "Trays", Unit = MaterialStockRules.TrayUnit, Cavity = t.TraySize,
            AreaId = t.AreaId, AreaName = t.AreaName ?? "", PolyhouseId = t.PolyhouseId, PolyhouseName = t.PolyhouseName ?? "(Area level, 4 Oct 2026)",
            Category = MaterialStockRules.TrayCategory(t.TransactionType), TypeLabel = t.TypeLabel,
            SignedQuantity = t.Quantity, BalanceAfter = t.AfterQuantity,
            ReferenceText = t.ReferenceType switch
            {
                null => null,
                TrayStockRepository.AddReferenceType => null,
                "TrayStockMigration" or "TrayStockPolyhouseSplit" => null,
                "TrayStockReversal" => $"Entry #{t.ReferenceId}",
                MaterialStockRules.SowingReference => $"Sowing #{t.ReferenceId}",
                MaterialStockRules.ApprovalReference => $"Approval #{t.ReferenceId}",
                TrayTransferRules.AdjustReferenceType => null,
                TrayTransferRules.TransferReferenceType => $"Transfer #{t.ReferenceId}",
                _ => $"{t.ReferenceType} #{t.ReferenceId}",
            },
            SowingId = t.ReferenceType == MaterialStockRules.SowingReference ? t.ReferenceId : null,
            UserName = t.UserName, Remarks = t.Remarks,
        };

        public Dictionary<string, string?> Route(int page) => new()
        {
            ["Material"] = Material == "" ? null : Material,
            ["AreaId"] = AreaId?.ToString(),
            ["PolyhouseId"] = PolyhouseId?.ToString(),
            ["Cavity"] = Cavity,
            ["Type"] = Type,
            ["FromDate"] = FromDate?.ToString("yyyy-MM-dd"),
            ["ToDate"] = ToDate?.ToString("yyyy-MM-dd"),
            ["Search"] = Search,
            ["pg"] = page.ToString(),
        };
    }
}
