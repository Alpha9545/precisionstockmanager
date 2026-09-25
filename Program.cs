using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using Microsoft.AspNetCore.HttpOverrides;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/var/aspnet-keys"))
    .SetApplicationName("PlantStockManager");



builder.Services.AddSingleton<DatabaseHelper>();
builder.Services.AddScoped<SeedSourcesRepository>();
builder.Services.AddScoped<PolyhouseRepository>();
builder.Services.AddScoped<PlantTypeRepository>();
builder.Services.AddScoped<PlantSpeciesRepository>();
builder.Services.AddScoped<SeedEntryRepository>();
builder.Services.AddScoped<InventoryRepository>();
builder.Services.AddScoped<BookingRepository>();
builder.Services.AddScoped<VendorPurchaseRepository>();
builder.Services.AddScoped<EmployeeRepository>();

// Production module: Mother Plant / Cutting workflow
builder.Services.AddScoped<AreaRepository>();
builder.Services.AddScoped<BatchNumberRepository>();
builder.Services.AddScoped<MotherPlantRepository>();
builder.Services.AddScoped<CuttingPlanRepository>(); // Phase 3
builder.Services.AddScoped<ActualCuttingRepository>(); // Phase 4
builder.Services.AddScoped<CuttingDeliveryRepository>(); // Phase 5
builder.Services.AddScoped<PropagationBatchRepository>(); // Phase 6
builder.Services.AddScoped<EmptyPotInventoryRepository>(); // Phase 7
builder.Services.AddScoped<PottedPlantStockRepository>(); // Phase 7
builder.Services.AddScoped<PotProductionRepository>(); // Phase 7
builder.Services.AddScoped<InternalTransferRepository>(); // Phase 8
builder.Services.AddScoped<PottedPlantBookingRepository>(); // Phase 9
builder.Services.AddScoped<DispatchRepository>(); // Phase 10
builder.Services.AddScoped<VendorRepository>(); // Phase 11
builder.Services.AddScoped<PurchaseOrderRepository>(); // Phase 11
builder.Services.AddScoped<LabRequestRepository>(); // Phase 12
builder.Services.AddScoped<LabourLogRepository>(); // Phase 13

// Role/permission foundation (Phase 14)
builder.Services.AddScoped<RoleRepository>();
builder.Services.AddScoped<PermissionRepository>();
builder.Services.AddScoped<UserRoleRepository>();

// Mother Plant workflow: location-scoped raw cutting stock (Phase 15)
builder.Services.AddScoped<CuttingStockRepository>();

// Growing Partner foundation (Phase 17) -- deliberately separate from
// VendorRepository/dbo.Vendors (Phase 11), see GrowingPartner.cs.
builder.Services.AddScoped<GrowingPartnerRepository>();

// Growing Partner Access & Responsibilities (Phase 17/B): reusable
// Area-scope check, reads the RoleName/AreaAccess claims stamped at
// login. Stateless, but Scoped to match every other service's lifetime
// in this project.
builder.Services.AddScoped<AreaAccessService>();
// F1: resolves the Area of Phase 3-6 records through their Mother Plant
// so those pages can use AreaAccessService (Authorization/MotherPlantAreaScope.cs).
builder.Services.AddScoped<MotherPlantAreaScope>();
builder.Services.AddScoped<AdministrativeAccessGuard>(); // Phase A: user/role anti-escalation rules
// Phase B: Area scope for the seedling workflow pages only (switchable while
// Polyhouses/Areas are not configured yet). See Authorization/SeedlingAreaScope.cs.
builder.Services.Configure<SeedlingWorkflowOptions>(builder.Configuration.GetSection(SeedlingWorkflowOptions.SectionName));
builder.Services.AddScoped<SeedlingAreaScope>();

// Main Office -> Polyhouse/Growing Area Seed Issue (Phase 22/Phase H):
// a wholly new, dedicated Seed Stock domain (Physical/InTransit +
// ledger) and its own Issue header table -- deliberately NOT a new
// StockType on InternalTransferRepository. See
// Database/Phase22_MainOfficeSeedIssue.sql for the full reasoning.
builder.Services.AddScoped<SeedStockRepository>();
// (SeedIssueRepository removed: the Seed Issue workflow is retired; the
//  dbo.SeedIssues table is kept and still read by the Management Dashboard.)

