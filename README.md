# Tegola Update

A command-line utility for loading MBI administrative boundary shapefiles into PostGIS and preparing them for Tegola vector tile services.

## Overview

This tool automates the annual geographic data refresh process for **US Admin1 (States)** and **US Admin2 (Counties)**. 

### Pipeline Workflow
1. **Validation:** Verifies that all required shapefile components exist.
2. **Import & Reprojection:** Loads shapefiles into PostGIS via GDAL (`ogr2ogr`) and reprojects geometries to `EPSG:3857` for Tegola compatibility.
3. **Geometry Repair:** Fixes invalid geometries (`ST_MakeValid`).
4. **Optimization:** Builds `GIST` spatial indexes and runs `ANALYZE` to optimize query performance.
5. **View Management:** Updates Tegola-facing database views to point to the latest tables.

---

## Prerequisites

### Software
* **.NET 9 SDK / Runtime**
* **PostgreSQL** with the **PostGIS** extension enabled
* **GDAL (`ogr2ogr`)** installed and added to your system `PATH`

Verify your GDAL installation:
```powershell
ogr2ogr --version
```

### Input Data
By default, the utility expects shapefiles to be located in:
```text
C:\Data-Pipeline\staging\MBI_US_R2026
```

Each dataset must include the four core shapefile components (`.shp`, `.shx`, `.dbf`, `.prj`):
* `MBI_US_R2026_States.shp`
* `MBI_US_R2026_Counties.shp`

---

## Configuration

You can provide the PostgreSQL connection string via a CLI flag or an environment variable.

### Environment Variable
Set `TEGOLA_PG_CONN` in your terminal session:
```powershell
\$env:TEGOLA_PG_CONN="Host=localhost;Port=5007;Database=gis;Username=postgres;Password=postgres"
```

---

## Usage

### Basic Commands

| Action | Command |
| :--- | :--- |
| **Load Admin1 (States)** | `tegola-update adm1-us` |
| **Load Admin2 (Counties)** | `tegola-update adm2-us` |
| **Load Both Layers** | `tegola-update all` |

### Optional Parameters

| Flag | Description | Example |
| :--- | :--- | :--- |
| `--dir` | Override the default input directory. | `tegola-update all --dir "D:\MBIData"` |
| `--connection` | Override the default or environment connection string. | `tegola-update all --connection "Host=localhost;Port=5432;Database=gis;Username=postgres;Password=postgres"` |
| `--dry-run` | Validate files and preview actions without modifying the database. | `tegola-update all --dry-run` |

---

## Generated Database Objects

Running the update pipeline creates and updates the following PostGIS objects:

* **Tables:**
  * `public.mbi_us_adm1_r2026`
  * `public.mbi_us_adm2_r2026`
* **Views (Tegola Endpoints):**
  * `public.us_adm1`
  * `public.us_adm2`
* **Indexes:**
  * `GIST` spatial indexes are automatically generated on all geometry columns.

---

## Development Notes

Created to streamline annual MBI administrative boundary updates and standardize spatial dataset preparation for Tegola vector tile generation.
