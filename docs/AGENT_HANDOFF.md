# LeadsFinder handoff — 9 October 2026

## 19 October update

The remainder of this file is a historical snapshot. The project is now a Git repository, and committed code includes Google Maps Scraper integration and a command-line enrichment pilot. The README describes current startup behavior, including automatic migrations for the bundled local development database. Google Places API remains unregistered.

The current working tree adds manually triggered duplicate checking against source observations, a PostGIS candidate search, explainable name/address/distance rules, immutable reports, and a dashboard comparison panel. An optional Amazon Bedrock Converse adapter uses a locally stored short-term API key and is disabled by default. It sends bounded pairs only when explicitly requested. No automatic merge, final lead table, admin decision flow, or correction flow has been added. See `DUPLICATE_MATCHING.md` for AWS configuration and limitations. Existing enrichment rows supply missing values for comparisons.

Local configuration now lives in the Git-ignored repository-root `.env`, with a tracked `.env.example` template. API, worker, enrichment pilot, and EF design-time tooling load it. Nonblank file values override appsettings/user-secrets; OS variables and CLI arguments take precedence. Existing user-secrets still work. Do not print or commit actual `.env` contents after the user adds keys. Restart .NET services after edits. Bedrock is still disabled pending the user's model and key configuration.

On this PC, the existing local database had one completed job, 20 source observations, and 20 enrichment results at initial inspection; the user has since run additional searches. The OSM extract is present. Docker Desktop and the application were started in this chat. Dashboard tooling uses the bundled Node runtime because system Node 18 is too old. The Google scraper submodule is now initialized, and the CLI and Chromium are installed. Windows Chromium crashed with the pinned dependency's single-process/no-zygote flags; use `scripts/Setup-GoogleMapsScraper.ps1` to reproduce the compatibility build without editing the submodule or shared Go module cache. The worker now checks structured CLI failures even on exit code zero and logs raw-row and request counts. Recheck process/data state before assuming these remain current.

Verified Google job `a5861549-ad52-4507-8939-25fe95e5722f` completed for Hotels within 1 km of the user's latest center (28.678194, 77.322129), saving four observations: all had addresses, two had phones and websites. Earlier missing-executable and pre-fix empty test jobs were retained as historical rows. The final solution build passed with zero warnings/errors, and 36 tests passed, including temporary-table PostGIS tests and scraper failure diagnostics. No Bedrock requests were made during scraper troubleshooting.

Search jobs now open dedicated dashboard pages at `/jobs/{id}`, including immediately after creation. The page shows status, settings/timings, saved observation rows with pagination and raw JSON, and the worker's recorded failure reason. Active jobs poll every five seconds; rows are committed by the worker at completion. Duplicate checks now live on this page. A back link returns to `/#jobs`. Vite supports direct navigation; a production static host needs an `index.html` fallback for `/jobs/*`. Dashboard build passed, and browser checks covered completed/failed/missing jobs, direct refresh, and back navigation.

Saved rows now use `rapid-grid-react@0.1.0` and its `rapid-grid` dependency. `SavedRowsGrid.tsx` renders read-only cells with stable columns, fixed row heights, and a separate raw JSON inspector; no React roots are mounted in recycled cells. API record pages request 100 rows. Browser verification with a 20-row job confirmed only seven rows mounted at a time and recycling on scroll through row 20. Dashboard build passed.

This file is context for a new chat. Inspect the code and current database before acting: the user may have run more searches since this snapshot. `MVP_REQUIREMENTS.md` describes the intended product, but its implementation-status sections are old. A new request from the user takes precedence over suggestions in either document.

## What works now

The local app has a working **React map UI → ASP.NET Core API → PostgreSQL job queue → .NET fetch worker → quarantine** path. It fetches real OpenStreetMap lodging data from the Geofabrik Northern Zone India PBF extract. The user chooses a point and radius on a Leaflet map, a lodging type, desired lead fields, requested row count, shortage policy, and schedule. Jobs and raw source observations are visible in the dashboard at `http://127.0.0.1:5173/`.

- Workspace: `C:\Users\akshay.rajput\source\LeadsFinder`; solution: `src\LeadsFinder\LeadsFinder.slnx`. This workspace is **not a Git repository** as of handoff.
- Projects: `LeadsFinder.Api`, `LeadsFinder.Data`, `LeadsFinder.Worker`, and `LeadsFinder.Dashboard`. `README.md` has the local startup commands. Docker Compose runs PostGIS 16. EF migrations are applied explicitly, not on app startup.
- API routes: `POST /api/jobs`, `GET /api/jobs`, `GET /api/jobs/{id}`, `GET /api/jobs/{id}/records`, and `GET /api/config`. The API listens on port 5088; Vite proxies `/api` from port 5173.
- Current source strategy: `GeofabrikSearchStrategy` is the only registered strategy. Two demo strategy classes remain in the Worker project but are not registered. The PBF cache is `LeadsFinder.Worker/data/northern-zone-latest.osm.pbf`, ignored by Git. The cached file was about 223.7 MB on 9 October; parsing took roughly 52 seconds and found 5,863 lodging OSM objects across the extract. The worker holds the parsed catalog in memory until restart. See the strategy code for the exact OSM tourism tags and place-type mapping (`Hotels` currently includes `hotel` and `motel`).
- The map radius slider covers **100 m–10 km**; the editable metre input allows **100 m–50 km**. The backend validates the same 100 m–50 km bounds. A typed value beyond 10 km leaves the slider at its maximum while the map circle uses the typed value.
- A job requests **up to exactly N new observations**, if available. The earlier 20% buffer was removed at the user's request. The requested fields are recorded for later lead qualification; they do not filter OSM observations yet. `return_fewer` stays inside the selected circle. `expand_radius` looks for more matches up to 50 km.

