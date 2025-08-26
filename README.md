# Fritz.Charlie.Components
A collection of intelligent Blazor components for maps and location-based UI.

## Overview

Fritz.Charlie.Components contains reusable Blazor components, services and client assets for building web-based user interfaces with ASP.NET Core. Components are implemented as Razor components with C# code-behind, scoped CSS and JavaScript interop.

## Key components and files

- `ChatterMapDirect.razor` + `ChatterMapDirect.razor.cs` — primary map component with scoped CSS (`ChatterMapDirect.razor.css`).
  - `Map/` — interfaces and models for the map (for example `IViewerLocationService`, `ViewerLocationEvent`).
  - `wwwroot/chattermap.js` — client-side helpers used by the components.
- `Services/` — services such as `ChatterMapService` and `LocationTextService`.
- `_Imports.razor` and `_Usings.cs` — shared usings and imports for consumers.

## Getting started

1. Clone the repository and open the solution:
   - git clone <repo-url>
   - dotnet build Fritz.Charlie.Components.sln
2. Add the project as a project reference to your Blazor app, or build and publish a NuGet package.
3. Use the component in a page (ensure the library namespace is imported):
   - Add `@using Fritz.Charlie.Components` to `_Imports.razor` or the page.
   - Place the component in a Razor page: <ChatterMapDirect />

## Build and test

- Build the solution: `dotnet build Fritz.Charlie.Components.sln`
- Run unit tests: `dotnet test ./Test.Components/Test.Components.csproj`

## Development notes

- The project uses scoped CSS for components (`*.razor.css`) and serves static assets from `wwwroot`.
- Targets .NET 9.0 and follows SDK-style project layout.
- Check `.razor.cs` files for component logic and `Services/` for helper services.

## Contributing

Contributions are welcome. Please open issues or pull requests. When contributing:

- Run and add unit tests under `Test.Components`.
- Follow the existing coding style and add XML comments for public APIs.

## License

See the `LICENSE` file in the repository for license details.
