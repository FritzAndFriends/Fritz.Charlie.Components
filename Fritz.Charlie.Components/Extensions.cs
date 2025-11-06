using Fritz.Charlie.Components.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fritz.Charlie.Components;

public static class Extensions
{
	public static IHostApplicationBuilder AddFritzCharlieComponents(this IHostApplicationBuilder builder)
	{

		builder.Services.AddSingleton<MapTourService>();

		// Register services and components here
		return builder;
	}
}