using Fritz.Charlie.Common;
using Fritz.Charlie.Components.Map;
using Fritz.Charlie.Components.Services;

namespace Fritz.Charlie.Components;

public partial class ChatterMapDirect : ComponentBase, IAsyncDisposable
{
    [Parameter] public int Height { get; set; } = 640;
    [Parameter] public string Width { get; set; } = "100%";
    [Parameter] public bool TestMode { get; set; } = false;
    [Parameter] public EventCallback OnMapInitialized { get; set; }
    [Parameter] public EventCallback<string> OnError { get; set; }
    [Parameter] public EventCallback<string> OnTestMessage { get; set; }
    [Parameter] public MapZoomLevel InitialZoom { get; set; } = MapZoomLevel.CountryView;
    [Parameter] public int MaxZoom { get; set; } = 6;
    [Parameter] public EventCallback<ViewerLocationEvent> OnLocationPlotted { get; set; }
    [Parameter] public EventCallback<Guid> OnLocationRemoved { get; set; }
    [Parameter] public IEnumerable<ViewerLocationEvent>? InitialLocations { get; set; }
    [Inject] public IViewerLocationService ViewerLocationService { get; set; } = null!;
    [Inject] public IJSRuntime JSRuntime { get; set; } = null!;
    private IJSObjectReference? mapModule;
    private bool mapInitialized = false;
    private string mapElementId = $"chatter-map-{Guid.NewGuid():N}";
    private string TestMessage { get; set; } = string.Empty;
    private string TestResult { get; set; } = string.Empty;

    [Parameter] public bool ShowTourControls { get; set; } = true;
    public bool IsTourActive { get; private set; } = false;
    public Dictionary<Guid, ViewerLocationEvent> TourLocations { get; } = new();
    private readonly Queue<ViewerLocationEvent> PendingLocationQueue = new();
    private object? CurrentTourStatus { get; set; }
    private ViewerLocationEvent? CurrentTourLocation { get; set; }
    private DotNetObjectReference<ChatterMapDirect>? dotNetObjectRef;
    private List<ClusterGroup> tourClusters = new();

    // NEW: Aggregation structures
    private readonly Dictionary<(double lat, double lng), AggregateLocation> aggregatedMarkers = new();
    private readonly Dictionary<Guid, (double lat, double lng)> locationKeyIndex = new();

    // NEW: Pin Announcement features
    [Parameter] public bool EnablePinAnnouncements { get; set; } = true;
    [Parameter] public int AnnouncementDurationMs { get; set; } = 5000;
    [Parameter] public EventCallback<ViewerLocationEvent> OnPinAnnounced { get; set; }
    
