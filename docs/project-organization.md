# Project organization: tags, folders and activity by tag

Tags and one level of folders group a workspace's projects; a read-only view shows runs and credits per tag. Customer pages: [`user-guide/projects/organizing-projects.md`](user-guide/projects/organizing-projects.md) and [`user-guide/projects/activity-by-tag.md`](user-guide/projects/activity-by-tag.md).

## Data model

| Table | Columns and rules |
|---|---|
| `project_tags` | Id, OrganizationId, Name (1 to 40, trimmed, no control characters), Color, CreatedAt. Unique per organization on `lower(name)`. At most 200 per organization. Named `Tenant` query filter. |
| `project_tag_links` | (ProjectId, TagId) composite key, cascade on both sides. At most 20 tags per project. No query filter; every write goes through the project. |
| `project_folders` | Id, OrganizationId, Name (1 to 60), ParentFolderId (always NULL in v1), CreatedAt. Unique on (organization, `lower(name)`) where the parent is NULL. At most 500 per organization. `Tenant` filter. |
| `projects.folder_id` | Nullable FK, `ON DELETE SET NULL`. `Project.FolderId` is on the shared model; the engine maps projects read-only. |

Colours are a closed set: gray (default), red, orange, yellow, green, teal, blue, indigo, violet, pink. `project_tags` and `project_folders` are on the `TenantBoundaryInventoryTests` list.

Deleting a tag removes its links only. Deleting a folder sets `folder_id` to NULL. Deleting a project cascades its links. Transferring a project to another organization drops its links and folder.

## Tenancy

Every write checks that the tag, folder and project belong to the caller's active organization. A foreign or missing id answers the same `404`.

## Routes (`api/v1`)

| Route | Notes |
|---|---|
| `GET /project-tags` | Needs View. Rows: `id, name, color, projectCount, createdAt`. |
| `POST /project-tags`, `PATCH`/`DELETE /project-tags/{id}` | Needs org Edit. Body `{name, color}`. `409 TAG_NAME_TAKEN` (case-insensitive), `409 TAG_LIMIT_REACHED`. |
| `GET /project-folders` | Needs View. Rows: `id, name, projectCount, createdAt`. |
| `POST /project-folders`, `PATCH`/`DELETE /project-folders/{id}` | Needs org Edit. Body `{name}`. `409 FOLDER_NAME_TAKEN`, `409 FOLDER_LIMIT_REACHED`. |
| `PUT /projects/{id}/organization` | Edit on the project. Body `{tagIds, folderId \| null}`; replaces the tag set. `409 PROJECT_TAG_LIMIT_REACHED` over 20. |
| `POST /projects/organize` | Org Edit. Body `{projectIds (1..100), addTagIds, removeTagIds, folderId?, clearFolder?}`. One save, all or nothing, `204`. `400 PROJECT_IDS_INVALID`, `400 FOLDER_CHOICE_INVALID` (folder and clear together), `404` on any foreign id, `409 PROJECT_TAG_LIMIT_REACHED`. |
| `GET /projects` | Optional `tagId` (repeatable, AND), `folderId`, `unfiled=true`. |

`ProjectResponse` carries `folderId` and `tags` (`id, name, color`). `CreateProjectRequest` accepts optional `tagIds` and `folderId`.

## ETag

`GET /projects` and `GET /projects/{id}` use `ConditionalGet`. Every organization write bumps `Project.UpdatedAt` of each affected project in the same save, so the ETag changes when tags, folders or links change. Renaming or deleting a tag or folder bumps `UpdatedAt` of its projects in the same transaction as the change itself (a 409 leaves it untouched). The `tagId` filter on `GET /projects` accepts at most 20 ids (otherwise 400 `TOO_MANY_TAG_FILTERS`). A caller without project View (the Billing role) sees aggregates only on the tag detail view. Tag and folder names reject invisible Unicode format characters (bidi overrides, zero-width).

## Activity by tag

Served by `ProjectTagActivityController` and `ProjectTagActivityService` (separate from `BillingController`). Any member with an organization role can read it; it never returns `costUsd`.

- `GET /project-tags/activity?days=30` returns the window, `billingEnabled`, `workspace`, one row per tag (`projects, runs, passed, failed, credits`) and `untagged`.
- `GET /project-tags/{id}/activity?days=30` returns the tag's totals, per-project runs and credits, and the 50 newest runs. An unknown or foreign tag is `404`.
- The web page offers 7, 30 and 90 days; the API clamps `days` through `ProjectTagActivityService.ClampDays` (1 to 365, default 30).
- Runs are `workflow_executions` of the organization whose `StartedAt` falls in the window (runs without `StartedAt` are not counted). Credits are the sum of `usage_events.credits_millis` for the project (and per execution), divided by 1000. With billing off, `credits` is null and runs still show.
- A project with several tags counts under each, so tag rows do not add up to `workspace`; the response carries `workspace` and `untagged` so the page can say so.

## Assistant

No new tools. `ListProjects` takes optional `tag` and `folder` names (case-insensitive) and its rows carry `tags` (names) and `folder` (name). `ProposeCreateProject` and `ProposeUpdateProject` (and their Apply) take optional `tags` (names) and `folder` (a name). Missing tags and folders are created in the caller's workspace, at most 5 new tags per call. On update, an omitted (null) `tags` or `folder` leaves the value unchanged; `[]` removes all tags and `""` clears the folder. The approval card says what will change, including "will create tag X". Details and the tool list: [`platform-assistant-mcp.md`](platform-assistant-mcp.md).

## Web

Components are in `web/components/projects` (rail, chips, `CardMenu`, `ProjectForm`, `ManageOrganizationModal`, `BulkBar`) and `web/components/tags` (the page at `/app/tags` and its detail). Dragging a card onto a rail folder uses native HTML5 drag; the card menu is always present and is the keyboard and phone path. Strings are in `projects.json` (`organization`) and `tags.json`, English and Italian.
