# Plant Stock Manager — Role/Workflow Redesign: Audit & Implementation Plan

Status: **PLAN ONLY — no code or schema changed.** Written per your Section 18/21 instructions. Decisions already confirmed with you are marked ✅CONFIRMED; everything else is a proposal for your review.

---

## A. Current Architecture Audit

### A1. Pages relevant to this redesign
- `Pages/Production/CuttingPlan/*` — planning-only workflow, currently on the main menu.
- `Pages/Production/MotherPlant/*`, `CuttingDelivery` (via `ActualCuttingRepository`/`PropagationBatchRepository`), `PotProduction/*`, `EmptyPotInventory/*`, `PottedPlantStock/*`, `InternalTransfer/*`, `PottedPlantBooking/*`, `Dispatch/*`, `LabRequest/*` — Phase 3–13 pages, all under `Pages/Production/`, all currently reachable by *any authenticated user* (folder-level `AuthorizeFolder` only).
- `Pages/Bookings/*` — a **separate, older** booking system (`Book`, `Bookinglist`, `DirectBooking`, `FulfillBooking`, `RevertBooking`, `CancleBooking`, `EditBookingRecords`, `TotalBookings`), backed by `dbo.Bookings` + `BookingRepository`. This is distinct from Phase 9's `dbo.PottedPlantBookings` + `PottedPlantBookingRepository`, which only reuses `BookingRepository` read-only for its States/Districts lookup.
- `Pages/Office/SeedBank*` — seed-stage stock (`dbo.Inventory`, `dbo.SeedEntries`), unrelated to cutting/pot stock.
- `Pages/Admin/Area.cshtml(.cs)` — Area CRUD, subject of the Section 11 bug (root cause below).

### A2. Current tables (by domain)
- **Seed/seedling stage:** `dbo.Inventory`, `dbo.SeedEntries` — not part of this redesign's scope; "Main Office" does **not** map here.
- **Mother plant:** `dbo.MotherPlants` (Phase 2), `dbo.CuttingPlans` (Phase 3, to be hidden not deleted), `dbo.ActualCuttings`/`dbo.PropagationBatches` (cutting execution).
- **Pot/tray production:** `dbo.EmptyPotInventory`, `dbo.PotProduction`, `dbo.PottedPlantStock` (+ its own ledger `dbo.PottedPlantStockTransactions`) — Phase 7/8. **Both `EmptyPotInventory` and `PottedPlantStock` already carry a nullable `AreaId`** added in Phase 8 specifically so stock can be attributed to a physical location; the code comment on `PottedPlantStock.AreaId` already says "every NEW row going forward always has a real AreaId."
- **Transfers/booking/dispatch:** `dbo.InternalTransfers` (Phase 8), `dbo.PottedPlantBookings` (Phase 9), `dbo.Dispatches` (Phase 10).
- **Old booking system:** `dbo.Bookings` (pre-Phase-2, independent of the above).
- **Location/org:** `dbo.Polyhouses`, `dbo.Area` (Phase 2 — currently just `Id, PolyhouseId, Name, AreaCode, AreaSize, AreaUnit, Capacity, CapacityUnit, IsActive, CreatedAt`; no Type, no Supervisor, no Location, no Remarks).
- **Users/roles:** `dbo.IMSUsers` (real, live user table) joined to `dbo.Designation` (`DesignationId, DesignationName` — flat, no permissions). A separate `dbo.Employee` (singular) table exists but is only touched by unused `EmployeeRepository.GetAllEmployees/Add/Update` methods — everything actually wired (every ResponsiblePerson/Supervisor dropdown in Phase 2–13) uses `IMSUsers`+`Designation` via `GetAllEmployeesBooking/GetAllEmployeesSowing/GetAllActiveUsers`.

### A3. Current repositories
29 repositories under `Data/`, one per table/domain, all raw `Microsoft.Data.SqlClient` + parameterized SQL (no ORM). Relevant to this redesign: `AreaRepository`, `EmployeeRepository`, `PottedPlantStockRepository`, `EmptyPotInventoryRepository`, `InternalTransferRepository`, `PottedPlantBookingRepository`, `DispatchRepository`, `CuttingPlanRepository`, `MotherPlantRepository`, `BookingRepository` (old system).

### A4. Current stock ledgers
Each Phase 7–10 stock table has its own dedicated transaction/ledger table (e.g. `PottedPlantStockTransactions`), written in the same DB transaction as any balance change — this "single ledger per stock entity, never write balance without a ledger row" pattern is already established and should be preserved, not replaced, for any new stock movement this redesign introduces.