    private readonly Queue<ViewerLocationEvent> announcementQueue = new();
    private bool isAnnouncementActive = false;
  private bool isUserNavigating = false;
    private Timer? navigationDebounceTimer;
    private ViewerLocationEvent? currentAnnouncedLocation;
    private bool initialLoadComplete = false; // Track if initial load is done

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
 {
   try
       {
    ViewerLocationService.LocationPlotted += OnNewLocationFromService;
    ViewerLocationService.LocationRemoved += OnLocationRemovedFromService;

     mapModule = await JSRuntime.InvokeAsync<IJSObjectReference>("import",
     "./_content/Fritz.Charlie.Components/chattermap.js");

     await mapModule.InvokeVoidAsync("initializeMap", mapElementId, Height, Width, 39.8283, -98.5795, (int)InitialZoom, MaxZoom);
 mapInitialized = true;

          dotNetObjectRef = DotNetObjectReference.Create(this);
      await mapModule.InvokeVoidAsync("setDotNetReference", dotNetObjectRef);

          // Load InitialLocations parameter first (if any)
    if (InitialLocations != null)
    {
       await LoadMarkersAsync(InitialLocations);
            }

   // Call parent's initialization callback to load persisted/feature locations
      // This happens BEFORE marking initial load complete to prevent announcements
        await OnMapInitialized.InvokeAsync();

     // NOW mark initial load as complete - any NEW markers after this point will trigger announcements
           initialLoadComplete = true;
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
        // During tour, queue instead of immediate plot
            if (IsTourActive)
            {
             PendingLocationQueue.Enqueue(location);
     await InvokeAsync(StateHasChanged);
      return;
 }

    // Check if this exact location ID is already plotted (prevent duplicates)
          if (locationKeyIndex.ContainsKey(location.Id))
      {
    Console.WriteLine($"Skipping duplicate location {location.Id}");
  return;
    }

 // Aggregation key – 4 decimal places is ~11m resolution
            var key = (lat: Math.Round((double)location.Latitude, 4), lng: Math.Round((double)location.Longitude, 4));

        bool isNewMarker = !aggregatedMarkers.ContainsKey(key);

            if (aggregatedMarkers.TryGetValue(key, out var aggregate))
            {
     // Add to existing aggregate
                aggregate.Locations.Add(location);
         locationKeyIndex[location.Id] = key;

       // Update TourLocations with the new location
      if (!TourLocations.ContainsKey(location.Id))
    {
       TourLocations.Add(location.Id, location);
    }

            // Update marker (count + popup content)
         var popupContent = BuildAggregatedPopupContent(aggregate);
  await mapModule.InvokeVoidAsync("updateAggregatedMarker",
    aggregate.MarkerId,
          aggregate.Locations.Count,
         popupContent);

       await OnLocationPlotted.InvokeAsync(location);
     }
else
            {
 // Create new marker
          var markerId = location.Id.ToString();
     var description = Truncate(location.LocationDescription, 100);
     await mapModule.InvokeVoidAsync("addMarker",
        markerId,
           Math.Round((double)location.Latitude, 6),
         Math.Round((double)location.Longitude, 6),
         location.UserType,
      description,
            location.Service == "YouTube" ? "YouTube" : "Twitch",
      1); // initial count

     var newAggregate = new AggregateLocation(markerId, key.lat, key.lng);
 newAggregate.Locations.Add(location);
                aggregatedMarkers[key] = newAggregate;
        locationKeyIndex[location.Id] = key;

    // Add to TourLocations
    if (!TourLocations.ContainsKey(location.Id))
   {
     TourLocations.Add(location.Id, location);
}

                await OnLocationPlotted.InvokeAsync(location);
   }

    // NEW: Queue announcement for new markers if feature is enabled
            if (EnablePinAnnouncements && isNewMarker)
      {
   QueuePinAnnouncement(location);
      }
        }
      catch (Exception ex)
        {
            Console.WriteLine($"ERROR plotting location {location.Id}: {ex.Message}");
   }
    }

    // NEW: Queue a pin announcement
    private void QueuePinAnnouncement(ViewerLocationEvent location)
    {
        // Don't queue if initial load hasn't completed yet
        if (!initialLoadComplete)
        {
 Console.WriteLine($"Skipping announcement for {location.LocationDescription} (initial load not complete)");
        return;
        }

        // Don't queue if tour is active or user is navigating
        if (IsTourActive || isUserNavigating)
        {
            Console.WriteLine($"Skipping announcement for {location.LocationDescription} (tour active: {IsTourActive}, user navigating: {isUserNavigating})");
 return;
   }

 announcementQueue.Enqueue(location);
        Console.WriteLine($"Queued announcement for {location.LocationDescription}. Queue size: {announcementQueue.Count}");
        
        // Process queue if not already active
        if (!isAnnouncementActive)
        {
            _ = ProcessAnnouncementQueueAsync();
        }
    }

    // NEW: Process the announcement queue
    private async Task ProcessAnnouncementQueueAsync()
    {
        while (announcementQueue.Count > 0 && !IsTourActive && !isUserNavigating)
   {
       isAnnouncementActive = true;
       var location = announcementQueue.Dequeue();
    currentAnnouncedLocation = location;
 
            Console.WriteLine($"Processing announcement for {location.LocationDescription}");
            
            try
  {
            await ShowPinAnnouncementAsync(location);
                await OnPinAnnounced.InvokeAsync(location);
            }
   catch (Exception ex)
    {
     Console.WriteLine($"Error showing pin announcement: {ex.Message}");
     }
    
            currentAnnouncedLocation = null;
     await InvokeAsync(StateHasChanged);
     
   // Wait for announcement duration
    await Task.Delay(AnnouncementDurationMs);
  }
        
        isAnnouncementActive = false;
    }

