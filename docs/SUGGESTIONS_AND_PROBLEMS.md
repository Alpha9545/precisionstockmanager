# Suggestions & Problems

Internal business board: an employee posts a **Problem** or a **Suggestion** for an Area (and a
Polyhouse of that Area), optionally with one photo, and colleagues who may see that Area discuss it
with **comments and replies**. There are **no likes, dislikes or reactions** of any kind.

Migrations: `Database/Migrations/2026-10-07_SuggestionsProblems.sql` (undo: `…_Rollback.sql`, refuses
while any post exists), then `2026-10-07_SuggestionsGlobalAccess.sql` (global access; undo: `…_Rollback.sql`).

## Pages

| Route | Page rule | Purpose |
|---|---|---|
| `/Suggestions/Index` | `@authenticated` (any signed-in user) | List (20 per page, newest first), counts, filters |
| `/Suggestions/Create` | `@authenticated` | Create a post (camera / gallery photo) |
| `/Suggestions/Details?id=` | `@authenticated` | Post, photo, comments / replies, administrator actions, history |
| `/Suggestions/Details?id=&handler=Photo` | (same) | The post's photo, only for users who may see the post |

Menu: **Suggestions & Problems → All Posts / Create Post**, shown to every signed-in user.

## Access: a GLOBAL module

Suggestions & Problems is **not restricted by Area**. Every signed-in user — any role, with or without
an Area assignment (Electrician, Welder and Plumber included), even a user with no role — holds the
module's global grants, defined in code in `SuggestionAuthorization.GlobalPermissions`:

```
Authenticated user
    ├── Suggestion.View      active and permanent posts of EVERY Area
    ├── Suggestion.Create    a post for ANY active Area (Area + Polyhouse still validated)
    └── Suggestion.Comment   comments and replies on visible posts
Administrator (Suggestion.Manage — full access; granted to no role)
    └── expired / deleted posts, make permanent, status, delete, moderate comments
```

* The three pages use the existing `@authenticated` page rule — they still require sign-in
  (`[Authorize]`); anonymous requests are refused.
* View / Create / Comment are **not** RolePermissions rows: `2026-10-07_SuggestionsGlobalAccess.sql`
  removed the earlier per-role grants and codes. `Suggestion.Manage` remains a normal permission.
* `AreaAccessService` is not used by this module. **Area authorization of every other module
  (Labour, Maintenance, Store, Mother Plant, Tray Stock, Empty Pot, Booking, …) is unchanged.**
* Every handler still re-loads the post / comment from the database (no IDOR through `id`,
  `CommentId`, `ParentCommentId`); administrator actions are refused on the server for others.
* Permanent posts are visible to everyone. Expired and deleted posts remain administrator views;
  everyone can filter Active / Permanent, type, status, Area and Polyhouse.

## Location rules

* Every post has an active Area (any Area — the module is global); an unknown / inactive Area is refused.
* The Polyhouse must belong to that Area. It is **required for the MainOffice Area** (the Daily
  Labour convention, `DailyLabourRules.RequiresPolyhouse`), optional for other Areas with Polyhouses,
  and not shown for Areas without Polyhouses.
* The database trigger `TR_SuggestionPosts_Rules` repeats these rules (51713 / 51834 / 51835).

## Lifetime, Permanent and Status — three separate things

* **Lifetime**: `ExpiresAt = DATEADD(HOUR, 48, CreatedAt)` is a **persisted computed column** — the
  browser and the page never supply it. A post is expired when `IsPermanent = 0 AND ExpiresAt <= SYSUTCDATETIME()`,
  evaluated by the database clock whenever posts are read. No background job. Times are stored in
  UTC and shown in India time (`AppClock`, Asia/Kolkata).
  Example: created 07-10-2026 10:30 AM → expires 09-10-2026 10:30 AM.
* **Permanent**: administrator action **⭐ Make Permanent** (`IsPermanent`, who, when). A permanent
  post never expires; `CreatedAt` is never changed; it cannot be made non-permanent again. Making
  it permanent twice is refused (“already permanent”).
* **Status** (business state): Open → In Discussion → In Progress → Resolved / Closed. The first
  comment by someone other than the poster moves an Open post to In Discussion automatically; the
  administrator sets any status. An expired post can still be “In Progress”.

## Delete (soft)

