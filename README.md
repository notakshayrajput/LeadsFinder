# LeadsFinder

LeadsFinder has a working **map UI → API → PostgreSQL job → fetch worker → quarantine** path. Choose the Geofabrik/OpenStreetMap extract or the cloned Google Maps Scraper CLI, select a search center and radius, then inspect the raw lodging observations. Lead cleaning and AI decisions are not active yet.

## Run locally

Prerequisites: .NET 10 SDK, Node.js 20.19+ or 22.12+, npm, and Docker Desktop with its Linux engine running.

From `src/LeadsFinder`:

```powershell
docker compose up -d db
$env:LEADSFINDER_DB = 'Host=127.0.0.1;Port=5432;Database=leadsfinder;Username=leadsfinder;Password=devpassword'
dotnet ef database update --project LeadsFinder.Data --startup-project LeadsFinder.Data
```

The connection string is for the local database in `compose.yaml`. Set `LEADSFINDER_DB` in **each** PowerShell terminal used for the API or worker. Start them in separate terminals:

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

The map uses interactive [OpenStreetMap tiles](https://operations.osmfoundation.org/policies/tiles/) with visible attribution. The extract data is © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), distributed through Geofabrik. Keep that attribution visible in any derived UI or export.

Use the search control above the map to find a place by name, then select a result to move the search pin and radius circle there. Place lookup uses the [public Nominatim service](https://operations.osmfoundation.org/policies/nominatim/) through the API. Searches run only when submitted, are cached for 24 hours, and are limited to one upstream request per second for this app instance. The location button centers the search pin on your current position and requests browser location access only when clicked. If access was already granted, the map starts at your position and keeps a separate blue location marker updated while the page is open. The button remains visible without location access.

### Google Maps Scraper setup

The upstream [google-maps-scraper](https://github.com/gosom/google-maps-scraper) repository is included as a Git submodule at `src/LeadsFinder/google-maps-scraper` (commit `d0b51bcf3cd56d9a3f71e049cb6e226554e24162`). After cloning LeadsFinder, run `git submodule update --init src/LeadsFinder/google-maps-scraper` from the repository root. Its current source requires Go 1.27.1 and Playwright Chromium. From that directory on Windows:

```powershell
go build -o bin/google-maps-scraper.exe .
$env:PLAYWRIGHT_INSTALL_ONLY = '1'
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path (Get-Location) '.playwright'
.\bin\google-maps-scraper.exe
Remove-Item Env:PLAYWRIGHT_INSTALL_ONLY
```

The worker looks for `google-maps-scraper/bin/google-maps-scraper.exe` on Windows (or `bin/google-maps-scraper` on Unix) and the browser under `google-maps-scraper/.playwright`. Override these with `GoogleMapsScraper__ExecutablePath` and `GoogleMapsScraper__BrowserPath` if needed. The worker disables upstream telemetry. It launches one CLI process per Google search job, with separate input and JSONL output files; the current worker handles one job at a time. Keep the worker running to process scheduled jobs. A missing executable appears as a failed job with a clear error.

For circles up to 1 km requesting at most 20 rows, the CLI receives direct Google Maps search URLs at the selected center. Larger requests use the upstream CLI's `-grid-bbox` and `-grid-cell` flags. The upstream grid generator splits the box into smaller cells and deduplicates across them; LeadsFinder chooses a cell size that keeps roughly eight cells across the circle's diameter, with a 0.5–10 km cell bound. Grid results are then filtered to the selected circle, because the scraper's box only positions searches. A `return_fewer` job stays in the selected radius. An `expand_radius` job runs a 50 km fallback only if the first pass found fewer unseen places than requested. Existing Google place IDs are skipped across jobs.

The worker gives Google searches an area-based limit: up to 1 km gets 6 minutes; up to 5 km, 12 minutes; up to 15 km, 25 minutes; up to 30 km, 50 minutes; and up to 50 km, 90 minutes. Jobs that may expand to 50 km also get a 90-minute limit. The database lease includes a five-minute margin. The CLI runs four concurrent pages inside two browsers for each job. These limits cap search work; they do not guarantee a target row count. Google Maps search results may omit businesses or fields, and the worker records only observations returned by the scraper.

Apply migrations explicitly after model changes. Neither the API nor worker changes the database schema on startup. `dotnet build LeadsFinder.slnx` and `npm run build` check the two applications.

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
| `GET` | `/api/config` | Expose accepted form values and source mode. |
| `GET` | `/api/locations?q=Baga%20Beach%2C%20Goa` | Search place names for map positioning. |

Quarantine records are observations, not cleaned leads. Web search, AWS extraction, evidence comparisons, duplicate decisions, reviews, and corrections are separate future slices.