    // NEW: Show pin announcement with celebration
    private async Task ShowPinAnnouncementAsync(ViewerLocationEvent location)
    {
        if (mapModule == null) return;
        
        try
        {
 await mapModule.InvokeVoidAsync("showPinCelebration",
   Math.Round((double)location.Latitude, 6),
                Math.Round((double)location.Longitude, 6),
        location.LocationDescription,
location.Service,
       location.UserType,
                AnnouncementDurationMs);
         
    Console.WriteLine($"Showing celebration for {location.LocationDescription}");
        }
 catch (Exception ex)
        {
     Console.WriteLine($"Error in ShowPinAnnouncementAsync: {ex.Message}");
     }
    }

    // NEW: Handle user navigation detection from JavaScript
    [JSInvokable]
    public Task OnUserNavigationStart()
{
        isUserNavigating = true;
    Console.WriteLine("User navigation started - pausing announcements");
   
        // Reset debounce timer
      navigationDebounceTimer?.Dispose();
        navigationDebounceTimer = null;
        
   return Task.CompletedTask;
    }

  // NEW: Handle user navigation end with debounce
    [JSInvokable]
    public Task OnUserNavigationEnd()
    {
        Console.WriteLine("User navigation ended - debouncing...");
        
      // Debounce navigation end to avoid rapid start/stop
        navigationDebounceTimer?.Dispose();
        navigationDebounceTimer = new Timer(_ =>
        {
       isUserNavigating = false;
   Console.WriteLine("User navigation debounce complete - resuming announcements");
            
    // Resume announcement queue processing if there are pending announcements
  if (announcementQueue.Count > 0 && !isAnnouncementActive && !IsTourActive)
       {
                _ = ProcessAnnouncementQueueAsync();
 }
        }, null, 2000, Timeout.Infinite); // 2 second debounce
    
return Task.CompletedTask;
    }

    private async Task StartMapTour()
    {
        if (!mapInitialized || mapModule == null || TourLocations.Count == 0) return;

        try
        {
            // Clear announcement queue when tour starts
            announcementQueue.Clear();
   isAnnouncementActive = false;
       currentAnnouncedLocation = null;
          
      var locationsList = TourLocations.Values.ToList();
            if (locationsList.Count > 200)
         {
         locationsList = locationsList.Take(200).ToList();
        }

      tourClusters = GenerateClusters(locationsList, forTour: true);
            
            // Log clustering results
         Console.WriteLine($"Generated {tourClusters.Count} clusters from {locationsList.Count} locations");
       foreach (var cluster in tourClusters)
{
         Console.WriteLine($"  Cluster: {cluster.Count} locations, center ({cluster.CenterLatitude:F2}, {cluster.CenterLongitude:F2}), avg distance: {cluster.AverageDistanceFromCenter:F0}km");
    }
     
            var arrangedClusters = ArrangeClustersByDistance(tourClusters);

      if (arrangedClusters.Count > 15)
{
          arrangedClusters = arrangedClusters.Take(15).ToList();
 }

        var tourStops = arrangedClusters.Select(cluster => new
            {
       lat = Math.Round(cluster.CenterLatitude, 6),
  lng = Math.Round(cluster.CenterLongitude, 6),
             zoom = DetermineZoomLevel(cluster),
              description = GetLocationDescription(cluster),
     locationCount = cluster.Locations.Count,
    locations = cluster.Locations.Take(10).Select(loc => new
   {
      description = Truncate(loc.LocationDescription, 50),
    lat = Math.Round((double)loc.Latitude, 6),
         lng = Math.Round((double)loc.Longitude, 6),
              userType = loc.UserType,
         service = loc.Service == "YouTube" ? "YouTube" : "Twitch"
   }).ToArray()
  }).ToArray();
         
    // Log tour stops with zoom levels
            Console.WriteLine($"Tour will visit {tourStops.Length} stops:");
 for (int i = 0; i < tourStops.Length; i++)
            {
                Console.WriteLine($"  Stop {i + 1}: {tourStops[i].description} - {tourStops[i].locationCount} locations, zoom level {tourStops[i].zoom}");
   }

            await mapModule.InvokeVoidAsync("startTour", (object)tourStops);
        IsTourActive = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
 Console.WriteLine($"ERROR starting tour: {ex.Message}");
       IsTourActive = false;
            CurrentTourLocation = null;
            StateHasChanged();
        }
    }

