# J'sUnitySync

A professional multiplayer Unity development and collaboration platform.
Multiple Unity developers work together inside the same Unity project in real time: shared scenes, synchronized GameObjects, collaborative script editing, asset sharing, chat, voice, and screen sharing.

## Components

| Component | Project | Technology |
|---|---|---|
| Desktop application | `src/JsUnitySync.Desktop` | WPF (.NET 8+) |
| Unity Editor extension | `src/JsUnitySync.Unity` | Unity 6, netstandard2.1 |
| Backend API | `src/JsUnitySync.Api` | ASP.NET Core |
| Real-time collaboration service | `src/JsUnitySync.RealTime` | ASP.NET Core WebSockets |
| Asset synchronization service | `src/JsUnitySync.AssetSync` | S3-compatible storage |
| Shared domain & protocol | `src/JsUnitySync.Domain`, `src/JsUnitySync.Contracts` | .NET libraries |

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full system design,
[DEVELOPMENT.md](DEVELOPMENT.md) for conventions, and
[PHASES.md](PHASES.md) for progress tracking.

## Prerequisites

- .NET SDK 10.0 (or 8.0+) — https://dotnet.microsoft.com/download
- (Phase 6+) PostgreSQL 15+ and Redis 7+
- Unity 6 (Editor extension development only)

## Setup

```bash
# restore and build everything
dotnet build JsUnitySync.slnx

# run all tests
dotnet test JsUnitySync.slnx

# run the backend API (Phase 1: health endpoint only)
dotnet run --project src/JsUnitySync.Api
```

The API listens on `http://localhost:5080` by default (see `src/JsUnitySync.Api/appsettings.json`).
`GET /health/live` and `GET /health/ready` report service health.

## Solution layout

```
JsUnitySync.slnx
docs/               Architecture and phase documentation
src/
  JsUnitySync.Domain/     Core domain model (no infrastructure dependencies)
  JsUnitySync.Contracts/  Wire protocol shared by client & server
  JsUnitySync.Api/        Backend HTTP API host
  JsUnitySync.RealTime/   Real-time collaboration service (built in Phase 7)
  JsUnitySync.AssetSync/  Asset synchronization service (built in Phase 12)
  JsUnitySync.Desktop/    WPF desktop application (built in Phase 2)
  JsUnitySync.Unity/      Unity Editor extension (built in Phase 8)
tests/
  JsUnitySync.Domain.Tests/
```
