using Fritz.Charlie.Components.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class DebugFriendsTest
{
    public static void Main()
    {
        var service = new LocationTextService(NullLogger<LocationTextService>.Instance);
        
        var testMessage = "visiting friends";
        Console.WriteLine($"Testing: '{testMessage}'");
        
        var result = service.GetLocationText(testMessage);
        Console.WriteLine($"Result: '{result}'");
        
        if (!string.IsNullOrEmpty(result))
        {
            Console.WriteLine("ERROR: Should have returned empty string!");
        }
        else
        {
            Console.WriteLine("SUCCESS: Correctly returned empty string");
        }
    }
}