### A5. Current roles/users
- Real users live in `dbo.IMSUsers`; their role/title is `dbo.Designation.DesignationName` (a free-text-ish lookup, not a permission set).
- There is **no concept today** of "Kunjir Supervisor" / "Kiran Supervisor" / "Outlet Supervisor" / "Lab Worker" as distinct roles — whatever Designation values exist today are whatever was typed into `dbo.Designation`, with no tie to what pages/actions that title should be allowed to touch.

### A6. Current authorization mechanism
- `Program.cs`: Cookie authentication only. `AddAuthorization()` is called with **zero policies registered**. Folder-level checks only: `AuthorizeFolder("/SeedEntry")`, `AuthorizeFolder("/Admin")`, `AuthorizeFolder("/Bookings")`, `AuthorizeFolder("/Production")` (authenticated-user-only, not role-based), and `AllowAnonymousToFolder("/Data")` still present.
- `Pages/Account/Login.cshtml.cs` already signs in with `ClaimTypes.Role = Designation.DesignationName` plus custom `DesignationId`/`DesignationName`/`UserId` claims — built for role-based `[Authorize(Roles=...)]` but **never consumed anywhere**.
- `Authorization/MinimumAuthorizationLevelHandler.cs` and `MinimumAuthorizationLevelRequirement.cs` are both **empty stub classes** — scaffolding for a policy-based system that was started and abandoned, not wired into `Program.cs`.
- Net effect: any logged-in user, regardless of Designation, can open and post to every Production/Admin/Bookings page today.

### A7. What can be reused as-is
- The `ClaimTypes.Role` claim already set at login (just needs something to check it).
- `PottedPlantStock`/`EmptyPotInventory`'s existing `AreaId` column and ledger-per-entity pattern — this already anticipates a location dimension, which is exactly what Kunjir/Kiran/Outlet/Main Office need.
- `dbo.IMSUsers`/`dbo.Designation` as the user/title source of truth (not the dead `dbo.Employee` table).
- All Phase 3–13 stock-movement logic, transaction safety, and never-negative constraints — none of this needs to change; only *who can reach it* and *how it's grouped in the menu* changes.

### A8. What must be changed
- A real permission model needs to exist and be enforced (today there is none beyond "is logged in").
- `dbo.Area` needs an `AreaType` and a `SupervisorId` so Kunjir/Kiran/Outlet/Main Office can be *expressed* as Areas at all (see Decision 1 below, ✅CONFIRMED).
- The `Name` field bug (A9) needs an actual fix, not attribute removal.
- Navigation (`_Layout.cshtml` or equivalent) needs to be driven by the permission model instead of one flat static menu.
- Cutting Plan needs to disappear from that menu for everyone except whoever administers it, without touching its table or routes.

### A9. Root cause of "The Name field is required" (Section 11)
**Confirmed by code inspection, not by removing anything speculatively:**
- `Models/Area.cs`: `public string Name { get; set; }` — a non-nullable reference type with no default value.
- `PlantStockManager.csproj` has `<Nullable>enable</Nullable>` project-wide.
- ASP.NET Core's model binding/validation pipeline, when `Nullable` is enabled and `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` is **not** set (it isn't, anywhere in this project), automatically treats every non-nullable reference-type property on a `[BindProperty]`-bound model as implicitly required — using the framework's generic default message template, literally `"The {0} field is required."` → **"The Name field is required."**
- This is a completely separate mechanism from your own `ValidateArea()` helper in `Area.cshtml.cs`, whose message is worded *differently* ("Area Name is required.") — the fact that the reported text doesn't match that custom message is the tell that it's the framework's implicit check firing, not your own validation logic. There is no `[Required]` attribute anywhere to remove, which is exactly why searching for one turns up nothing.
- This does not require a workaround like disabling nullable checks project-wide. The correct fix (folded into the migration below, not applied yet) is to give `Area.Name` an explicit non-null default (`= string.Empty`) so an unbound/empty case fails your own friendly `ValidateArea` message instead of the framework's generic one, **and** to double check the `<input asp-for="NewArea.Name">` binds correctly once `AreaType` is added to the form (new required fields are exactly where this kind of implicit-validation trap tends to resurface, so it needs to be handled explicitly for every new non-nullable field added to `Area`).

---

## B. Proposed Role/Workflow Architecture