    private async Task StopMapTour()
    {
        if (!mapInitialized || mapModule == null) return;

      IsTourActive = false;
        CurrentTourLocation = null;
  await mapModule.InvokeVoidAsync("stopTour");
        await ProcessPendingLocations();
     
        // Resume announcement processing if queue has items
    if (announcementQueue.Count > 0 && !isAnnouncementActive && !isUserNavigating)
        {
 _ = ProcessAnnouncementQueueAsync();
        }
        
  StateHasChanged();
    }

    private async Task ProcessPendingLocations()
    {
        while (PendingLocationQueue.TryDequeue(out var location))
        {
            await PlotLocation(location);
        }
    }

    [JSInvokable]
    public async Task OnTourStatusChanged(bool isActive, int currentIndex, int totalLocations)
    {
        try
        {
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

            if (IsTourActive != isActive)
            {
                IsTourActive = isActive;
                if (!IsTourActive)
                {
                    await ProcessPendingLocations();
                }
                await InvokeAsync(StateHasChanged);
            }
            else if (IsTourActive)
            {
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

    // --- Improved clustering logic ---
    private List<ClusterGroup> GenerateClusters(List<ViewerLocationEvent> locations, bool forTour = false)
    {
        var validLocations = locations.Where(IsValidLocation).ToList();
        if (validLocations.Count == 0) return new List<ClusterGroup>();

    var clusters = new List<ClusterGroup>();
   var processed = new HashSet<Guid>();

        // Use 1000km for all clustering scenarios
        double clusterDistanceKm = 1000.0;

// Calculate density scores to prioritize high-density areas
        var locationsByDensity = CalculateDensityScores(validLocations, clusterDistanceKm);

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

              if (IsWithinDistance(current, nearby, clusterDistanceKm) &&
  IsSameRegion(current, nearby))  // Use region instead of continent
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
    /// Calculate density scores for each location based on nearby neighbors
    /// </summary>
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

  /// <summary>
    /// Check if two locations are in the same geographic region (more granular than continent)
    /// </summary>
    private bool IsSameRegion(ViewerLocationEvent loc1, ViewerLocationEvent loc2)
    {
        var region1 = GetRegionCode((double)loc1.Latitude, (double)loc1.Longitude);
        var region2 = GetRegionCode((double)loc2.Latitude, (double)loc2.Longitude);
        return region1 == region2;
    }

    /// <summary>
    /// Get more granular region codes with sub-regions for better clustering
    /// </summary>
    private string GetRegionCode(double latitude, double longitude)
    {
        // North America with sub-regions
        if (latitude >= 5 && longitude >= -180 && longitude <= -30)
        {
       if (longitude <= -125) return "NAM-WEST";       // West Coast (Pacific)
       if (longitude <= -105) return "NAM-MOUNTAIN";   // Mountain/Rockies
if (longitude <= -95) return "NAM-CENTRAL";     // Great Plains/Central
      if (longitude <= -80) return "NAM-MIDWEST";     // Midwest/Great Lakes
            return "NAM-EAST";                // East Coast (Atlantic)
 }

        // South America with sub-regions
        if (latitude >= -60 && latitude < 15 && longitude >= -85 && longitude <= -30)
{
      if (longitude <= -70) return "SAM-WEST";        // Western (Andes)
     if (latitude < -20) return "SAM-SOUTH";   // Southern cone
      return "SAM-EAST";   // Eastern (Brazil/Atlantic)
        }

        // Europe with sub-regions
 if (latitude >= 35 && longitude >= -10 && longitude <= 60)
        {
   if (longitude <= 15) return "EUR-WEST"; // Western Europe
      if (longitude <= 30) return "EUR-CENTRAL";      // Central Europe
            if (latitude >= 55) return "EUR-NORTH";         // Northern Europe
  if (latitude <= 45) return "EUR-SOUTH";  // Southern Europe
  return "EUR-EAST";        // Eastern Europe
        }

        // Africa with sub-regions
        if (latitude >= -35 && latitude <= 40 && longitude >= -20 && longitude <= 55)
        {
        if (latitude >= 15) return "AFR-NORTH";       // Northern Africa
 if (longitude <= 15) return "AFR-WEST";         // Western Africa
            if (longitude >= 35) return "AFR-EAST"; // Eastern Africa
            return "AFR-CENTRAL";       // Central Africa
        }

        // Asia with sub-regions
 if (latitude >= 0 && longitude >= 60 && longitude <= 180)
        {
         if (longitude <= 100) return "ASI-SOUTH";       // South Asia
 if (latitude >= 50) return "ASI-NORTH";         // Northern Asia (Siberia)
        if (longitude >= 140) return "ASI-EAST";    // East Asia
        if (latitude <= 30) return "ASI-SOUTHEAST";     // Southeast Asia
  return "ASI-CENTRAL";                // Central Asia
        }

        // Southeast Asia (additional coverage)
        if (latitude >= -10 && latitude <= 25 && longitude >= 90 && longitude <= 150)
 return "ASI-SOUTHEAST";

      // Oceania with sub-regions
        if (latitude >= -50 && latitude <= 0 && longitude >= 110 && longitude <= 180)
        {
     if (longitude >= 160) return "OCE-PACIFIC";   // Pacific Islands
 if (latitude >= -25) return "OCE-NORTH";        // Northern Australia
            return "OCE-SOUTH";        // Southern Australia
     }

  // Antarctica
      if (latitude < -60) return "ANT";

        // Ocean/undefined regions
      return "OCN";
    }

    private List<ClusterGroup> ArrangeClustersByDistance(List<ClusterGroup> clusters)
    {
        if (clusters.Count <= 1) return clusters;

 var arranged = new List<ClusterGroup>();
        var remaining = new List<ClusterGroup>(clusters);
        var centerLat = 39.8283;
        var centerLng = -98.5795;

   var current = remaining.OrderBy(c =>
            Math.Sqrt(Math.Pow(c.CenterLatitude - centerLat, 2) + Math.Pow(c.CenterLongitude - centerLng, 2)))
 .First();

        arranged.Add(current);
  remaining.Remove(current);

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

    private bool IsWithinDistance(ViewerLocationEvent loc1, ViewerLocationEvent loc2, double maxDistanceKm) =>
CalculateDistanceKm((double)loc1.Latitude, (double)loc1.Longitude,
          (double)loc2.Latitude, (double)loc2.Longitude) <= maxDistanceKm;

    private double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371;
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
                    return 6371 * c;
                });
            }
        }
    }

