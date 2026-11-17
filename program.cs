//NuGet libraries - helps read csv files, makes any command line flags, and Npgsql helps with postgresql
//Applies to the two centroid tables (containing different columns)

using System.CommandLine;
using System.Globalization;
using CsvHelper;
using Npgsql;

namespace GeocoderUpdater;

//default string acts as the fallback connection string to point to postgis_local port 5432 and the geocoder database (geocode_data)

class Program
{
    private const string DefaultConnString =
        "Host=localhost;Port=5432;Database=geocode_data;Username=postgres;Password=postgres";

    /* <summary>
    Defining CLI options:
    static async ... Allows app to wait for file reads and database writes without freezing 'Task<int>' returns an exit code: 0 for success 1 for error
    Option<T>: Defines every command line flag. IsRequired means the command isnt executable without that specific parameter.
    </summary>
    */
    static async Task<int> Main(string[] args)
    {
        var tableOption = new Option<string>(
            aliases: ["--table", "-t"],
            description: "Target table: 'zip5' (us_zip5_population_weighted) or 'cresta' (global_crestas).")
        { IsRequired = true };

        var fileOption = new Option<FileInfo>(
            aliases: ["--file", "-f"],
            description: "Path to the input centroids CSV file.")
        { IsRequired = true };

        var connOption = new Option<string>(
            aliases: ["--connection", "-c"],
            getDefaultValue: () => Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") ?? DefaultConnString,
            description: "PostgreSQL connection string (or set DB_CONNECTION_STRING env var).");

        var updatedByOption = new Option<string>(
            aliases: ["--updated-by", "-u"],
            getDefaultValue: () => Environment.UserName,
            description: "Value recorded in the updated_by audit column.");

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run"],
            getDefaultValue: () => false,
            description: "Validate CSV rows without committing to PostgreSQL.");


        ///Routing and Validation: Checks that csv file actually exists on disk, if run is false it opens a TCP connection

        var rootCommand = new RootCommand("CLI tool for upserting geocoder centroids into PostgreSQL.")
        {
            tableOption, fileOption, connOption, updatedByOption, dryRunOption
        };

