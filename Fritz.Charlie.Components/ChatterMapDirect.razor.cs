
using Fritz.Charlie.Common;
using Fritz.Charlie.Components.Map;
using Fritz.Charlie.Components.Services;

namespace Fritz.Charlie.Components;

public partial class ChatterMapDirect : ComponentBase, IAsyncDisposable
{
    [Parameter]
    public int Height { get; set; } = 640;

    [Parameter]
    public string Width { get; set; } = "100%";

    [Parameter]
    public bool TestMode { get; set; } = false;

    [Parameter] 
    public EventCallback OnMapInitialized { get; set; }
    
    [Parameter] 
    public EventCallback<string> OnError { get; set; }

    [Parameter]
    public EventCallback<string> OnTestMessage { get; set; }

    [Parameter]
    public MapZoomLevel InitialZoom { get; set; } = MapZoomLevel.CountryView;

    [Parameter]
    public int MaxZoom { get; set; } = 6;

    [Parameter]
    public EventCallback<ViewerLocationEvent> OnLocationPlotted { get; set; }

    [Parameter]
    public EventCallback<Guid> OnLocationRemoved { get; set; }

    [Parameter]
    public IEnumerable<ViewerLocationEvent>? InitialLocations { get; set; }
    
    [Inject]
    public IViewerLocationService ViewerLocationService { get; set; } = null!;
    
    [Inject]
    public IJSRuntime JSRuntime { get; set; } = null!;
    private IJSObjectReference? mapModule;
    private bool mapInitialized = false;
    private string mapElementId = $"chatter-map-{Guid.NewGuid():N}";
    
    // Test message properties
    private string TestMessage { get; set; } = string.Empty;
    private string TestResult { get; set; } = string.Empty;

	// Tour management
	[Parameter]
		public bool ShowTourControls { get; set; } = true;
    public bool IsTourActive { get; private set; } = false;
    public Dictionary<Guid, ViewerLocationEvent> TourLocations { get; } = new();
    private readonly Queue<ViewerLocationEvent> PendingLocationQueue = new();
    private object? CurrentTourStatus { get; set; }
    private ViewerLocationEvent? CurrentTourLocation { get; set; }
    private DotNetObjectReference<ChatterMapDirect>? dotNetObjectRef;
    private List<ClusterGroup> tourClusters = new();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                // Subscribe to location events from the service
                ViewerLocationService.LocationPlotted += OnNewLocationFromService;
                ViewerLocationService.LocationRemoved += OnLocationRemovedFromService;

                // Import the isolated JavaScript module
                mapModule = await JSRuntime.InvokeAsync<IJSObjectReference>("import", 
                    "./_content/Fritz.Charlie.Components/chattermap.js");
                
                // Initialize the map with the specific element ID, dimensions, and configurable max zoom
                await mapModule.InvokeVoidAsync("initializeMap", mapElementId, Height, Width, 39.8283, -98.5795, (int)InitialZoom, MaxZoom);
                mapInitialized = true;

                // Create .NET object reference for JavaScript callbacks
                dotNetObjectRef = DotNetObjectReference.Create(this);
                await mapModule.InvokeVoidAsync("setDotNetReference", dotNetObjectRef);

                // Load initial locations if provided
                if (InitialLocations != null)
                {
                    await LoadMarkersAsync(InitialLocations);
                }
              
