using Fritz.Charlie.Common;

namespace Fritz.Charlie.Components.Models;

/// <summary>
/// Represents a tour group provided by an external service (like TourGroupingService with AI region names).
/// This allows the ChatterMapDirect component to use custom grouping logic.
/// </summary>
public class ExternalTourGroup
{
	/// <summary>
	/// The individual locations in this group.
	/// </summary>
	public List<ViewerLocationEvent> Locations { get; set; } = new();

	/// <summary>
	/// The center latitude of the group.
	/// </summary>
	public double CenterLatitude { get; set; }

	/// <summary>
	/// The center longitude of the group.
	/// </summary>
	public double CenterLongitude { get; set; }

	/// <summary>
	/// The name for this region (e.g., "Iberian Peninsula", "Pacific Northwest").
	/// </summary>
	public string RegionName { get; set; } = string.Empty;

	/// <summary>
	/// The optimal zoom level to view all pins in this group.
	/// </summary>
	public int OptimalZoomLevel { get; set; }

	/// <summary>
	/// Number of locations in this group.
	/// </summary>
	public int LocationCount => Locations.Count;
}