    // NEW: AggregateLocation helper
    private class AggregateLocation
    {
        public string MarkerId { get; }
        public double Lat { get; }
        public double Lng { get; }
        public List<ViewerLocationEvent> Locations { get; }

        public AggregateLocation(string markerId, double lat, double lng)
        {
            MarkerId = markerId;
            Lat = lat;
            Lng = lng;
            Locations = new List<ViewerLocationEvent>();
        }
    }

    private int DetermineZoomLevel(ClusterGroup cluster)
    {
      // For single location, zoom in to city level
        if (cluster.Count == 1)
 {
          return Math.Min(8, MaxZoom); // Street/building level for single pin
     }

        // Calculate the zoom level based on the actual geographic spread of the cluster
  // to ensure all pins are visible
   var avgDistance = cluster.AverageDistanceFromCenter;
        
        // Zoom levels for tight clustering:
        // - <10km: Neighborhood/street level (zoom 10-12)
  // - 10-50km: City level (zoom 8-10)
     // - 50-200km: Metropolitan area (zoom 6-8)
        // - 200-500km: Regional (zoom 5-6)
        // - 500-1000km: Multi-state/province (zoom 4-5)
        // - >1000km: Country level (zoom 3-4)
        
 if (avgDistance < 10)
        {
  return Math.Min(10, MaxZoom); // Very tight cluster - neighborhood level
        }
else if (avgDistance < 50)
      {
        return Math.Min(8, MaxZoom); // City level
        }
        else if (avgDistance < 200)
        {
            return Math.Min(7, MaxZoom); // Metropolitan area
        }
     else if (avgDistance < 500)
   {
       return Math.Min(5, MaxZoom); // Regional view
      }
   else if (avgDistance < 1000)
        {
     return Math.Min(4, MaxZoom); // Multi-region view
      }
        else
 {
     return Math.Min(3, MaxZoom); // Country/continent view
   }
    }

    private string GetLocationDescription(ClusterGroup cluster)
    {
        if (cluster.Count == 1)
        {
            return Truncate(cluster.Locations[0].LocationDescription, 100);
        }

        var regionName = GetContinentName(cluster.CenterLatitude, cluster.CenterLongitude);
        return $"{regionName} ({cluster.Count} viewers)";
    }

