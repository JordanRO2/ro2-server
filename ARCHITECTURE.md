# Architecture

An RO2 server emulator organized by DDD bounded contexts over a shared kernel, deployed as separate
server processes. It is a **modular monolith**: one regional deployment composed of context modules, not
microservices.

## Layout

```
source/
  Shared/        Cross-cutting, no domain behaviour.
                 RO2.BuildingBlocks       primitives, base types, result/error
                 RO2.BuildingBlocks.Bus   in-process message/event bus
                 RO2.GameData             the game-data kernel: generated table types + the typed
                                          store. Domain layers may reference it (and only it + BuildingBlocks).
  Identity/      Account / authentication context.  Domain / Application / Infrastructure(+Mongo,+Postgres).
                 Consumed by the Login host; depends only on Shared, never on the game domain.
  Economy/       Cash & game mall, craft, auction, NPC shop, trade/stall.  Domain / Application /
                 Infrastructure.{Mongo,Postgres}.
  Inventory/     Bags, item instances, sockets.  Domain / Application / Infrastructure.{Mongo,Postgres}.
  Character/     Character aggregate, stat math, progression, appearance, name, union, pro-job.
                 Domain / Application / Infrastructure.{Mongo,Postgres}.
  Skill/         Learned skills, talents.  Domain / Application / Infrastructure.{Mongo,Postgres}.
  Combat/        Cast path, damage/stat formulas, combat ledgers, gates, cast-path flows.
                 Domain / Application only — pure rules + flows, no persistence.
  World/         Movement, warp, NPC runtime, world events, the shared ActorGate precondition.
                 Domain (Application added as its use-case flows are carved).
  Game/          The remaining game core still being carved into contexts above.
                 RO2.Domain       entities/enums/value objects + the leaves not yet extracted
                 RO2.Application  use cases + ports (Interfaces/) for the leaves not yet extracted
  Platform/      Transport, persistence providers, composition.
                 RO2.Network              ProudNet protocol, RMI handlers, packets (the wire layer)
                 RO2.Persistence.Mongo    MongoDB provider (default)
                 RO2.Persistence.Postgres PostgreSQL provider
                 RO2.Persistence.Redis    Redis provider (ephemeral state + cross-process pub/sub)
                 RO2.Infrastructure       composition root (selects the provider by config)
  Hosts/         The deployable executables.
                 RO2.Server.Login (7101), RO2.Server.Zone (7201), RO2.Server.Chat (7401)
tests/
  RO2.Tests      unit + integration (Testcontainers) tests
```

A carved context has up to three layers: `<Context>.Domain`, `<Context>.Application`, and
`<Context>.Infrastructure.{Mongo,Postgres}` (omitted when the context persists nothing, e.g. Combat, or
has none yet, e.g. World). The data pipeline (`data-pipeline/`) regenerates the JSON under `data/` from the
client's tables; `sample/` holds example message bodies for the login, zone and chat flows.

## Naming

Projects are `RO2.<Context>.<Layer>` — e.g. `RO2.Character.Domain`, `RO2.Combat.Application`,
`RO2.Persistence.Postgres`. Folders group projects by context, not by namespace. Namespaces are **plural**
for the gameplay contexts (`RO2.Characters.Domain`, `RO2.Skills.Domain`, `RO2.Combats.Domain`,
`RO2.Worlds.Domain`) so the namespace cannot shadow a same-named type (e.g. a `Combat`/`World` type); the
project/assembly name stays singular.

## Layers and ports

Each context owns its layers; dependencies point inward.

- **Domain** — entities, value objects, pure rules and the context's own **repository port** (what the
  aggregate needs persisted: `ICharacterRepository`, `IInventoryRepository`, `ISkillRepository`, …) plus
  pure-service primitives (`IClock`, `IRoller`, `IPasswordHasher`). A Domain references **only**
  `RO2.BuildingBlocks` and the `RO2.GameData` kernel — and, where a rule builds on a foundational
  sibling, that sibling's Domain (e.g. `RO2.Combat.Domain` → `RO2.World.Domain` for the shared
  `ActorGateRules`): cross-context Domain references are allowed only toward a foundational context.
- **Application** — the use-case flows, and the **ports those flows consume** to reach live state, the
  host, or another context: gateways/views/notifiers/sessions/catalogs such as `IPlayerSessionGateway`,
  `ICharacterGameData`, `ILearnedSkillsSession`, `ICombatantView`, `ISkillCatalog`. References its own
  Domain + `RO2.GameData` + `RO2.BuildingBlocks` (+ a foundational sibling's Domain when a flow builds on
  one). No wire, no DB, no live registries, no clock — those arrive through a port.
- **Infrastructure.{Mongo,Postgres}** — one implementation per provider of the context's repository ports.
  Nothing in Domain/Application references a store.
- **Host** (`RO2.Server.Zone`/`Login`/`Chat`) — the only place with wire/packets (`RO2.Network`), DI, the
  live registries (`PlayerStateManager`, `NpcStateManager`, `ProudSession`-keyed state), `GameDataStore`
  access and `ITickable`/background loops. The host composes contexts and implements their
  Application/Domain ports with adapters over that live state. Send-ordering and byte-identical packet
  construction stay here.

Ports for leaves not yet extracted still live in `Game/RO2.Application/Interfaces`; that project shrinks as
each leaf is carved into its own context. (This replaces the former "all ports in one `RO2.Application`"
model.)

## Persistence (config-selected)

`Database:Provider` in `config.json` picks the backend at startup: `Mongo` (default) or `Postgres` (a
relational durable store, plus Redis for the cross-process close-range signal bus). Each aggregate sits
behind its context's repository port with one implementation per provider; the composition root
(`RO2.Infrastructure`) wires the chosen one. A Postgres deployment creates its own schema on boot, with no
external migration step.

## Deployment model

The three hosts run as separate processes, mirroring the retail topology. The target for scale is
**shared-nothing per region**: each region runs its own Zone/Chat plus Postgres/Redis owning the local live
state and economy, with a thin **global** layer for accounts/auth (Identity), a character-home directory and
cross-region transfer. That is why Identity is a decoupled bounded context rather than code inside a host.

## Direction (DDD carve, incremental)

The game core (`Game/RO2.Domain` + `Game/RO2.Application`) is being split into the bounded contexts above
over the shared kernel. Done so far: Identity, Economy, Inventory, Character, Skill, and Combat (Domain +
Application); World is in progress (Domain seeded, Application to follow). Remaining leaves still in the game
core — Quest, Mail, Collections, Guild, Social, Ranking, Companion, Dungeon, and the Monster-AI/threat
runtime — are extracted one at a time, each as its own context with cross-context pressure broken by
`BuildingBlocks.Bus` events and ports. A module is promoted to its own **process** only when a concrete
deployment need appears; today the only such boundary is global (Identity) vs. regional.
