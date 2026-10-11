using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Item Master (read: Store.View / Store.Manage; every POST: Store.Manage, enforced by the page
    // rule's Write policy). Categories and items; a new item starts with 0 stock (stock comes in
    // through Stock Entry only). Item code is unique (case-insensitive) and never changes; unit and
    // type change only while the item has no movements / request lines.
    public class ItemsModel : PageModel
    {
        private readonly StoreRepository _repo;
        private readonly StoreAuthorization _auth;

        public ItemsModel(StoreRepository repo, StoreAuthorization auth)
        {
            _repo = repo;
            _auth = auth;
        }

        [BindProperty(SupportsGet = true)] public int? EditId { get; set; }
        [BindProperty(SupportsGet = true)] public int? CategoryFilter { get; set; }
        [BindProperty(SupportsGet = true)] public bool ShowInactive { get; set; }
        [BindProperty] public string? NewCategory { get; set; }
        [BindProperty] public string? ItemCode { get; set; }
        [BindProperty] public string? Name { get; set; }
        [BindProperty] public int? CategoryId { get; set; }
        [BindProperty] public string? Description { get; set; }
        [BindProperty] public int? UnitId { get; set; }
        [BindProperty] public string? ItemType { get; set; } = StoreRules.Consumable;
        [BindProperty] public decimal? MinimumStock { get; set; }
        [BindProperty] public string? Location { get; set; }
        [BindProperty] public bool IsActive { get; set; } = true;
        [BindProperty] public string? Remarks { get; set; }

        public bool IsManager { get; set; }
        public List<StoreCategory> Categories { get; set; } = new();
        public List<(int Id, string Name)> Units { get; set; } = new();
        public List<StoreItem> Items { get; set; } = new();
        public StoreItem? Editing { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadAsync();
            if (EditId.HasValue)
            {
                if (!IsManager) return Forbid();
                Editing = await _repo.GetItemAsync(EditId.Value);
                if (Editing == null) return NotFound();
                (ItemCode, Name, CategoryId, Description, UnitId, ItemType, MinimumStock, Location, IsActive, Remarks) =
                    (Editing.ItemCode, Editing.Name, Editing.CategoryId, Editing.Description, Editing.UnitId, Editing.ItemType, Editing.MinimumStock, Editing.Location, Editing.IsActive, Editing.Remarks);
            }
            return Page();
        }

        public async Task<IActionResult> OnPostCategoryAsync()
        {
            var error = StoreRules.Required(NewCategory, "Category name", 100);
            if (error == null)
            {
                var id = await _repo.CreateCategoryAsync(NewCategory!, User.GetUserId());
                if (id == null) error = $"Category \"{NewCategory!.Trim()}\" already exists.";
                else
                {
                    TempData["Success"] = $"Category \"{NewCategory!.Trim()}\" added.";
                    return RedirectToPage();
                }
            }
            ModelState.AddModelError(nameof(NewCategory), error);
            await LoadAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostSaveAsync()
        {
            await LoadAsync();
            if (EditId.HasValue)
            {
                Editing = await _repo.GetItemAsync(EditId.Value);
                if (Editing == null) return NotFound();
            }
            AddIfError(nameof(ItemCode), Editing == null ? StoreRules.Required(ItemCode, "Item code", 30) : null);
            if (Editing == null && ItemCode != null && !System.Text.RegularExpressions.Regex.IsMatch(ItemCode.Trim(), "^[A-Za-z0-9][A-Za-z0-9._/-]*$"))
                ModelState.AddModelError(nameof(ItemCode), "Item code: letters, digits, '.', '_', '/', '-' only.");
            AddIfError(nameof(Name), StoreRules.Required(Name, "Item name", 150));
            if (!Categories.Any(c => c.Id == CategoryId && (c.IsActive || c.Id == Editing?.CategoryId)))
                ModelState.AddModelError(nameof(CategoryId), "Choose a category.");
            if (!Units.Any(u => u.Id == UnitId))
                ModelState.AddModelError(nameof(UnitId), "Choose a unit.");
            if (!StoreRules.ItemTypes.Contains(ItemType))
                ModelState.AddModelError(nameof(ItemType), "Choose Consumable or Returnable.");
            AddIfError(nameof(MinimumStock), StoreRules.ValidateQuantity(MinimumStock ?? 0, "Minimum stock", ItemType ?? StoreRules.Consumable, allowZero: true));
            AddIfError(nameof(Description), StoreRules.Optional(Description, "Description"));
            AddIfError(nameof(Location), StoreRules.Optional(Location, "Location", 100));
            AddIfError(nameof(Remarks), StoreRules.Optional(Remarks, "Remarks"));
            if (!ModelState.IsValid)
                return Page();

            var item = new StoreItem
            {
                Id = Editing?.Id ?? 0, ItemCode = ItemCode ?? "", Name = Name!, CategoryId = CategoryId!.Value, Description = Description, UnitId = UnitId!.Value,
                ItemType = ItemType!, MinimumStock = MinimumStock ?? 0, Location = Location, IsActive = IsActive, Remarks = Remarks,
            };
            if (Editing == null)
            {
                var id = await _repo.CreateItemAsync(item, User.GetUserId());
                if (id == null)
                {
                    ModelState.AddModelError(nameof(ItemCode), $"Item code {ItemCode!.Trim().ToUpperInvariant()} already exists.");
                    return Page();
                }
                TempData["Success"] = $"Item {ItemCode!.Trim().ToUpperInvariant()} created (stock 0 -- add stock with a Stock Entry).";
            }
            else
            {
                await _repo.UpdateItemAsync(item, User.GetUserId());
                TempData["Success"] = $"Item {Editing.ItemCode} saved" + (Editing.HasMovements && (UnitId != Editing.UnitId || ItemType != Editing.ItemType)
                    ? " (unit and type are kept: the item already has movements)." : ".");
            }
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            IsManager = _auth.IsManager(User);
            Categories = await _repo.GetCategoriesAsync();
            Units = await _repo.GetUnitsAsync();
            Items = (await _repo.GetItemsAsync(categoryId: CategoryFilter)).Where(i => ShowInactive || i.IsActive).ToList();
        }

        private void AddIfError(string key, string? error)
        {
            if (error != null) ModelState.AddModelError(key, error);
        }
    }
}