    public async Task AddMarkerAsync(ViewerLocationEvent location)
    {
        await PlotLocation(location);
        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearAllMarkersAsync()
    {
        if (!mapInitialized || mapModule == null) return;

        try
        {
            await mapModule.InvokeVoidAsync("clearMarkers");
            TourLocations.Clear();
            PendingLocationQueue.Clear();
            aggregatedMarkers.Clear();
            locationKeyIndex.Clear();
            Console.WriteLine("Cleared all markers");
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error clearing markers: {ex.Message}");
        }
    }

    public int GetMarkerCount() => aggregatedMarkers.Sum(a => a.Value.Locations.Count);

    public async Task RemoveMarkerAsync(Guid locationId) => await RemoveLocation(locationId);

    public async Task LoadMarkersAsync(IEnumerable<ViewerLocationEvent> locations)
    {
        if (!mapInitialized || mapModule == null) return;

        try
        {
            var validLocations = locations.Where(IsValidLocation).ToList();

            if (validLocations.Count > 500)
            {
                validLocations = validLocations.Take(500).ToList();
            }

            foreach (var location in validLocations)
            {
                await PlotLocation(location);
            }
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR loading locations: {ex.Message}");
        }
    }

    public async Task ZoomToLocationAsync(double latitude, double longitude, MapZoomLevel zoomLevel = MapZoomLevel.StateView)
    {
        if (!mapInitialized || mapModule == null) return;
        try
        {
            await mapModule.InvokeVoidAsync("zoomToLocation", latitude, longitude, (int)zoomLevel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error zooming to location: {ex.Message}");
        }
    }

    public async Task SetZoomLevelAsync(MapZoomLevel zoomLevel)
    {
        if (!mapInitialized || mapModule == null) return;
        try
        {
            await mapModule.InvokeVoidAsync("setZoom", (int)zoomLevel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting zoom level: {ex.Message}");
        }
    }

    public async Task<int> GetMaxZoomAsync()
    {
        if (!mapInitialized || mapModule == null) return MaxZoom;
        try
        {
            return await mapModule.InvokeAsync<int>("getMaxZoom");
        }
        catch
        {
            return MaxZoom;
        }
    }

    public async Task<bool> SetMaxZoomAsync(int maxZoom)
    {
        if (!mapInitialized || mapModule == null) return false;
        try
        {
            var result = await mapModule.InvokeAsync<bool>("setMaxZoom", maxZoom);
            if (result) MaxZoom = maxZoom;
            return result;
        }
        catch
        {
            return false;
        }
    }

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
            await OnTestMessage.InvokeAsync(TestMessage);
            var messageForDisplay = TestMessage;
            TestMessage = string.Empty;
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
        if (e.Key == "Enter") await ProcessTestMessage();
    }

    private async void OnNewLocationFromService(object? sender, ViewerLocationEvent viewerEvent)
    {
        var location = viewerEvent.ForDisplay();
     if (IsTourActive)
        {
   PendingLocationQueue.Enqueue(location);
       await InvokeAsync(StateHasChanged);
      return;
        }
        await PlotLocation(location);
     await InvokeAsync(StateHasChanged);
    }

    private async void OnLocationRemovedFromService(object? sender, Guid locationId)
    {
        if (IsTourActive)
        {
            TourLocations.Remove(locationId);
   var tempQueue = new Queue<ViewerLocationEvent>();
  while (PendingLocationQueue.TryDequeue(out var queuedLocation))
            {
          if (queuedLocation.Id != locationId)
      tempQueue.Enqueue(queuedLocation);
       }
    while (tempQueue.TryDequeue(out var q))
        {
  PendingLocationQueue.Enqueue(q);
        }
            await InvokeAsync(StateHasChanged);
return;
  }

        await RemoveLocation(locationId);
    await InvokeAsync(StateHasChanged);
    }

    private async Task RemoveLocation(Guid locationId)
    {
        if (!mapInitialized || mapModule == null) return;

        // Remove from TourLocations for tour purposes
   TourLocations.Remove(locationId);

        // Aggregation removal
 if (locationKeyIndex.TryGetValue(locationId, out var key) &&
            aggregatedMarkers.TryGetValue(key, out var aggregate))
        {
    var idx = aggregate.Locations.FindIndex(l => l.Id == locationId);
      if (idx >= 0)
            {
    aggregate.Locations.RemoveAt(idx);
       }
            locationKeyIndex.Remove(locationId);

     if (aggregate.Locations.Count == 0)
            {
    // Remove marker completely
await mapModule.InvokeVoidAsync("removeMarker", aggregate.MarkerId);
                aggregatedMarkers.Remove(key);
            }
    else
    {
 // Update existing aggregated marker with new count
            var popupContent = BuildAggregatedPopupContent(aggregate);
        await mapModule.InvokeVoidAsync("updateAggregatedMarker",
          aggregate.MarkerId,
         aggregate.Locations.Count,
  popupContent);
            }
        }
        else
        {
  // Fallback if not found in aggregation (legacy or mismatch)
  await mapModule.InvokeVoidAsync("removeMarker", locationId.ToString());
   }

        await OnLocationRemoved.InvokeAsync(locationId);
    }

    private string BuildAggregatedPopupContent(AggregateLocation aggregate)
{
        var count = aggregate.Locations.Count;
        // Show up to 5 distinct (service + userType) samples
        var serviceSummary = aggregate.Locations
            .GroupBy(l => l.Service)
            .Select(g => $"{g.Key}:{g.Count()}")
            .Take(3);

        var userTypeSummary = aggregate.Locations
            .GroupBy(l => l.UserType)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}")
            .Take(5);

        var firstDescription = aggregate.Locations.First().LocationDescription;
        return $"{Truncate(firstDescription, 60)}<br/>Viewers: {count}<br/>Services: {string.Join(", ", serviceSummary)}<br/>Roles: {string.Join(", ", userTypeSummary)}";
    }

    private string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
    }

