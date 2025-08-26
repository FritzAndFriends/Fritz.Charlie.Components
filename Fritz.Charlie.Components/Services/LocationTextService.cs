using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fritz.Charlie.Components.Services;

public class LocationTextService(ILogger<LocationTextService> Logger)
{

	/// <summary>
	/// Extracts a location that was mentioned in the text provided
	/// </summary>
	/// <param name="message">Text to analyze for a location</param>
	/// <returns>Location, if any mentioned in the message.  Returns empty string if there is no location found</returns>
	public string GetLocationText(string message)
	{

		// Early exit for empty or very short messages
		if (string.IsNullOrWhiteSpace(message) || message.Length < 5)
			return string.Empty;

		// Convert to lowercase once for all pattern matching
		var lowerMessage = message.ToLowerInvariant();

		// Quick pre-filtering: check if message contains any location indicators
		if (!ContainsLocationIndicators(lowerMessage))
			return string.Empty;

		// Sequential processing with early exit (maintains pattern priority)
		for (int i = 0; i < CompiledPatterns.Length; i++)
		{
			var regex = CompiledPatterns[i];
			var match = regex.Match(lowerMessage);
			if (match.Success)
			{
				// For state/national park patterns (first two patterns), use group 2 (the location after "near")
				// For all other patterns, use group 1
				var groupIndex = (i < 2) ? 2 : 1;

				// Check if the desired group exists
				if (match.Groups.Count > groupIndex)
				{
					var extractedLocation = match.Groups[groupIndex].Value.AsSpan().Trim().TrimEnd(TrimChars);

					// Filter out common false positives using span
					if (IsValidLocationExtraction(extractedLocation))
					{
						// Convert to string only when we have a valid location
						var locationString = extractedLocation.ToString();

						// Try to preserve comma + state/country suffixes that were excluded by greedy lookaheads
						// e.g. match captured "pueblo" but original message had "pueblo, co" or "sydney, australia".
						try
						{
							var matchEnd = match.Groups[groupIndex].Index + match.Groups[groupIndex].Length;
							if (matchEnd < lowerMessage.Length)
							{
								var suffixCandidateMatch = Regex.Match(lowerMessage.Substring(matchEnd), "^\\s*,\\s*([a-zA-Z\\s\\.'-]{1,40})");
								if (suffixCandidateMatch.Success)
								{
									var candidate = suffixCandidateMatch.Groups[1].Value.Trim().TrimEnd(TrimChars).Trim();
									// Reject obvious non-location suffixes (weekdays, greetings, temperature, etc.)
									var rejectKeywords = new[] { "happy", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "temperature", "sun", "degrees" };
									var words = candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
									var hasReject = false;
									foreach (var w in words)
									{
										if (InvalidTerms.Contains(w) || rejectKeywords.Contains(w)) { hasReject = true; break; }
									}
									if (!hasReject && words.Length <= 4 && candidate.Length > 0 && candidate.Length <= 30)
									{
										locationString = string.Concat(locationString, ", ", candidate);
									}
								}
							}
						}
						catch { /* non-fatal; fall back to the captured value */ }

						// Additional URL check - skip URLs that were captured
						if (Uri.TryCreate(locationString, UriKind.Absolute, out var uriResult) &&
							(uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
						{
							continue; // Skip URLs and try next pattern
						}

						Logger.LogInformation($"Found location '{locationString}' from message: {message}");
						return locationString;
					}
				}
			}
		}

		Logger.LogDebug($"No valid location found in message: {message}");
		return string.Empty;

	}


	// Pre-computed keywords for fast filtering
	private static readonly string[] LocationKeywords =
	[
		"from", "in", "at", "live", "born", "based", "residing", "located",
		"visiting", "vacation", "traveling", "represent", "here", "cold",
		"hot", "weather", "degrees", "timezone", "morning", "evening",
		"cafe", "restaurant", "store", "shop", "hotel", "st.", "st", "home",
		"park", "state", "national", "forest", "preserve", "recreation", "near"
	];

	private static bool ContainsLocationIndicators(string message)
	{
		// Fast string contains check before expensive regex operations
		foreach (var keyword in LocationKeywords)
		{
			if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	// Pre-computed HashSet for O(1) lookup performance
	private static readonly HashSet<string> InvalidTerms = new(StringComparer.OrdinalIgnoreCase)
	{
		"a", "an", "the", "and", "or", "but", "so", "for", "nor", "yet",
		"this", "that", "these", "those", "here", "there", "where",
		"good", "bad", "great", "nice", "cool", "hot", "cold", "warm",
		"time", "day", "night", "morning", "evening", "afternoon",
		"weather", "today", "yesterday", "tomorrow", "now", "then",
		"very", "really", "quite", "pretty", "just", "only", "also",
		"love", "like", "hate", "enjoy", "prefer", "want", "need",
		"cafe", "restaurant", "store", "shop", "hotel", "its", "it's",
		"work", "home", "friends", "place", "moment", "stream", "bot",
		"server", "github", "http", "https", "localhost", "com", "org",
		"week", "month", "year", "i'm", "im"
	};

	private static bool IsValidLocationExtraction(ReadOnlySpan<char> location)
	{
		if (location.IsWhiteSpace() || location.Length < 2)
			return false;

		// Trim and clean the location
		location = location.Trim();

		// Check for common false positives at the start
		if (location.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
		{
			location = location.Slice(4).Trim();
		}

		if (location.Length < 2) return false;

		// Check if it looks like a URL
		var locationStr = location.ToString();
		if (locationStr.Contains("://") || locationStr.Contains("www.") ||
			locationStr.Contains(".com") || locationStr.Contains(".org") ||
			locationStr.Contains("localhost"))
		{
			return false;
		}

		// Additional check for single invalid words
		if (!location.Contains(' ') && location.Length < 4)
		{
			// For single short words, check if they're common invalid terms
			if (InvalidTerms.Contains(locationStr))
				return false;
		}

		var wordCount = 0;
		var hasValidWord = false;

		// Count words and check validity without creating string array
		var start = 0;
		for (var i = 0; i <= location.Length; i++)
		{
			if (i == location.Length || char.IsWhiteSpace(location[i]) || location[i] == ',')
			{
				if (i > start)
				{
					wordCount++;
					var word = location.Slice(start, i - start);

					// Skip commas and whitespace
					if (word.Length > 0 && !word.IsWhiteSpace())
					{
						// Check if this word is not in the invalid terms list
						// Only convert to string for the HashSet lookup when necessary
						if (!InvalidTerms.Contains(word.ToString()))
						{
							hasValidWord = true;
						}
					}
				}
				start = i + 1;
			}
		}

		// Check if all words are invalid terms
		if (!hasValidWord)
			return false;

		// Check for minimum meaningful content and reasonable length limits
		if (wordCount == 1 && location.Length < 3)
			return false;

		// Don't allow overly long extractions (increased limit for full location names)
		if (wordCount > 8 || location.Length > 60)
			return false;

		// Reject obvious non-location captures
		var locStrLow = location.ToString().ToLowerInvariant();
		if (locStrLow.Contains("temperature") || locStrLow.Contains("sun") || locStrLow.Contains("degrees") || locStrLow.Contains("fahrenheit") || locStrLow.Contains("celsius"))
			return false;

		return true;
	}

	// Pre-compiled regex patterns for better performance
	private static readonly Regex[] CompiledPatterns = 
	[
		// State/National Park patterns - HIGHEST PRIORITY to capture city names from park references
		new(@"(?:hey|hello|hi|greetings)?\s*(?:there\s+)?from\s+(?:the\s+)?([a-zA-Z][\w\s'.-]+?)\s+(?:state\s+park|national\s+park|park|forest|preserve|recreation\s+area)\s+(?:near|in|at|close\s+to)\s+([a-zA-Z][a-zA-Z0-9\s,.''-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:at|visiting|in)\s+(?:the\s+)?([a-zA-Z][\w\s'.-]+?)\s+(?:state\s+park|national\s+park|park|forest|preserve|recreation\s+area)\s+(?:near|in|at|close\s+to)\s+([a-zA-Z][a-zA-Z0-9\s,.''-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Venue-specific patterns - HIGHEST PRIORITY to capture proper city names
		new(@"(?:hey|hello|hi)\s+(?:there\s+)?from\s+(?:a\s+|an\s+|the\s+)?(?:cafe|restaurant|store|shop|hotel|bar|office)\s+in\s+([a-zA-Z][a-zA-Z0-9\s,.''-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:at|in)\s+(?:a\s+|an\s+|the\s+)?(?:cafe|restaurant|store|shop|hotel|bar|office)\s+in\s+([a-zA-Z][a-zA-Z0-9\s,.''-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"from\s+(?:a\s+|an\s+|the\s+)?(?:cafe|restaurant|store|shop|hotel|bar|office)\s+in\s+([a-zA-Z][a-zA-Z0-9\s,.''-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Activity + from patterns - HIGH PRIORITY to capture "watching from", "streaming from", etc.
		new(@"(?:watching|streaming|coding|working|eating|studying|shopping|walking|running|sitting|standing|playing|gaming|reading|writing|relaxing|chilling|hanging|staying|spending|enjoying|having)\s+from\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s*[,;]\s*|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Original patterns (highest priority - most common) - improved word boundaries
		new(@"(?:I am from|I'm from|I'm in|I am in|I'm at|I am at|I live in)\s+(?:a\s+)?(?:cafe|restaurant|store|shop|hotel)?\s*(?:in\s+|at\s+|near\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"^from\s+(?:a\s+)?(?:cafe|restaurant|store|shop|hotel)?\s*(?:in\s+|at\s+|near\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Consolidated greeting patterns - covers hello/hi/hey/greetings from, greetings and salutations from, good morning/afternoon/evening from
	new(@"(?:h*hello|hi|hey|greetings|greetings and salutations|good morning|good afternoon|good evening)(?:\s+.*?)?\s+from\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for)\b|[,.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Flexible greeting pattern for misspellings - matches any word-like sequence ending in common greeting sounds followed by "from"
	new(@"(?:h+[aeiou]*l+[aeiou]*|h+[aeiou]*y+|gr+[aeiou]*t+[ings]*)\s+from\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for)\b|[,.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Current location patterns (common) - better word boundaries
		new(@"(?:living in|residing in|based in|located in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|now|today|these|right|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:currently\s+(?:living|residing|based)\s+in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|now|today|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:now\s+(?:living|residing|based)\s+in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|today|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Origin/born patterns - fixed word boundaries
		new(@"(?:originally from|born in|grew up in|raised in|native of)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:moved from|relocated from|came from)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:to|here)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Visiting/travel patterns - HIGH PRIORITY to avoid conflicts with weather patterns
		new(@"(?:currently\s+visiting)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|yesterday|today|last|next|week|month|this|because|since|with|for)\b|[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:visiting|traveled to|travelled to|traveling to|travelling to|just arrived in|arrived at)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|yesterday|today|last|next|week|month|this|because|since|with|for)\b|[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:on vacation in|vacationing in|holidaying in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|this|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:staying in|spending time in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|this|because|since|with|for)\b|[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Pattern specifically for "Buffalo is my home" style - HIGH PRIORITY
		new(@"^([a-zA-Z][\w\s,.''-]*?)\s+is\s+my\s+(?:home|city)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Location-first patterns - improved to avoid over-capturing
		new(@"^([a-zA-Z][\w\s,.''-]*?)\s+(?:here|represent|representing|in the house|gang)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"^([a-zA-Z][\w\s,.''-]*?)\s+(?:checking in|tuning in|watching|viewer)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"([a-zA-Z][\w\s,.''-]*?)\s+is\s+(?:where I'm from|where I am from|my (?:home|city|location))(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Weather/time patterns - improved to capture full location names with proper boundaries
		new(@"(?:it's|its)\s+(?:cold|hot|warm|sunny|rainy|snowing|freezing|beautiful)\s+(?:here\s+)?in\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|today|right|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:currently|right now)\s+(?:\d+[°]?[CF]?\s+)?(?:degrees\s+)?(?:in\s+|at\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:the weather is|weather's)\s+[\w\s]+\s+(?:here\s+)?in\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|today|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:it's|its)\s+(?:\d{1,2}[:\.]?\d{0,2}\s*(?:am|pm|AM|PM)?|late|early|morning|evening|night|noon|midnight)\s+(?:here\s+)?(?:in\s+|at\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// General "in [location]" pattern for activities - medium priority
		new(@"(?:watching|eating|working|studying|shopping|walking|running|sitting|standing|playing|gaming|streaming|coding|reading|writing|relaxing|chilling|hanging|staying|spending|enjoying|having)\s+(?:[a-zA-Z0-9\s]+\s+)?in\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|today|right|now|because|since|with|for)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Time zone patterns - fixed parenthetical capture and improved matching
		new(@"(?:my\s+)?time zone is\s+(?:[A-Z]{3,4}\s*)?\(?([a-zA-Z][\w\s,.''-]*?)\)?(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"time zone is\s+(?:[A-Z]{3,4}\s+)([a-zA-Z][\w\s,.''-]*?)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Casual mention patterns (lower priority) - improved to avoid over-capturing
		new(@"(?:here in|we in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:we\s+)?(?:have|are|love|enjoy)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:love|miss|enjoying)\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:weather|food|culture|life|sunshine)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:typical|classic|normal)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:weather|day|night|winter|summer|spring|fall)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
	// Simple "from" pattern - covers 'from St. Petersburg, FL' and similar
	new(@"^\s*from\s+([a-zA-Z][a-zA-Z0-9\s,.''-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
	// Variant to handle time-of-day greetings like 'Morning from Virginia' or 'hello from Virginia Beach, happy Tuesday'
	new(@"(?:^|\b)(?:good\s+)?(?:morning|afternoon|evening)\s+from\s+([a-zA-Z][a-zA-Z0-9\s'.-]*?)(?=[,.;:\?!]|\s+where|\s+who|\s+what|\s+when|\s+why|\s+how|\s+we\s+are|\s+it's|\s+im|\s+and|\s+but|\s+or|\s+so|\s+because|\s+since|\s+with|\s+for|\s+is|\s+are|\s+was|\s+were|\s+am|\s+be|\s+have|\s+has|\s+had|\s+will|\s+shall|\s+can|\s+may|\s+should|\s+would|\s+could|\s+must|\s+do|\s+does|\s+did|\s+not|\s+no|\s+yes|\s*$)", RegexOptions.Compiled | RegexOptions.IgnoreCase)
	];

	// Pre-allocated char array for trimming operations
	private static readonly char[] TrimChars = ['.', ',', '!', '?', ';', ':'];



}
