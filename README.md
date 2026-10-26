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

- .NET 11.0 SDK (11.0.100-rc.3 or later until the November 2026 GA — the build gate blocks SDKs with known CVEs)
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

A **modular monolith** organized by DDD bounded contexts over a shared kernel. See
[ARCHITECTURE.md](ARCHITECTURE.md) for the layering, port, and dependency rules.

```
source/
  Shared/          RO2.BuildingBlocks, RO2.BuildingBlocks.Bus, RO2.GameData (game-data kernel)
  Identity/        Account / auth context       — Domain / Application / Infrastructure(+Mongo,+Postgres)
  Economy/         Mall, craft, auction, shop    — Domain / Application / Infrastructure.{Mongo,Postgres}
  Inventory/       Bags, items, sockets          — Domain / Application / Infrastructure.{Mongo,Postgres}
  Character/       Character, stats, progression — Domain / Application / Infrastructure.{Mongo,Postgres}
  Skill/           Learned skills, talents       — Domain / Application / Infrastructure.{Mongo,Postgres}
  Combat/          Cast path, formulas, gates    — Domain / Application (pure, no persistence)
  World/           Movement, warp, NPC runtime   — Domain (Application as flows are carved)
  Game/            RO2.Domain + RO2.Application — the remaining core still being carved into contexts
  Platform/        RO2.Network (wire), RO2.Persistence.{Mongo,Postgres,Redis}, RO2.Infrastructure (composition)
  Hosts/           RO2.Server.Login (7101), RO2.Server.Zone (7201), RO2.Server.Chat (7401)
tests/
  RO2.Tests/       unit + integration (Testcontainers) tests
```

Each carved context keeps its own `Domain` (entities, rules, its repository port) and `Application` (use-case
flows + the ports they consume); persistence lives in `Infrastructure.{Mongo,Postgres}`; the wire layer, DI and
live state stay in the hosts. Game data is **not hardcoded** — it is generated from the client's `.tbl`/XLSX
tables into JSON under `data/` (and `data/strings/`) by the `data-pipeline/` project.

## Documentation

The code is the reference: the handlers under `src/RO2.Server.*/Handlers` document the protocol
messages they implement, and `sample/` holds example message bodies for the login, zone and chat
flows. The `data-pipeline/` project regenerates the JSON under `data/` from the client's data tables.

## License

MIT License - see [LICENSE](LICENSE)
