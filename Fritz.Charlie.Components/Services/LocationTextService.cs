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
	/// <summary>
	/// Extracts a location that was mentioned in the text provided with optional cache-first progressive truncation
	/// </summary>
	/// <param name="message">Text to analyze for a location</param>
	/// <param name="cacheChecker">Optional function to check if a location exists in cache (for progressive truncation)</param>
	/// <returns>Location, if any mentioned in the message. Returns empty string if there is no location found</returns>
	public string GetLocationText(string message, Func<string, bool>? cacheChecker = null)
	{

		// Early exit for empty or very short messages
		if (string.IsNullOrWhiteSpace(message) || message.Length < 5)
			return string.Empty;

		// Convert to lowercase once for all pattern matching
		var lowerMessage = message.ToLowerInvariant();

		// Quick pre-filtering: check if message contains any location indicators
		if (!ContainsLocationIndicators(lowerMessage))
			return string.Empty;

		// Special handling for messages with multiple "from" occurrences
		// Count occurrences of "from" to detect potential conflicts
		var fromCount = 0;
		var fromPositions = new List<int>();
		var searchStart = 0;
		while ((searchStart = lowerMessage.IndexOf("from", searchStart, StringComparison.OrdinalIgnoreCase)) != -1)
		{
			fromPositions.Add(searchStart);
			fromCount++;
			searchStart += 4;
		}

		// If we have multiple "from" occurrences, we need to be more careful about pattern matching
		if (fromCount > 1)
		{
			// Find all potential matches with their positions
			var potentialMatches = new List<(int Position, string Location, int PatternIndex)>();

			// Sequential processing with early exit (maintains pattern priority)
			for (int i = 0; i < CompiledPatterns.Length; i++)
			{
				var regex = CompiledPatterns[i];
				var matches = regex.Matches(lowerMessage);
				
				foreach (Match match in matches)
				{
					if (match.Success)
					{
						// For state/national park patterns (first two patterns), use group 2 (the location after "near")
						// For all other patterns, use group 1
						var groupIndex = (i < 2) ? 2 : 1;

						// Check if the desired group exists
						if (match.Groups.Count > groupIndex)
						{
							var extractedLocation = match.Groups[groupIndex].Value.AsSpan().Trim().TrimEnd(TrimChars);
							if (extractedLocation.Length > 0 && IsValidLocationExtraction(extractedLocation))
							{
								potentialMatches.Add((match.Groups[groupIndex].Index, extractedLocation.ToString(), i));
							}
						}
					}
				}
			}

			// Sort by position (earliest first) and then by pattern priority (lower index = higher priority)
			potentialMatches.Sort((a, b) => {
				var positionComparison = a.Position.CompareTo(b.Position);
				return positionComparison != 0 ? positionComparison : a.PatternIndex.CompareTo(b.PatternIndex);
			});

			// Test the earliest valid match
			foreach (var (position, location, patternIndex) in potentialMatches)
			{
				var extractedLocation = location.AsSpan().Trim().TrimEnd(TrimChars);
				var locationString = CleanLocationSuffixes(extractedLocation.ToString());

				// Apply post-processing cleanup to remove prefixes and action words
				locationString = CleanExtractedLocation(locationString);

				// Try to preserve comma + state/country suffixes that were excluded by greedy lookaheads
				try
				{
					var matchEnd = position + location.Length;
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
					continue; // Skip URLs and try next match
				}

				// Apply progressive truncation if cache checker is available
				if (cacheChecker != null)
				{
					var truncatedLocation = TryProgressiveTruncation(locationString, cacheChecker);
					if (!string.IsNullOrEmpty(truncatedLocation))
					{
						Logger.LogInformation($"Found cached location '{truncatedLocation}' (truncated from '{locationString}') from message: {message}");
						return truncatedLocation;
					}
					// If progressive truncation didn't find anything, continue with traditional cleaning
				}

				// Fallback to traditional emote removal if no cache checker or cache miss
				locationString = RemoveEmotes(locationString);
				
				// Validate we still have a location after cleaning
				if (string.IsNullOrWhiteSpace(locationString) || locationString.Length < 2)
				{
					continue; // Try next match if cleaning removed everything
				}
				
				// Final validation check after all post-processing
				if (!IsValidLocationExtraction(locationString.AsSpan()))
				{
					continue; // Try next match if validation fails
				}

				Logger.LogInformation($"Found location '{locationString}' from message: {message}");
				return locationString;
			}
		}
		else
		{
			// Original sequential processing for single "from" messages
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
							var locationString = CleanLocationSuffixes(extractedLocation.ToString());

							// Apply post-processing cleanup to remove prefixes and action words
							locationString = CleanExtractedLocation(locationString);

							// Try to preserve comma + state/country suffixes that were excluded by greedy lookaheads
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
						// Apply progressive truncation if cache checker is available
						if (cacheChecker != null)
						{
							var truncatedLocation = TryProgressiveTruncation(locationString, cacheChecker);
							if (!string.IsNullOrEmpty(truncatedLocation))
							{
								Logger.LogInformation($"Found cached location '{truncatedLocation}' (truncated from '{locationString}') from message: {message}");
								return truncatedLocation;
							}
							// If progressive truncation didn't find anything, continue with traditional cleaning
						}

						// Fallback to traditional emote removal if no cache checker or cache miss
						locationString = RemoveEmotes(locationString);
						
						// Validate we still have a location after cleaning
						if (string.IsNullOrWhiteSpace(locationString) || locationString.Length < 2)
						{
							continue; // Try next pattern if cleaning removed everything
						}
						
						// Final validation check after all post-processing
						if (!IsValidLocationExtraction(locationString.AsSpan()))
						{
							continue; // Try next pattern if validation fails
						}
						
						Logger.LogInformation($"Found location '{locationString}' from message: {message}");
						return locationString;
						}
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
		"hot", "weather", "degrees", "timezone", "morning", "evening", "mornin",
		"cafe", "restaurant", "store", "shop", "hotel", "st.", "st", "home",
		"park", "state", "national", "forest", "preserve", "recreation", "near",
		"city", "mo", "fl", "ca", "tx", "ny", "il", "oh", "pa", "mi", "ga", "nc", "va"
	];

	private static bool ContainsLocationIndicators(string message)
	{
		// Fast string contains check before expensive regex operations
		foreach (var keyword in LocationKeywords)
		{
			if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		
		// For very short messages (potential standalone locations), check if it looks like a location
		// This handles cases like "Mexico City" without any keywords
		if (message.Trim().Length <= 30 && message.Trim().Split(' ').Length <= 3)
		{
			// Check if it starts with capital letters (likely location format)
			var trimmed = message.Trim();
			if (trimmed.Length >= 2 && char.IsUpper(trimmed[0]))
			{
				var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (words.Length >= 1 && words.Length <= 3)
				{
					// Check if all words start with capitals and are reasonable length
					var allCapitalized = true;
					foreach (var word in words)
					{
						// Clean word of punctuation for checking
						var cleanWord = word.TrimEnd('.', ',', '!', '?', ';', ':');
						if (cleanWord.Length < 2 || !char.IsUpper(cleanWord[0]) || InvalidTerms.Contains(cleanWord.ToLowerInvariant()))
						{
							allCapitalized = false;
							break;
						}
					}
					if (allCapitalized) return true;
				}
			}
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
		"week", "month", "year", "i'm", "im", "in", "experience", "message",
		"response", "system", "automated", "my", "on", "is", "are", "was",
		"were", "be", "been", "have", "has", "had", "do", "does", "did",
		"will", "would", "could", "should", "can", "may", "might", "must",
		"hello", "hi", "hey", "content", "tutorial", "coding", "going", "from"
	};

	private static bool IsValidLocationExtraction(ReadOnlySpan<char> location)
	{
		if (location.IsWhiteSpace() || location.Length < 2)
			return false;

		// Trim and clean the location
		location = location.Trim();

		// Quick contextual phrase rejection for common false positives
		var locationStr = location.ToString().ToLowerInvariant();
		
		// Reject obvious non-location phrases
		string[] falsePositivePhrases = [
			"the stream", "the weather", "the weather is nice", "the good place",
			"my experience", "on my experience", "system message", "automated response",
			"response from server", "message from bot", "from the stream",
			"from home", "good place", "this place", "that place", "some place",
			"any place", "one place", "nice place", "great place", "bad place",
			"the place", "a place", "the moment", "this moment", "that moment",
			"the time", "good time", "great time", "nice time", "bad time",
			"the weather is", "weather is nice", "weather is good", "weather is great"
		];
		
		foreach (var phrase in falsePositivePhrases)
		{
			if (locationStr == phrase || locationStr.Contains(phrase))
				return false;
		}
		
		// Reject single words that are clearly not locations
		if (!locationStr.Contains(' ') && locationStr.Length <= 10)
		{
			string[] singleWordRejects = [
				"friends", "experience", "message", "response", "system", "automated",
				"weather", "stream", "content", "tutorial", "coding", "going",
				"everyone", "hello", "thanks", "love", "nice", "great", "good"
			];
			
			if (singleWordRejects.Contains(locationStr))
				return false;
		}

		// Additional check for single invalid words using the InvalidTerms set
		if (!locationStr.Contains(' '))
		{
			// For single words, be more strict about InvalidTerms checking
			if (InvalidTerms.Contains(locationStr))
				return false;
				
			// Also check the single word rejects list
			string[] singleWordRejects = [
				"friends", "experience", "message", "response", "system", "automated",
				"weather", "stream", "content", "tutorial", "coding", "going",
				"everyone", "hello", "thanks", "love", "nice", "great", "good"
			];
			
			if (singleWordRejects.Contains(locationStr))
				return false;
		}

		// Check for common false positives at the start
		if (location.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
		{
			location = location.Slice(4).Trim();
		}

		if (location.Length < 2) return false;

		// Check if it looks like a URL
		var locationString = location.ToString();
		if (Uri.TryCreate(locationString, UriKind.Absolute, out _))
		{
			return false;
		}

		// Additional check for single invalid words
		if (!location.Contains(' ') && location.Length < 4)
		{
			// For single short words, check if they're common invalid terms
			if (InvalidTerms.Contains(locationString))
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

		// Additional sentence-like structure detection
		// Reject if it contains too many common English words that suggest it's a sentence, not a location
		// But be more permissive with "the" as it's common in location names
		string[] sentenceIndicators = ["is", "are", "was", "were", "a", "an", "my", "your", "his", "her", "our", "their"];
		var sentenceWordCount = 0;
		var totalWords = wordCount;
		
		foreach (var indicator in sentenceIndicators)
		{
			if (locStrLow.Contains($" {indicator} ") || locStrLow.StartsWith($"{indicator} ") || locStrLow.EndsWith($" {indicator}"))
			{
				sentenceWordCount++;
			}
		}
		
		// Special case: allow "the" in location names like "The Democratic Republic of the Congo"
		// Only count "the" as sentence indicator if it appears multiple times or with other indicators
		var theCount = 0;
		var theIndex = locStrLow.IndexOf(" the ");
		while (theIndex >= 0)
		{
			theCount++;
			theIndex = locStrLow.IndexOf(" the ", theIndex + 1);
		}
		if (locStrLow.StartsWith("the ")) theCount++;
		if (locStrLow.EndsWith(" the")) theCount++;
		
		// Only penalize "the" if it appears with other sentence indicators or multiple times inappropriately
		if (theCount > 0 && sentenceWordCount == 0 && theCount <= 2)
		{
			// Allow "the" in location names
		}
		else if (theCount > 0)
		{
			sentenceWordCount += Math.Min(theCount, 2); // Cap penalty from "the"
		}
		
		// If more than 35% of the text consists of sentence structure words, likely not a location  
		// Increased threshold to be more permissive of location names with "the"
		if (totalWords > 2 && sentenceWordCount > 0 && (double)sentenceWordCount / totalWords > 0.35)
			return false;
		
		// Reject extractions that look like full sentences (contain verbs + articles)
		// But be more lenient with longer location names
		if (wordCount > 4 && (
		    (locStrLow.Contains(" is ") || locStrLow.Contains(" are ") || locStrLow.Contains(" was ") || locStrLow.Contains(" were ")) ||
		    (locStrLow.Contains(" the ") && wordCount > 5 && sentenceWordCount > 1)
		    ))
		{
			return false;
		}

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
		
		// Original patterns (highest priority - most common) - improved word boundaries and added "in" to stop words
		new(@"(?:I am from|I'm from|I'm in|I am in|I'm at|I am at|I live in)\s+(?:a\s+)?(?:cafe|restaurant|store|shop|hotel)?\s*(?:in\s+|at\s+|near\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|in|i'm|im)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"^from\s+(?:a\s+)?(?:cafe|restaurant|store|shop|hotel)?\s*(?:in\s+|at\s+|near\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|in|i'm|im)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Consolidated greeting patterns - covers hello/hi/hey/greetings from, greetings and salutations from, good morning/afternoon/evening from
		new(@"(?:h*hello|hi|hey|greetings|greetings and salutations|good morning|good afternoon|good evening)(?:\s+.*?)?\s+from\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|in|i'm|im)\b|[,.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
	// "Good day/night" greeting patterns - captures "good day friends from Location" and similar
	new(@"(?:good\s+day|good\s+night)(?:\s+\w+)?\s+from\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|in|i'm|im)\b|[,.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
	
	// Flexible "good [anything] from" pattern - handles typos and variations in greetings that appear anywhere in message
	new(@"(?:good\s+[a-z]{3,9}(?:ing)?)\s+from\s+(?:the\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|in|i'm|im)\b|[,.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Origin/born patterns - fixed word boundaries
		new(@"(?:originally from|born in|grew up in|raised in|native of)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:moved from|relocated from|came from)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:to|here)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Visiting/travel patterns - HIGH PRIORITY to avoid conflicts with weather patterns
		new(@"(?:currently\s+visiting)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|yesterday|today|last|next|week|month|this|because|since|with|for|in)\b|[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:visiting|traveled to|travelled to|traveling to|travelling to|just arrived in|arrived at)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|yesterday|today|last|next|week|month|this|because|since|with|for|in)\b|[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:on vacation in|vacationing in|holidaying in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|this|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:staying in|spending time in)\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|this|because|since|with|for|in)\b|[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Pattern specifically for "Buffalo is my home" style - HIGH PRIORITY
		new(@"^([a-zA-Z][\w\s,.''-]*?)\s+is\s+my\s+(?:home|city)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Location-first patterns - improved to avoid over-capturing
		new(@"^([a-zA-Z][\w\s,.''-]*?)\s+(?:here|represent|representing|in the house|gang)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"^([a-zA-Z][\w\s,.''-]*?)\s+(?:checking in|tuning in|watching|viewer)(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"([a-zA-Z][\w\s,.''-]*?)\s+is\s+(?:where I'm from|where I am from|my (?:home|city|location))(?:[.,;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Weather/time patterns - improved to capture full location names with proper boundaries
		new(@"(?:it's|its)\s+(?:cold|hot|warm|sunny|rainy|snowing|freezing|beautiful)\s+(?:here\s+)?in\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|today|right|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:currently|right now)\s+(?:\d+[°]?[CF]?\s+)?(?:degrees\s+)?(?:in\s+|at\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:the weather is|weather's)\s+[\w\s]+\s+(?:here\s+)?in\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|today|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		new(@"(?:it's|its)\s+(?:\d{1,2}[:\.]?\d{0,2}\s*(?:am|pm|AM|PM)?|late|early|morning|evening|night|noon|midnight)\s+(?:here\s+)?(?:in\s+|at\s+)?([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// General "in [location]" pattern for activities - medium priority
		new(@"(?:watching|eating|working|studying|shopping|walking|running|sitting|standing|playing|gaming|streaming|coding|reading|writing|relaxing|chilling|hanging|staying|spending|enjoying|having)\s+(?:[a-zA-Z0-9\s]+\s+)?in\s+([a-zA-Z][\w\s,.''-]*?)(?=\s+(?:and|but|where|which|that|today|right|now|because|since|with|for|in)\b|[.;:\?!]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
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
		new(@"(?:^|\b)(?:good\s+)?(?:morning|afternoon|evening)\s+from\s+([a-zA-Z][a-zA-Z0-9\s'.-]*?)(?=[,.;:\?!]|\s+where|\s+who|\s+what|\s+when|\s+why|\s+how|\s+we\s+are|\s+it's|\s+im|\s+and|\s+but|\s+or|\s+so|\s+because|\s+since|\s+with|\s+for|\s+is|\s+are|\s+was|\s+were|\s+am|\s+be|\s+have|\s+has|\s+had|\s+will|\s+shall|\s+can|\s+may|\s+should|\s+would|\s+could|\s+must|\s+do|\s+does|\s+did|\s+not|\s+no|\s+yes|\s+in|\s*$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Catch-all "from" pattern - captures any reasonable location after "from" keyword (added as lower priority)
		new(@"\bfrom\s+([a-zA-Z][a-zA-Z0-9\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|in|i'm|im|this|we|have|are|love|enjoy|get|got|is|was|were)\b|[.;:\?!,]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Catch-all "in" pattern - captures any reasonable location after "in" keyword (for cases like "living in Nashville")
		new(@"\b(?:living|residing|based|located|currently|now)\s+in\s+([a-zA-Z][a-zA-Z0-9\s,.''-]*?)(?=\s+(?:and|but|where|which|that|it|its|it's|the|a|an|here|today|now|because|since|with|for|i'm|im|this|we|have|are|love|enjoy|get|got|is|was|were)\b|[.;:\?!,]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Time-based greetings without "from" - like "top of the mornin. Kansas City, MO!"
		new(@"(?:top\s+(?:of\s+)?the\s+(?:mornin|morning))[^a-zA-Z]*([A-Z][a-zA-Z\s,.''-]+?)(?:[.!?]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
		
		// Standalone location pattern - captures 1-4 capitalized words that could be location names (lowest priority)
		new(@"^([A-Z][a-zA-Z]+(?:[,\s]+[A-Z][a-zA-Z]+){0,3})(?:[,.]*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)
	];

	// Pre-allocated char array for trimming operations
	private static readonly char[] TrimChars = ['.', ',', '!', '?', ';', ':'];

	// Regex to strip common location suffixes (burbs, suburbs, area, metro)
	private static readonly Regex LocationSuffixRegex = new(@"\s+(burbs|suburbs|area|metro)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

	// Enhanced regex to detect Twitch emotes anywhere in the string (not just at end)
	// Matches patterns like: csharpGritty, LUL, Kappa, monkaS, 4Head, PogChamp, etc.
	private static readonly Regex EmotePattern = new(@"\b(?:[a-z][a-zA-Z0-9]*[A-Z][a-zA-Z0-9]*|[A-Z]{2,}[a-z]*|\d+[A-Z][a-zA-Z]*|[a-z]+[A-Z]\d*|[A-Z][a-z]+[A-Z][a-z]*|[a-z]+[0-9]+[A-Z][a-z]*|[A-Z]+\d+)\b", RegexOptions.Compiled);

	// Enhanced regex to detect emoji and Unicode symbols anywhere in the string  
	// Uses proper .NET Unicode escape syntax for emoji ranges
	private static readonly Regex EmojiPattern = new(@"[\p{So}\p{Sk}\p{Sm}\p{Sc}\p{Cn}]|[\uD83C-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u26FF]|[\u2700-\u27BF]|[\uFE0E\uFE0F]", RegexOptions.Compiled);

	// Common Twitch emote names for additional detection
	private static readonly HashSet<string> CommonTwitchEmotes = new(StringComparer.OrdinalIgnoreCase)
	{
		"4head", "lul", "kappa", "pogchamp", "monkas", "pepehands", "sadge", "omegalul",
		"pepega", "5head", "weirdchamp", "pepelaugh", "kekw", "copium", "hopium", "malding",
		"pog", "poggers", "ez", "clap", "ez clap", "gg", "wp", "rip", "f", "w", "l", "ratio", "based",
		"cringe", "sus", "cap", "nocap", "fr", "ong", "bet", "say", "less", "sheesh",
		"bussin", "salty", "toxic", "griefing", "throwing", "inting", "feed", "feeding",
		"csharpgritty", "csharpfritz", "dotnetbot", "visualstudio", "blazor", "aspnet",
		"pepehappy", "pepesad", "pepesmile", "pepe", "monka", "feelsbad", "feelsgood",
		"feelsbadman", "feelsgoodman", "pepeweird", "pepepls", "pepejam", "widehappy",
		"widepeepohappy", "modcheck", "noted", "surely", "copege", "despair"
	};

	// Multi-word emote patterns that should be detected together
	private static readonly string[] MultiWordEmotes = [
		"ez clap", "5 head", "big brain", "smooth brain", "no cap", "on god"
	];

	private static string CleanLocationSuffixes(string location)
	{
		return LocationSuffixRegex.Replace(location.Trim(), string.Empty).Trim();
	}

	/// <summary>
	/// Cleans extracted location by removing common prefixes and action words that shouldn't be part of location names
	/// </summary>
	private static string CleanExtractedLocation(string location)
	{
		if (string.IsNullOrWhiteSpace(location))
			return string.Empty;

		// Remove common prefixes that shouldn't be part of location names
		var prefixesToRemove = new[]
		{
			"living in ", "based in ", "residing in ", "located in ", "currently in ",
			"now living in ", "currently living in ", "now in ", "currently ", "living ", 
			"residing ", "based ", "located ", "from the ", "in the ", "at the ", 
			"visiting ", "traveling to ", "heading to ", "going to ", "staying in ", 
			"working in ", "studying in ", "born in ", "raised in ", "grew up in ", 
			"here in "
		};

		var cleaned = location.Trim();
		
		foreach (var prefix in prefixesToRemove)
		{
			if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				cleaned = cleaned.Substring(prefix.Length).Trim();
				break; // Only remove first matching prefix
			}
		}

		// Additional cleanup - remove single words that are clearly not locations
		var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 1)
		{
			var singleWord = words[0].ToLowerInvariant();
			string[] invalidSingleWords = [
				"home", "work", "school", "office", "house", "apartment", "hotel",
				"living", "based", "located", "residing", "traveling", "heading",
				"going", "staying", "working", "studying", "visiting", "stream",
				"hello", "here", "there", "somewhere", "anywhere", "everywhere"
			];

			if (invalidSingleWords.Contains(singleWord))
				return string.Empty;
		}

		return cleaned;
	}

	/// <summary>
	/// Removes Twitch emotes and emoji characters from location strings using multiple cleaning passes
	/// </summary>
	private static string RemoveEmotes(string location)
	{
		if (string.IsNullOrWhiteSpace(location))
			return location;

		var cleaned = location.Trim();

		// Pass 1: Remove Unicode emojis and symbols
		cleaned = EmojiPattern.Replace(cleaned, " ").Trim();

		// Pass 2: Remove Twitch emote patterns
		cleaned = EmotePattern.Replace(cleaned, " ").Trim();

		// Pass 3: Remove known Twitch emote names
		cleaned = RemoveKnownEmotes(cleaned);

		// Pass 4: Clean up multiple spaces and re-trim
		cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();

		// Pass 5: Remove any remaining isolated single characters or numbers that might be emote fragments
		cleaned = RemoveEmoteFragments(cleaned);

		// Pass 6: Final validation - reject if only invalid words remain
		if (!string.IsNullOrWhiteSpace(cleaned))
		{
			var remainingWords = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var hasValidLocationWord = false;
			
			foreach (var word in remainingWords)
			{
				var cleanWord = word.Trim('.', ',', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}').ToLowerInvariant();
				if (!InvalidTerms.Contains(cleanWord) && !CommonTwitchEmotes.Contains(cleanWord))
				{
					hasValidLocationWord = true;
					break;
				}
			}
			
			if (!hasValidLocationWord)
			{
				return string.Empty; // No valid location words remain after emote cleaning
			}
		}

		// Final cleanup
		return cleaned.Trim();
	}

	/// <summary>
	/// Removes known Twitch emote names from the location string
	/// </summary>
	private static string RemoveKnownEmotes(string location)
	{
		if (string.IsNullOrWhiteSpace(location))
			return location;

		var cleaned = location;

		// First pass: Remove multi-word emotes
		foreach (var multiEmote in MultiWordEmotes)
		{
			cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, 
				$@"\b{Regex.Escape(multiEmote)}\b", " ", RegexOptions.IgnoreCase);
		}

		// Second pass: Remove single-word emotes
		var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var cleanedWords = new List<string>();

		foreach (var word in words)
		{
			// Clean word of punctuation for checking
			var cleanWord = word.Trim('.', ',', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}');
			
			// Skip if it's a known emote
			if (!CommonTwitchEmotes.Contains(cleanWord))
			{
				cleanedWords.Add(word);
			}
		}

		return string.Join(" ", cleanedWords).Trim();
	}

	/// <summary>
	/// Removes isolated single characters, numbers, or obvious emote fragments
	/// </summary>
	private static string RemoveEmoteFragments(string location)
	{
		if (string.IsNullOrWhiteSpace(location))
			return location;

		var words = location.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var cleanedWords = new List<string>();

		foreach (var word in words)
		{
			// Clean word of punctuation for analysis
			var cleanWord = word.Trim('.', ',', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}');
			
			// Skip single characters, isolated numbers, or very short non-alphabetic sequences
			if (cleanWord.Length == 1 || 
			    (cleanWord.Length <= 2 && cleanWord.All(char.IsDigit)) ||
			    (cleanWord.Length <= 3 && cleanWord.Any(c => !char.IsLetter(c) && !char.IsWhiteSpace(c))))
			{
				// Skip potential emote fragments
				continue;
			}
			
			cleanedWords.Add(word);
		}

		return string.Join(" ", cleanedWords);
	}

	/// <summary>
	/// Progressive truncation strategy: tries cache lookups from full location down to individual words.
	/// This allows fast cache hits for known locations while handling emotes/noise gracefully.
	/// </summary>
	/// <param name="location">Extracted location that may contain emotes or noise</param>
	/// <param name="cacheChecker">Function to check if a location exists in cache</param>
	/// <returns>Validated location from cache, or empty string if no cached match found</returns>
	private string TryProgressiveTruncation(string location, Func<string, bool> cacheChecker)
	{
		if (string.IsNullOrWhiteSpace(location))
			return string.Empty;

		// Split into words and try progressively shorter combinations
		var words = location.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		
		// Try from longest to shortest (right-to-left truncation to remove trailing emotes)
		for (int i = words.Length; i > 0; i--)
		{
			var candidate = string.Join(" ", words.Take(i)).Trim();
			
			// Skip very short candidates
			if (candidate.Length < 2)
				continue;
			
			// Skip if it would fail basic validation
			if (!IsValidLocationExtraction(candidate.AsSpan()))
				continue;
			
			// Check cache - this is the key optimization
			try 
			{
				if (cacheChecker(candidate))
				{
					Logger.LogDebug($"Progressive truncation cache hit: '{candidate}' (from '{location}')");
					return candidate;
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, $"Error checking cache for candidate: {candidate}");
				// Continue trying other candidates
			}
		}
		
		// No cached matches found
		Logger.LogDebug($"Progressive truncation found no cached matches for: {location}");
		return string.Empty;
	}

}
