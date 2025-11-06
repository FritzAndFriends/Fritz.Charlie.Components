namespace Fritz.Charlie.Components.Models;

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
                return 6371 * c; // Earth radius in kilometers
            });
        }
    }
}
