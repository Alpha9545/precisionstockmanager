using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    // Fertilizer issue. Correction #8: "Received By" is chosen from the active users (not typed) and the person who entered
    // the issue is recorded; the issue itself is FertilizerTransactionRepository.IssueAsync (row lock on the stock batch,
    // active-receiver check, double-submit guard). The stock rules and the page's layout are unchanged.
    public class UsageModel : PageModel
    {
        private readonly DatabaseHelper _db;
        private readonly FertilizerTransactionRepository _repo;
        public UsageModel(DatabaseHelper db, FertilizerTransactionRepository repo)
        {
            _db = db;
            _repo = repo;
        }

        // Show only available stock
        public List<AvailableStockTableVM> AvailableStocks { get; set; } = new();

        // the people the fertilizer can be issued to: active users
        public List<FertilizerFilterOption> Receivers { get; set; } = new();

        [BindProperty]
        public FertilizerUsage Usage { get; set; }

        public async Task OnGetAsync()
        {
            LoadAvailableStock();
            Receivers = await _repo.GetReceiverChoicesAsync();
        }

        public async Task<IActionResult> OnPostSaveAsync()
        {
            // the entering user is always the logged-in user -- never a posted value
            var result = await _repo.IssueAsync(Usage.StockId, Usage.UsedQuantity, Usage.IssueDate, Usage.ReceivedById,
                                                Usage.Remarks, User.GetUserId());
            if (!result.Success)
            {
                TempData["Error"] = result.Message;
                return RedirectToPage();
            }
            if (result.Duplicate)
                TempData["Success"] = result.Message;
            else
                TempData["Success"] = "Fertilizer issued.";
            return RedirectToPage();
        }


        private void LoadAvailableStock()
        {
            using var con = _db.GetConnection();
            var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
            SELECT fs.StockId,
                   fm.FertilizerName,
                   fs.BatchNumber,
                   fs.LatestAvailableQuantity,
                   um.UnitName,
                   fs.ExpiryDate
            FROM FertilizerStock fs
            JOIN FertilizerMaster fm ON fs.FertilizerId = fm.FertilizerId
            JOIN UnitMaster um ON fs.UnitId = um.UnitId
            WHERE fs.LatestAvailableQuantity > 0
              AND fs.IsUtilized = 0
            ORDER BY fs.ExpiryDate, fs.CreatedAt", con);

            con.Open();
            var r = cmd.ExecuteReader();

            while (r.Read())
            {
                AvailableStocks.Add(new AvailableStockTableVM
                {
                    StockId = (int)r["StockId"],
                    FertilizerName = r["FertilizerName"].ToString(),
                    BatchNumber = r["BatchNumber"]?.ToString(),
                    AvailableQuantity = (decimal)r["LatestAvailableQuantity"],
                    UnitName = r["UnitName"].ToString(),
                    ExpiryDate = r["ExpiryDate"] as DateTime?
                });
            }
        }

    }
}

public class AvailableStockTableVM
{
    public int StockId { get; set; }
    public string FertilizerName { get; set; }
    public string BatchNumber { get; set; }
    public decimal AvailableQuantity { get; set; }
    public string UnitName { get; set; }
    public DateTime? ExpiryDate { get; set; }
}


// View model
public class FertilizerUsage
{
    public int UsageId { get; set; }
    public int StockId { get; set; }
    public decimal UsedQuantity { get; set; }
    public DateTime IssueDate { get; set; }
    // the selected receiver (an active user); the name that is stored with the issue comes from IMSUsers, not from the form
    public int? ReceivedById { get; set; }
    public string Remarks { get; set; }
}
