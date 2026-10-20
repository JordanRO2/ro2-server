# RO2_Server

[![CI](https://github.com/JordanRO2/ro2-server/actions/workflows/ci.yml/badge.svg)](https://github.com/JordanRO2/ro2-server/actions/workflows/ci.yml)

Ragnarok Online 2 server emulator written in C# 15 / .NET 11.0.

## Status

**Working:**
- ProudNet protocol (RSA key exchange, session encryption, heartbeat, P2P/transport RMIs)
- Login + authentication (account lookup, no auto-register) and character list / creation / selection
- World entry and UNENC game-RMI dispatch, verified against real retail captures
- Zone gameplay: movement, normal + skill combat, monster AI (proximity aggro, chase, leash/reset),
  drops and looting, party, chat, inventory, equipment refinement, derived stats
- Domain-driven architecture with pluggable persistence (MongoDB default; PostgreSQL and Redis providers)
  and a dedicated Identity bounded context

**In Progress / known gaps:**
- Player "in battle" stance polish (enter works; the leave/revert behaviour and weapon→stance map are unfinished)
- Broader content coverage and the ongoing hardening/verification passes

## Requirements

- .NET 11.0 SDK (11.0.100-rc.1 or later until the November 2026 GA)
- MongoDB (running on `localhost:27017` by default)
- PostgreSQL and Redis are optional (alternative persistence providers)

## Building

```bash
# Kill any running server processes first (DLLs get locked), then:
dotnet build --no-restore
```

If the NuGet cache gets corrupted: `dotnet restore --packages ".packages"`.

## Running

```bash
# Login server (port 7101)
dotnet run --project source/Hosts/RO2.Server.Login --no-build --packages ".packages"

# Zone server (port 7201)
dotnet run --project source/Hosts/RO2.Server.Zone --no-build --packages ".packages"

# Chat server (port 7401)
dotnet run --project source/Hosts/RO2.Server.Chat --no-build --packages ".packages"
```

Convenience launchers: `start-all.bat`, `start-debug-all.bat`.

## Configuration

Edit `config.json`:

```json
{
  "Login":    { "Host": "0.0.0.0", "Port": 7101 },
  "Zone":     { "Host": "0.0.0.0", "Port": 7201 },
  "Database": { "ConnectionString": "mongodb://localhost:27017", "DatabaseName": "ro2_server" },
  "Game":     { "ExpRate": 1, "DropRate": 1, "GoldRate": 1 }
}
```

## Architecture

```
source/
  Shared/
    RO2.BuildingBlocks/        # Shared primitives
    RO2.BuildingBlocks.Bus/    # In-process message bus
  Game/
    RO2.Domain/                # Entities, enums, value objects, game-data models
    RO2.Application/           # Use cases, repository interfaces
  Platform/
    RO2.Infrastructure/        # Cross-cutting infrastructure wiring
    RO2.Network/               # ProudNet protocol, RMI handlers, packets
    RO2.Persistence.Mongo/     # MongoDB persistence provider (default)
    RO2.Persistence.Postgres/  # PostgreSQL persistence provider
    RO2.Persistence.Redis/     # Redis persistence provider
  Identity/                    # Identity bounded context (DDD): Domain / Application /
                               #   Infrastructure + Mongo and Postgres implementations
  Hosts/
    RO2.Server.Login/          # Login server  (7101)
    RO2.Server.Zone/           # Zone server   (7201)
    RO2.Server.Chat/           # Chat server   (7401)
tests/
  RO2.Tests/                   # Unit + integration tests
```

Game data is **not hardcoded** — it is generated from the client's `.tbl`/XLSX tables into
JSON under `data/` (and `data/strings/`) by the `data-pipeline/` project.

## Documentation

The code is the reference: the handlers under `src/RO2.Server.*/Handlers` document the protocol
messages they implement, and `sample/` holds example message bodies for the login, zone and chat
flows. The `data-pipeline/` project regenerates the JSON under `data/` from the client's data tables.

## License

MIT License - see [LICENSE](LICENSE)
