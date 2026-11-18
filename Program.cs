using System.CommandLine;
using System.Diagnostics;
using Npgsql;

// Default input directory and PostGIS connection.
const string DefaultBaseDir = @"C:\Data-Pipeline\staging\MBI_US_R2026";
const string DefaultPgConn = "Host=localhost;Port=5007;Database=gis;Username=postgres;Password=postgres";

// Root directory containing source shapefiles.
var dirOption = new Option<string>("--dir", "-d")
{
    Description = "Directory containing the MBI US R2026 shapefiles",
    DefaultValueFactory = _ => DefaultBaseDir,
    Recursive = true
};

// PostgreSQL/PostGIS connection string.
var connOption = new Option<string>("--connection", "-c")
{
    Description = "PostgreSQL/PostGIS connection string",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TEGOLA_PG_CONN") ?? DefaultPgConn,
    Recursive = true
};

// Preview actions without modifying PostGIS.
var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Validate file paths and preview commands without modifying PostGIS",
    Recursive = true
};

// US State boundaries (Admin1).
var admin1Job = new BoundaryJob(
    Name: "adm1-us",
    ShapefileName: "MBI_US_R2026_States.shp",
    TargetTable: "public.mbi_us_adm1_r2026",
    ViewName: "public.us_adm1",
    MinExpectedRows: 50
);

// US County boundaries (Admin2).
var admin2Job = new BoundaryJob(
    Name: "adm2-us",
    ShapefileName: "MBI_US_R2026_Counties.shp",
    TargetTable: "public.mbi_us_adm2_r2026",
    ViewName: "public.us_adm2",
    MinExpectedRows: 3000
);

// Command: Load Admin1 boundaries.
var adm1Command = new Command("adm1-us", "Load US Admin1 (States) boundaries into PostGIS for Tegola");
adm1Command.SetAction(async (parseResult, ct) =>
{
    var dir = parseResult.GetValue(dirOption)!;
    var conn = parseResult.GetValue(connOption)!;
    var dryRun = parseResult.GetValue(dryRunOption);
    return await RunPipelineAsync([admin1Job], dir, conn, dryRun, ct);
});

// Command: Load Admin2 boundaries.
var adm2Command = new Command("adm2-us", "Load US Admin2 (Counties) boundaries into PostGIS for Tegola");
adm2Command.SetAction(async (parseResult, ct) =>
{
    var dir = parseResult.GetValue(dirOption)!;
    var conn = parseResult.GetValue(connOption)!;
    var dryRun = parseResult.GetValue(dryRunOption);
    return await RunPipelineAsync([admin2Job], dir, conn, dryRun, ct);
});

// Command: Load both Admin1 and Admin2.
var allCommand = new Command("all", "Perform a complete geographic data refresh for US Admin1 and Admin2");
allCommand.SetAction(async (parseResult, ct) =>
{
    var dir = parseResult.GetValue(dirOption)!;
    var conn = parseResult.GetValue(connOption)!;
    var dryRun = parseResult.GetValue(dryRunOption);
    return await RunPipelineAsync([admin1Job, admin2Job], dir, conn, dryRun, ct);
});

// Configure CLI root command and options.
var rootCommand = new RootCommand("tegola-update: MBI to Tegola spatial boundary update CLI");
rootCommand.Options.Add(dirOption);
rootCommand.Options.Add(connOption);
rootCommand.Options.Add(dryRunOption);
rootCommand.Subcommands.Add(adm1Command);
rootCommand.Subcommands.Add(adm2Command);
rootCommand.Subcommands.Add(allCommand);

return await rootCommand.Parse(args).InvokeAsync();

