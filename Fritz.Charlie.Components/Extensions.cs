using Microsoft.Extensions.Hosting;

namespace Fritz.Charlie.Components;

public static class Extensions
{
	public static IHostApplicationBuilder AddFritzCharlieComponents(this IHostApplicationBuilder builder)
	{

		// Register services and components here
		return builder;
	}
}