using Fritz.Charlie.Common;

namespace Fritz.Charlie.Components.Services;

/// <summary>
/// Implementation of IChatterMapService that acts as a bridge between Web features and ChatterMapDirect component
/// </summary>
public class ChatterMapService //: IChatterMapService
{
    private readonly List<ViewerLocationEvent> _currentLocations = new();
    private readonly object _lockObject = new();

    /// <summary>
    /// Event fired when a new location is plotted on the map
    /// </summary>
    public event EventHandler<ViewerLocationEvent>? LocationPlotted;
    
    /// <summary>
    /// Event fired when a location is removed from the map
    /// </summary>
    public event EventHandler<Guid>? LocationRemoved;

    /// <summary>
    /// Plots a location on the map
    /// </summary>
    /// <param name="location">The location to plot</param>
    public Task PlotLocationAsync(ViewerLocationEvent location)
    {
        lock (_lockObject)
        {
            // Remove any existing location with the same ID
            _currentLocations.RemoveAll(l => l.Id == location.Id);
            
            // Add the new location
            _currentLocations.Add(location);
        }

        // Fire the event to notify subscribers (like ChatterMapDirect)
        LocationPlotted?.Invoke(this, location);
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes a location from the map
    /// </summary>
    /// <param name="locationId">The ID of the location to remove</param>
    public Task RemoveLocationAsync(Guid locationId)
    {
        bool removed;
        lock (_lockObject)
        {
            removed = _currentLocations.RemoveAll(l => l.Id == locationId) > 0;
        }

        if (removed)
        {
            // Fire the event to notify subscribers (like ChatterMapDirect)
            LocationRemoved?.Invoke(this, locationId);
        }
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all currently plotted locations
    /// </summary>
    /// <returns>Collection of currently plotted locations</returns>
    public Task<IReadOnlyCollection<ViewerLocationEvent>> GetCurrentLocationsAsync()
    {
        lock (_lockObject)
        {
            return Task.FromResult<IReadOnlyCollection<ViewerLocationEvent>>(_currentLocations.ToList());
        }
    }
}
