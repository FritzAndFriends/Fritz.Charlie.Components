using Fritz.Charlie.Components.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Components;

public class LocationTests
{
    public static TheoryData<string, string> LocationExtractionTestData =>
        new TheoryData<string, string>
        {
            // Basic "from" patterns
            { "I'm from New York", "new york" },
            { "I am from Los Angeles, California", "los angeles, california" },
            { "from Chicago here", "chicago" },
            { "Hello from Seattle", "seattle" },
            { "Hey from Boston", "boston" },
            { "Good morning from London", "london" },
            { "Good afternoon from Tokyo", "tokyo" },
            { "Good evening from Paris", "paris" },
            { "Greetings from Berlin", "berlin" },
            { "Greetings and salutations csharpfritz and chat from Pueblo, CO", "pueblo, co" },
            { "Greetings and salutations from Denver", "denver" },
            { "Greetings and salutations everyone from New York City", "new york city" },
            // New scenarios from user (2025-08-18)
            { "hello from Virginia Beach, happy Tuesday!", "virginia beach" },
            { "Morning from Virginia.", "virginia" },
            { "Morning from Pittsburgh where we are currently the temperature of the sun", "pittsburgh" },
            
            // Venue-specific patterns (enhanced regex)
            { "hey there from a cafe in St. Louis", "st. louis" },
            { "Hello from a restaurant in Miami", "miami" },
            { "from the hotel in Las Vegas", "las vegas" },
            { "from a shop in Portland", "portland" },
            { "I'm at a store in Denver", "denver" },
            
            // Current residence patterns
            { "I live in Phoenix", "phoenix" },
            { "living in Austin Texas", "austin texas" },
            { "currently living in Nashville", "nashville" },
            { "now living in Orlando", "orlando" },
            { "based in San Francisco", "san francisco" },
            { "located in Atlanta", "atlanta" },
            { "residing in Philadelphia", "philadelphia" },
            
            // Origin patterns
            { "originally from Detroit", "detroit" },
            { "born in Cleveland", "cleveland" },
            { "grew up in Dallas", "dallas" },
            { "raised in Houston", "houston" },
            { "native of Minneapolis", "minneapolis" },
            { "moved from Tampa to here", "tampa" },
            { "came from Pittsburgh here", "pittsburgh" },
            
            // Location-first patterns
            { "Sacramento represent!", "sacramento" },
            { "Portland here!", "portland" },
            { "Kansas City checking in", "kansas city" },
            { "Milwaukee tuning in", "milwaukee" },
            { "Columbus watching", "columbus" },
            { "Richmond is where I'm from", "richmond" },
            { "Buffalo is my home", "buffalo" },
            
            // Weather/time patterns
            { "It's cold here in Minneapolis", "minneapolis" },
            { "its hot in Phoenix today", "phoenix" },
            { "currently 75 degrees in San Diego", "san diego" },
            { "right now 32°F in Chicago", "chicago" },
            { "The weather is beautiful here in Hawaii", "hawaii" },
            { "It's 3am here in New York", "new york" },
            { "It's morning here in London", "london" },
            { "Its midnight in Tokyo", "tokyo" },
            
            // Travel patterns
            { "visiting Paris this week", "paris" },
            { "traveled to Rome yesterday", "rome" },
            { "just arrived in Barcelona", "barcelona" },
            { "on vacation in Cancun", "cancun" },
            { "staying in Amsterdam", "amsterdam" },
            { "currently visiting Prague", "prague" },
            
            // Time zone patterns
            { "my time zone is EST (New York)", "new york" },
            { "time zone is PST Los Angeles", "los angeles" },
            
            // Casual mentions
            { "here in Canada we love hockey", "canada" },
            { "we in Texas have great BBQ", "texas" },
            { "love the California weather", "california" },
            { "enjoying Florida sunshine", "florida" },
            { "typical Michigan winter", "michigan" },
            
            // Complex multi-word locations
            { "from New York City", "new york city" },
            { "I'm from San Francisco, CA", "san francisco, ca" },
            { "hello from Los Angeles County", "los angeles county" },
            { "from United States of America", "united states of america" },
            { "I live in North Carolina", "north carolina" },
            { "based in South Dakota", "south dakota" },

					// trolling from Elliface
						{ "watching from the democratic republic of the congo, petting cheetas or whatever they do there", "democratic republic of the congo" },
						{ "hhello from the Bermuda triangle", "bermuda triangle" },
            
            // International locations
					{ "from London, England", "london, england" },
            { "I'm from Toronto, Canada", "toronto, canada" },
            { "hello from Sydney, Australia", "sydney, australia" },
            { "from Mumbai, India", "mumbai, india" },
            { "I live in São Paulo, Brazil", "são paulo, brazil" },
            { "based in Stockholm, Sweden", "stockholm, sweden" },
            
            // State/National Park patterns
            { "Hello from Rock Cut State park near Rockford, IL", "rockford, il" },
            { "Hey there from Starved Rock State Park near Ottawa, IL", "ottawa, il" },
            { "At Devil's Lake State Park near Baraboo, Wisconsin", "baraboo, wisconsin" },
            { "Visiting Yellowstone National Park near West Yellowstone, MT", "west yellowstone, mt" },
            { "In Great Smoky Mountains National Park near Gatlinburg, TN", "gatlinburg, tn" },
            { "from the Redwood National Forest near Crescent City, CA", "crescent city, ca" },
            
            // Edge cases with punctuation
            { "I'm from Boston.", "boston" },
            { "from Seattle!", "seattle" },
            { "Hello from Denver?", "denver" },
            { "from Miami; great city", "miami" },
            { "I'm from Portland: love it here", "portland" }
        };

