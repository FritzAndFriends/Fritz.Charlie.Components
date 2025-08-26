using System;

namespace Fritz.Charlie.Common;

/// <summary>
/// Represents a comprehensive viewer location for both real-time display and persistence.
/// Consolidates functionality from ChatterMapLocation and ViewerLocationEvent.
/// </summary>
public record struct ViewerLocationEvent(decimal Latitude, decimal Longitude, string LocationDescription, string Service = "Unknown", string UserType = "user")
{
	/// <summary>
	/// Unique identifier for this location instance
	/// </summary>
	public Guid Id { get; set; } = Guid.NewGuid();
	
	/// <summary>
	/// The UTC timestamp when the pin was plotted.
	/// </summary>
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// The hashed or anonymized user ID.
	/// </summary>
	public string UserId { get; set; } = string.Empty;

	/// <summary>
	/// The latitude of the viewer as double (for compatibility with existing persistence code).
	/// </summary>
	public readonly double LatitudeDouble => (double)Latitude;

	/// <summary>
	/// The longitude of the viewer as double (for compatibility with existing persistence code).
	/// </summary>
	public readonly double LongitudeDouble => (double)Longitude;

	/// <summary>
	/// The stream/session ID (see StreamId standard).
	/// </summary>
	public string StreamId { get; set; } = string.Empty;

	/// <summary>
	/// Static instance for unknown/invalid locations
	/// </summary>
	public readonly static ViewerLocationEvent Unknown = new ViewerLocationEvent(0, 0, "Unknown");

	/// <summary>
	/// Validates that this location has meaningful coordinates
	/// </summary>
	public readonly bool IsValid => 
		Latitude != 0 || Longitude != 0 && 
		Math.Abs((double)Latitude) <= 90 && 
		Math.Abs((double)Longitude) <= 180 &&
		!Equals(Unknown);

	/// <summary>
	/// Implicit conversion for tuple destructuring
	/// </summary>
	public static implicit operator (decimal, decimal, string)(ViewerLocationEvent value)
	{
		return (value.Latitude, value.Longitude, value.LocationDescription);
	}

	/// <summary>
	/// Implicit conversion from tuple
	/// </summary>
	public static implicit operator ViewerLocationEvent((decimal, decimal, string) value)
	{
		return new ViewerLocationEvent(value.Item1, value.Item2, value.Item3);
	}

	/// <summary>
	/// Creates a display-friendly version with new ID for UI purposes
	/// </summary>
	public readonly ViewerLocationEvent ForDisplay()
	{
		return this with { Id = Guid.NewGuid() };
	}

	/// <summary>
	/// Creates a persistence version with populated metadata
	/// </summary>
	public readonly ViewerLocationEvent ForPersistence(string userId, string streamId)
	{
		return this with 
		{ 
			UserId = userId, 
			StreamId = streamId, 
			Timestamp = DateTime.UtcNow 
		};
	}
}
