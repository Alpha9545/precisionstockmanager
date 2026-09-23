# Database Setup

## Connection

The app reads its connection string from `appsettings.json` → `ConnectionStrings:DefaultConnection` (loaded by `Data/DatabaseHelper.cs`). Point this at your `PlantsIMS2` SQL Server database before running migrations or the app. `appsettings.Development.json` can override it locally. Neither file in this deliverable contains a real password — replace the placeholder before deploying.

## Applying the migrations

1. **Back up `PlantsIMS2` first.** Every script in `Database/` is additive and idempotent (safe to re-run), but a backup is still the right first step before altering a production database.
2. Run the twelve scripts in `Database/` **in the order listed in `DATABASE_MIGRATION_ORDER.md`** — each one assumes the previous one has already been applied (e.g. Phase 9 requires `dbo.PottedPlantStock` from Phase 7 to already exist).
3. Run them with a tool that executes `GO`-separated batches correctly (SQL Server Management Studio, Azure Data Studio, or `sqlcmd -i <file>.sql`) — several scripts use `GO` between a dynamic `CREATE FUNCTION` and the `ALTER TABLE ... ADD CONSTRAINT` that depends on it, because `CREATE FUNCTION` must be the first statement in its own batch.
4. After all twelve have run, use the sanity-check query in `DATABASE_MIGRATION_ORDER.md` to confirm every new table exists.

## What was **not** touched

- `dbo.VendorPurchases` and its C# stub (`Models/VendorPurchase.cs`, `Data/VendorPurchaseRepository.cs`) — left completely alone per Decision 2. Phase 11 built a separate `PurchaseOrder`/`PurchaseReceipt` system instead.
- `dbo.Inventory`, `dbo.InventoryTransactions`, `dbo.SeedCuttingBank`, `dbo.SeedCuttingTx`, `dbo.Bookings` and every existing page under `Pages/Bookings/*`, `Pages/SeedEntry/*`, `Pages/Fertilizer/*`, `Pages/Admin/*` (other than the new `Vendor.cshtml`) — all preserved exactly as they worked before this project began.
- No table was dropped, renamed, or had a column removed anywhere in this project.

## Environment note on build verification

This sandbox has **no network access to `api.nuget.org`** (an organization egress policy, not a code defect) — every `dotnet build`/`dotnet restore` attempt in this environment fails at the NuGet restore step with `NU1301` / a 403 from the outbound proxy, regardless of how correct the code is. This was re-confirmed after every phase in this project, with the identical failure each time. Because of this, **no phase's build could be verified in this sandbox.** Every phase was instead reviewed manually: brace/paren balance across every changed file, every repository method's signature cross-checked against every call site, every new class's DI registration checked against `Program.cs`, and every model property's type checked against what it reads from `SqlDataReader`. Run `dotnet build` (or open the solution in Visual Studio) on a machine with normal internet access as the real sign-off step before deploying.
