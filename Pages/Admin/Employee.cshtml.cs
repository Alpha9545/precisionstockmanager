using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    public class EmployeeModel : PageModel
    {
        private readonly EmployeeRepository _employeeRepo;
        private readonly DatabaseHelper _dbHelper;

        public EmployeeModel(EmployeeRepository employeeRepo, DatabaseHelper dbHelper)
        {
            _employeeRepo = employeeRepo;
            _dbHelper = dbHelper;
        }

        public List<Employee> Employees { get; set; } = new();

        public List<Designation> DistinctDesignations { get; set; } = new();


        [BindProperty]
        public string NewEmployeeName { get; set; }
        [BindProperty]
        public string Designation { get; set; }

        [BindProperty]
        public int EditId { get; set; }

        [BindProperty]
        public string EditEmployeeName { get; set; }

        public async Task OnGetAsync()
        {
            Employees = await _employeeRepo.GetAllEmployees();



            DistinctDesignations = await GetAllDesignation();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewEmployeeName))
            {
                await _employeeRepo.AddEmployee(NewEmployeeName, Designation);
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditId > 0 && !string.IsNullOrWhiteSpace(EditEmployeeName))
            {
                await _employeeRepo.UpdateEmployee(EditId, EditEmployeeName, Designation);
            }
            return RedirectToPage();
        }



        public async Task<List<Designation>> GetAllDesignation()
        {
            var designations = new List<Designation>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();

                var cmd = new SqlCommand("SELECT DesignationID, DesignationName FROM Designation ORDER BY DesignationName", conn);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        designations.Add(new Designation
                        {
                            DesignationId = reader.GetInt32(0),
                            DesignationName = reader.GetString(1)
                        });
                    }
                }
            }

            return designations;
        }

    }
}
