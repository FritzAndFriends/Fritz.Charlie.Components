using Fritz.Charlie.Common;

namespace Fritz.Charlie.Components.Services;

/// <summary>
/// Extension methods for ViewerLocationEvent
/// </summary>
public static class ViewerLocationEventExtensions
{
    /// <summary>
    /// Checks if a ViewerLocationEvent is valid for plotting
    /// </summary>
    public static bool IsValid(this ViewerLocationEvent location)
    {
        if (location.Equals(ViewerLocationEvent.Unknown)) return false;
        if (location.Latitude == 0 && location.Longitude == 0) return false;
        if (Math.Abs((double)location.Latitude) < 1.0 && Math.Abs((double)location.Longitude) < 1.0) return false;
        if (Math.Abs((double)location.Latitude) > 90 || Math.Abs((double)location.Longitude) > 180) return false;
        return true;
    }
}
