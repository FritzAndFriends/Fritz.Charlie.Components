using Fritz.Charlie.Components.Models;

namespace Fritz.Charlie.Components.Services;

/// <summary>
/// Service responsible for managing map tours, including clustering locations,
/// determining optimal tour routes, and calculating appropriate zoom levels.
/// </summary>
public class MapTourService
{
    private const double ClusterDistanceKm = 1000.0;
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Generates clusters of locations based on geographic proximity and regional grouping.
    /// Uses density-based clustering to create meaningful groupings for tour stops.
    /// </summary>
    /// <param name="locations">List of viewer locations to cluster</param>
    /// <returns>List of cluster groups</returns>
    public List<ClusterGroup> GenerateClusters(List<ViewerLocationEvent> locations)
    {
        var validLocations = locations.Where(IsValidLocation).ToList();
        if (validLocations.Count == 0) return new List<ClusterGroup>();

        var clusters = new List<ClusterGroup>();
        var processed = new HashSet<Guid>();

        // Calculate density scores to prioritize high-density areas
        var locationsByDensity = CalculateDensityScores(validLocations, ClusterDistanceKm);

        // Process locations by density (highest first) to create better clusters
        foreach (var location in locationsByDensity.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key))
        {
            if (processed.Contains(location.Id)) continue;

            var cluster = new ClusterGroup();
            var toProcess = new Queue<ViewerLocationEvent>();
            toProcess.Enqueue(location);

            // BFS-style expansion: find all connected locations within range
            while (toProcess.Count > 0)
            {
                var current = toProcess.Dequeue();
                if (processed.Contains(current.Id)) continue;

                cluster.Locations.Add(current);
                processed.Add(current.Id);

                // Find all nearby unprocessed locations and add them to the queue
                foreach (var nearby in validLocations)
                {
                    if (processed.Contains(nearby.Id)) continue;

                    if (IsWithinDistance(current, nearby, ClusterDistanceKm) &&
                        IsSameRegion(current, nearby))
                    {
                        toProcess.Enqueue(nearby);
                    }
                }
            }

            cluster.CalculateCenter();
            clusters.Add(cluster);
        }