Eight roles, matching what you listed, each scoped to specific Areas via the new `Area.SupervisorId`/`AreaType`:

| Role | Scope | Core actions |
|---|---|---|
| Admin | Everything | Full access, user/role/permission administration |
| Mother Plant Supervisor | Their Mother-Plant Area(s) | Take from Main Office, view "My Mother Plants," enter cuttings, give cuttings to Main Office (→ PENDING_CONFIRMATION), view own transactions |
| Main Office / Production Officer | Main Office Area(s) | Confirm incoming cutting deliveries, choose destination Polyhouse+Supervisor, hold Main-Office-side stock |
| Kunjir Area Supervisor | Kunjir Area(s) | Take from Main Office / give to Main Office (same pattern as Mother Plant) |
| Kiran Area Supervisor | Kiran Area(s) | Direct pot/tray production, no confirmation step |
| Outlet Supervisor | Outlet Area(s) | Confirm pending receipts, record sales, internal receive-from-location (distinct from vendor purchase) |
| Lab Worker | Lab | Fully separate workflow/permissions (existing `LabRequest` pages, scoped) |
| Management | Everything, read-only + Reports | Cross-location visibility, no data-entry actions |

Cutting Plan (Phase 3) is **not** part of any role's normal menu — it stays in the codebase and database untouched, per your confirmed decision (see Section E), reachable only by Admin if ever needed again.

---

## C. Database Changes Required

All additive, nullable-first, non-destructive, rerun-safe — following the same pattern already used in Phase 2/8/9's fixes this session.

### Decision 1 ✅CONFIRMED: Extend `dbo.Area`, don't create a parallel location table
```sql
ALTER TABLE dbo.Area ADD AreaType NVARCHAR(30) NULL;      -- 'MotherPlant' | 'Kunjir' | 'Kiran' | 'Outlet' | 'MainOffice' | NULL (=legacy/general)
ALTER TABLE dbo.Area ADD SupervisorId INT NULL;             -- FK -> dbo.IMSUsers(Id)
ALTER TABLE dbo.Area ADD Location NVARCHAR(200) NULL;
ALTER TABLE dbo.Area ADD Remarks NVARCHAR(500) NULL;
-- Name gets an explicit default so the A9 bug's blast radius shrinks:
-- handled in application code (Area.cs), not schema.
```
Justification: Area already carries the PolyhouseId link and is already the FK target of `PottedPlantStock.AreaId`/`EmptyPotInventory.AreaId` from Phase 8. Reusing it means Kunjir/Kiran/Outlet/Main-Office locations automatically get real stock rows through existing tables with zero new join logic. "Main Office" becomes one or more `dbo.Area` rows with `AreaType='MainOffice'` (a real physical/organizational location, not a polyhouse-attached growing area, so `PolyhouseId` on those rows can point at a designated "Head Office" polyhouse row — or, if you'd rather not force that, `Area.PolyhouseId` could be made nullable instead. That's the one open sub-decision flagged in Section G; not a blocker).

A CHECK constraint will be added restricting `AreaType` to the fixed set of values above, so a bad value fails loudly rather than silently.

### Decision 2 ✅CONFIRMED: Reuse `PottedPlantStock`/`EmptyPotInventory`, no new stock tables
- No new `MainOfficeStock`/`OutletStock` tables. Main Office and Outlet stock are simply `PottedPlantStock`/`EmptyPotInventory` rows whose `AreaId` points at a `dbo.Area` row with the corresponding `AreaType`.
- `InternalTransfers` (Phase 8) already models an AreaId → AreaId movement — this is the exact primitive "take from Main Office," "give to Main Office," and "Outlet internal receive-from-location" all need. No new transfer table required; only new *workflow states* on top of it (see below).

### New: pending-confirmation workflow support
Cutting delivery (Mother Plant/Kunjir → Main Office) and Outlet's pending receipts both need a confirm/reject step that Phase 8's `InternalTransfers` doesn't currently have (Phase 8 transfers are direct/immediate).
```sql
ALTER TABLE dbo.InternalTransfers ADD Status NVARCHAR(20) NOT NULL CONSTRAINT DF_InternalTransfers_Status DEFAULT ('CONFIRMED');
   -- existing rows backfill to 'CONFIRMED' (today's direct-transfer behavior, unchanged)
ALTER TABLE dbo.InternalTransfers ADD ConfirmedBy INT NULL;        -- FK -> IMSUsers
ALTER TABLE dbo.InternalTransfers ADD ConfirmedDate DATETIME2 NULL;
ALTER TABLE dbo.InternalTransfers ADD RejectionReason NVARCHAR(500) NULL;
```
Justification: keeps the single existing transfer ledger as the one source of truth for every movement (mother plant ↔ main office, Kunjir ↔ main office, main office → destination, Outlet receive) instead of inventing a second parallel "pending movements" table. Kiran stays on the existing direct/`Status='CONFIRMED'`-by-default path — no behavior change for Kiran at all.