Administrator only, with a required reason (`IsDeleted`, `DeletedById`, `DeletedAt`, `DeleteReason`).
The row, its comments and photo stay for audit; users no longer see it; administrators find it with
**Show = Deleted**. A deleted post cannot be restored (none is offered; the trigger refuses it).

## Comments and replies

* `dbo.SuggestionComments` with `ParentCommentId`. Replies are **one level**: replying to a reply
  attaches to the same top-level comment.
* A reply's parent must be a comment of the **same post** — composite foreign key
  `(PostId, ParentCommentId) → (PostId, Id)` plus trigger 51840; the page refuses other ids first.
* 1–1000 characters, trimmed; shown HTML-encoded (Razor; no `Html.Raw`), line breaks kept.
* The author edits own comment **within 30 minutes** (previous text kept in the history) and can
  delete own comment; the administrator deletes any comment. Deletes are soft
  (“This comment was deleted.”); replies under it remain.

## Photos (`Services/SuggestionPhotoStore.cs`)

Exactly the Maintenance photo rules (`MaintenancePhotoStore`):

* One optional photo per post: **📷 Take Photo** (`capture="environment"`, opens the phone camera)
  or **🖼 Choose from Gallery**; preview before submitting. A camera photo over 5 MB is scaled down
  in the browser first; the server checks every upload anyway.
* Type decided from the file's own bytes (JPEG / PNG / WebP only), at most 5 MB.
* Stored under a new GUID name; the original name is display text only.
* Folder **outside wwwroot / publish**: setting `SuggestionStorage:PhotoRoot`; empty =
  `<LocalApplicationData>/PlantStockManager/SuggestionPhotos`. **Production should set a persistent
  folder and back it up with the database.** The folder in use is logged at start-up.
* Served only through the Details `Photo` handler after the visibility check, with the stored
  content type, `X-Content-Type-Options: nosniff`, `Cache-Control: no-store` (a shared office browser never shows a
  cached photo to the next user) and a sandbox CSP;
  only names this store created resolve, and only inside its folder (no path traversal).
* If saving the post fails after the file was written, the file is removed (`SaveThenAsync`).

## Database

| Table | Notes |
|---|---|
| `SuggestionPostTypes` | `Problem`, `Suggestion` — more types = more rows |
| `SuggestionPosts` | `PostNo` = `SP-` + 6 digits (computed); `ExpiresAt` computed; status / permanent / delete CHECKs |
| `SuggestionPostPhotos` | metadata only; one per post (`UQ PostId`); content type / size / file-name CHECKs |
| `SuggestionComments` | replies via `ParentCommentId`; soft delete |
| `SuggestionPostEvents` | insert-only history: Created, StatusChanged, MadePermanent, Deleted, CommentEdited, CommentDeleted |

Indexes: `IX_SuggestionPosts_Area_Created` (feed of a user's Areas), `IX_SuggestionPosts_Created`
(administrator feed), `IX_SuggestionPosts_Polyhouse`, `IX_SuggestionPosts_PostedBy`,
`IX_SuggestionComments_Post`, `IX_SuggestionPostEvents_Post`.

Triggers: `TR_SuggestionPosts_Rules` (location; new posts start Open / not permanent / not deleted /
not future-dated; type, location, author and creation time never change; no un-permanent /
un-delete), `TR_SuggestionComments_Rules` (one reply level; immutable post / parent / author / time;
a deleted comment is never changed), `TR_SuggestionPostEvents_Protect` (history never updated, nor
deleted while its post exists). An Area with posts cannot be deleted (`DeletionRules`).

## Double submission

Every form carries a `SubmissionTokenGuard` token (scope `SuggestionCreate` / `Suggestion:{id}`);
a second identical post is refused. Buttons are disabled after the first click.

## Tests

* `tests/PlantStockManager.Tests/SuggestionRulesTests.cs` — lifetime / permanent / status, text
  rules, photo validation (JPEG, PNG, WebP, invalid, oversize), photo folder, path traversal,
  orphan cleanup, authorization matrix, wiring, antiforgery, no reactions, migration content.
* `tests/PlantStockManager.Tests/SuggestionE2ETests.cs` — real pages against a scratch copy
  (`PSM_SCRATCH_CONNECTION=…PlantsIMS2_Scratch_*`): create with photo, validation, Area /
  Polyhouse tampering, unauthorized user, duplicate submission, automatic expiry, permanent,
  delete, status, comments, replies, tampered parents, edit window, moderation, photo access,
  Area-scoped list, paging, permissions.