        return clusters;
    }

    /// <summary>
    /// Arranges clusters in an optimal tour order, starting from US center and
    /// moving to the farthest cluster each time for maximum visual impact.
    /// </summary>
    /// <param name="clusters">List of cluster groups to arrange</param>
    /// <returns>Ordered list of clusters for the tour</returns>
    public List<ClusterGroup> ArrangeClustersByDistance(List<ClusterGroup> clusters)
    {
        if (clusters.Count <= 1) return clusters;

        var arranged = new List<ClusterGroup>();
        var remaining = new List<ClusterGroup>(clusters);
        var centerLat = 39.8283; // Geographic center of continental US
        var centerLng = -98.5795;

        // Start with cluster closest to US center
        var current = remaining.OrderBy(c =>
            Math.Sqrt(Math.Pow(c.CenterLatitude - centerLat, 2) + Math.Pow(c.CenterLongitude - centerLng, 2)))
            .First();

        arranged.Add(current);
        remaining.Remove(current);

        // Each iteration, jump to the farthest cluster from current position
        while (remaining.Count > 0)
        {
            var farthest = remaining.OrderByDescending(c =>
                Math.Sqrt(Math.Pow(c.CenterLatitude - current.CenterLatitude, 2) +
                    Math.Pow(c.CenterLongitude - current.CenterLongitude, 2)))
                .First();

            arranged.Add(farthest);
            remaining.Remove(farthest);
            current = farthest;
        }

        return arranged;
    }

    /// <summary>
    /// Determines the appropriate zoom level for a cluster based on its size and spread.
    /// </summary>
    /// <param name="cluster">The cluster group to analyze</param>
    /// <param name="maxZoom">Maximum allowed zoom level</param>
    /// <returns>Appropriate zoom level (1-10)</returns>
    public int DetermineZoomLevel(ClusterGroup cluster, int maxZoom = 6)
    {
        // For single location, zoom in to city level
        if (cluster.Count == 1)
        {
            return Math.Min(8, maxZoom);
        }

        var avgDistance = cluster.AverageDistanceFromCenter;

        // Zoom levels based on geographic spread:
        // <10km: Neighborhood level (10-12)
        // 10-50km: City level (8-10)
        // 50-200km: Metropolitan area (6-8)
        // 200-500km: Regional (5-6)
        // 500-1000km: Multi-state (4-5)
        // >1000km: Country level (3-4)

        if (avgDistance < 10)
            return Math.Min(10, maxZoom);
        else if (avgDistance < 50)
            return Math.Min(8, maxZoom);
        else if (avgDistance < 200)
            return Math.Min(7, maxZoom);
        else if (avgDistance < 500)
            return Math.Min(5, maxZoom);
        else if (avgDistance < 1000)
            return Math.Min(4, maxZoom);
        else
            return Math.Min(3, maxZoom);
    }

    /// <summary>
    /// Gets a human-readable description for a cluster based on its location.
    /// </summary>
    /// <param name="cluster">The cluster to describe</param>
    /// <returns>Descriptive string for the cluster location</returns>
    public string GetLocationDescription(ClusterGroup cluster)
    {
        if (cluster.Count == 1)
        {
            return Truncate(cluster.Locations[0].LocationDescription, 100);
        }

        var regionName = GetRegionName(cluster.CenterLatitude, cluster.CenterLongitude);
        return $"{regionName} ({cluster.Count} viewers)";
    }

    /// <summary>
    /// Calculates the distance in kilometers between two geographic coordinates.
    /// Uses the Haversine formula for accurate distance calculation on a sphere.
    /// </summary>
    public double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    /// <summary>
    /// Gets the region code for more granular geographic classification.
    /// </summary>
    public string GetRegionCode(double latitude, double longitude)
    {
        // North America with sub-regions
        if (latitude >= 5 && longitude >= -180 && longitude <= -30)
        {
            if (longitude <= -125) return "NAM-WEST";       // West Coast (Pacific)
            if (longitude <= -105) return "NAM-MOUNTAIN";   // Mountain/Rockies
            if (longitude <= -95) return "NAM-CENTRAL";     // Great Plains/Central
            if (longitude <= -80) return "NAM-MIDWEST";     // Midwest/Great Lakes
            return "NAM-EAST";                              // East Coast (Atlantic)
        }

        // South America with sub-regions
        if (latitude >= -60 && latitude < 15 && longitude >= -85 && longitude <= -30)
        {
            if (longitude <= -70) return "SAM-WEST";        // Western (Andes)
            if (latitude < -20) return "SAM-SOUTH";         // Southern cone
            return "SAM-EAST";                              // Eastern (Brazil/Atlantic)
        }

        // Europe with sub-regions
        if (latitude >= 35 && longitude >= -10 && longitude <= 60)
        {
            if (longitude <= 15) return "EUR-WEST";         // Western Europe
            if (longitude <= 30) return "EUR-CENTRAL";      // Central Europe
            if (latitude >= 55) return "EUR-NORTH";         // Northern Europe
            if (latitude <= 45) return "EUR-SOUTH";         // Southern Europe
            return "EUR-EAST";                              // Eastern Europe
        }

        // Africa with sub-regions
        if (latitude >= -35 && latitude <= 40 && longitude >= -20 && longitude <= 55)
        {
            if (latitude >= 15) return "AFR-NORTH";         // Northern Africa
            if (longitude <= 15) return "AFR-WEST";         // Western Africa
            if (longitude >= 35) return "AFR-EAST";         // Eastern Africa
            return "AFR-CENTRAL";                           // Central Africa
        }

        // Asia with sub-regions
        if (latitude >= 0 && longitude >= 60 && longitude <= 180)
        {
            if (longitude <= 100) return "ASI-SOUTH";       // South Asia
            if (latitude >= 50) return "ASI-NORTH";         // Northern Asia (Siberia)
            if (longitude >= 140) return "ASI-EAST";        // East Asia
            if (latitude <= 30) return "ASI-SOUTHEAST";     // Southeast Asia
            return "ASI-CENTRAL";                           // Central Asia
        }

        // Southeast Asia (additional coverage)
        if (latitude >= -10 && latitude <= 25 && longitude >= 90 && longitude <= 150)
            return "ASI-SOUTHEAST";

        // Oceania with sub-regions
        if (latitude >= -50 && latitude <= 0 && longitude >= 110 && longitude <= 180)
        {
            if (longitude >= 160) return "OCE-PACIFIC";     // Pacific Islands
            if (latitude >= -25) return "OCE-NORTH";        // Northern Australia
            return "OCE-SOUTH";                             // Southern Australia
        }

        // Antarctica
        if (latitude < -60) return "ANT";

        // Ocean/undefined regions
        return "OCN";
    }

    /// <summary>
    /// Gets a human-readable region name from coordinates.
    /// </summary>
    public string GetRegionName(double latitude, double longitude)
    {
        if (latitude >= -60 && latitude < 15 && longitude >= -85 && longitude <= -30)
        {
            if (longitude <= -70) return "Western South America";
            if (latitude < -20) return "Southern South America";
            return "Eastern South America";
        }
        if (latitude >= 5 && longitude >= -180 && longitude <= -30)
        {
            if (longitude <= -130) return "Western North America";
            if (longitude <= -95) return "Central North America";
            if (longitude <= -60) return "Eastern North America";
            return "Northern North America";
        }
        if (latitude >= 35 && longitude >= -10 && longitude <= 60)
        {
            if (longitude <= 15) return "Western Europe";
            if (longitude <= 30) return "Central Europe";
            if (latitude >= 55) return "Northern Europe";
            if (latitude <= 45) return "Southern Europe";
            return "Eastern Europe";
        }
        if (latitude >= -35 && latitude <= 40 && longitude >= -20 && longitude <= 55)
        {
            if (latitude >= 15) return "Northern Africa";
            if (longitude <= 15) return "Western Africa";
            if (longitude >= 35) return "Eastern Africa";
            return "Central Africa";
        }
        if (latitude >= 0 && longitude >= 60 && longitude <= 180)
        {
            if (longitude <= 100) return "South Asia";
            if (latitude >= 50) return "Northern Asia";
            if (longitude >= 140) return "East Asia";
            if (latitude <= 30) return "Southeast Asia";
            return "Central Asia";
        }
        if (latitude >= -10 && latitude <= 25 && longitude >= 90 && longitude <= 150) return "Southeast Asia";
        if (latitude >= -50 && latitude <= 0 && longitude >= 110 && longitude <= 180)
        {
            if (longitude >= 160) return "Pacific Islands";
            if (latitude >= -25) return "Northern Australia";
            return "Southern Australia";
        }
        if (latitude < -60) return "Antarctica";
        return "Ocean Region";
    }

    #region Private Helper Methods

    private bool IsValidLocation(ViewerLocationEvent location)
    {
        if (location.Equals(ViewerLocationEvent.Unknown)) return false;
        if (location.Latitude == 0 && location.Longitude == 0) return false;
        if (Math.Abs((double)location.Latitude) < 1.0 && Math.Abs((double)location.Longitude) < 1.0) return false;
        if (Math.Abs((double)location.Latitude) > 90 || Math.Abs((double)location.Longitude) > 180) return false;
        return true;
    }

    private Dictionary<ViewerLocationEvent, int> CalculateDensityScores(
        List<ViewerLocationEvent> locations, double radiusKm)
    {
        var densityScores = new Dictionary<ViewerLocationEvent, int>();

        foreach (var location in locations)
        {
            int nearbyCount = locations.Count(other =>
                other.Id != location.Id &&
                IsWithinDistance(location, other, radiusKm));
            densityScores[location] = nearbyCount;
        }

        return densityScores;
    }

    private bool IsSameRegion(ViewerLocationEvent loc1, ViewerLocationEvent loc2)
    {
        var region1 = GetRegionCode((double)loc1.Latitude, (double)loc1.Longitude);
        var region2 = GetRegionCode((double)loc2.Latitude, (double)loc2.Longitude);
        return region1 == region2;
    }

    private bool IsWithinDistance(ViewerLocationEvent loc1, ViewerLocationEvent loc2, double maxDistanceKm)
    {
        return CalculateDistanceKm((double)loc1.Latitude, (double)loc1.Longitude,
            (double)loc2.Latitude, (double)loc2.Longitude) <= maxDistanceKm;
    }

    private string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
    }

    #endregion
}
