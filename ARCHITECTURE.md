# Architecture

An RO2 server emulator organized by Clean Architecture layering and DDD bounded contexts, deployed as
separate server processes.

## Layout

```
source/
  Shared/     Cross-cutting primitives with no domain knowledge.
              RO2.BuildingBlocks, RO2.BuildingBlocks.Bus (in-process message bus)
  Identity/   The account / authentication bounded context — its own Domain / Application /
              Infrastructure with Mongo and Postgres stores. Consumed by the Login host; depends
              only on Shared, never on the game domain.
  Game/       The game domain core.
              RO2.Domain       Entities, enums, value objects, game-data models (depends on nothing)
              RO2.Application  Use cases and repository interfaces (the ports)
  Platform/   Transport, persistence providers, and composition.
              RO2.Network              ProudNet protocol, RMI handlers, packets
              RO2.Persistence.Mongo    MongoDB provider (default)
              RO2.Persistence.Postgres PostgreSQL provider
              RO2.Persistence.Redis    Redis provider (ephemeral state + pub/sub)
              RO2.Infrastructure       Composition root (selects the provider by config)
  Hosts/      The deployable server executables.
              RO2.Server.Login (7101), RO2.Server.Zone (7201), RO2.Server.Chat (7401)
tests/
  RO2.Tests   Unit and integration (Testcontainers) tests
```

The data pipeline (`data-pipeline/`) regenerates the JSON under `data/` from the client's data tables,
and `sample/` holds example message bodies for the login, zone and chat flows.

## Naming

Projects are `RO2.<Context>.<Layer>` — e.g. `RO2.Identity.Domain`, `RO2.Persistence.Postgres`,
`RO2.Server.Zone`. The namespace matches the project name; folders group projects by role, not by
namespace.

## Dependency rules (Clean Architecture)

Dependencies point inward, toward the domain:

- `RO2.Domain` depends on nothing.
- `RO2.Application` depends only on `RO2.Domain` and defines the repository interfaces (ports).
- Persistence providers implement those interfaces; nothing in Domain/Application references a store.
- `RO2.Network` depends only on `RO2.Domain`.
- Hosts compose everything and are the only executables.
- The Identity context depends only on `Shared` — never on the game domain.

## Persistence (config-selected)

`Database:Provider` in `config.json` picks the backend at startup: `Mongo` (default) or `Postgres`
(a relational durable store plus Redis for the cross-process close-range signal bus). Every aggregate
sits behind an interface in `RO2.Application.Interfaces` with one implementation per provider; the
composition root (`RO2.Infrastructure`) wires the chosen one. A Postgres deployment creates its own
schema on boot, with no external migration step.

## Deployment model

The three hosts run as separate processes, mirroring the retail topology. The target for scale is
**shared-nothing per region**: each region runs its own Zone/Chat plus Postgres/Redis owning the local
live state and economy, with a thin **global** layer for accounts/auth (the Identity context), a
character-home directory, and cross-region transfer. That is why Identity is a decoupled bounded
context rather than code inside a host.

## Direction (DDD, incremental)

The game core (`RO2.Domain` / `RO2.Application`) is being split into bounded-context modules
(Economy, Social, Character/Progression, World) over a shared kernel, as a **modular monolith** — one
regional deployment composed of modules, not microservices. A module is promoted to its own service
only when a concrete deployment need appears; today the only such boundary is global vs. regional.