    public async ValueTask DisposeAsync()
    {
     try
        {
     ViewerLocationService.LocationPlotted -= OnNewLocationFromService;
      ViewerLocationService.LocationRemoved -= OnLocationRemovedFromService;

  navigationDebounceTimer?.Dispose();
            navigationDebounceTimer = null;

      dotNetObjectRef?.Dispose();
   dotNetObjectRef = null;

   if (mapModule != null)
            {
  try { await mapModule.InvokeVoidAsync("dispose"); } catch { }
        try { await mapModule.DisposeAsync(); } catch { }
          }

       TourLocations.Clear();
            PendingLocationQueue.Clear();
   tourClusters.Clear();
          aggregatedMarkers.Clear();
            locationKeyIndex.Clear();
        announcementQueue.Clear();
     }
  catch (Exception ex)
        {
        Console.WriteLine($"WARNING: Error during component disposal: {ex.Message}");
        }
    }

    /// <summary>
    /// Optional custom icon provider for marker icons.
    /// If not provided, default icon logic will be used.
    /// </summary>
    [Inject]
    public IMapIconProvider? IconProvider { get; set; }

    /// <summary>
    /// Callback method invoked from JavaScript to get the icon URL for a marker
    /// </summary>
    /// <param name="userType">User type (broadcaster, moderator, subscriber, vip, user)</param>
    /// <param name="service">Service name (Twitch, YouTube, etc.)</param>
    /// <returns>Icon URL string or null to use JavaScript fallback</returns>
    [JSInvokable]
    public Task<string?> GetIconUrl(string userType, string service)
    {
        try
        {

            Console.WriteLine($"CustomIconProvider is of type : {(IconProvider != null ? IconProvider.GetType().FullName : "null")}");

            // If a custom icon provider is configured, use it first
            if (IconProvider != null)
            {
                Console.WriteLine($"Invoking custom icon provider for userType='{userType}', service='{service}'");
                var customIconUrl = IconProvider.GetIconUrl(userType, service);
                if (!string.IsNullOrEmpty(customIconUrl))
                {
                    Console.WriteLine($"Custom icon URL provided: {customIconUrl}");
                    return Task.FromResult<string?>(customIconUrl);
                }
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetIconUrl callback: {ex.Message}");
            // Return null on error to let JavaScript use fallback
        }
        Console.WriteLine($"No custom icon URL provided for userType='{userType}', service='{service}'. Using default.");
        return Task.FromResult<string?>(null);
    }

}

public enum MapZoomLevel
{
    WorldView = 1,
    ContinentalView = 2,
    CountryView = 3,
    RegionalView = 4,
    StateView = 5,
    CityView = 6,
    UrbanView = 7,
    StreetView = 8,
    BuildingView = 9,
    DetailView = 10
}