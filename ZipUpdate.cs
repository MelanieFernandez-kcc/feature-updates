using System.CommandLine;
using System.Text.RegularExpressions;
using Npgsql;

// Default directory where Claritas data is expected
// Default ZIP shapefile name
// Default destination table in PostGIS
// Safety check.
// We expect roughly 32k ZIP polygons.
// If fewer than 20k are loaded, something is probably wrong.

internal static class ZipUpdate
{
    private const string DefaultDirectory = @"C:\Data-Pipeline\staging\Claritas 2026";
    private const string DefaultShapefile = "zip26_us.shp";
    private const string DefaultTable = "zip26_us";
    private const int DefaultMinimumRows = 20_000;
    // Temporary geometry column in staging table
    // Temporary feature id column in staging table
    private const string StageGeometry = "_tegola_geometry";
    private const string StageId = "_tegola_fid";
/// <summary>
/// Configure command-line options for the ZIP boundary update command,
/// including the source shapefile, destination PostGIS table, validation
/// thresholds, and source field mappings used during the import process.
/// </summary>
    internal static Command CreateCommand(
        Option<string> directory, Option<string> connection, Option<bool> dryRun)
    {
        var shapefile = new Option<string>("--shapefile")
        {
            Description = "Source .shp filename relative to --dir, or a full path"
        };
        var table = new Option<string>("--table")
        {
            Description = "Destination table name in public",
            DefaultValueFactory = _ => DefaultTable
        };
        var minimumRows = new Option<int>("--min-rows")
        {
            Description = "Minimum accepted feature count",
            DefaultValueFactory = _ => DefaultMinimumRows
        };
        var zipField = new Option<string>("--zip-field")
        {
            Description = "Source ZIP code field",
            DefaultValueFactory = _ => "ID"
        };
        var postOfficeField = new Option<string>("--post-office-field")
        {
            Description = "Source post office name field",
            DefaultValueFactory = _ => "PO_NAME"
        };
        var cityField = new Option<string>("--city-field")
        {
            Description = "Source city name field",
            DefaultValueFactory = _ => "CITY_NAME"
        };

        var command = new Command("zip-us", "Load Claritas US ZIP code boundaries into PostGIS for Tegola");
        command.Options.Add(shapefile);
        command.Options.Add(table);
        command.Options.Add(minimumRows);
        command.Options.Add(zipField);
        command.Options.Add(postOfficeField);
        command.Options.Add(cityField);
        command.SetAction((parsed, ct) => RunAsync(
            parsed.GetValue(directory) ?? DefaultDirectory,
            parsed.GetValue(shapefile),
            parsed.GetValue(connection)!,
            parsed.GetValue(table)!,
            parsed.GetValue(minimumRows),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = parsed.GetValue(zipField)!,
                ["PO_NAME"] = parsed.GetValue(postOfficeField)!,
                ["CITY_NAME"] = parsed.GetValue(cityField)!
            },
            parsed.GetValue(dryRun), ct));
        return command;
    }

    /// <summary>
    /// Orchestrates shapefile discovery, staging ingestion via ogr2ogr, transactional publish, and staging cleanup.
    /// </summary>
    private static async Task<int> RunAsync(
        string directory, string? shapefile, string connectionString, string tableName,
        int minimumRows, Dictionary<string, string> fields, bool dryRun, CancellationToken ct)
    {
        try
        {
            if (!Regex.IsMatch(tableName, @"\A[a-z][a-z0-9_]{0,62}\z"))
                throw new InvalidOperationException("--table must be an unqualified lowercase PostgreSQL identifier.");
            if (minimumRows < 1)
                throw new InvalidOperationException("--min-rows must be positive.");

            var source = FindShapefile(directory, shapefile);
            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(settings.Host) ||
                string.IsNullOrWhiteSpace(settings.Database) ||
                string.IsNullOrWhiteSpace(settings.Username))
                throw new InvalidOperationException("The connection string must specify Host, Database, and Username.");

            var target = InPublic(tableName);
            Console.WriteLine($"[1/4] Verified shapefile bundle: {source}");
            Console.WriteLine($"      Destination: {settings.Host}:{settings.Port}/{settings.Database} → public.{tableName}");
            Console.WriteLine($"      Minimum features: {minimumRows:N0}");
            if (dryRun)
            {
                Console.WriteLine($"[DRY RUN] Would import as EPSG:3857, validate and repair geometries, then refresh {target}.");
                Console.WriteLine($"          Source fields: {string.Join(", ", fields.Select(field => $"{field.Value} → {field.Key}"))}");
                return 0;
            }

            // Open PostgreSQL connection and confirm the PostGIS extension is enabled.

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            const string checkPostgis = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis')";
            await using (var check = new NpgsqlCommand(checkPostgis, conn))
            {
                if (await check.ExecuteScalarAsync(ct) is not true)
                    throw new InvalidOperationException("PostGIS must be installed in the destination database.");
            }

            var stageName = $"claritas_zip_stage_{Guid.NewGuid():N}";
            var stage = InPublic(stageName);
            try
            {
                Console.WriteLine("[2/4] Importing to a staging table with ogr2ogr...");
                string[] arguments =
                // Load shapefile into staging, reprojecting to EPSG:3857 and forcing 2D MultiPolygons.
                // SPATIAL_INDEX=NONE skips indexing staging for faster bulk load; LAUNDER=NO preserves exact column casing.
                [
                    "-f", "PostgreSQL", "PG:application_name=tegola-update", source,
                    "-nln", $"public.{stageName}", "-nlt", "PROMOTE_TO_MULTI",
                    "-dim", "XY", "-t_srs", "EPSG:3857",
                    "-lco", $"GEOMETRY_NAME={StageGeometry}", "-lco", $"FID={StageId}",
                    "-lco", "LAUNDER=NO", "-lco", "SPATIAL_INDEX=NONE"
                ];
                if (!await OgrImport.RunAsync(arguments, ct, GdalEnvironment(settings)))
                    throw new InvalidOperationException("ogr2ogr failed to load the Claritas shapefile.");

                Console.WriteLine("[3/4] Validating ZIP fields and repairing polygon geometries...");
                var featureCount = await PublishAsync(conn, stageName, tableName, fields, minimumRows, ct);
                Console.WriteLine($"[4/4] Published {featureCount:N0} features to public.{tableName}.");
                return 0;
            }
            finally
            {
                // Always drop the temporary staging table, even if validation or publishing fails.
                try
                {
                    await using var cleanup = new NpgsqlCommand($"DROP TABLE IF EXISTS {stage}", conn);
                    await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not remove staging table {stage}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Claritas update cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Claritas update failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Resolves the target .shp path (auto-discovering recursively if not overridden) and verifies sidecar files.
    /// </summary>

    private static string FindShapefile(string directory, string? overridePath)
    {
        string path;
        if (overridePath is not null)
        {
            path = Path.IsPathFullyQualified(overridePath)
                ? Path.GetFullPath(overridePath)
                : Path.GetFullPath(Path.Combine(directory, overridePath));
        }
        else
        {
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Claritas staging directory does not exist: {root}");

            var matches = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file).Equals(DefaultShapefile, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            path = matches.Length switch
            {
                1 => matches[0],
                0 => throw new FileNotFoundException($"Could not find {DefaultShapefile} under {root}. Use --shapefile to select the ZIP boundary file."),
                _ => throw new InvalidOperationException($"Multiple {DefaultShapefile} files found under {root}. Use --shapefile to select one.")
            };
        }

        if (!Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException($"ZIP boundary .shp file does not exist: {path}");

        var bundle = Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
            .Select(file => Path.GetFileName(file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { ".shx", ".dbf", ".prj" })
        {
            var sidecar = Path.ChangeExtension(path, extension);
            if (!bundle.Contains(Path.GetFileName(sidecar)))
                throw new FileNotFoundException($"Missing required shapefile component: {sidecar}");
        }
        return path;
    }

// Build the environment variables required by GDAL/ogr2ogr to connect
// to PostgreSQL. These values are derived from the application's database
// connection string and passed to external import processes during the
// shapefile-to-PostGIS import workflow

    private static Dictionary<string, string> GdalEnvironment(NpgsqlConnectionStringBuilder settings)
    {
        var environment = new Dictionary<string, string>
        {
            ["PGHOST"] = settings.Host!,
            ["PGPORT"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGDATABASE"] = settings.Database!,
            ["PGUSER"] = settings.Username!,
            ["PGPASSWORD"] = settings.Password ?? "",
            ["PGSSLMODE"] = settings.SslMode switch
            {
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => settings.SslMode.ToString().ToLowerInvariant()
            }
        };
        if (!string.IsNullOrEmpty(settings.RootCertificate))
            environment["PGSSLROOTCERT"] = settings.RootCertificate;
        return environment;
    }

    /// <summary>
    /// Validates the imported ZIP boundary staging table and publishes it to the
    /// destination PostGIS layer. This process verifies source fields and ZIP code
    /// quality, repairs invalid geometries, enforces EPSG:3857 MultiPolygon
    /// geometry requirements, creates the destination table if necessary, performs
    /// a full layer refresh, validates row counts, ensures a spatial index exists,
    /// and updates PostgreSQL statistics before committing the transaction.
    /// </summary>

    private static async Task<long> PublishAsync(
        NpgsqlConnection conn, string stageName, string targetName,
        Dictionary<string, string> fields, int minimumRows, CancellationToken ct)
    {
        var stage = InPublic(stageName);
        var target = InPublic(targetName);
        var geometry = Quote(StageGeometry);
        await using var tx = await conn.BeginTransactionAsync(ct);
        NpgsqlCommand Command(string sql) => new(sql, conn, tx) { CommandTimeout = 300 };

        var stageColumns = await GetColumnsAsync(conn, tx, stageName, ct);
        var sourceColumns = stageColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var key in fields.Keys.ToArray())
        {
            if (!sourceColumns.TryGetValue(fields[key], out var column) ||
                column.Name is StageId or StageGeometry)
                throw new InvalidOperationException($"Source field '{fields[key]}' for {key} was not found. Available fields: {string.Join(", ", sourceColumns.Keys.Where(name => name is not (StageId or StageGeometry)))}.");
            fields[key] = column.Name;
        }

        var zip = $"btrim({Quote(fields["ID"])}::text)";
        await using var checkRows = Command($"""
            SELECT count(*), count(*) FILTER (WHERE {zip} IS NULL OR {zip} !~ @zipPattern)
            FROM {stage}
            """);
        checkRows.Parameters.AddWithValue("zipPattern", "^[0-9]{1,5}$");
        long rowCount;
        await using (var results = await checkRows.ExecuteReaderAsync(ct))
        {
            await results.ReadAsync(ct);
            rowCount = results.GetInt64(0);
            if (rowCount < minimumRows)
                throw new InvalidOperationException($"Found {rowCount:N0} features. Expected at least {minimumRows:N0}.");
            if (results.GetInt64(1) > 0)
                throw new InvalidOperationException($"Found {results.GetInt64(1):N0} missing or malformed ZIP codes in {fields["ID"]}.");
        }

        await using var repair = Command($"""
            UPDATE {stage}
            SET {geometry} = ST_Multi(ST_UnaryUnion(ST_CollectionExtract(ST_MakeValid({geometry}), 3)))
            WHERE NOT ST_IsValid({geometry})
            """);
        var repaired = await repair.ExecuteNonQueryAsync(ct);
        await using var checkGeometry = Command($"""
            SELECT count(*) FROM {stage}
            WHERE {geometry} IS NULL OR ST_IsEmpty({geometry}) OR NOT ST_IsValid({geometry})
                OR GeometryType({geometry}) <> 'MULTIPOLYGON' OR ST_SRID({geometry}) <> 3857
            """);
        var unusable = (long)(await checkGeometry.ExecuteScalarAsync(ct))!;
        if (unusable > 0)
            throw new InvalidOperationException($"Found {unusable:N0} features without valid, nonempty EPSG:3857 multipolygons.");
        Console.WriteLine($"      Validated {rowCount:N0} features and repaired {repaired:N0} geometries.");

        // Serialize publication of this layer. Staging tables are private to each run.
        await using var lockLayer = Command("SELECT pg_advisory_xact_lock(hashtextextended(@target, 0))");
        lockLayer.Parameters.AddWithValue("target", target);
        await lockLayer.ExecuteNonQueryAsync(ct);

        await using var getKind = Command("SELECT relkind::text FROM pg_class WHERE oid = to_regclass(@target)");
        getKind.Parameters.AddWithValue("target", target);
        var kind = (string?)await getKind.ExecuteScalarAsync(ct);
        if (kind is null)
        {
            await using var create = Command($"""
                CREATE TABLE {target} (
                    gid integer PRIMARY KEY,
                    "ID" text NOT NULL,
                    "PO_NAME" text,
                    "CITY_NAME" text,
                    geometry geometry(MultiPolygon, 3857) NOT NULL
                )
                """);
            await create.ExecuteNonQueryAsync(ct);
        }
        else if (kind != "r" && kind != "p")
        {
            throw new InvalidOperationException($"{target} is not a table. Select the ZIP layer's backing table with --table.");
        }
        else
        {
            await using var lockTable = Command($"LOCK TABLE {target} IN SHARE ROW EXCLUSIVE MODE");
            await lockTable.ExecuteNonQueryAsync(ct);
        }

        // A full layer refresh must not remove rows from other tables through cascading foreign keys.
        await using var checkReferences = Command("""
            SELECT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE contype = 'f' AND confrelid = to_regclass(@target)
            )
            """);
        checkReferences.Parameters.AddWithValue("target", target);
        if (await checkReferences.ExecuteScalarAsync(ct) is true)
            throw new InvalidOperationException($"{target} has foreign key references from another relation. Use the ZIP layer's standalone backing table.");

        var targetColumns = await GetColumnsAsync(conn, tx, targetName, ct);
        var expressions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gid"] = Quote(StageId),
            ["geometry"] = geometry,
            ["ID"] = $"lpad({zip}, 5, '0')",
            ["PO_NAME"] = Quote(fields["PO_NAME"]),
            ["CITY_NAME"] = Quote(fields["CITY_NAME"])
        };
        foreach (var expected in expressions.Keys)
        {
            if (targetColumns.Count(column => column.Name.Equals(expected, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException($"{target} must have exactly one column named {expected} (case-insensitive).");
        }

        var columns = new List<string>();
        var values = new List<string>();
        foreach (var column in targetColumns)
        {
            if (column.Generated ||
                (column.Name.Equals("gid", StringComparison.OrdinalIgnoreCase) && column.HasDefault))
                continue;

            if (!expressions.TryGetValue(column.Name, out var expression))
            {
                if (sourceColumns.TryGetValue(column.Name, out var sourceColumn))
                    expression = Quote(sourceColumn.Name);
                else if (column.HasDefault)
                    continue;
                else
                    throw new InvalidOperationException($"Destination column '{column.Name}' has no matching source field or default.");
            }

            columns.Add(Quote(column.Name));
            values.Add(expression);
        }

        var targetGeometry = targetColumns.Single(column =>
            column.Name.Equals("geometry", StringComparison.OrdinalIgnoreCase)).Name;
        await using var replace = Command($"""
            DELETE FROM {target};
            INSERT INTO {target} ({string.Join(", ", columns)})
            SELECT {string.Join(", ", values)} FROM {stage} ORDER BY {Quote(StageId)};
            """);
        await replace.ExecuteNonQueryAsync(ct);

        await using var verify = Command($"SELECT count(*) FROM {target}");
        if ((long)(await verify.ExecuteScalarAsync(ct))! != rowCount)
            throw new InvalidOperationException("The destination feature count does not match staging.");

        await using var checkIndex = Command("""
            SELECT EXISTS (
                SELECT 1 FROM pg_index i
                JOIN pg_class idx ON idx.oid = i.indexrelid
                JOIN pg_am am ON am.oid = idx.relam
                JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0]
                WHERE i.indrelid = to_regclass(@target) AND i.indisvalid AND i.indpred IS NULL
                    AND am.amname = 'gist' AND a.attname = @geometry
            )
            """);
        checkIndex.Parameters.AddWithValue("target", target);
        checkIndex.Parameters.AddWithValue("geometry", targetGeometry);
        if (await checkIndex.ExecuteScalarAsync(ct) is not true)
        {
            await using var index = Command($"CREATE INDEX ON {target} USING GIST ({Quote(targetGeometry)})");
            await index.ExecuteNonQueryAsync(ct);
        }

        await using var analyze = Command($"ANALYZE {target}");
        await analyze.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return rowCount;
    }

    private static async Task<List<Column>> GetColumnsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT attname::text, atthasdef OR attidentity <> '', attgenerated <> ''
            FROM pg_attribute
            WHERE attrelid = to_regclass(@table) AND attnum > 0 AND NOT attisdropped
            ORDER BY attnum
            """;
        await using var command = new NpgsqlCommand(sql, conn, tx);
        command.Parameters.AddWithValue("table", InPublic(table));
        await using var rows = await command.ExecuteReaderAsync(ct);
        var columns = new List<Column>();
        while (await rows.ReadAsync(ct))
            columns.Add(new Column(rows.GetString(0), rows.GetBoolean(1), rows.GetBoolean(2)));

        return columns;
    }

    private static string InPublic(string name) => $"public.{Quote(name)}";
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private sealed record Column(string Name, bool HasDefault, bool Generated);
}
