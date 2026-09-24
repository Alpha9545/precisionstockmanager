using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Account
{
    // Phase A: the server-side rule for this page ("Admin.ManageUsers") is declared
    // centrally in Authorization/FeatureAuthorizationConventions.cs.
    public class AddUserModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly IPasswordHasher<User> _passwordHasher;

        public AddUserModel(DatabaseHelper dbHelper, IPasswordHasher<User> passwordHasher)
        {
            _dbHelper = dbHelper;
            _passwordHasher = passwordHasher;
        }

        [BindProperty] public string Username { get; set; }
        [BindProperty] public string Password { get; set; }
        [BindProperty] public string Role { get; set; }
        public string Message { get; set; }

        // F1: this page was ANONYMOUS (no convention covered /Account
        // except Login) and inserted straight into dbo.IMSUsers with a
        // free-text Role, so anyone on the internet could create a login.
        // It now requires the same "Admin.ManageUsers" permission as the
        // real user-management page and no longer writes anything itself:
        // both verbs redirect to /Admin/Users, which is the single, guarded
        // account-creation path (designation, duplicate and
        // administrator-escalation checks live there).
        public IActionResult OnGet() => RedirectToPage("/Admin/Users");

        public IActionResult OnPost() => RedirectToPage("/Admin/Users");
    
    }
}
