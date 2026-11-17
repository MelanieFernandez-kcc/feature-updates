Tegola Update
Command-line utility for loading MBI administrative boundary shapefiles into PostGIS and preparing them for use with Tegola vector tile services.

Overview
This tool automates the geographic data refresh process for:

US Admin1 (States)
US Admin2 (Counties)
The application:

Validates required shapefile components.
Imports shapefiles into PostGIS using GDAL/ogr2ogr.
Reprojects data to EPSG:3857 for Tegola compatibility.
Repairs invalid geometries.
Creates spatial indexes.
Runs ANALYZE for query optimization.
Updates Tegola-facing database views.
Requirements
Software
.NET 9 (or project target framework)
PostgreSQL with PostGIS
GDAL / ogr2ogr available on PATH
Verify GDAL installation:

ogr2ogr --version
Database
Example connection string:

Host=localhost;Port=5007;Database=gis;Username=postgres;Password=postgres
Input Data
Expected default directory:

C:\Data-Pipeline\staging\MBI_US_R2026
Required shapefile components:

.shp
.shx
.dbf
.prj
Example files:

MBI_US_R2026_States.shp
MBI_US_R2026_Counties.shp
Commands
Load Admin1 (States)
tegola-update adm1-us
Load Admin2 (Counties)
tegola-update adm2-us
Load Both Layers
tegola-update all
Optional Parameters
Custom Input Directory
tegola-update all --dir "D:\MBIData"
Custom Connection String
tegola-update all --connection "Host=localhost;Port=5432;Database=gis;Username=postgres;Password=postgres"
Dry Run
Validates files and displays actions without making database changes.

tegola-update all --dry-run
Environment Variable
The PostgreSQL connection string can be supplied through:

TEGOLA_PG_CONN
Example:

$env:TEGOLA_PG_CONN="Host=localhost;Port=5007;Database=gis;Username=postgres;Password=postgres"
Generated Database Objects
Tables
public.mbi_us_adm1_r2026
public.mbi_us_adm2_r2026
Views
public.us_adm1
public.us_adm2
Spatial Indexes
GIST indexes are automatically created on geometry columns to support efficient spatial queries.

Development Notes
This utility was created to support annual MBI geographic boundary updates and simplify the process of preparing spatial datasets for Tegola vector tile generation.