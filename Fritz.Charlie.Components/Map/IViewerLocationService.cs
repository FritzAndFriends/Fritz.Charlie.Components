using System.Threading.Tasks;
using System.Collections.Generic;

//namespace Fritz.Charlie.Common;
namespace Fritz.Charlie.Components.Map;

/// <summary>
/// Service interface for persisting and retrieving viewer location events.
/// </summary>
public interface IViewerLocationService
{
	/// <summary>
	/// Event fired when a new location is plotted on the map
	/// </summary>
	event EventHandler<ViewerLocationEvent>? LocationPlotted;
	
	/// <summary>
	/// Event fired when a location is removed from the map
	/// </summary>
	event EventHandler<Guid>? LocationRemoved;

	/// <summary>
	/// Persists a viewer location event.
	/// </summary>
	Task SaveLocationAsync(ViewerLocationEvent locationEvent);

	/// <summary>
	/// Retrieves all viewer location events for a given stream.
	/// </summary>
	Task<IReadOnlyList<ViewerLocationEvent>> GetLocationsForStreamAsync(string streamId);

	/// <summary>
	/// Clears all viewer location events for a given stream.
	/// </summary>
	Task ClearLocationsForStreamAsync(string streamId);

	/// <summary>
	/// Gets all available stream IDs that have viewer location data.
	/// </summary>
	Task<IReadOnlyList<string>> GetAllStreamIdsAsync();

	/// <summary>
	/// Plots a location in real-time and persists it
	/// </summary>
	/// <param name="locationEvent">The location event to plot and save</param>
	Task PlotLocationAsync(ViewerLocationEvent locationEvent);

	/// <summary>
	/// Removes a location from real-time display
	/// </summary>
	/// <param name="streamId">The stream ID from which to remove the location</param>
	/// <param name="userId">The user ID whose location to remove</param>
	Task RemoveLocationAsync(string streamId, string userId);
}
