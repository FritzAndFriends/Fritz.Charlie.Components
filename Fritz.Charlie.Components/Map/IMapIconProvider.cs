//namespace Fritz.Charlie.Common;
namespace Fritz.Charlie.Components.Map;

/// <summary>
/// Interface for providing custom marker icon URLs
/// </summary>
public interface IMapIconProvider
{
    /// <summary>
    /// Get the icon URL for a specific user type and service
    /// </summary>
    /// <param name="userType">User type (broadcaster, moderator, subscriber, vip, user)</param>
    /// <param name="service">Service name (Twitch, YouTube, etc.)</param>
    /// <returns>Icon URL or null to use default logic</returns>
    string? GetIconUrl(string userType, string service);
}
