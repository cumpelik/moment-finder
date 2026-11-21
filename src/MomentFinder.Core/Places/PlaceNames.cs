using System.Globalization;
using System.IO.Compression;

namespace MomentFinder.Places;

/// <summary>
/// Offline place names from GeoNames cities1000 (CC BY 4.0): every place with at least 1,000 people, the
/// nearest one to a GPS position. Ski resorts are mostly named after their village, so this is the name the user
/// knows. Loaded once into a 1 degree grid.
/// </summary>
public sealed class PlaceNames
{
    private const double MaxKm = 25;

    private readonly Dictionary<(int, int), List<(double Lat, double Lon, string Name)>> _grid = [];

    public PlaceNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.First(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            // geonameid, name, asciiname, alternatenames, latitude, longitude, ...
            var f = line.Split('\t');
            if (f.Length < 6
                || !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            var cell = Cell(lat, lon);
            if (!_grid.TryGetValue(cell, out var list)) _grid[cell] = list = [];
            list.Add((lat, lon, f[1]));
        }
    }

    private static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat), (int)Math.Floor(lon));

    /// <summary>The nearest place, or null when nothing is within 25 km (at sea, in the wilderness).</summary>
    public string? Name(double lat, double lon)
    {
        var (cy, cx) = Cell(lat, lon);
        string? best = null;
        var bestKm = MaxKm;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_grid.TryGetValue((cy + dy, cx + dx), out var list)) continue;
                foreach (var p in list)
                {
                    var km = Distance(lat, lon, p.Lat, p.Lon);
                    if (km < bestKm) (bestKm, best) = (km, p.Name);
                }
            }
        return best;
    }

    /// <summary>The place most of these positions are at (a day out: the photos of the resort, not of the drive).</summary>
    public string? MostCommon(IEnumerable<(double Lat, double Lon)> positions) =>
        positions.Select(p => Name(p.Lat, p.Lon)).OfType<string>()
            .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }
}