        rootCommand.SetHandler(async (table, file, connString, updatedBy, dryRun) =>
        {
            if (!file.Exists)
            {
                Console.Error.WriteLine($"Error: File not found at {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            //await using: guarantees the database connection closes cleanly when the program finishes

            await using var conn = new NpgsqlConnection(connString);
            if (!dryRun) await conn.OpenAsync();
            //switch: depending on what table used, it allows for route execution to either UpsertZip5Async or UpsertCrestaAsync

            switch (table.ToLowerInvariant())
            {
                case "zip5":
                case "us_zip5_population_weighted":
                    await UpsertZip5Async(conn, file, updatedBy, dryRun);
                    break;
                case "cresta":
                case "global_crestas":
                    await UpsertCrestaAsync(conn, file, updatedBy, dryRun);
                    break;
                default:
                    Console.Error.WriteLine("Error: --table must be 'zip5' or 'cresta'.");
                    Environment.ExitCode = 1;
                    break;
            }
        }, tableOption, fileOption, connOption, updatedByOption, dryRunOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task UpsertZip5Async(NpgsqlConnection conn, FileInfo file, string updatedBy, bool dryRun)
    {
        using var reader = new StreamReader(file.FullName);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        int upserted = 0, skipped = 0;
        await using var tx = dryRun ? null : await conn.BeginTransactionAsync();
        //Neither Postgresql table has a unique index on zip_code or cresta_code
        /* <summary>
        Postgres attempts to update any existing row matching @zip_code, then if it finds one, it updates the coordinates and returns 1
        INSERT runs if the insert returned nothing (WHERE NOT EXISTS)
        </summary>
        */
        const string sql = @"
            WITH updated AS (
                UPDATE public.us_zip5_population_weighted
                SET state = @state,
                    city = @city,
                    county = @county,
                    latitude = @lat,
                    longitude = @lon,
                    state_fips = @state_fips,
                    county_fips = @county_fips,
                    place_fips = @place_fips,
                    zip_type = @zip_type,
                    vintage = @vintage,
                    updated_date = NOW(),
                    updated_by = @updated_by
                WHERE zip_code = @zip_code
                RETURNING 1
            )
            INSERT INTO public.us_zip5_population_weighted
                (zip_code, state, city, county, latitude, longitude, state_fips, county_fips, place_fips, zip_type, vintage, updated_date, updated_by)
            SELECT @zip_code, @state, @city, @county, @lat, @lon, @state_fips, @county_fips, @place_fips, @zip_type, @vintage, NOW(), @updated_by
            WHERE NOT EXISTS (SELECT 1 FROM updated);";

        /*
        <summary>
        Reads the CSV file line by line instead of loading the entire file into memory at once
        For each row CsvHelper creates a new UsZipCentroid object from models.cs and it stores it in the variable r using await foreach keeps memory usage tiny.
        </summary> 
        */
        await foreach (var r in csv.GetRecordsAsync<UsZipCentroid>())
        {
            if (!r.IsValid()) { skipped++; continue; }

            if (!dryRun)
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("zip_code", r.ZipCode.Trim());
                cmd.Parameters.AddWithValue("state", (object?)r.State ?? DBNull.Value);
                cmd.Parameters.AddWithValue("city", (object?)r.City ?? DBNull.Value);
                cmd.Parameters.AddWithValue("county", (object?)r.County ?? DBNull.Value);
                cmd.Parameters.AddWithValue("lat", r.Latitude);
                cmd.Parameters.AddWithValue("lon", r.Longitude);
                cmd.Parameters.AddWithValue("state_fips", (object?)r.StateFips ?? DBNull.Value);
                cmd.Parameters.AddWithValue("county_fips", (object?)r.CountyFips ?? DBNull.Value);
                cmd.Parameters.AddWithValue("place_fips", (object?)r.PlaceFips ?? DBNull.Value);
                cmd.Parameters.AddWithValue("zip_type", (object?)r.ZipType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("vintage", DateTime.SpecifyKind(r.Vintage ?? DateTime.UtcNow, DateTimeKind.Utc));
                cmd.Parameters.AddWithValue("updated_by", updatedBy);
                await cmd.ExecuteNonQueryAsync();
            }
            upserted++;
        }

        if (tx != null) await tx.CommitAsync();
        Console.WriteLine($"us_zip5_population_weighted -> Processed: {upserted}, Skipped: {skipped} (DryRun: {dryRun})");
    }

    private static async Task UpsertCrestaAsync(NpgsqlConnection conn, FileInfo file, string updatedBy, bool dryRun)
    {
        using var reader = new StreamReader(file.FullName);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        int upserted = 0, skipped = 0;
        await using var tx = dryRun ? null : await conn.BeginTransactionAsync();

        const string sql = @"
            WITH updated AS (
                UPDATE public.global_crestas
                SET description = @description,
                    resolution = @resolution,
                    latitude = @lat,
                    longitude = @lon,
                    vintage = @vintage,
                    source = @source,
                    updated_date = NOW(),
                    updated_by = @updated_by
                WHERE country_code = @country_code
                  AND cresta_code = @cresta_code
                  AND cresta_version = @cresta_version
                RETURNING 1
            )
            INSERT INTO public.global_crestas
                (country_code, cresta_code, description, resolution, cresta_version, latitude, longitude, vintage, source, updated_date, updated_by)
            SELECT @country_code, @cresta_code, @description, @resolution, @cresta_version, @lat, @lon, @vintage, @source, NOW(), @updated_by
            WHERE NOT EXISTS (SELECT 1 FROM updated);";

        await foreach (var r in csv.GetRecordsAsync<GlobalCrestaCentroid>())
        {
            if (!r.IsValid()) { skipped++; continue; }

            if (!dryRun)
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("country_code", r.CountryCode.Trim());
                cmd.Parameters.AddWithValue("cresta_code", r.CrestaCode.Trim());
                cmd.Parameters.AddWithValue("description", r.Description);
                cmd.Parameters.AddWithValue("resolution", r.Resolution);
                cmd.Parameters.AddWithValue("cresta_version", r.CrestaVersion);
                cmd.Parameters.AddWithValue("lat", r.Latitude);
                cmd.Parameters.AddWithValue("lon", r.Longitude);
                cmd.Parameters.AddWithValue("vintage", DateTime.SpecifyKind(r.Vintage ?? DateTime.UtcNow, DateTimeKind.Utc));
                cmd.Parameters.AddWithValue("source", r.Source);
                cmd.Parameters.AddWithValue("updated_by", updatedBy);
                await cmd.ExecuteNonQueryAsync();
            }
            upserted++;
        }



        if (tx != null) await tx.CommitAsync();
        Console.WriteLine($"global_crestas -> Processed: {upserted}, Skipped: {skipped} (DryRun: {dryRun})");
    }
}

