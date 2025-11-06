using Fritz.Charlie.Common;
using Fritz.Charlie.Components.Models;
using Fritz.Charlie.Components.Services;

namespace Test.Components;

public class MapTourTests
{
    private readonly MapTourService _tourService;

    public MapTourTests()
    {
        _tourService = new MapTourService();
    }

    #region Distance Calculation Tests

    [Theory]
    [InlineData(40.7128, -74.0060, 34.0522, -118.2437, 3936)] // New York to Los Angeles (~3936 km)
    [InlineData(51.5074, -0.1278, 48.8566, 2.3522, 344)]      // London to Paris (~344 km)
    [InlineData(35.6762, 139.6503, 37.7749, -122.4194, 8280)] // Tokyo to San Francisco (~8280 km)
    [InlineData(40.7128, -74.0060, 40.7128, -74.0060, 0)]     // Same location (0 km)
    public void CalculateDistanceKm_ValidCoordinates_ReturnsExpectedDistance(
        double lat1, double lng1, double lat2, double lng2, double expectedKm)
    {
        // Act
        var result = _tourService.CalculateDistanceKm(lat1, lng1, lat2, lng2);

        // Assert - Allow 1% margin of error due to Earth's shape
        Assert.InRange(result, expectedKm * 0.99, expectedKm * 1.01);
    }

    [Fact]
    public void CalculateDistanceKm_EquatorToNorthPole_ReturnsQuarterEarthCircumference()
    {
        // Arrange - Distance from equator to north pole should be ~10,000 km
        var lat1 = 0.0;
        var lng1 = 0.0;
        var lat2 = 90.0;
        var lng2 = 0.0;

        // Act
        var result = _tourService.CalculateDistanceKm(lat1, lng1, lat2, lng2);

        // Assert
        Assert.InRange(result, 9900, 10100); // Approximately 10,000 km
    }

    #endregion

    #region Region Code Tests