### New: role/permission model
```sql
CREATE TABLE dbo.Roles (Id INT IDENTITY PK, Name NVARCHAR(50) UNIQUE NOT NULL, IsSystemRole BIT DEFAULT(0));
CREATE TABLE dbo.Permissions (Id INT IDENTITY PK, Code NVARCHAR(100) UNIQUE NOT NULL, Description NVARCHAR(200));
   -- e.g. 'MotherPlant.Enter', 'MainOffice.Confirm', 'Outlet.Sell', 'Admin.ManageUsers'
CREATE TABLE dbo.RolePermissions (RoleId INT FK, PermissionId INT FK, PRIMARY KEY(RoleId, PermissionId));
CREATE TABLE dbo.UserRoles (UserId INT FK -> IMSUsers, RoleId INT FK -> Roles, AreaId INT NULL FK -> Area, PRIMARY KEY(UserId, RoleId, AreaId));
```
Justification: `dbo.Designation` stays exactly as it is (job title, used by HR-style dropdowns) — it is **not** repurposed as the permission system, avoiding any change to existing Designation-driven UI. `UserRoles.AreaId` is what lets "Kunjir Area Supervisor" scope to *their* Kunjir area(s) specifically rather than all Kunjir areas everywhere, satisfying "admin-configurable, not hard-coded per user."

All four tables are new, additive, and touch nothing in Phase 2–13. `Admin` gets seeded as a system role with every permission; existing users get no role by default (Admin assigns explicitly) — see Migration Plan for how this is sequenced so nobody is locked out on day one.

---

## D. Permission Model

- **Enforcement point:** a real `IAuthorizationHandler` replacing the currently-empty `MinimumAuthorizationLevelHandler` stub, registered as an actual `AddAuthorization(options => options.AddPolicy(...))` policy per permission code, checked via `[Authorize(Policy = "MainOffice.Confirm")]` on the relevant PageModels (or a folder-level policy where a whole folder maps 1:1 to a permission, same mechanism already used for `AuthorizeFolder`, just parameterized).
- **Claims:** at login, in addition to today's `ClaimTypes.Role = DesignationName`, add one claim per resolved permission code (computed once from `UserRoles`→`RolePermissions`→`Permissions` at sign-in) so authorization checks are cheap claim lookups, not a DB query per page hit.
- **Menu filtering:** the same claims drive which top-level menu items render (Section E) — a user with no `MainOffice.*` permission simply never sees a Main Office menu entry, rather than seeing it and hitting a 403.
- **Admin UI:** a new `Pages/Admin/Roles` and `Pages/Admin/UserRoles` pair (mirroring the existing `Pages/Admin/Area.cshtml` CRUD pattern already in the codebase) so role/permission/area assignment is admin-configurable through the app, not a SQL script.

---

## E. Page/Menu Changes

Proposed top-level menu (rendered conditionally per the claims above):

```
Dashboard          (existing)
My Work            (new — role-specific landing: pending confirmations, low-stock alerts for that user's Area)
Mother Plant        (existing MotherPlant pages, + Take from Main Office / Give to Main Office wired to InternalTransfers)
Cutting             (CuttingDelivery/ActualCutting — kept, reframed as its own independent flow, decoupled from Cutting Plan)
Pot & Tray Production   (PotProduction, EmptyPotInventory — independent of Cutting)
Outlet              (new pages: pending receipts/confirm, sales, internal receive-from-location)
Lab                 (existing LabRequest, its own permission set)
Internal Movement   (InternalTransfer — now confirmation-aware)
Booking             (PottedPlantBooking; old Bookings system stays reachable under Admin/legacy only, not merged)
Purchase            (VendorPurchase/PurchaseOrder — untouched, dbo.VendorPurchases never touched per standing rule)
Reports             (Management-facing, read-only)
Administration      (Area, Roles, UserRoles, Designation — Admin only)
```
Cutting Plan: **removed from this menu entirely** (✅CONFIRMED: menu/permission hidden only — routes and pages stay exactly as-is, reachable by direct URL only for Admin, per your confirmed answer). No `[Authorize]` denial added to the folder itself, so nothing about its current behavior changes; it simply isn't linked or granted to any role by default.

