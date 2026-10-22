# Workflow corrections 2026-10-19 (owner requirements of 2026-10-12)

Branch `restructure/production-workflow-v2`. Not committed, not deployed. One migration, `2026-10-19_Cavity70.sql`: applied to the
local PlantsIMS2_Test on 2026-10-12 (owner-approved), **not on live** (row 28 of `PRODUCTION_PREFLIGHT.md`).

## 1. Mother Plant & Cutting > Sowing Approvals

- New page `/Production/MotherPlant/SowingApprovals` (menu: Mother Plant & Cutting, right after Ready Stock). Permission
  `MotherPlant.View`; Area scope = the regular Area rule, always enforced.
- Lists the tray batches transplanted in the user's Mother Plant Areas (`SeedSowings.SourceType = 'Transplant'`, Area type
  `MotherPlant`) that wait for approval: batch, transplant code, variety, source Ready Stock batch (lineage link), Area,
  cavity, trays, plants, transplant date, Ready Date, status, who approves, and the action.
- **Approve** opens the existing `ReadyConfirmation/Confirm` page (`?from=mp` only chooses where to return). The approval
  transaction, the Area Supervisor rule, the Ready Date rule and the one Ready Stock posting are unchanged
  (`ReadyConfirmationRepository.ConfirmAsync` + `TR_ReadyConfirmations_*`). No second ledger, no second posting.

## 2. Mother Plant Area Ready Stock actions

- Both actions are always shown on every ready tray batch; when one is not available the button is disabled and the reason
  is written under it (`Services/AreaReadyStockActionRules.cs`, display only -- the server re-checks everything).
- **Send to Outlet** = the existing `ReadyStock/Move` (whole-batch `InternalTransfers` 'ReadyStock', replay token, row lock).
  Outlet preselected when opened from this page (Main Office stays allowed, as before). The page now shows the source batch,
  plants AND trays, and the reason when not eligible. **Unchanged business rule:** a batch moves only as a whole, before any
  of it is reserved, sold, dispatched or in transit. Sending part of a batch that already has sales would need a
  batch-splitting feature -- a separate business decision.
- **Sell to customer** = the existing `MotherPlant/SellTrays` (`DirectTraySaleAsync`, `SeedlingDispatches.SubmissionToken`).
  No change to its logic; the page now shows "N complete trays" next to available plants.
- Stock received for Pot Production: neither action ("potted, not sent or sold as trays").

## 3. Sowing Approvals visibility

Finding: the approval LIST was never filtered by the user's id -- it is filtered by Area scope. The only per-user filter was
the home page counter "Sowings waiting for your approval" (`SupervisorId == me`), which never counted transplanted or Bed
Sowing batches (they have no assigned supervisor). Assigned-supervisor checks on the approval itself are intentional and kept.

- One shared read, `Data/SowingApprovalQueueRepository.cs`, used by both approval pages and the home page.
- **Visible** = every pending batch in the user's Area / workflow scope (`SowingApprovalRules.IsVisible`): seedling Area scope
  for Seed / Cutting / Bed Sowing batches; the strict Area rule for Mother Plant transplant batches.
- **May approve** = `SowingApprovalRules.Evaluate`, the same checks as the approval in the same order: `ReadyStock.Confirm`
  permission, `DirectSowingRules.CanApprove` (assigned supervisor; Bed Sowing = the designated approver Maya Shinde only;
  never one's own seed sowing), Area Supervisor of the batch's own Area for transplant batches (or System Administrator), and
  the Ready Date. Seeing a batch never grants approving it; rows show who approves and why the user cannot.
- The main page is grouped (and filterable) by workflow: Direct Seed and Cutting sowings / Bed Sowing tray batches /
  Mother Plant Area transplant batches.
- Home: "Sowings waiting for your approval" = batches THIS user may approve now; new "Sowings pending approval in your Areas".

## 4. 70 Cavity

- `DirectSowingRules.CavityTypes` (the one closed list) gains `70 Cavity` (between 42 and 102). Every calculation already
  derives from it: complete trays = floor(qty / 70); Direct / Cutting sowing physical trays = CEILING (existing rule: the
  left-over seeds start a tray, and are wasted automatically); transplant / bed transplant = complete trays only, remainder
  to waste with a reason; Ready Stock trays, direct-sale trays, Outlet pages, filters and reports all use `CavityCount`.
- The database repeats the list in 10 CHECK constraints and 3 triggers, so `Database/Migrations/2026-10-19_Cavity70.sql`
  (+ `_Rollback.sql`) is required. Until it is applied: forms do not offer 70 (`WorkflowCorrectionsSchema.SelectableCavitiesAsync`)
  and Tray Stock add / adjust / transfer refuse 70 with a clear message, so nothing can be sown or transplanted into 70.
- Assumption to confirm: 70-cavity trays use 1.5 kg Cocopeat / Pitmos per tray, like every other cavity (editable in
  Material Stock > Settings).

## Tests

- `tests/.../WorkflowCorrections1019RulesTests.cs` (pure rules) and `WorkflowCorrections1019E2ETests.cs` (scratch copies
  only; real users' claims; real page models and repositories).
- Browser-test logins: `Database/ZZTEST_Wf1019Users.sql` (scratch copies only).

## 2026-10-20 additions

**Mother Plant transplant batches approved on any date.** `BedSowingRules.ValidateApprovalReadyDate(..., transplantAnyDate)`
(used by the approval transaction, the Confirm page and the lists) lets an Area Supervisor approve a `Transplant` batch before,
on or after its Ready Date once `2026-10-20_TransplantApprovalAnyDate.sql` is applied (probe `TransplantAnyDateProbeSql`);
the database trigger `TR_ReadyConfirmations_BedReadyDate` repeats it. Unchanged: who approves (Area Supervisor of the batch's
Area or System Administrator), the Ready Date itself, "must have a Ready Date", the row lock / one Ready Stock posting, and
every other workflow (Bed Sowing: Maya only, any date via 2026-10-17; Seed / Cutting: no date rule). The migration is
applied to the local PlantsIMS2_Test (2026-10-12, owner-approved) and **not on live** (preflight row 29).

**"Approved by me".** Mother Plant & Cutting > Sowing Approvals now has two tabs: *Approved by me* (default) and *Waiting
for approval*. The first lists only `ReadyConfirmations` rows whose `ApprovedById` is the signed-in user (from the
server-side principal; the page binds only the tab), for `Transplant` batches of Mother Plant Areas, wherever their Ready
Stock is now, with the real approval time (`ConfirmationDate`) and status. The second is the unchanged shared queue (not
filtered by user). No schema change was needed: the approver and approval time were already recorded per approval.
