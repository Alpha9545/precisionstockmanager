using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Add Material -- ONE page for three separate stocks, chosen with one "Material Type"
    // selector; each type posts its own form to its own handler and keeps its own rules:
    //   Cocopeat / Pitmos (MaterialStock.Add): a Polyhouse of a Main Office Area the user
    //     is assigned to; usable stock = entered quantity x the material's conversion
    //     factor (Cocopeat 5.2: 5 kg dry -> 26 kg; Pitmos 1.0), calculated by the SERVER
    //     from dbo.MaterialTypes -- the browser figure is a preview only. Area and Main
    //     Office location are re-checked in MaterialStockRepository.ReceiveAsync.
    //   Tray (TrayStock.Enter): the former Add Tray Stock page, unchanged (TrayAddForm):
    //     Area + Polyhouse + cavity + count, any active non-Outlet Area of the user.
    // A user sees -- and the server accepts -- only the types they hold the permission for.
    public class AddModel : PageModel
    {
        private readonly MaterialStockRepository _repo;
        private readonly AreaAccessService _areaAccess;
        private readonly TrayAddForm _trayForm;

        public AddModel(MaterialStockRepository repo, AreaAccessService areaAccess,
            TrayStockRepository trayStockRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo)
        {
            _repo = repo;
            _areaAccess = areaAccess;
            _trayForm = new TrayAddForm(trayStockRepo, areaRepo, polyhouseRepo, areaAccess);
        }

        // ---- Cocopeat / Pitmos form ----
        [BindProperty] public int AreaId { get; set; }
        [BindProperty] public int PolyhouseId { get; set; }
        [BindProperty] public int? MaterialTypeId { get; set; }
        [BindProperty] public decimal? InputQuantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        // ---- Tray form (fields named Tray.*) ----
        [BindProperty(Name = TrayAddForm.Prefix)] public TrayAddInput Tray { get; set; } = TrayAddInput.New();

        // The selected type: a material Code (COCOPEAT / PITMOS) or TRAYS.
        public string Kind { get; set; } = "";
        public bool CanAddMaterials { get; private set; }
        public bool CanAddTrays { get; private set; }

        public List<MaterialType> Materials { get; set; } = new();
        public List<(int AreaId, string AreaName, int PolyhouseId, string PolyhouseName)> Locations { get; set; } = new();
        public List<Area> TrayAreas { get; set; } = new();
        public List<Polyhouse> TrayPolyhouses { get; set; } = new();
        public IReadOnlyList<string> TraySizes => _trayForm.TraySizes;

        public async Task OnGetAsync(int? areaId, int? polyhouseId, string? type)
        {
            SubmissionToken = Guid.NewGuid();
            Tray = TrayAddInput.New();
            await LoadAsync();
            AreaId = areaId ?? (Locations.Select(l => l.AreaId).Distinct().Count() == 1 ? Locations[0].AreaId : 0);
            PolyhouseId = polyhouseId ?? 0;
            Kind = ChooseKind(type);
            MaterialTypeId = Materials.FirstOrDefault(m => m.Code == Kind)?.Id ?? Materials.FirstOrDefault()?.Id;
            if (TrayAreas.Count == 1)
                Tray.AreaId = TrayAreas[0].Id;
            TrayPolyhouses = await _trayForm.PolyhousesForAsync(User, Tray.AreaId);
        }

        // Tray form: only that Area's Polyhouses, only for an Area the user may add trays to.
        public async Task<IActionResult> OnGetTrayPolyhousesAsync(int areaId)
        {
            if (!User.CanAddTrays()) return Forbid();
            return new JsonResult((await _trayForm.PolyhousesForAsync(User, areaId)).Select(p => new { id = p.Id, name = p.Name }));
        }

        // Cocopeat / Pitmos
        public async Task<IActionResult> OnPostAsync()
        {
            if (!User.CanAddMaterials()) return Forbid();
            ModelState.Clear();
            await LoadAsync();
            Kind = Materials.FirstOrDefault(m => m.Id == MaterialTypeId)?.Code ?? ChooseKind(null);
            if (Request.Form.TryGetValue(nameof(MaterialTypeId), out var posted) && posted.Count(v => !string.IsNullOrWhiteSpace(v)) > 1)
                ModelState.AddModelError(string.Empty, "Select exactly one material.");
            if (MaterialTypeId is not > 0 || !Materials.Any(m => m.Id == MaterialTypeId))
                ModelState.AddModelError(nameof(MaterialTypeId), "Select the material: Cocopeat or Pitmos.");
            if (AreaId <= 0)
                ModelState.AddModelError(nameof(AreaId), "Area is required.");
            else if (!Locations.Any(l => l.AreaId == AreaId))
                ModelState.AddModelError(nameof(AreaId), MaterialStockRules.NotAuthorizedAreaMessage);
            if (PolyhouseId <= 0)
                ModelState.AddModelError(nameof(PolyhouseId), "Polyhouse is required.");
            else if (AreaId > 0 && !Locations.Any(l => l.AreaId == AreaId && l.PolyhouseId == PolyhouseId))
                ModelState.AddModelError(nameof(PolyhouseId), MaterialStockRules.PolyhouseNotInAreaMessage);
            var qtyError = MaterialStockRules.ValidateQuantity(InputQuantity);
            if (qtyError != null)
                ModelState.AddModelError(nameof(InputQuantity), qtyError);
            if (!ModelState.IsValid)
                return await PageWithFreshTrayFormAsync();

            var result = await _repo.ReceiveAsync(AreaId, PolyhouseId, MaterialTypeId!.Value, InputQuantity!.Value, SubmissionToken,
                User.GetUserId(), Remarks, areaId => _areaAccess.CanAccessRequiredArea(User, areaId));
            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Message ?? "Failed to add material stock.");
                return await PageWithFreshTrayFormAsync();
            }

            var unit = result.Unit;
            TempData["Success"] = result.Duplicate
                ? $"This entry was already saved -- nothing was added twice. {result.MaterialName} in {result.PolyhouseName}: {MaterialStockRules.Kg(result.Balance, unit)}."
                : $"Added {MaterialStockRules.Kg(result.StockAdded, unit)} {result.MaterialName} to {result.PolyhouseName} "
                  + $"({MaterialStockRules.Kg(result.InputQuantity, unit)} entered x {result.ConversionFactor:0.####}). "
                  + $"{result.MaterialName} in {result.PolyhouseName} now: {MaterialStockRules.Kg(result.Balance, unit)}.";
            return RedirectToPage("/Production/MaterialStock/Index", new { Material = Kind, AreaId, PolyhouseId });
        }

        // Trays (the former Add Tray Stock POST, same validation and repository call)
        public async Task<IActionResult> OnPostTrayAsync()
        {
            if (!User.CanAddTrays()) return Forbid();
            ModelState.Clear();
            await LoadAsync();
            Kind = MaterialStockRules.TraysKind;
            SubmissionToken = Guid.NewGuid();
            TrayPolyhouses = await _trayForm.PolyhousesForAsync(User, Tray.AreaId);
            _trayForm.Validate(User, Tray, TrayAreas, TrayPolyhouses, ModelState);
            if (!ModelState.IsValid)
                return Page();

            var (success, message, duplicate, balance) = await _trayForm.SaveAsync(User, Tray);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add Tray Stock.");
                return Page();
            }

            var where = $"{TrayAreas.First(a => a.Id == Tray.AreaId).Name}, {TrayPolyhouses.First(p => p.Id == Tray.PolyhouseId).Name}, {Tray.TraySize}";
            TempData["Success"] = duplicate
                ? $"This Tray Stock entry was already saved -- nothing was added twice. {where}: {QuantityFormat.Qty(balance)} trays available."
                : $"Added {QuantityFormat.Qty(Tray.Quantity)} trays to {where}. Available now: {QuantityFormat.Qty(balance)} trays.";
            return RedirectToPage("/Production/MaterialStock/Index",
                new { Material = MaterialStockRules.TraysKind, AreaId = Tray.AreaId, PolyhouseId = Tray.PolyhouseId, Cavity = Tray.TraySize });
        }

        private async Task<IActionResult> PageWithFreshTrayFormAsync()
        {
            Tray = TrayAddInput.New();
            if (TrayAreas.Count == 1) Tray.AreaId = TrayAreas[0].Id;
            TrayPolyhouses = await _trayForm.PolyhousesForAsync(User, Tray.AreaId);
            return Page();
        }

        private string ChooseKind(string? requested)
            => MaterialStockRules.NormalizeKind(requested, Materials, CanAddMaterials, CanAddTrays) switch
            {
                "" => Materials.FirstOrDefault()?.Code ?? (CanAddTrays ? MaterialStockRules.TraysKind : ""),
                var k => k,
            };

        private async Task LoadAsync()
        {
            CanAddMaterials = User.CanAddMaterials();
            CanAddTrays = User.CanAddTrays();
            if (CanAddMaterials)
            {
                Materials = await _repo.GetTypesAsync();
                Locations = await _repo.GetLocationsAsync(a => _areaAccess.CanAccessRequiredArea(User, a));
            }
            if (CanAddTrays)
                TrayAreas = await _trayForm.AreasAsync(User);
        }
    }
}