`Pages/Admin/Area.cshtml`/`.cs` get the new `AreaType`/`SupervisorId`/`Location`/`Remarks` fields added to both the Add and Edit forms, and `Area.cs` gets `Name = string.Empty` as an explicit default (A9 fix).

---

## F. Migration Plan (sequenced, each step independently safe to stop after)

1. **Schema-only, zero behavior change:** add the `Area` columns (Decision 1) and `InternalTransfers` columns (pending-confirmation) with safe defaults that reproduce today's behavior exactly (`AreaType` NULL = today's plain Area; `InternalTransfers.Status` defaults to `'CONFIRMED'` = today's direct-transfer behavior). Verified by rerunning existing Phase 2–13 flows unchanged.
2. **Create Roles/Permissions/RolePermissions/UserRoles tables**, seed `Admin` role with all permissions, seed permission codes for every planned menu section. No enforcement wired yet — tables exist but nothing checks them.
3. **Fix the Area bug (A9)** and add the new fields to the Area Admin UI. Verify Add/Edit actually saves Name correctly with a real DB round-trip.
4. **Wire the authorization handler** (replace the empty stub), register policies, add permission claims at login. Deploy with **every existing user assigned the Admin role** as a safety net (nobody loses access on cutover day) — you then reassign real roles at your own pace through the new Admin UI.
5. **Menu filtering** goes live, reading the new claims.
6. **New workflow pages**: Take/Give from Main Office (Mother Plant + Kunjir), Main Office confirm+route, Kiran direct production (mostly a menu/permission change, minimal new page since Kiran reuses existing PotProduction directly), Outlet pending-receipt/confirm/sell/internal-receive.
7. **Reassign real roles/areas** to real users (you do this via the Admin UI), narrowing everyone down from the temporary blanket Admin grant.
8. Only then, optionally: unlink Cutting Plan from the menu (step 5 already covers this by simply not granting it) — no separate step actually needed.

Each step is additive/reversible and independently testable against your TEST database before the next step, matching how you've verified every Phase 2/8/9 fix this session.

---

## G. Risks / Conflicts With Existing Phase 2–13 Work

- **`dbo.Bookings` vs `dbo.PottedPlantBookings`:** these are two separate systems today. Outlet's new "sales" workflow must clearly pick one — I'm recommending Outlet sales use `PottedPlantBookings`/`PottedPlantStock` (the newer, ledger-backed Phase 9 system) and leave the old `dbo.Bookings` system exactly as-is, untouched, for whatever it's currently used for. **Flagging this explicitly rather than assuming** — if the old Bookings system is actually what Outlet is meant to keep using, that changes Section E's Outlet pages.
- **`Area.PolyhouseId` for Main Office rows:** Main Office isn't a growing area, so it may not have a natural Polyhouse. Open sub-decision: either point Main Office Area rows at a designated placeholder Polyhouse, or make `Area.PolyhouseId` nullable. Needs your call before Decision 1 is implemented.
- **`InternalTransfers.Status` default:** adding a NOT NULL column with a default is safe, but any report or query that does `SELECT *` from `InternalTransfers` and expects a fixed column count will need re-checking — a mechanical, low-risk sweep, not a design risk.
- **Temporary blanket Admin grant (Migration step 4):** necessary to avoid a lockout, but means there's a window where every current user technically has full permissions until you reassign them — worth doing steps 4→7 in one sitting rather than leaving it open-ended.
- **Area.Name default change:** changing `string Name` to `string Name = string.Empty` is a pure C# change, no schema impact, and doesn't affect the existing NOT NULL column in `dbo.Area` — no conflict.
- **No conflict identified** with Phase 2 (MotherPlant), Phase 8 (InternalTransfer design — actually *extended*, not replaced), Phase 9 (Booking reservation semantics — untouched), or `dbo.VendorPurchases` (not referenced anywhere in this plan).

---

**This is the point specified in your Section 21 instructions to stop.** No implementation has begun. Once you've reviewed A–G (and the one open sub-point flagged in Section G about whether "Main Office" rows need a real or nullable `PolyhouseId`, and the Bookings-vs-PottedPlantBookings question), tell me to proceed and I'll implement in the sequence above, starting with the schema-only step.