                // Notify parent that map is ready
                await OnMapInitialized.InvokeAsync();
            }
            catch (Exception ex)
            {
                await OnError.InvokeAsync($"Error initializing map: {ex.Message}");
            }
        }
    }

    private async Task PlotLocation(ViewerLocationEvent location)
    {
        if (!IsValidLocation(location) || !mapInitialized || mapModule == null) return;

        try
        {
            // Check for duplicate locations by coordinates to prevent memory bloat
            if (TourLocations.Values.Any(existingLocation => 
                Math.Abs((double)(existingLocation.Latitude - location.Latitude)) < 0.0001 &&
                Math.Abs((double)(existingLocation.Longitude - location.Longitude)) < 0.0001))
            {
                Console.WriteLine($"WARNING: Duplicate location at {location.Latitude:F4}, {location.Longitude:F4} ({location.LocationDescription}), skipping");
                return;
            }

            // Also check by ID for exact same instance
            if (TourLocations.ContainsKey(location.Id))
            {
                Console.WriteLine($"WARNING: Duplicate location ID {location.Id}, skipping");
                return;
            }

            // Limit total locations to prevent performance issues
            if (TourLocations.Count >= 1000)
            {
                Console.WriteLine($"WARNING: Maximum location limit reached (1000), removing oldest location");
                var oldestLocation = TourLocations.Values.First(); // Remove first added (FIFO)
                await RemoveLocation(oldestLocation.Id);
            }

            TourLocations.Add(location.Id, location);
            await mapModule.InvokeVoidAsync("addMarker",
                location.Id.ToString(),
                Math.Round((double)location.Latitude, 6),
                Math.Round((double)location.Longitude, 6),
                location.UserType,
                location.LocationDescription?.Length > 100 ? location.LocationDescription.Substring(0, 97) + "..." : location.LocationDescription,
                location.Service == "YouTube" ? "YouTube" : "Twitch");
                
            // Notify parent component that a location was plotted
            await OnLocationPlotted.InvokeAsync(location);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR plotting location {location.Id}: {ex.Message}");
        }
    }

    private async Task RemoveLocation(Guid locationId)
    {
        if (!mapInitialized || mapModule == null) return;

        TourLocations.Remove(locationId);
        await mapModule.InvokeVoidAsync("removeMarker", locationId.ToString());
        
        // Notify parent component that a location was removed
        await OnLocationRemoved.InvokeAsync(locationId);
    }

    private async Task StartMapTour()
    {
        if (!mapInitialized || mapModule == null || TourLocations.Count == 0) return;

        try
        {
            // Limit tour to reasonable number of locations to prevent JSInterop issues
            var locationsList = TourLocations.Values.ToList();
            if (locationsList.Count > 200)
            {
                Console.WriteLine($"WARNING: Large number of locations ({locationsList.Count}), limiting to 200 for tour performance");
                locationsList = locationsList.Take(200).ToList();
            }

            // Create tour stops using clustering logic from actual map data
            tourClusters = GenerateClusters(locationsList, forTour: true);
            var arrangedClusters = ArrangeClustersByDistance(tourClusters);
            
            // Limit tour stops to prevent excessive tour duration and JSInterop payload size
            if (arrangedClusters.Count > 15)
            {
                Console.WriteLine($"WARNING: Large number of tour stops ({arrangedClusters.Count}), limiting to 15 for performance");
                arrangedClusters = arrangedClusters.Take(15).ToList();
            }
            
            // Convert clusters to tour stops for JavaScript - limit location details to prevent large payloads
            var tourStops = arrangedClusters.Select(cluster => new 
            { 
                lat = Math.Round(cluster.CenterLatitude, 6),
                lng = Math.Round(cluster.CenterLongitude, 6),
                zoom = DetermineZoomLevel(cluster),
                description = GetLocationDescription(cluster),
                locationCount = cluster.Locations.Count,
                // Only include first few locations to limit payload size
                locations = cluster.Locations.Take(10).Select(loc => new
                {
                    description = loc.LocationDescription?.Length > 50 ? loc.LocationDescription.Substring(0, 47) + "..." : loc.LocationDescription,
                    lat = Math.Round((double)loc.Latitude, 6),
                    lng = Math.Round((double)loc.Longitude, 6),
                    userType = loc.UserType,
                    service = loc.Service == "YouTube" ? "YouTube" : "Twitch"
                }).ToArray()
            }).ToArray();
            
            Console.WriteLine($"DEBUG: About to start tour with {tourStops.Length} stops from {TourLocations.Count} locations");
            foreach (var stop in tourStops)
            {
                Console.WriteLine($"  Stop: {stop.description} at {stop.lat:F4}, {stop.lng:F4} (zoom {stop.zoom}) - {stop.locationCount} locations");
            }
            
            // Call JavaScript with clustered tour stops BEFORE setting IsTourActive
            await mapModule.InvokeVoidAsync("startTour", (object)tourStops);
            
            // Set tour active after JavaScript call succeeds
            IsTourActive = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR starting tour: {ex.Message}");
            // Reset tour state on error
            IsTourActive = false;
            CurrentTourLocation = null;
            StateHasChanged();
        }
    }

    private int DetermineZoomLevel(ClusterGroup cluster)
    {
        // Determine appropriate zoom level based on cluster size and spread, respecting MaxZoom
        int targetZoom;
        if (cluster.Count == 1) targetZoom = 8; // Individual location - zoom in close
        else if (cluster.AverageDistanceFromCenter < 50) targetZoom = 6; // Tight cluster - medium zoom
        else if (cluster.AverageDistanceFromCenter < 200) targetZoom = 5; // Moderate spread - wider view
        else targetZoom = 4; // Large spread - wide view
        
        return Math.Min(targetZoom, MaxZoom); // Respect the configured max zoom
    }

    private string GetLocationDescription(ClusterGroup cluster)
    {
        if (cluster.Count == 1)
        {
            return cluster.Locations.First().LocationDescription;
        }

        var continent = GetContinentName(cluster.CenterLatitude, cluster.CenterLongitude);
        return $"{continent} Region ({cluster.Count} locations)";
    }

    private async Task StopMapTour()
    {
        if (!mapInitialized || mapModule == null) return;

        IsTourActive = false;
        CurrentTourLocation = null;
        await mapModule.InvokeVoidAsync("stopTour");
        
        // Process pending locations
        await ProcessPendingLocations();
        StateHasChanged();
    }

    private async Task ProcessPendingLocations()
    {
        while (PendingLocationQueue.TryDequeue(out var location))
        {
            await PlotLocation(location);
        }
    }

    /// <summary>
    /// Called by JavaScript when the tour status changes
    /// </summary>
    [JSInvokable]
    public async Task OnTourStatusChanged(bool isActive, int currentIndex, int totalLocations)
    {
        try
        {
            Console.WriteLine($"Tour status callback: active={isActive}, index={currentIndex}, total={totalLocations}");
            
            // Update current tour location if tour is active
            if (isActive && currentIndex > 0 && currentIndex <= tourClusters.Count)
            {
                var currentCluster = tourClusters[currentIndex - 1];
                CurrentTourLocation = new ViewerLocationEvent(
                    (decimal)Math.Round(currentCluster.CenterLatitude, 6),
                    (decimal)Math.Round(currentCluster.CenterLongitude, 6),
                    GetLocationDescription(currentCluster))
                {
                    Id = Guid.NewGuid()
                };
            }
            else
            {
                CurrentTourLocation = null;
            }
            
            // Check if tour status has changed
            if (IsTourActive != isActive)
            {
                Console.WriteLine($"Tour status changed: {IsTourActive} -> {isActive}");
                IsTourActive = isActive;
                
                if (!IsTourActive)
                {
                    Console.WriteLine("Tour ended - processing pending locations");
                    await ProcessPendingLocations();
                }
                
                await InvokeAsync(StateHasChanged);
            }
            else if (IsTourActive)
            {
                // Update UI if tour is active to show progress
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in OnTourStatusChanged: {ex.Message}");
        }
    }

    private bool IsValidLocation(ViewerLocationEvent location)
    {
        if (location.Equals(ViewerLocationEvent.Unknown)) return false;
        if (location.Latitude == 0 && location.Longitude == 0) return false;
        if (Math.Abs((double)location.Latitude) < 1.0 && Math.Abs((double)location.Longitude) < 1.0) return false;
        if (Math.Abs((double)location.Latitude) > 90 || Math.Abs((double)location.Longitude) > 180) return false;
        return true;
    }

    #region Clustering Logic (Extracted from original implementation)
    
    private List<ClusterGroup> GenerateClusters(List<ViewerLocationEvent> locations, bool forTour = false)
    {
        var validLocations = locations.Where(IsValidLocation).ToList();
        if (validLocations.Count == 0) return new List<ClusterGroup>();

        var clusters = new List<ClusterGroup>();
        var processed = new HashSet<Guid>();
        
        double clusterDistanceKm = forTour ? 2000.0 : // 1250 miles for tour - increased for better visual grouping
            validLocations.Count switch
            {
                <= 10 => 500.0,  // Increased from 150km
                <= 25 => 800.0,  // Increased from 200km  
                <= 50 => 1200.0, // Increased from 300km
                _ => 1500.0      // Increased from 500km
            };

        foreach (var location in validLocations)
        {
            if (processed.Contains(location.Id)) continue;

            var cluster = new ClusterGroup();
            cluster.Locations.Add(location);
            processed.Add(location.Id);

            foreach (var otherLocation in validLocations)
            {
                if (processed.Contains(otherLocation.Id)) continue;

                if (IsWithinDistance(location, otherLocation, clusterDistanceKm) && 
                    IsSameContinent(location, otherLocation))
                {
                    cluster.Locations.Add(otherLocation);
                    processed.Add(otherLocation.Id);
                }
            }

            cluster.CalculateCenter();
            clusters.Add(cluster);
        }

        return clusters;
    }

    private List<ClusterGroup> ArrangeClustersByDistance(List<ClusterGroup> clusters)
    {
        if (clusters.Count <= 1) return clusters;

        var arranged = new List<ClusterGroup>();
        var remaining = new List<ClusterGroup>(clusters);

        // Start with the cluster closest to the center of the US
        var centerLat = 39.8283;
        var centerLng = -98.5795;
        
        var current = remaining.OrderBy(c => 
            Math.Sqrt(Math.Pow(c.CenterLatitude - centerLat, 2) + Math.Pow(c.CenterLongitude - centerLng, 2)))
            .First();
        
        arranged.Add(current);
        remaining.Remove(current);

        // Greedily select the farthest cluster from the current one
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

    private bool IsWithinDistance(ViewerLocationEvent loc1, ViewerLocationEvent loc2, double maxDistanceKm)
    {
        return CalculateDistanceKm((double)loc1.Latitude, (double)loc1.Longitude,
                                  (double)loc2.Latitude, (double)loc2.Longitude) <= maxDistanceKm;
    }

    private bool IsSameContinent(ViewerLocationEvent loc1, ViewerLocationEvent loc2)
    {
        var continent1 = GetContinentCode((double)loc1.Latitude, (double)loc1.Longitude);
        var continent2 = GetContinentCode((double)loc2.Latitude, (double)loc2.Longitude);
        return continent1 == continent2;
    }

    private string GetContinentCode(double latitude, double longitude)
    {
        // Return continent codes to prevent cross-ocean clustering
        
        // South America (check first to avoid overlap with North America)
        if (latitude >= -60 && latitude < 15 && longitude >= -85 && longitude <= -30)
        {
            return "SAM";
        }
        
        // North America (including Central America and Caribbean)
        if (latitude >= 5 && longitude >= -180 && longitude <= -30)
        {
            return "NAM";
        }
        
        // Europe (including European Russia west of Urals)
        if (latitude >= 35 && longitude >= -10 && longitude <= 60)
        {
            return "EUR";
        }
        
        // Africa
        if (latitude >= -35 && latitude <= 40 && longitude >= -20 && longitude <= 55)
        {
            return "AFR";
        }
        
        // Asia (including Asian Russia east of Urals)
        if (latitude >= 0 && longitude >= 60 && longitude <= 180)
        {
            return "ASI";
        }
        
        // Southeast Asia and Indonesia (special case to separate from mainland Asia)
        if (latitude >= -10 && latitude <= 25 && longitude >= 90 && longitude <= 150)
        {
            return "SEA";
        }
        
        // Australia and Oceania
        if (latitude >= -50 && latitude <= 0 && longitude >= 110 && longitude <= 180)
        {
            return "OCE";
        }
        
        // Antarctica
        if (latitude < -60)
        {
            return "ANT";
        }
        
        // Default to ocean/unknown - these won't cluster with anything
        return "OCN";
    }

    private double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371; // Earth's radius in km
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private string GetContinentName(double latitude, double longitude)
    {
        // Enhanced continent detection with directional specificity
        // Using same boundaries as GetContinentCode to ensure consistency
        
        // South America with directional subdivisions (check first to avoid overlap with North America)
        if (latitude >= -60 && latitude < 15 && longitude >= -85 && longitude <= -30)
        {
            if (longitude <= -70) return "Western South America"; // Chile, Peru, Western areas
            if (latitude < -20) return "Southern South America";  // Argentina, Southern areas
            return "Eastern South America"; // Brazil, Eastern areas
        }
        
        // North America with directional subdivisions (including Central America and Caribbean)
        if (latitude >= 5 && longitude >= -180 && longitude <= -30)
        {
            if (longitude <= -130) return "Western North America"; // Alaska, Western Canada, US West Coast
            if (longitude <= -95) return "Central North America";  // Central US/Canada
            if (longitude <= -60) return "Eastern North America";  // US East Coast, Eastern Canada
            return "Northern North America"; // Greenland, Arctic
        }
        
        // Europe with directional subdivisions (including European Russia west of Urals)
        if (latitude >= 35 && longitude >= -10 && longitude <= 60)
        {
            if (longitude <= 15) return "Western Europe";    // UK, France, Spain, etc.
            if (longitude <= 30) return "Central Europe";    // Germany, Poland, etc.
            if (latitude >= 55) return "Northern Europe";    // Scandinavia, Baltics
            if (latitude <= 45) return "Southern Europe";    // Italy, Greece, Balkans
            return "Eastern Europe"; // Russia, Eastern countries
        }
        
        // Africa with directional subdivisions
        if (latitude >= -35 && latitude <= 40 && longitude >= -20 && longitude <= 55)
        {
            if (latitude >= 15) return "Northern Africa";    // Egypt, Libya, Morocco, etc.
            if (longitude <= 15) return "Western Africa";    // Nigeria, Ghana, etc.
            if (longitude >= 35) return "Eastern Africa";    // Kenya, Ethiopia, etc.
            return "Central Africa"; // Congo, Central African Republic, etc.
        }
        
        // Asia with directional subdivisions (including Asian Russia east of Urals)
        if (latitude >= 0 && longitude >= 60 && longitude <= 180)
        {
            if (longitude <= 100) return "South Asia";       // India, Pakistan, etc.
            if (latitude >= 50) return "Northern Asia";      // Siberia, Mongolia
            if (longitude >= 140) return "East Asia";        // Japan, Korea, Eastern China
            if (latitude <= 30) return "Southeast Asia";     // Thailand, Indonesia, etc.
            return "Central Asia"; // China, Kazakhstan, etc.
        }
        
        // Southeast Asia and Indonesia (special case)
        if (latitude >= -10 && latitude <= 25 && longitude >= 90 && longitude <= 150)
        {
            return "Southeast Asia";
        }
        
        // Australia/Oceania with subdivisions
        if (latitude >= -50 && latitude <= 0 && longitude >= 110 && longitude <= 180)
        {
            if (longitude >= 160) return "Pacific Islands";  // Fiji, New Zealand area
            if (latitude >= -25) return "Northern Australia"; // Northern territories
            return "Southern Australia"; // Most of populated Australia
        }
        
        // Antarctica
        if (latitude < -60)
        {
            return "Antarctica";
        }
        
        return "Ocean Region";
    }

    #endregion

    #region Support Classes

    public class ClusterGroup
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<ViewerLocationEvent> Locations { get; } = new();
        public double CenterLatitude { get; private set; }
        public double CenterLongitude { get; private set; }
        public double AverageDistanceFromCenter { get; private set; }
        public int Count => Locations.Count;
        public string PrimaryUserType => Locations.GroupBy(l => l.UserType).OrderByDescending(g => g.Count()).First().Key;

        public void CalculateCenter()
        {
            if (Locations.Count == 0) return;
            
            CenterLatitude = Locations.Average(l => (double)l.Latitude);
            CenterLongitude = Locations.Average(l => (double)l.Longitude);
            
            if (Locations.Count > 1)
            {
                AverageDistanceFromCenter = Locations.Average(l =>
                {
                    var dLat = ((double)l.Latitude - CenterLatitude) * Math.PI / 180;
                    var dLng = ((double)l.Longitude - CenterLongitude) * Math.PI / 180;
                    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                            Math.Cos(CenterLatitude * Math.PI / 180) * Math.Cos((double)l.Latitude * Math.PI / 180) *
                            Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
                    var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
                    return 6371 * c; // Earth's radius in km
                });
            }
        }
    }

    public class TourStop
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
        public int Zoom { get; set; }
        public string Description { get; set; } = string.Empty;
        public TourLocation[] Locations { get; set; } = Array.Empty<TourLocation>();
    }

    public class TourLocation
    {
        public string Description { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lng { get; set; }
        public string UserType { get; set; } = string.Empty;
        public string Service { get; set; } = string.Empty;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Adds a marker to the map
    /// </summary>
    /// <param name="location">The location to add to the map</param>
    public async Task AddMarkerAsync(ViewerLocationEvent location)
    {
        await PlotLocation(location);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Clears all markers from the map
    /// </summary>
    public async Task ClearAllMarkersAsync()
    {
        if (!mapInitialized || mapModule == null) return;

        try
        {
            await mapModule.InvokeVoidAsync("clearMarkers");
            TourLocations.Clear();
            PendingLocationQueue.Clear();
            
            Console.WriteLine("Cleared all markers");
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error clearing markers: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current marker count
    /// </summary>
    public int GetMarkerCount()
    {
        return TourLocations.Count;
    }

    /// <summary>
    /// Removes a marker from the map
    /// </summary>
    /// <param name="locationId">The ID of the location to remove</param>
    public async Task RemoveMarkerAsync(Guid locationId)
    {
        await RemoveLocation(locationId);
    }

    /// <summary>
    /// Loads multiple markers onto the map
    /// </summary>
    /// <param name="locations">The locations to add to the map</param>
    public async Task LoadMarkersAsync(IEnumerable<ViewerLocationEvent> locations)
    {
        if (!mapInitialized || mapModule == null) return;

        try
        {
            var validLocations = locations.Where(IsValidLocation).ToList();
            
            // Limit initial load to prevent performance issues
            if (validLocations.Count > 500)
            {
                Console.WriteLine($"WARNING: Large number of locations ({validLocations.Count}), limiting initial load to 500");
                validLocations = validLocations.Take(500).ToList();
            }

            foreach (var location in validLocations)
            {
                // Check for duplicate locations by coordinates to prevent memory bloat
                if (TourLocations.Values.Any(existingLocation => 
                    Math.Abs((double)(existingLocation.Latitude - location.Latitude)) < 0.0001 &&
                    Math.Abs((double)(existingLocation.Longitude - location.Longitude)) < 0.0001))
                {
                    Console.WriteLine($"WARNING: Duplicate location at {location.Latitude:F4}, {location.Longitude:F4} ({location.LocationDescription}), skipping during batch load");
                    continue;
                }

                // Also check by ID for exact same instance
                if (TourLocations.ContainsKey(location.Id))
                {
                    Console.WriteLine($"WARNING: Duplicate location ID {location.Id}, skipping during batch load");
                    continue;
                }

                TourLocations.Add(location.Id, location);
                await mapModule.InvokeVoidAsync("addMarker", 
                    location.Id.ToString(), 
                    Math.Round((double)location.Latitude, 6), 
                    Math.Round((double)location.Longitude, 6),
                    location.UserType, 
                    location.LocationDescription?.Length > 100 ? location.LocationDescription.Substring(0, 97) + "..." : location.LocationDescription,
                    location.Service == "YouTube" ? "YouTube" : "Twitch");
            }
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR loading locations: {ex.Message}");
        }
    }

    /// <summary>
    /// Zooms the map to a specific location with smooth animation
    /// </summary>
    /// <param name="latitude">The latitude to zoom to</param>
    /// <param name="longitude">The longitude to zoom to</param>
    /// <param name="zoomLevel">The zoom level (default is StateView)</param>
    public async Task ZoomToLocationAsync(double latitude, double longitude, MapZoomLevel zoomLevel = MapZoomLevel.StateView)
    {
        if (!mapInitialized || mapModule == null) return;

        try
        {
            await mapModule.InvokeVoidAsync("zoomToLocation", latitude, longitude, (int)zoomLevel);
            Console.WriteLine($"Zoomed to location: {latitude:F4}, {longitude:F4} at zoom level {zoomLevel} ({(int)zoomLevel})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error zooming to location: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the map zoom level
    /// </summary>
    /// <param name="zoomLevel">The zoom level to set</param>
    public async Task SetZoomLevelAsync(MapZoomLevel zoomLevel)
    {
        if (!mapInitialized || mapModule == null) return;

        try
        {
            await mapModule.InvokeVoidAsync("setZoom", (int)zoomLevel);
            Console.WriteLine($"Set map zoom level to {zoomLevel} ({(int)zoomLevel})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting zoom level: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current maximum zoom level
    /// </summary>
    public async Task<int> GetMaxZoomAsync()
    {
        if (!mapInitialized || mapModule == null) return MaxZoom;

        try
        {
            return await mapModule.InvokeAsync<int>("getMaxZoom");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting max zoom level: {ex.Message}");
            return MaxZoom;
        }
    }

    /// <summary>
    /// Sets the maximum zoom level after initialization
    /// </summary>
    /// <param name="maxZoom">The maximum zoom level to set</param>
    public async Task<bool> SetMaxZoomAsync(int maxZoom)
    {
        if (!mapInitialized || mapModule == null) return false;

        try
        {
            var result = await mapModule.InvokeAsync<bool>("setMaxZoom", maxZoom);
            if (result)
            {
                MaxZoom = maxZoom; // Update the parameter value
                Console.WriteLine($"Updated max zoom level to: {maxZoom}");
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting max zoom level: {ex.Message}");
            return false;
        }
    }

    #endregion

    // Test message handling methods - these will be exposed as parameters/callbacks for parent component to handle
    private async Task ProcessTestMessage()
    {
        if (string.IsNullOrWhiteSpace(TestMessage))
        {
            TestResult = "Please enter a test message";
            StateHasChanged();
            return;
        }

        try
        {
            TestResult = $"Processing location for: '{TestMessage}'...";
            StateHasChanged();

            // Send the test message to parent component for geocoding and processing
            await OnTestMessage.InvokeAsync(TestMessage);
            
            var messageForDisplay = TestMessage; // Save the message before clearing
            TestMessage = string.Empty; // Clear the input
            TestResult = $"Message sent for geocoding '{messageForDisplay}'";
        }
        catch (Exception ex)
        {
            TestResult = $"Error: {ex.Message}";
        }

        StateHasChanged();
    }

    public void UpdateTestResult(string result)
    {
        TestResult = result;
        StateHasChanged();
    }

    private async Task HandleTestMessageKeyPress(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await ProcessTestMessage();
        }
    }

    #region Service Event Handlers

    private async void OnNewLocationFromService(object? sender, ViewerLocationEvent viewerEvent)
    {
        // Use the ViewerLocationEvent directly, create new ID for display
        var location = viewerEvent.ForDisplay();

        if (IsTourActive)
        {
            // During tour, queue the location instead of adding immediately
            PendingLocationQueue.Enqueue(location);
            Console.WriteLine($"Tour active: Queued location {location.LocationDescription} ({PendingLocationQueue.Count} pending)");
            await InvokeAsync(StateHasChanged); // Update UI to show queued count
            return;
        }

        await PlotLocation(location);
        await InvokeAsync(StateHasChanged);
    }

    private async void OnLocationRemovedFromService(object? sender, Guid locationId)
    {
        if (IsTourActive)
        {
            // During tour, just remove from our collections but don't refresh map
            TourLocations.Remove(locationId);
            // Also remove from queue if it's there (though unlikely)
            var tempQueue = new Queue<ViewerLocationEvent>();
            while (PendingLocationQueue.TryDequeue(out var queuedLocation))
            {
                if (queuedLocation.Id != locationId)
                {
                    tempQueue.Enqueue(queuedLocation);
                }
            }
            // Put back the non-removed items
            while (tempQueue.TryDequeue(out var queuedLocation))
            {
                PendingLocationQueue.Enqueue(queuedLocation);
            }
            Console.WriteLine($"Tour active: Removed location {locationId} from collections");
            await InvokeAsync(StateHasChanged);
            return;
        }

        await RemoveLocation(locationId);
        await InvokeAsync(StateHasChanged);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Unsubscribe from service events
            ViewerLocationService.LocationPlotted -= OnNewLocationFromService;
            ViewerLocationService.LocationRemoved -= OnLocationRemovedFromService;

            // Dispose the .NET object reference
            dotNetObjectRef?.Dispose();
            dotNetObjectRef = null;
            
            // Since we no longer subscribe to Feature events directly, no need to unsubscribe
            
            // Dispose JavaScript module
            if (mapModule != null)
            {
                try
                {
                    await mapModule.InvokeVoidAsync("dispose");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: Error disposing map module: {ex.Message}");
                }
                
                try
                {
                    await mapModule.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: Error disposing JSObjectReference: {ex.Message}");
                }
            }
            
            // Clear collections to help GC
            TourLocations.Clear();
            PendingLocationQueue.Clear();
            tourClusters.Clear();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Error during component disposal: {ex.Message}");
        }
    }
}

/// <summary>
/// Defines the zoom levels available for the map with descriptive labels
/// </summary>
public enum MapZoomLevel
{
    /// <summary>
    /// World view - shows entire continents (zoom level 1)
    /// </summary>
    WorldView = 1,
    
    /// <summary>
    /// Continental view - shows large regions like North America (zoom level 2)
    /// </summary>
    ContinentalView = 2,
    
    /// <summary>
    /// Country view - shows entire countries (zoom level 3)
    /// </summary>
    CountryView = 3,
    
    /// <summary>
    /// Regional view - shows states/provinces (zoom level 4)
    /// </summary>
    RegionalView = 4,
    
    /// <summary>
    /// State view - shows individual states or large metropolitan areas (zoom level 5)
    /// </summary>
    StateView = 5,
    
    /// <summary>
    /// City view - shows cities and surrounding areas (zoom level 6)
    /// </summary>
    CityView = 6,
    
    /// <summary>
    /// Urban view - shows urban areas and neighborhoods (zoom level 7)
    /// </summary>
    UrbanView = 7,
    
    /// <summary>
    /// Street view - shows detailed street-level view (zoom level 8)
    /// </summary>
    StreetView = 8,
    
    /// <summary>
    /// Building view - shows individual buildings (zoom level 9)
    /// </summary>
    BuildingView = 9,
    
    /// <summary>
    /// Detail view - maximum zoom for detailed viewing (zoom level 10)
    /// </summary>
    DetailView = 10
}
