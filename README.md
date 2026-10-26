# LeadsFinder

LeadsFinder has a working **map UI → API → PostgreSQL job → fetch worker → quarantine** path and a command-line enrichment pilot. Choose the Geofabrik/OpenStreetMap extract or the cloned Google Maps Scraper CLI, select a search center and radius, then inspect the raw lodging observations. The enrichment pilot can research those observations and store field-level evidence. Lead cleaning and business deduplication are still separate future work.

## Run locally

Prerequisites: .NET 10 SDK, Node.js 20.19+ or 22.12+, npm, and Docker Desktop with its Linux engine running.

### Local keys and configuration

Copy the repository-root `.env.example` to `.env` and paste real keys into `.env`. The local `.env` file is ignored by Git; `.env.example` contains placeholders and is intended to be committed. If `.env` already exists, edit it directly instead of overwriting it.

```powershell
Copy-Item .env.example .env
```

For AWS duplicate checks, fill in `DuplicateMatching__Bedrock__ApiKey` and the model/profile ID, then set `DuplicateMatching__Bedrock__Enabled=true`. The Sydney region is prefilled. For enrichment, fill in `Ollama__ApiKey`. Optional database and source-path settings are listed in the example.

The API, worker, enrichment pilot, and EF design-time factory read the repository-root file, including when started from a project subdirectory. Restart the services after editing it. Configuration precedence is appsettings/user-secrets, then nonblank `.env` values, then OS environment variables, then command-line arguments. Existing user-secrets remain supported. Single- and double-quoted values and inline comments are supported; use single quotes for Windows paths. Shell execution and variable interpolation are not supported. The file populates server configuration without exporting keys to child processes. Keep credentials out of `VITE_` variables and dashboard files.

With Docker Desktop running, the API, worker, or enrichment pilot starts the bundled PostgreSQL/PostGIS database on `127.0.0.1:55432` when needed and applies pending migrations. This default database is for local development. Set `LEADSFINDER_DB` to use an existing database; configured databases are never started or migrated automatically.

To start or migrate the bundled database manually, from `src/LeadsFinder`:

```powershell
docker compose up -d db
$env:LEADSFINDER_DB = 'Host=127.0.0.1;Port=55432;Database=leadsfinder;Username=leadsfinder;Password=devpassword'
dotnet ef database update --project LeadsFinder.Data --startup-project LeadsFinder.Data
```

The connection string is for the local database in `compose.yaml`. Leave `LEADSFINDER_DB` unset for automatic local startup, or set it in **each** PowerShell terminal when using a configured database. Start the API and worker in separate terminals:

```powershell
dotnet run --project LeadsFinder.Api --launch-profile http
```

```powershell
dotnet run --project LeadsFinder.Worker
```

From `src/LeadsFinder/LeadsFinder.Dashboard`:

```powershell
npm ci
npm run dev
```