    public static TheoryData<string> InvalidLocationExtractionTestData =>
        new TheoryData<string>
        {
            // Messages with no location indicators
            { "Hello everyone!" },
            { "How's the stream going?" },
            { "Love your content!" },
            { "What are you coding today?" },
            { "Thanks for the tutorial" },
            
            // Messages with location keywords but no actual locations
            { "I'm from work" },
            { "hello from the stream" },
            { "currently tired" },
            { "visiting friends" },
            { "it's cold today" },
            { "the weather is nice" },
            
            // Messages that are too short
            { "hi" },
            { "hey" },
            { "lol" },
            { "nice" },
            
            // Messages with only invalid terms
            { "from the good place" },
            { "I live in the moment" },
            { "based on my experience" },
            { "located in time and space" },
            { "currently very happy" },
            
            // URLs or technical terms
            { "from https://example.com" },
            { "I'm from github.com/user" },
            { "visiting http://localhost:3000" },
            
            // Bot-like messages
            { "System message from bot" },
            { "Automated response from server" },
            
            // Ambiguous locations
            { "from home" },
            { "I'm here" },
            { "currently there" },
            { "visiting this place" }
        };

    [Theory]
    [MemberData(nameof(LocationExtractionTestData))]
    public void ExtractLocationName_ValidLocationMessages_ReturnsExpectedLocation(string message, string expectedLocation)
    {
        // Arrange
        var testableFeature = new LocationTextService(NullLogger<LocationTextService>.Instance);

        // Act
        var result = testableFeature.GetLocationText(message);

        // Assert
        Assert.Equal(expectedLocation, result);
    }

    [Theory]
    [MemberData(nameof(InvalidLocationExtractionTestData))]
    public void ExtractLocationName_InvalidLocationMessages_ReturnsEmptyString(string message)
    {
        // Arrange
        var testableFeature = new LocationTextService(NullLogger<LocationTextService>.Instance);

        // Act
        var result = testableFeature.GetLocationText(message);

        // Assert
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("I'm from NEW YORK", "new york")] // Test case sensitivity
    [InlineData("I'm from New York City!!!", "new york city")] // Multiple punctuation
    [InlineData("   from Chicago   ", "chicago")] // Whitespace handling
    [InlineData("Hello from São Paulo", "são paulo")] // Unicode characters
    [InlineData("from O'Fallon, Missouri", "o'fallon, missouri")] // Apostrophes
    [InlineData("from St. Petersburg, FL", "st. petersburg, fl")] // Periods in names
    public void ExtractLocationName_EdgeCases_HandlesCorrectly(string message, string expectedLocation)
    {
        // Arrange
        var testableFeature = new LocationTextService(NullLogger<LocationTextService>.Instance);

        // Act
        var result = testableFeature.GetLocationText(message);

        // Assert
        Assert.Equal(expectedLocation, result);
    }

    [Fact]
    public void ExtractLocationName_EmptyMessage_ReturnsEmptyString()
    {
        // Arrange
        var testableFeature = new LocationTextService(NullLogger<LocationTextService>.Instance);

        // Act & Assert
        Assert.Empty(testableFeature.GetLocationText(""));
        Assert.Empty(testableFeature.GetLocationText("   "));
        Assert.Empty(testableFeature.GetLocationText(null!));
    }

    [Fact]
    public void ExtractLocationName_VeryShortMessage_ReturnsEmptyString()
    {
        // Arrange
        var testableFeature = new LocationTextService(NullLogger<LocationTextService>.Instance);

        // Act & Assert
        Assert.Empty(testableFeature.GetLocationText("hi"));
        Assert.Empty(testableFeature.GetLocationText("ok"));
        Assert.Empty(testableFeature.GetLocationText("yes"));
        Assert.Empty(testableFeature.GetLocationText("no"));
    }

}
