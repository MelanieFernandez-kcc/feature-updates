# GeocoderUpdater CLI 

A standalone .NET 10 command-line interface (CLI) tool for idempotently upserting geocoder centroid records from CSV files into the PostgreSQL `geocode_data` database.

---

## Features

* **Idempotent Upserts:** Uses atomic PostgreSQL Common Table Expressions (`WITH updated AS (UPDATE ...) INSERT ... WHERE NOT EXISTS`) inside a single transaction so re-running the tool updates existing centroids without creating duplicate rows.
* **Schema & Coordinate Validation:** Validates required business keys and geographic bounds (`latitude` between `-90` and `90`, `longitude` between `-180` and `180`) before writing to the database.
* **Dry-Run Mode:** Supports `--dry-run` to validate CSV formatting and row counts without committing changes to PostgreSQL.
* **Audit Tracking:** Automatically stamps `updated_date` (`NOW()`) and `updated_by` on every inserted or updated record.

---

## Supported Target Tables

| `--table` Flag | PostgreSQL Table | Business Key (Upsert Match) |
| :--- | :--- | :--- |
| `zip5` | `public.us_zip5_population_weighted` | `zip_code` |
| `cresta` | `public.global_crestas` | `country_code`, `cresta_code`, `cresta_version` |

---

## Prerequisites

* [.NET 10 SDK](https://microsoft.com) (for building from source)
* Access to the target PostgreSQL instance (e.g., `postgis_local` running on `localhost:5432` with database `geocode_data`)

---

### Installing Dependencies
Because this project uses C# (.NET 10), dependencies (`CsvHelper`, `Npgsql`, and `System.CommandLine`) are managed in `GeocoderUpdater.csproj` rather than a Python `requirements.txt`. Run the following command to restore all NuGet packages:

```powershell
dotnet restore
```

---

## CLI Options

| Option | Alias | Required | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `--table` | `-t` | **Yes** | — | Target table: `zip5` or `cresta` |
| `--file` | `-f` | **Yes** | — | Path to the input centroids `.csv` file |
| `--connection` | `-c` | No | `DB_CONNECTION_STRING` env var, or `Host=localhost;Port=5432;Database=geocode_data;Username=postgres;Password=postgres` | PostgreSQL connection string |
| `--updated-by` | `-u` | No | Current OS username | Value recorded in the `updated_by` audit column |
| `--dry-run` | — | No | `false` | Validate CSV rows without committing to PostgreSQL |
| `--help` | `-h`, `-?` | No | — | Display CLI help and usage information |

---

## Usage Examples

### 1. Validate a File Without Writing (`--dry-run`)
```powershell
.\publish\GeocoderUpdater.exe --table zip5 --file .\us_zip5.csv --dry-run
```

### 2. Upsert US ZIP5 Centroids
```powershell
.\publish\GeocoderUpdater.exe --table zip5 --file .\us_zip5.csv --updated-by=DEV-4672
```

### 3. Upsert Global CRESTA Centroids
```powershell
.\publish\GeocoderUpdater.exe --table cresta --file .\global_crestas.csv --updated-by=DEV-4672
```

### 4. Target a Custom Environment via Connection String
```powershell
.\publish\GeocoderUpdater.exe `
  --table zip5 `
  --file .\us_zip5.csv `
  --connection "Host=my-db-host;Port=5432;Database=geocode_data;Username=postgres;Password=secret"
```

---

## Expected Input CSV Headers

### `zip5` (`us_zip5_population_weighted`)
```csv
zip_code,state,city,county,latitude,longitude,state_fips,county_fips,place_fips,zip_type,vintage
02101,MA,Boston,Suffolk,42.3706,-71.0269,25,025,07000,PO BOX,2026-01-01
```

### `cresta` (`global_crestas`)
```csv
country_code,cresta_code,description,resolution,cresta_version,latitude,longitude,vintage,source
US,US_MA_01,Massachusetts Zone 1,High,2026.1,42.3601,-71.0589,2026-01-01,KCC_Pipeline
```

*(Note: If `vintage` is omitted or blank in the CSV, it defaults to the current UTC timestamp.)*

---

## Building & Packaging

### Run Directly via `dotnet`
```powershell
dotnet run -- --table zip5 --file .\us_zip5.csv --dry-run
```

### Publish Standalone Executable
```powershell
# Windows (x64)
dotnet publish -c Release -r win-x64 -o .\publish

# Linux (x64)
dotnet publish -c Release -r linux-x64 -o ./publish
```

---

## Project Structure

```text
geocoder-update/
├── GeocoderUpdater.csproj   # .NET 10 project file & NuGet dependencies (System.CommandLine, CsvHelper, Npgsql)
├── Models.cs                # CSV column mappings & coordinate validation rules
├── Program.cs               # CLI option definitions, routing, & PostgreSQL transaction upsert logic
└── README.md                # Usage & deployment documentation
```
