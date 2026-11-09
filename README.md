# LeadsFinder

This is the demo branch. Business discovery and enrichment use generated sample data; sales activity and field reviews persist in the local database. The street map loads OpenStreetMap tiles over the internet. Existing business-provider keys and PostgreSQL settings are unused by the API.

## Run locally

Install .NET 10 SDK and Node.js 22.12+ (or 20.19+), then run from the repository root:

```powershell
.\run.cmd
```

Open [LeadsFinder](http://127.0.0.1:5173/). Create an account on the sign-in page; there are no preconfigured credentials. The launcher stays attached to your terminal and shows API and dashboard logs. Press Ctrl+C or close the terminal to stop both services and their child processes. This also works in the VS Code / Antigravity integrated terminal. To run each component in its own terminal:

```powershell
.\run.cmd -SetupOnly
.\run.cmd -Component Api
.\run.cmd -Component Dashboard
```

The API listens on port 5088 and Vite on 5173. Docker, PostgreSQL, API keys, the scraper, and a separate worker are unnecessary. The old worker entry point is disabled on this branch.

## Administration and account access

Provision the administrator once by setting these environment variables **before starting the API**. If the app is already running, restart the API with these values:

```powershell
$env:Demo__AdminEmail = 'your-admin@example.com'
$env:Demo__AdminPassword = '<choose a unique password of 8–128 characters>'
$env:Demo__AdminName = 'Administrator'
.\run.cmd
```

Sign in with those credentials and open **Administration** in the sidebar. There is no default admin password, and public registrations always create ordinary users. After provisioning, the account and password hash persist in the data directory; remove the bootstrap password from the environment or configure it as a host secret. For an existing ordinary account, the configured password must match its current password before it can become an administrator. An existing administrator's password is never reset on restart.

Administrators can search the user list, approve or revoke approval, block or unblock accounts, and set separate clean-record and quarantine-record limits. Both limits default to 1,000 per account and can be set to zero. Quarantine limits count observations, including duplicates awaiting cleanup; clean limits count unique places. Pending jobs reserve capacity so concurrent requests cannot bypass a limit. Lowering a limit preserves existing rows and prevents further additions; running enrichment pauses when it reaches the new limit. A fetch stopped by a lowered quarantine limit keeps its partial results; create a new search after freeing or increasing capacity. Administrator accounts cannot be blocked or have approval revoked through these controls.

Unapproved ordinary accounts get access for **72 hours from registration**. At that deadline they are locked, including existing sessions, until approved. At **10 days from registration**, an unapproved account is deleted along with its credentials, jobs, and personal record history. Shared global hotel records remain available to other users. Approved users and administrators are exempt from both deadlines. Revoking approval uses the original registration date, so an account already over ten days old is deleted immediately; the admin page warns before that action. Blocked accounts remain inaccessible even if approved.

The API applies these rules on requests and in its background worker, including startup after downtime. Jobs pause while an account is locked or blocked and resume when access is restored. Existing accounts without a recorded registration date receive a fresh trial on first upgrade; their existing records are retained.

The **Data source** tab lets an administrator choose a location and radius on the NCR map and add 1–1,000,000 new hotel source rows per batch, with capacity for 10,000,000 additional rows. Exactly two of address, phone, email, and website start empty. Batches are global, persistent, and immediately discoverable through normal searches; generating source rows does not place them in any user's quarantine. Batch recipes are stored without materializing every row. The API accepts valid worldwide coordinates; the current map UI is scoped to Delhi NCR.

## Workflow

- **Dashboard:** uncontacted leads, contacted leads, possible sales, DND totals, the next contacts, and recent searches.
- **Discover:** select an NCR locality or click/pan/zoom the street map, set the radius/type, and fetch 1–200 sample observations.
- **Jobs:** each fetch saves observations in quarantine. Open a job and run enrichment to merge duplicate observations, fill missing fields, and move its places to clean records. Progress is saved.
- **Quarantine:** inspect pending observations across jobs, filter by job, search, or add a place manually.
- **Clean records:** inspect completed places with filled address, phone, email, and website.
- **Sales:** filter contact-bearing places by sales stage, contact channel, source job, enrichment status, and DND. Open Details to save contacted-on-email/phone/WhatsApp flags, notes, and DND. Stages are New, In progress, Possible sale, Won, and Lost. Opening a contact app does not automatically mark a conversation as completed.

The account avatar appears only in the sidebar. Tables support direct page-number entry and first/last page navigation; record lists also allow 20, 50, 100, or 200 rows per page. Out-of-range API pages clamp to the last existing page.

Call, WhatsApp, email, and website links work for usable manually supplied contacts. Generated `.example` contacts and non-dialable sample numbers have disabled outreach controls. DND disables contact actions and is excluded from the sales queue by default. Account sales activity is private, while reports on shared business values are visible as anonymous counts.

In Details, each phone, email, website, and address can receive a personal Low/Medium/High confidence rating. Reports persist the exact field value and optional reason. One user gets one report per business/field/value, with a withdrawal option. One report shows a warning; three distinct reporters produce a Low community-confidence signal. That signal is a review heuristic, not a verified accuracy percentage. Changing a value does not transfer its old reports or ratings.

**Export for Microsoft Sales** downloads a UTF-8 CSV for Dynamics 365 Sales, covering every matching page and one row per business (up to 100,000 businesses per file). DND and sample data require explicit export choices. Standard contact and address fields have separate columns; sales stage, contact-channel flags, and notes are retained in Description. DND maps to the email and phone contact restriction columns. For organization-only prospects, the company name supplies the required Last Name; replace it with a contact surname when known. Review column and option mappings in the Dynamics import wizard, especially if your organization has custom required fields. This is a file export, not a direct CRM synchronization.

Discovery saves batches of five observations and enrichment processes four observations per worker tick. The default tick is 750 ms so progress can be followed in the demo; it does not represent actual provider latency. Tests use a faster tick without changing the batching logic.

Each account owns its jobs and record history. Searches skip businesses the current user has fetched, including those in other running jobs. Other accounts can discover those same businesses from the shared catalog. Canonical business values are stored globally; quarantine/clean state and job associations belong to the user. Enriching a place does not change another user's workflow status.

Quarantine may contain multiple observations of the same place until enrichment. Dashboard record totals count unique places, while job counts describe observations and duplicates removed. Search results stay inside the selected radius and can return fewer records if that area/type has no unseen places left.

## Data and scale

The source contains **1,000,000 deterministic observations**, including 10% duplicate observations, spread over Delhi, Gurugram, Noida, Greater Noida, Ghaziabad, Faridabad, and surrounding NCR coordinates. Fields are deliberately incomplete. A separate deterministic local lookup supplies missing values during enrichment; manually created places use the same local filling workflow. Generated contact domains are reserved `.example` addresses and phone numbers use non-dialable values.

The catalog is virtual: spatial grid candidates are generated as needed, without allocating or saving a million rows. Only accounts, fetched global businesses, user histories, and jobs are persisted. Fixed grid layers preserve existing identifiers and coordinates when increasing the source size.

Configuration (environment variables or ASP.NET command-line settings):

| Setting | Default | Purpose |
| --- | --- | --- |
| `Demo__CatalogSize` | `1000000` | Source observations, supported from 1,000 to 10,000,000. |
| `Demo__DataPath` | `App_Data` | Durable database and cookie key directory, relative to API content root or absolute. |
| `Demo__TickMilliseconds` | `750` | Demo processing interval, from 10 to 5,000 ms. |
| `Demo__AdminEmail` | Unset | Email of the administrator to provision or promote. |
| `Demo__AdminPassword` | Unset | Initial password, or existing user's current password for promotion. |
| `Demo__AdminName` | `Administrator` | Display name for a newly provisioned administrator. |
| `ASPNETCORE_URLS` | Launch profile: `http://127.0.0.1:5088` | HTTP listener for deployed host. |

For example, set `$env:Demo__CatalogSize = '10000000'` before starting the API. Never commit the data directory. Back up the entire directory, including cookie protection keys, to preserve accounts, sessions, and histories.

The file-backed database is designed for a **single API instance with a persistent writable disk**. Writes are serialized and atomically replaced. Run one replica; shared-volume multi-replica hosting and ephemeral/serverless disks are unsuitable. The generated source scales to ten million observations; the persisted fetched subset is loaded in memory and rewritten on updates.

## Publish

Create a single server deployment containing the built UI:

```powershell
.\scripts\Publish-LeadsFinder.ps1
Set-Location artifacts/publish
$env:Demo__DataPath = 'C:\leadsfinder-data'
$env:ASPNETCORE_URLS = 'http://0.0.0.0:8080'
dotnet LeadsFinder.Api.dll
```

The API serves the built dashboard and its deep links from `wwwroot`; deploy both on the same origin. Terminate HTTPS at your host or reverse proxy and keep the HTTP listener private. Forward the original Host and `X-Forwarded-Proto` headers. Forwarded headers trust loopback proxies by default; configure your host's trusted proxy addresses when the proxy is on another machine or container. Preserve the data volume across deploys. This repository also includes a multistage Docker build:

```powershell
docker build -t leadsfinder .
docker run --rm -p 8080:8080 -v leadsfinder-data:/data leadsfinder
```

No deployment or publishing to an external host is performed automatically. The app identifies itself as a demo workspace.

## Verify

```powershell
dotnet build src/LeadsFinder/LeadsFinder.slnx
dotnet test src/LeadsFinder/LeadsFinder.Matching.Tests --no-build
npm --prefix src/LeadsFinder/LeadsFinder.Dashboard run build
node scripts/test-lead-table.mjs
node scripts/test-demo.mjs
node scripts/test-admin.mjs
# After publishing, check the packaged UI and deep links too:
node scripts/test-demo.mjs --published
```

The HTTP checks start isolated APIs with their own generated accounts and data under ignored `artifacts/`. They verify authentication, ownership, the 200-row cap, geography, shared catalog reuse, unseen history, enrichment, filtering, sales, restart persistence, administrator permissions, blocking/approval, quotas, source generation, and account retention. Deterministic clock tests cover the exact three-day and ten-day boundaries. These checks never use the normal application database. Add `--release` to the Node commands when testing a Release build.
