# Development Conventions — J'sUnitySync

## Language & frameworks
- C# 12+, nullable reference types enabled everywhere.
- .NET 10 for hosts and test projects; **netstandard2.1** for anything the Unity
  Editor must load (`src/JsUnitySync.Unity` and any library it references).
- ASP.NET Core minimal hosting; WPF with MVVM for the desktop app.

## Project structure
- One concern per project (see ARCHITECTURE.md topology).
- `Domain` and `Contracts` have zero package dependencies beyond the BCL.
- Infrastructure projects may depend on infrastructure packages; client projects
  may not depend on server infrastructure.

## Naming
- Public types: `PascalCase`. Private fields: `_camelCase`. Constants: `PascalCase`.
- File-scoped namespaces. One primary type per file.
- Test method names: `MethodName_Scenario_ExpectedResult`.

## Dependency injection
- All services are registered via DI; no service locators, no static mutable state
  in application logic.
- Each library exposes `AddXxx(this IServiceCollection)` extension methods in a
  `DependencyInjection.cs` file.

## Logging
- Inject `ILogger<T>`. Structured message templates only:
  `Log.LogInformation("Scene event applied {EventId} for {SessionId}", eventId, sessionId)`.
- Never log secrets, tokens, or user project file contents.

## Errors & resilience
- Public APIs validate arguments with `ArgumentException` helpers.
- Client networking code retries with exponential backoff + jitter and reconnects
  with replay; never loops without a cancellation token.

## Git
- Conventional commits: `feat:`, `fix:`, `docs:`, `test:`, `refactor:`.
- Never commit `bin/`, `obj/`, user secrets, or generated Unity `Library/` data.

## Testing
- Every major feature lands with tests in the corresponding `tests/` project.
- `dotnet test` must be green before a phase is marked complete.
- Do not mark a phase complete with build errors or failing tests.

## Documentation
- Update `ARCHITECTURE.md` when a system design changes.
- Update `PHASES.md` at the end of every phase (status, date, notes).
- Update `README.md` when setup steps or component layout change.