/// <summary>
/// Executes the complete shapefile-to-PostGIS import pipeline.
/// </summary>
static async Task<int> RunPipelineAsync(
    BoundaryJob[] jobs,
    string baseDir,
    string npgsqlConnString,
    bool isDryRun,
    CancellationToken ct)
{
    // Convert Npgsql connection string to ogr2ogr PostgreSQL format.
    var pgOgrString = BuildOgrPgString(npgsqlConnString);

    foreach (var job in jobs)
    {
        Console.WriteLine($"\n=== Starting job: {job.Name} ===");

        var shpPath = Path.Combine(baseDir, job.ShapefileName);

        // Verify all shapefile components exist.
        if (!ValidateShapefileBundle(shpPath))
        {
            return 1;
        }

        Console.WriteLine($"[1/4] Verified shapefile: {shpPath}");
        Console.WriteLine($"      Target table:       {job.TargetTable}");

        // Preview without modifying the database.
        if (isDryRun)
        {
            Console.WriteLine($"[DRY RUN] Would run ogr2ogr into {job.TargetTable} (EPSG:3857) and update view {job.ViewName}.");
            continue;
        }

        Console.WriteLine("[2/4] Ingesting and reprojecting to EPSG:3857 via ogr2ogr...");

        // Import shapefile into PostGIS.
        var ogrArgs = new[]
        {
            "-f", "PostgreSQL",
            pgOgrString,
            shpPath,
            "-nln", job.TargetTable,
            "-nlt", "PROMOTE_TO_MULTI",
            "-t_srs", "EPSG:3857",
            "-lco", "GEOMETRY_NAME=geometry",
            "-lco", "FID=gid",
            "-overwrite"
        };

        if (!await RunOgr2OgrAsync(ogrArgs, ct))
        {
            Console.Error.WriteLine($"Error: ogr2ogr failed for {job.Name}.");
            return 1;
        }

        Console.WriteLine("[3/4] Running PostGIS geometry repair, spatial indexing, and ANALYZE...");

        try
        {
            await using var conn = new NpgsqlConnection(npgsqlConnString);
            await conn.OpenAsync(ct);

            // Create spatial index and refresh Tegola-facing view.
            var indexName = $"idx_{job.TargetTable.Replace(".", "_")}_geom";

            var postSql = $@"
                UPDATE {job.TargetTable}
                SET geometry = ST_Multi(ST_MakeValid(geometry))
                WHERE NOT ST_IsValid(geometry);

                CREATE INDEX IF NOT EXISTS {indexName}
                ON {job.TargetTable} USING GIST (geometry);

                ANALYZE {job.TargetTable};

                CREATE OR REPLACE VIEW {job.ViewName} AS
                SELECT * FROM {job.TargetTable};
            ";

            await using (var cmd = new NpgsqlCommand(postSql, conn))
            {
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Verify feature count after loading.
            await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {job.TargetTable};", conn);
            var rowCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            Console.WriteLine($"[4/4] Verified {rowCount:N0} features loaded into {job.TargetTable} (View: {job.ViewName}).");

            // Basic sanity check on expected volume.
            if (rowCount < job.MinExpectedRows)
            {
                Console.Error.WriteLine($"Warning: Expected at least {job.MinExpectedRows} rows, but found {rowCount}.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PostGIS SQL step failed for {job.Name}: {ex.Message}");
            return 1;
        }
    }

    Console.WriteLine("\nAll requested boundary updates completed successfully.");
    return 0;
}

/// <summary>
/// Confirms required shapefile sidecar files exist.
/// </summary>
static bool ValidateShapefileBundle(string shpPath)
{
    string[] requiredExtensions = [".shp", ".shx", ".dbf", ".prj"];

    foreach (var ext in requiredExtensions)
    {
        var sidecar = Path.ChangeExtension(shpPath, ext);

        if (!File.Exists(sidecar))
        {
            Console.Error.WriteLine($"Missing required shapefile component: {sidecar}");
            return false;
        }
    }

    return true;
}

/// <summary>
/// Converts an Npgsql connection string into GDAL/ogr2ogr format.
/// </summary>
static string BuildOgrPgString(string npgsqlConnString)
{
    var builder = new NpgsqlConnectionStringBuilder(npgsqlConnString);

    return $"PG:host={builder.Host} port={builder.Port} dbname={builder.Database} user={builder.Username} password={builder.Password}";
}

/// <summary>
/// Executes ogr2ogr and waits for completion.
/// </summary>
static async Task<bool> RunOgr2OgrAsync(string[] arguments, CancellationToken ct)
{
    var psi = new ProcessStartInfo
    {
        FileName = "ogr2ogr",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    foreach (var arg in arguments)
    {
        psi.ArgumentList.Add(arg);
    }

    try
    {
        using var process = Process.Start(psi);

        if (process is null)
        {
            return false;
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var errOutput = await stdErrTask;

        if (!string.IsNullOrWhiteSpace(errOutput))
        {
            Console.WriteLine($"ogr2ogr output: {errOutput.Trim()}");
        }

        return process.ExitCode == 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to start ogr2ogr. Ensure GDAL is installed and in your system PATH. Details: {ex.Message}");
        return false;
    }
}

// Defines the configuration for a boundary import job.
record BoundaryJob(
    string Name,
    string ShapefileName,
    string TargetTable,
    string ViewName,
    int MinExpectedRows
);
