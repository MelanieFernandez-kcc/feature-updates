/// <summary>
/// Shared helper class that safely executes the external GDAL command-line tool (ogr2ogr) in the background
/// for shapefile-to-PostGIS ingestion across the tegola-update pipeline.
///
/// Execution Overview:
///   Configures ProcessStartInfo with ArgumentList so file paths containing spaces are safely preserved.
///  Injects optional database credentials via private environment variables (e.g., PGPASSWORD) instead of CLI flags.
///  Asynchronously drains standard output and error streams concurrently to prevent OS buffer deadlocks.
///  Handles CancellationToken interrupts by terminating the entire process tree and returns exit status.
/// </summary>


using System.Diagnostics;
/// <summary>
/// Creates a utility class accessible anywhere inside the project without needing 'new static'
/// Contains the list of flags passed to ogr2ogr, ex: -f, PostgreSQL, -t_srs, EPSG:3857
/// Cntrl C (cancellation token) is handled in this class, and the process is killed if the user cancels the operation.
/// environment: An optional dictionary of environment variables. --> used by ZipUpdate.cs 
/// Task <bool> returns true if ogr2ogr succeeded, or false if it failed. 
/// </summary>


internal static class OgrImport
{
    internal static async Task<bool> RunAsync(
        IEnumerable<string> arguments, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ogr2ogr",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ogr2ogr.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw;
        }

        var output = await stdout;
        var errors = await stderr;
        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(output.Trim());
        if (!string.IsNullOrWhiteSpace(errors))
            Console.Error.WriteLine(errors.Trim());

        return process.ExitCode == 0;
    }
}