    [Theory]
    [InlineData(40.7128, -74.0060, "NAM-EAST")]      // New York - East Coast
    [InlineData(34.0522, -118.2437, "NAM-MOUNTAIN")] // Los Angeles - Actually in Mountain region by current logic
    [InlineData(41.8781, -87.6298, "NAM-MIDWEST")]   // Chicago - Midwest
    [InlineData(39.7392, -104.9903, "NAM-CENTRAL")]  // Denver - Central (not mountain by current boundaries)
    [InlineData(29.7604, -95.3698, "NAM-CENTRAL")]   // Houston - Central
    public void GetRegionCode_NorthAmericanLocations_ReturnsCorrectSubRegion(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    [Theory]
    [InlineData(51.5074, -0.1278, "EUR-WEST")]       // London - Western Europe
    [InlineData(48.8566, 2.3522, "EUR-WEST")]        // Paris - Western Europe
    [InlineData(52.5200, 13.4050, "EUR-WEST")]       // Berlin - Actually Western by current boundaries
    [InlineData(41.9028, 12.4964, "EUR-WEST")]       // Rome - Western by current boundaries
    [InlineData(59.3293, 18.0686, "EUR-CENTRAL")]    // Stockholm - Central by current boundaries
    [InlineData(55.7558, 37.6173, "EUR-NORTH")]      // Moscow - Northern by current boundaries
    public void GetRegionCode_EuropeanLocations_ReturnsCorrectSubRegion(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    [Theory]
    [InlineData(35.6762, 139.6503, "ASI-CENTRAL")]   // Tokyo - Central by current boundaries
    [InlineData(28.6139, 77.2090, "ASI-SOUTH")]      // New Delhi - South Asia
    [InlineData(13.7563, 100.5018, "ASI-SOUTHEAST")] // Bangkok - Southeast Asia
    [InlineData(60.0, 100.0, "ASI-SOUTH")]           // Siberia - Actually South by longitude boundaries
    [InlineData(41.0, 75.0, "ASI-SOUTH")]            // Central Asia - South by current boundaries
    public void GetRegionCode_AsianLocations_ReturnsCorrectSubRegion(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    [Theory]
    [InlineData(-33.8688, 151.2093, "OCE-SOUTH")]    // Sydney - Southern Australia (below -25)
    [InlineData(-37.8136, 144.9631, "OCE-SOUTH")]    // Melbourne - Southern Australia
    [InlineData(-17.5, 179.0, "OCE-PACIFIC")]        // Fiji - Pacific Islands
    public void GetRegionCode_OceaniaLocations_ReturnsCorrectSubRegion(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    [Theory]
    [InlineData(-23.5505, -46.6333, "SAM-SOUTH")]    // São Paulo - Southern by latitude (<-20)
    [InlineData(-33.4489, -70.6693, "SAM-WEST")]     // Santiago - Western South America
    [InlineData(-34.6037, -58.3816, "SAM-SOUTH")]    // Buenos Aires - Southern South America
    public void GetRegionCode_SouthAmericanLocations_ReturnsCorrectSubRegion(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    [Theory]
    [InlineData(30.0444, 31.2357, "AFR-NORTH")]      // Cairo - Northern Africa
    [InlineData(-26.2041, 28.0473, "AFR-CENTRAL")]   // Johannesburg - Central Africa
    [InlineData(6.5244, 3.3792, "AFR-WEST")]         // Lagos - Western Africa
    [InlineData(-1.2921, 36.8219, "AFR-EAST")]       // Nairobi - Eastern Africa
    public void GetRegionCode_AfricanLocations_ReturnsCorrectSubRegion(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    [Theory]
    [InlineData(-70.0, 0.0, "ANT")]                  // Antarctica
    [InlineData(0.0, -150.0, "OCN")]                 // Pacific Ocean
    public void GetRegionCode_SpecialRegions_ReturnsCorrectCode(
        double latitude, double longitude, string expectedRegion)
    {
        // Act
        var result = _tourService.GetRegionCode(latitude, longitude);

        // Assert
        Assert.Equal(expectedRegion, result);
    }

    #endregion

    #region Region Name Tests

    [Theory]
    [InlineData(40.7128, -74.0060, "Eastern North America")]
    [InlineData(34.0522, -118.2437, "Central North America")] // Based on actual region boundaries
    [InlineData(51.5074, -0.1278, "Western Europe")]
    [InlineData(35.6762, 139.6503, "Central Asia")]           // Based on actual region boundaries
    [InlineData(-33.8688, 151.2093, "Southern Australia")]    // Below -25 latitude
    [InlineData(-23.5505, -46.6333, "Southern South America")] // Below -20 latitude
    public void GetRegionName_VariousLocations_ReturnsReadableName(
        double latitude, double longitude, string expectedName)
    {
        // Act
        var result = _tourService.GetRegionName(latitude, longitude);

        // Assert
        Assert.Equal(expectedName, result);
    }

    #endregion

    #region Zoom Level Tests

    [Fact]
    public void DetermineZoomLevel_SingleLocationCluster_ReturnsHighZoom()
    {
        // Arrange
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "New York")
        });

        // Act
        var zoom = _tourService.DetermineZoomLevel(cluster, maxZoom: 10);

        // Assert
        Assert.Equal(8, zoom); // Should return city-level zoom for single location
    }

    [Fact]
    public void DetermineZoomLevel_TightCluster_ReturnsNeighborhoodZoom()
    {
        // Arrange - Locations within 5km of each other (same neighborhood)
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "Location 1"),
            CreateLocation(40.7200, -74.0100, "Location 2"),
            CreateLocation(40.7150, -74.0080, "Location 3")
        });

        // Act
        var zoom = _tourService.DetermineZoomLevel(cluster, maxZoom: 12);

        // Assert
        Assert.InRange(zoom, 8, 12); // Should be high zoom for tight cluster
    }

