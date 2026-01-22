using Fritz.Charlie.Components.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class EmoteCleaningTest
{
    public static void Main()
    {
        var service = new LocationTextService(NullLogger<LocationTextService>.Instance);
        
        Console.WriteLine("Testing emote/emoji cleaning in location extraction:");
        Console.WriteLine();
        
        var testCases = new[]
        {
            // Regular cases (should work fine)
            "Hello from Seattle",
            "I'm from New York City",
            
            // Cases with emotes at the end
            "Hello from Seattle LUL",
            "I'm from Boston Kappa",
            "from Chicago 😀",
            "visiting Miami 🌴",
            
            // Cases with emotes in the middle  
            "Hello from Seattle LUL Washington",
            "I'm from New 😀 York",
            "from Chicago Kappa Illinois", 
            "visiting Miami PogChamp Florida",
            
            // Cases with multiple emotes
            "Hello from Seattle LUL Kappa 😀",
            "I'm from Boston PogChamp 🎉 Massachusetts",
            
            // Edge cases
            "Hello from csharpGritty Seattle",
            "I'm from New York csharpFritz",
            "from Chicago dotnetbot Illinois"
        };
        
        foreach (var testCase in testCases)
        {
            var result = service.GetLocationText(testCase);
            Console.WriteLine($"Input:  '{testCase}'");
            Console.WriteLine($"Output: '{result}'");
            Console.WriteLine();
        }
    }
}