## Repeat-search and database behavior

The strategy orders matching OSM objects by distance, excludes all `(strategy, source ID)` identities already in `source_records`, then takes the requested count. Repeating the same search advances to the next nearest unseen objects. An exhausted area yields a completed job with zero new rows; the dashboard explains this. A unique database index on `(Strategy, SourceId)` guards against duplicate inserts, and the worker takes a PostgreSQL transaction advisory lock while it selects and saves results so concurrent workers cannot claim the same objects. A separate `SearchJobId` index supports job record queries.

`Worker.cs` claims due jobs with `FOR UPDATE SKIP LOCKED`, a 15-minute lease, a 10-minute job deadline, and at most three claims. It fences completion by attempt count and unexpired lease. An ordinary strategy exception marks a job failed; there is no automatic retry of those failures. Search jobs and observations are committed transactionally. Raw OSM tags, source links, coordinates, and optional contact fields are retained in quarantine. Business-level duplicates across *different* OSM IDs are not resolved yet. The global uniqueness rule also prevents re-observing the same OSM ID after an extract refresh; revisit that data model if periodic refresh becomes a requirement.

Current migrations, in order: `InitialQuarantine`, `AddScheduledJobs`, `AddJobLease`, `AddSearchRequestDetails`, `UniqueSourceIdentity`, `IndexSourceRecordsByJob`. Preserve the existing local database and inspect data before any migration or cleanup. As of this handoff the API reported 14 jobs and 184 quarantined rows; these counts can change as the user searches.

## Paharganj investigation on 9 October

The user saw few *new* rows because earlier Delhi searches had already quarantined many nearby OSM objects. A read-only probe called the actual Geofabrik strategy with a target large enough to count every match in each tested circle, then compared its totals with stored rows:

| Center/radius used by user | Lodging in cached strategy | Hotel/motel subset | Already in quarantine at the time |
| --- | ---: | ---: | ---: |
| First pin, 400–440 m | 8 | 8 | 8 |
| Later pin, 1.426 km | 138 | 107 | 138 |
| Later pin, 2.156 km | 158 | 124 | 158 |
| Later pin, 5 km | 249 | 187 | 171 total; 133 hotel/motel |

The first pin and later pin were different centers. The latest 2.156 km search found only eight **new** objects because the rest were already saved; no unseen candidates remained inside that circle. At 5 km from the later pin, there were 54 unseen hotel/motel objects at the time. A read-only replay with the saved IDs and the current exact-count code returned **50, then 4, then 0** for three consecutive 50-result `Hotels` searches. The replay did **not** add database rows. The temporary probe project was removed afterward. These figures describe the cached extract and strategy tags, not every operating hotel in Paharganj; a larger circle or another source may reveal more.

## Verification and local operation

After the latest exact-count change, `dotnet build LeadsFinder.slnx --no-restore -p:NuGetAudit=false -m:1 -nr:false -v:minimal` and `npm run build` both passed. The API and dashboard returned HTTP 200. Prior end-to-end repeat searches in Delhi produced disjoint sets of source IDs, with distances moving outward. The worker was restarted after the diagnostic probe. There is no automated test project yet; do not treat the development database as disposable test data.

Use `README.md` to start the services. On this Windows host, serial .NET builds (`-m:1`) have been more reliable; parallel solution builds sometimes exited without a useful error. NuGet audit may warn when the package feed is unreachable. The PBF cache is large and ignored by Git. OpenStreetMap attribution must remain visible in the UI.

## Next work, if the user asks to continue the MVP

On 9 October, the user decided to keep Google Places disabled for now, including the UI. The worker registers only `GeofabrikSearchStrategy`, the API accepts only OSM jobs, and the dashboard offers no Google choice. An unfinished `GooglePlacesSearchStrategy.cs` adapter remains in the Worker project but is not registered or called. Do not enable it without a new user request. The user wants to consider Overture Maps Places and other open data sources later.

The fetch-to-quarantine slice is complete for one source. Web search for missing fields, AWS-backed extraction/decision support, per-field evidence, nearby lead matching, a final lead table, review and correction flows, and other source strategies have **not** been implemented. The user previously outlined these as future steps: web search for missing information, extraction from search results, a decider that chooses supported field values, and comparison against nearby rows. Keep raw observations and source evidence distinct from final leads. Ask for provider/API credentials only when an implementation truly needs them; do independent schema and interface work first.

A useful first action in a new chat is to read this file and `README.md`, inspect current code/data, then follow the user's new request. If the next request is enrichment, define the missing-field evidence contract and a read-only provider integration before accepting automated field decisions.