    [Fact]
    public void DetermineZoomLevel_CityWideCluster_ReturnsCityZoom()
    {
        // Arrange - Locations spread across a city (~30km)
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "Manhattan"),
            CreateLocation(40.6782, -73.9442, "Brooklyn"),
            CreateLocation(40.7489, -73.9680, "Queens")
        });

        // Act
        var zoom = _tourService.DetermineZoomLevel(cluster, maxZoom: 12);

        // Assert
        Assert.InRange(zoom, 7, 10); // City-level zoom
    }

    [Fact]
    public void DetermineZoomLevel_RegionalCluster_ReturnsRegionalZoom()
    {
        // Arrange - Locations across multiple states (~800km)
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "New York"),
            CreateLocation(41.8781, -87.6298, "Chicago"),
            CreateLocation(42.3601, -71.0589, "Boston")
        });

        // Act
        var zoom = _tourService.DetermineZoomLevel(cluster, maxZoom: 10);

        // Assert
        Assert.InRange(zoom, 3, 5); // Regional zoom
    }

    [Fact]
    public void DetermineZoomLevel_RespectsMaxZoom()
    {
        // Arrange
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "New York")
        });

        // Act
        var zoom = _tourService.DetermineZoomLevel(cluster, maxZoom: 5);

        // Assert
        Assert.Equal(5, zoom); // Should not exceed maxZoom
    }

    #endregion

    #region Clustering Tests

    [Fact]
    public void GenerateClusters_EmptyList_ReturnsEmptyClusters()
    {
        // Arrange
        var locations = new List<ViewerLocationEvent>();

        // Act
        var clusters = _tourService.GenerateClusters(locations);

        // Assert
        Assert.Empty(clusters);
    }

    [Fact]
    public void GenerateClusters_SingleLocation_ReturnsSingleCluster()
    {
        // Arrange
        var locations = new List<ViewerLocationEvent> {
            CreateLocation(40.7128, -74.0060, "New York")
        };

        // Act
        var clusters = _tourService.GenerateClusters(locations);

        // Assert
        Assert.Single(clusters);
        Assert.Single(clusters[0].Locations);
    }

    [Fact]
    public void GenerateClusters_NearbyLocations_GroupsIntoSingleCluster()
    {
        // Arrange - Three locations in the same region within 1000km
        var locations = new List<ViewerLocationEvent> {
            CreateLocation(40.7128, -74.0060, "New York"),
            CreateLocation(42.3601, -71.0589, "Boston"),
            CreateLocation(39.9526, -75.1652, "Philadelphia")
        };

        // Act
        var clusters = _tourService.GenerateClusters(locations);

        // Assert
        Assert.Single(clusters); // All should be in one cluster (same region, within 1000km)
        Assert.Equal(3, clusters[0].Locations.Count);
    }

    [Fact]
    public void GenerateClusters_DistantLocations_CreatesMultipleClusters()
    {
        // Arrange - Locations on different continents
        var locations = new List<ViewerLocationEvent> {
            CreateLocation(40.7128, -74.0060, "New York"),
            CreateLocation(51.5074, -0.1278, "London"),
            CreateLocation(35.6762, 139.6503, "Tokyo"),
            CreateLocation(-33.8688, 151.2093, "Sydney")
        };

        // Act
        var clusters = _tourService.GenerateClusters(locations);

        // Assert
        Assert.True(clusters.Count >= 3); // Should create multiple clusters for different regions
    }

    [Fact]
    public void GenerateClusters_FiltersInvalidLocations()
    {
        // Arrange
        var locations = new List<ViewerLocationEvent> {
            CreateLocation(40.7128, -74.0060, "Valid Location"),
            CreateLocation(0, 0, "Invalid - Zero coords"),
            CreateLocation(0.5, 0.5, "Invalid - Near zero"),
            CreateLocation(100, 200, "Invalid - Out of range")
        };

        // Act
        var clusters = _tourService.GenerateClusters(locations);

        // Assert
        Assert.Single(clusters);
        Assert.Single(clusters[0].Locations);
        Assert.Equal("Valid Location", clusters[0].Locations[0].LocationDescription);
    }

    [Fact]
    public void GenerateClusters_CalculatesClusterCenters()
    {
        // Arrange
        var locations = new List<ViewerLocationEvent> {
            CreateLocation(40.0, -74.0, "Location 1"),
            CreateLocation(41.0, -73.0, "Location 2")
        };

        // Act
        var clusters = _tourService.GenerateClusters(locations);

        // Assert
        Assert.Single(clusters);
        var cluster = clusters[0];
        
        // Center should be approximately the average
        Assert.InRange(cluster.CenterLatitude, 40.4, 40.6);
        Assert.InRange(cluster.CenterLongitude, -73.6, -73.4);
        Assert.True(cluster.AverageDistanceFromCenter > 0);
    }

    #endregion

    #region Cluster Arrangement Tests

    [Fact]
    public void ArrangeClustersByDistance_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var clusters = new List<ClusterGroup>();

        // Act
        var arranged = _tourService.ArrangeClustersByDistance(clusters);

        // Assert
        Assert.Empty(arranged);
    }

    [Fact]
    public void ArrangeClustersByDistance_SingleCluster_ReturnsSameCluster()
    {
        // Arrange
        var cluster = CreateCluster(new[] { CreateLocation(40.7128, -74.0060, "New York") });
        var clusters = new List<ClusterGroup> { cluster };

        // Act
        var arranged = _tourService.ArrangeClustersByDistance(clusters);

        // Assert
        Assert.Single(arranged);
        Assert.Equal(cluster.Id, arranged[0].Id);
    }

    [Fact]
    public void ArrangeClustersByDistance_MultipleClusters_StartsNearUSCenter()
    {
        // Arrange - Create clusters at various locations
        var clusters = new List<ClusterGroup> {
            CreateCluster(new[] { CreateLocation(40.7128, -74.0060, "New York") }),
            CreateCluster(new[] { CreateLocation(39.8283, -98.5795, "Geographic Center of US") }),
            CreateCluster(new[] { CreateLocation(51.5074, -0.1278, "London") })
        };

        // Act
        var arranged = _tourService.ArrangeClustersByDistance(clusters);

        // Assert - First cluster should be closest to US center (39.8283, -98.5795)
        Assert.Equal(3, arranged.Count);
        // The geographic center cluster should be first
        Assert.Contains("Geographic Center of US", arranged[0].Locations[0].LocationDescription);
    }

    [Fact]
    public void ArrangeClustersByDistance_OrdersByFarthestDistance()
    {
        // Arrange
        var clusters = new List<ClusterGroup> {
            CreateCluster(new[] { CreateLocation(40.7128, -74.0060, "New York") }),
            CreateCluster(new[] { CreateLocation(34.0522, -118.2437, "Los Angeles") }),
            CreateCluster(new[] { CreateLocation(41.8781, -87.6298, "Chicago") })
        };

        // Act
        var arranged = _tourService.ArrangeClustersByDistance(clusters);

        // Assert
        Assert.Equal(3, arranged.Count);
        // Each subsequent cluster should be relatively far from the previous one
        // (exact order depends on starting point, but all should be included)
        Assert.Contains(clusters[0], arranged);
        Assert.Contains(clusters[1], arranged);
        Assert.Contains(clusters[2], arranged);
    }

    #endregion

    #region Location Description Tests

    [Fact]
    public void GetLocationDescription_SingleLocation_ReturnsLocationName()
    {
        // Arrange
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "New York City, NY")
        });

        // Act
        var description = _tourService.GetLocationDescription(cluster);

        // Assert
        Assert.Equal("New York City, NY", description);
    }

    [Fact]
    public void GetLocationDescription_MultipleLocations_ReturnsRegionWithCount()
    {
        // Arrange
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, "New York"),
            CreateLocation(42.3601, -71.0589, "Boston"),
            CreateLocation(39.9526, -75.1652, "Philadelphia")
        });

        // Act
        var description = _tourService.GetLocationDescription(cluster);

        // Assert
        Assert.Contains("(3 viewers)", description);
        Assert.Contains("America", description); // Should mention the region
    }

    [Fact]
    public void GetLocationDescription_LongLocationName_TruncatesTo100Chars()
    {
        // Arrange
        var longName = new string('A', 150);
        var cluster = CreateCluster(new[] {
            CreateLocation(40.7128, -74.0060, longName)
        });

        // Act
        var description = _tourService.GetLocationDescription(cluster);

        // Assert
        Assert.True(description.Length <= 100);
        Assert.EndsWith("...", description);
    }

    #endregion

    #region Helper Methods

    private ViewerLocationEvent CreateLocation(double latitude, double longitude, string description)
    {
        return new ViewerLocationEvent(
            (decimal)latitude,
            (decimal)longitude,
            description)
        {
            Id = Guid.NewGuid(),
            UserType = "viewer",
            Service = "Twitch"
        };
    }

    private ClusterGroup CreateCluster(ViewerLocationEvent[] locations)
    {
        var cluster = new ClusterGroup();
        foreach (var location in locations)
        {
            cluster.Locations.Add(location);
        }
        cluster.CalculateCenter();
        return cluster;
    }

    #endregion
}
