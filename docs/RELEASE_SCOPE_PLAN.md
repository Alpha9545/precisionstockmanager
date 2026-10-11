# Release scope plan — next production release WITHOUT Vehicle Tracking

Prepared 2026-10-10. **Nothing was committed, staged, reverted or deployed while preparing it.**

## 1. The problem

Branch `restructure/production-workflow-v2` has a large uncommitted working tree. It mixes:

* the work intended for release: Labour, Maintenance, Store, Suggestions, Material Stock, Booking orders, Ready Stock transfers and transplanting, Tray adjustment and transfers, receipt dates, Daily Operations, Dashboard and accessibility;
* the paused **Vehicle Tracking** module, which must **not** reach production;
* build output and deploy archives (`publish-live/`, `*.zip`), which do not belong in source control.

Vehicle Tracking is self-contained except for **three shared files**, which also carry release work:

| Shared file | Vehicle Tracking part |
|---|---|
| `Program.cs` | VT services, Google route provider and memory cache, the driver-API rate limiter (`AddRateLimiter` / `UseRateLimiter`), the driver device-token authentication scheme and policy, `MapVehicleTrackingApiEndpoints()`, `using System.Threading.RateLimiting` |
| `Authorization/FeatureAuthorizationConventions.cs` | the 7 `/VehicleTracking/*` page rules |
| `Pages/Shared/_Layout.cshtml` | the Vehicle Tracking menu section |

`PlantStockManager.csproj` only excludes `mobile\**` from the web build. That change is harmless and stays.

No other non-Vehicle-Tracking source file references a Vehicle Tracking type (checked with grep).

## 2. The manifest

[release/RELEASE_MANIFEST_2026-10-10.txt](release/RELEASE_MANIFEST_2026-10-10.txt) classifies every changed or untracked file from `git status`:

| Group | Files | In the release? |
|---|---|---|
| RELEASE: application | 169 | yes |
| RELEASE: tests | 45 | yes |
| RELEASE: migrations | 26 | yes (Vehicle Tracking migrations excluded) |
| RELEASE: docs | 10 | yes |
| SHARED | 3 | yes, **minus their Vehicle Tracking blocks** |
| EXCLUDE: Vehicle Tracking (paused) | 92 | no — kept untouched in the working tree |
| EXCLUDE: build output / deploy archive | 147 | no |
| EXCLUDE: scratch-only test logins (`Database/ZZTEST_*`) | 4 | no |
| EXCLUDE: local tooling / notes (`.claude/launch.json`, `migration-headers.txt`) | 2 | no |

Files created after the manifest was made must be added before staging; at minimum, this plan and the `docs/release/` folder. Re-run the classification (step 1 below) to pick them up.

## 3. Proven on a copy

`docs/release/assemble_release.py <repo> <out> <manifest>` builds a **release candidate copy** outside the repository:

* It starts from `git archive HEAD`.
* It adds the release files.
* It writes the shared files without exactly the Vehicle Tracking blocks listed in the script. Each block must match once, otherwise it stops.

The copy (2026-10-10, built in `%TEMP%\psmrc`):

* builds in Release;
* passes its full suite on a fresh scratch with migrations #12–#15: **2094 tests, 2091 passed, 0 failed, 3 skipped**. The skips are the manual WhatsApp sender and 2 booking data-condition tests. The difference from the working tree's 2274 is the 180 Vehicle Tracking tests;
* when run in the browser:
  * `/VehicleTracking/Trips`, `/Vehicles` and `/Devices` and `/api/vehicle-tracking/v1/auth/login` (GET and POST) answer **404**, even for an administrator;
  * the menu has no Vehicle Tracking entry;
  * Tray Transfers, Mother Plant Transfers and the Dashboard load (200).

## 4. Recommended way to isolate the release (needs the owner's go-ahead at each step)

Option A keeps every working-tree file exactly where it is and puts only the release scope into commits:

1. **Refresh the manifest.**
   * Run `git status --porcelain --untracked-files=all`.
   * Re-classify with the same rules (see the manifest headings).
   * Review the diff against the 2026-10-10 manifest.
2. **Release branch.** Create it from the current HEAD, e.g. `release/2026-10-production`.
   * This creates a ref only; nothing in the working tree changes.
   * Do it when the owner approves committing.
3. **Stage only the release files.** Use `git add -- <path>` for each RELEASE path in the manifest.
4. **Stage the shared files without their Vehicle Tracking blocks.** Use hunk-level staging (`git add -p <file>`) and decline every hunk that belongs to Vehicle Tracking.
   * `Program.cs` has hunks that mix Vehicle Tracking with Material Stock / Tray Transfers lines. Split them with `s`, or edit them with `e`.
   * Check the result: the staged blob must equal the release-candidate copy of that file:
     ```bash
     git diff --cached -- Program.cs
     ```
5. **Verify the staged tree is the candidate:**
   * `git diff --cached --stat` must list exactly the manifest's RELEASE + SHARED paths;
   * export the staged tree with `git checkout-index -a --prefix=<empty folder>/` into an empty folder (this reads the index and does not touch the working tree), build it, and compare it with the release-candidate copy;
   * `git stash` is not used at any point.
6. **Commit the release** (owner approval), push the release branch, and open a PR.
   * The Vehicle Tracking files stay untracked or unstaged in the working tree.
   * Their wiring hunks in the three shared files remain as unstaged changes. Nothing is lost.
7. **Build the publish output from the release branch's committed tree**, never from the working tree, so no Vehicle Tracking file can enter `publish-live/`.

Option B (not recommended for this release) is a configuration switch (`VehicleTracking:Enabled=false`) that skips the Vehicle Tracking wiring at start-up. It would change shared start-up code, ship unfinished code that is merely switched off, and need its own review. Option A ships none of it.

## 5. Must not happen

* Do not `git reset`, `git checkout -- <file>`, `git stash` or `git clean` the working tree. The Vehicle Tracking work and the uncommitted release work both live only there.
* Do not commit `publish-live/`, the zip archives, `.claude/launch.json` or the `ZZTEST_*` scripts.
* Do not apply `2026-10-08_VehicleTracking_*.sql` to production.
* Do not deploy a build made from the working tree.

## 6. Production database order for this release

See [PRODUCTION_PREFLIGHT.md](PRODUCTION_PREFLIGHT.md) §3b.

1. #1–#11 as listed there.
2. #12 `2026-10-10_TrayTransfers`.
3. #13 `2026-10-11_TrayAccessByLocation`.
4. #14 `2026-10-11_RolePermissionAlignment`, with explicit approval (it removes Booking Executive `Dispatch.*`).
5. #15 `2026-10-11_ReadyStockReceiptDate`.

Do not run any Vehicle Tracking migration.
