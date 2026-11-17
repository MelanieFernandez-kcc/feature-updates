using CsvHelper.Configuration.Attributes;

namespace GeocoderUpdater;


/* <summary>
models.cs acts as an interpreter that translates raw text rows from the database into C# objects
Each class mirrors one of the two centroid tables
*/

/*<summary>
Class UsZipCentroid maps to public.us_population_weighted
Name(..) attributes tells CsvHelper which CSV column header maps to which C# property *order does not matter*
'string' vs 'string?' 'string' meaning the value cannot be null 'string?' meaning that the value can be null
'decimal' Used for latitude and longitude instead of double because your PostgreSQL tables use the exact numeric type
</summary>
*/
public class UsZipCentroid
{
    [Name("zip_code")] public string ZipCode { get; set; } = string.Empty;
    [Name("state")] public string? State { get; set; }
    [Name("city")] public string? City { get; set; }
    [Name("county")] public string? County { get; set; }
    [Name("latitude")] public decimal Latitude { get; set; }
    [Name("longitude")] public decimal Longitude { get; set; }
    [Name("state_fips")] public string? StateFips { get; set; }
    [Name("county_fips")] public string? CountyFips { get; set; }
    [Name("place_fips")] public string? PlaceFips { get; set; }
    [Name("zip_type")] public string? ZipType { get; set; }
    [Name("vintage")] public DateTime? Vintage { get; set; }

/*<summary>
bool confirms ZipCode, CountryCode, CrestaCode isnt blank and that the coordinates fall within the values
-90,90,-180,180
</summary> 
*/
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ZipCode) &&
        Latitude is >= -90m and <= 90m &&
        Longitude is >= -180m and <= 180m;
}
/*<summary>
Class GlobalCrestaCentroid maps to public.global_crestas
</summary>
*/

public class GlobalCrestaCentroid
{
    [Name("country_code")] public string CountryCode { get; set; } = string.Empty;
    [Name("cresta_code")] public string CrestaCode { get; set; } = string.Empty;
    [Name("description")] public string Description { get; set; } = string.Empty;
    [Name("resolution")] public string Resolution { get; set; } = string.Empty;
    [Name("cresta_version")] public string CrestaVersion { get; set; } = string.Empty;
    [Name("latitude")] public decimal Latitude { get; set; }
    [Name("longitude")] public decimal Longitude { get; set; }
    [Name("vintage")] public DateTime? Vintage { get; set; }
    [Name("source")] public string Source { get; set; } = string.Empty;

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(CountryCode) &&
        !string.IsNullOrWhiteSpace(CrestaCode) &&
        Latitude is >= -90m and <= 90m &&
        Longitude is >= -180m and <= 180m;
}
