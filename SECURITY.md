# Security Policy

## Scope

`ro2-server` is a server emulator published for learning and research. It is meant to be
run locally against a client you already own; it is not a hosted service and ships no
production deployment. "Security" here means the code that parses untrusted network input
(the ProudNet transport and the RMI packet handlers) and the persistence layer, not the
secrecy of game content.

## Supported versions

Only the current `main` branch is supported. Fixes land on `main`; there are no
long-lived release branches to backport to.

| Version | Supported |
| ------- | --------- |
| `main`  | ✅        |
| anything else | ❌  |

## Reporting a vulnerability

Please report security issues **privately**, not in a public issue or pull request.

- Use GitHub's **"Report a vulnerability"** button on the
  [Security advisories](https://github.com/JordanRO2/ro2-server/security/advisories) page
  (Security tab → Report a vulnerability). This opens a private advisory only the
  maintainer can see.

When reporting, please include:

- the affected file or packet handler (a `path:line` is ideal);
- the input that triggers it (a captured frame, a minimal repro, or the exact bytes);
- the impact you observed (crash, hang, out-of-bounds read, unauthenticated state change,
  data corruption).

A proof-of-concept that merely demonstrates the class of problem is enough — please do not
include a weaponized exploit or a step-by-step extraction path.

### What to expect

This is a best-effort, maintainer-time project, so there is no paid bounty and no strict
SLA. Expect an initial acknowledgement within a few days. Once a fix is on `main` the
advisory will be published with credit to the reporter unless you ask to remain anonymous.

## Out of scope

- Game-balance or anti-cheat concerns (the emulator trusts the operator of its own box).
- Vulnerabilities that require already having administrative access to the host, the
  database, or the operator's machine.
- Issues in third-party dependencies with no demonstrated impact here — report those
  upstream; Dependabot tracks our dependency advisories.