Open `http://127.0.0.1:5173`. Vite forwards `/api` to the API at `http://127.0.0.1:5088`. The dashboard polls every five seconds. The first real search downloads the [Geofabrik Northern Zone OSM PBF extract](https://download.geofabrik.de/asia/india/northern-zone.html) (currently about 213 MB) to the worker project's `data` folder, then parses it. The file is cached and ignored by Git. Set `Geofabrik__ExtractPath` to an absolute path if you already have the extract or want a different cache location. Replace that file with a newer extract and restart the worker to refresh the in-memory catalog.

Click a search job to open `/jobs/{id}` with its status, settings, timings, saved observations, and duplicate checks. Creating a job opens this page automatically. Pending and running jobs refresh every five seconds; the worker saves observation rows when it finishes. Failed jobs show the recorded failure reason. The back link returns to the job list. When hosting the built dashboard, configure the web server to serve `index.html` for `/jobs/*` so direct links and refreshes work (Vite handles this locally).

Saved observations use `rapid-grid-react` (with the `rapid-grid` engine) to virtualize rows and columns in a fixed-height, read-only grid. The API supplies pages of up to 100 rows. Select cells to copy values; each place has a button that opens its raw JSON below the grid.

The map uses interactive [OpenStreetMap tiles](https://operations.osmfoundation.org/policies/tiles/) with visible attribution. The extract data is © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), distributed through Geofabrik. Keep that attribution visible in any derived UI or export.

Use the search control above the map to find a place by name, then select a result to move the search pin and radius circle there. Place lookup uses the [public Nominatim service](https://operations.osmfoundation.org/policies/nominatim/) through the API. Searches run only when submitted, are cached for 24 hours, and are limited to one upstream request per second for this app instance. The location button centers the search pin on your current position and requests browser location access only when clicked. If access was already granted, the map starts at your position and keeps a separate blue location marker updated while the page is open. The button remains visible without location access.

### Google Maps Scraper setup

The upstream [google-maps-scraper](https://github.com/gosom/google-maps-scraper) repository is included as a Git submodule at `src/LeadsFinder/google-maps-scraper` (commit `d0b51bcf3cd56d9a3f71e049cb6e226554e24162`). Its current source requires Go 1.27.1 and Playwright Chromium. With Go and Git installed, run this from the LeadsFinder repository root on Windows:

```powershell
.\scripts\Setup-GoogleMapsScraper.ps1
```

The script initializes the pinned submodule, lets Go obtain its required toolchain, builds the CLI, and installs Chromium. On Windows it removes incompatible single-process/no-zygote Chromium flags in an isolated copy of the pinned `scrapemate` dependency under ignored `artifacts/`; it leaves the submodule and shared Go module cache unchanged. Rebuilding with a plain `go build` on Windows would lose this compatibility fix. Use `-SkipBrowserInstall` for a subsequent rebuild when Chromium is already installed. The browser cache is excluded through the submodule's local Git exclude file.

The worker looks for `google-maps-scraper/bin/google-maps-scraper.exe` on Windows (or `bin/google-maps-scraper` on Unix) and the browser under `google-maps-scraper/.playwright`. Override these with `GoogleMapsScraper__ExecutablePath` and `GoogleMapsScraper__BrowserPath` if needed. The worker disables upstream telemetry. It launches one CLI process per Google search job, with separate input and JSONL output files; the current worker handles one job at a time. Keep the worker running to process scheduled jobs. A missing executable appears as a failed job with a clear error. Structured CLI errors are checked even when its process exits with code zero, so a browser crash with no results fails the job instead of appearing as a completed empty search. Partial requests are logged along with raw-row and request counts.

For circles up to 1 km requesting at most 20 rows, the CLI receives direct Google Maps search URLs at the selected center. Larger requests use the upstream CLI's `-grid-bbox` and `-grid-cell` flags. The upstream grid generator splits the box into smaller cells and deduplicates across them; LeadsFinder chooses a cell size that keeps roughly eight cells across the circle's diameter, with a 0.5–10 km cell bound. Grid results are then filtered to the selected circle, because the scraper's box only positions searches. A `return_fewer` job stays in the selected radius. An `expand_radius` job runs a 50 km fallback only if the first pass found fewer unseen places than requested. Existing Google place IDs are skipped across jobs.

The worker gives Google searches an area-based limit: up to 1 km gets 6 minutes; up to 5 km, 12 minutes; up to 15 km, 25 minutes; up to 30 km, 50 minutes; and up to 50 km, 90 minutes. Jobs that may expand to 50 km also get a 90-minute limit. The database lease includes a five-minute margin. The CLI runs four concurrent pages inside two browsers for each job. These limits cap search work; they do not guarantee a target row count. Google Maps search results may omit businesses or fields, and the worker records only observations returned by the scraper.

The default local Docker database is migrated at startup. Apply migrations explicitly for a configured database. `dotnet build LeadsFinder.slnx` and `npm run build` check the two applications.

## Search behavior

- The Geofabrik strategy reads OSM lodging nodes, ways, and relations tagged as hotels, motels, guest houses, hostels, apartments, chalets, alpine huts, wilderness huts, or bed and breakfasts.
- Lodging subtype filters map to the relevant OSM tags. A point for a way or relation is estimated from its member nodes.
- Results are ordered by distance from the selected center. OSM object IDs already in quarantine are excluded before choosing results, so a repeat search proceeds to the next nearest unseen objects. A request for 50 stages up to **50 new observations**; repeating it takes the next 50 until the selected area is exhausted. It does not invent rows to meet a quota.
- `return_fewer` stays within the chosen radius. `expand_radius` fills any shortfall with the nearest matching observations up to 50 km from the center.
- Google Maps Scraper queries hotels, guest houses, or vacation rentals according to the selected subtype. It stores the upstream JSON with each observation and uses the Google place ID as the source identity when present. Selecting Email also enables the scraper's website email extraction.
- Every quarantine row has a stable OSM object ID, an `openstreetmap.org` source link, coordinates, and raw OSM tags. An OSM object may lack a name, address, phone, email, or website; missing fields remain missing for later enrichment. An unnamed object receives an explicit placeholder label.
- The database allows each source identity (`strategy` + provider ID) once across all jobs. Search selection and saving are serialized across workers to prevent overlapping jobs from claiming the same object. A search completes with fewer or zero rows when its area runs out of unseen matches. Distinct source records may still describe the same business; business-level matching is a later stage.

## Current API

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/jobs` | Validate and queue a lodging search job. |
| `GET` | `/api/jobs?page=1&pageSize=20` | Page through jobs with quarantine counts. |
| `GET` | `/api/jobs/{id}` | Inspect one job. |
| `GET` | `/api/jobs/{id}/records?page=1&pageSize=20` | Page through its raw quarantine observations. |
| `GET` | `/api/enriched-records?page=1&pageSize=20` | Page through persisted enrichment results. |
| `GET` | `/api/enriched-records/{id}` | Inspect one result and its field evidence. |
| `GET` | `/api/duplicate-matching/config` | Inspect duplicate matching and local Bedrock readiness. |
| `POST` | `/api/jobs/{id}/duplicate-checks` | Compare source observations and save a report; optional bounded Bedrock comparison. |
| `GET` | `/api/jobs/{id}/duplicate-checks/latest` | Read the most recent duplicate report for a job. |
| `GET` | `/api/config` | Expose accepted form values and source mode. |
| `GET` | `/api/locations?q=Baga%20Beach%2C%20Goa` | Search place names for map positioning. |

Quarantine records are observations, not cleaned leads. The dashboard can compare potential duplicates using name/address similarity and location, with optional Bedrock suggestions. Final lead merging, admin decisions, and corrections remain future slices. See [Duplicate matching and AWS setup](docs/DUPLICATE_MATCHING.md) for configuration and limits.

## Enrichment pilot

`LeadsFinder.Enrichment` has a known-website strategy and an Ollama web-search strategy. It removes scripts, styles, and navigation from HTML, caps the text sent to a local Qwen model, and accepts extracted values only when the cited page contains the returned quote and value. Existing source values are retained. The `enriched_records` and `enrichment_evidence` tables keep one result per source observation and field-level provenance; they do not deduplicate businesses or mark model output as verified.

Install Ollama and pull `qwen3:8b` (the available Qwen3 size closest to the requested 7B). Set `Ollama__ApiKey` in the repository-root `.env`, then run:

```powershell
dotnet run --project LeadsFinder.EnrichmentPilot -- --limit 20 --context 'New Delhi'
```

Run these commands from `src/LeadsFinder`. `--context` supplies the search locality and requires matching pages to mention it near the heading or contact details; pass the locality of the selected source rows, especially when their raw address is empty. `LEADSFINDER_DB` overrides the default local Docker database. Set `Ollama__Model` and `Ollama__BaseUrl` to override the model or local API URL. The pilot skips source rows that already have an enrichment result and reports completed/partial rows, search requests, and model tokens. A partial row has missing fields; unsupported values remain null. Use `--retry-partial` to revisit partial rows while retaining their cited fields. Use `--sample` to run the separate, database-free 20-hotel sample in `samples/hotels-20.json`; its report is written under ignored `artifacts/` and is not inserted into the database. Ollama web search can return HTTP 429; the pilot records the error and keeps supported fields already found.