// Seed Stock -> Sowing (Phase 23/Phase I): a single-actor production
// event that consumes dbo.SeedStock, mirroring PotProductionRepository's
// shape rather than a StockType/transfer workflow. See
// Database/Phase23_SeedSowing.sql for the full reasoning.
builder.Services.AddScoped<SeedSowingRepository>();
builder.Services.AddScoped<ReadyStockRepository>(); // Phase 25 (Phase K)
builder.Services.AddScoped<ReadyConfirmationRepository>(); // Phase 25 (Phase K)
builder.Services.AddScoped<SeedlingFulfilmentRepository>(); // Phase C: Ready Stock -> booking reservation -> dispatch

// Management Dashboard (Phase 26/Phase L): a dedicated, READ-ONLY
// reporting repository -- never a duplicate of any operational
// repository above, see Data/ManagementDashboardRepository.cs.
builder.Services.AddScoped<ManagementDashboardRepository>();


builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();


//builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, ApplicationPasswordHasher>();
//builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, ApplicationClaimsPrincipalFactory>();

// Cookie-based Authentication
// Phase A: security options (full-access role names, principal revalidation
// interval) and the single claims builder shared by Login and the cookie
// revalidation below. See Authorization/SecurityOptions.cs and
// Authorization/UserClaimsFactory.cs.
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddScoped<UserClaimsFactory>();
builder.Services.AddScoped<FeatureAccessService>(); // menu visibility = same rule as the server

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login"; // Redirect to login page
        options.AccessDeniedPath = "/Account/AccessDenied"; // 403 page (Pages/Account/AccessDenied)
        options.ExpireTimeSpan = TimeSpan.FromDays(7);

        // Claims are re-checked against dbo.IMSUsers / dbo.UserRoles /
        // dbo.RolePermissions every SecurityOptions.PrincipalRevalidationMinutes:
        // a deactivated user is signed out, and changed roles / role
        // permissions / areas are re-stamped -- so editing a Role's
        // permissions reaches every user holding that Role automatically.
        options.Events.OnValidatePrincipal = UserClaimsFactory.ValidatePrincipalAsync;
    });

// Role-based feature authorization (Phase 14 architecture, completed in
// Phase A). A policy name is a dbo.Permissions.Code (or "A|B" = any of),
// satisfied by a "Permission" claim that the user receives ONLY through
// UserRoles -> Roles -> RolePermissions -> Permissions, or by the
// "FullAccess" claim of a full-access role (System Administrator).
// See Authorization/PermissionAuthorizationPolicyProvider.cs and
// Authorization/MinimumAuthorizationLevelHandler.cs.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
builder.Services.AddAuthorization(options =>
{
    // Deny-by-default: any endpoint without explicit authorization metadata
    // requires an authenticated user.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Razor Pages with auth support
builder.Services.AddRazorPages(options =>
{
    // Phase A: EVERY page's permission rule comes from ONE place --
    // Authorization/FeatureAuthorizationConventions.cs. Anonymous pages
    // (Login, Logout, AccessDenied, Error) are declared there too; any page
    // missing from that map is restricted to full-access users. This
    // replaces the former per-folder AuthorizeFolder/AllowAnonymous
    // conventions and the per-page [Authorize(Policy)] attributes.
    options.Conventions.Add(new FeatureAuthorizationPageConvention());
});
// (The legacy seed pipeline pages and their read-only write filter were
//  removed: the new Seed Stock -> Direct Sowing workflow is the only one.)



// Add services to the container.
//builder.Services.AddRazorPages();




var app = builder.Build();


//app.Use(async (context, next) =>
//{
//    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
//    context.Response.Headers["Pragma"] = "no-cache";
//    context.Response.Headers["Expires"] = "0";
//    await next();
//});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto
});


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    await next();
});

//app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
