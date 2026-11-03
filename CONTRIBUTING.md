# Contributing

## Setup

You need the .NET 11 SDK (11.0.100-rc.3 or newer) and MongoDB running on `localhost:27017`.

```
dotnet restore --packages .packages
dotnet build
dotnet test
```

The server is three hosts: Login on 7101, Zone on 7201 and Chat on 7401. Copy `config.example.json` to `config.json` and adjust it for your machine. You run it locally against a client you own.

## Making a change

Open an issue first for anything larger than a small fix, so we agree on the direction before you spend time on it.

Before sending a pull request, `dotnet build` must report 0 warnings and 0 errors and `dotnet test` must pass in full.

## Commits

Format: `[type]: lowercase description`, where type is one of fix, feat, refactor, docs, chore or test. For example: `[fix]: correct login form validation`.

- Describe the technical change only.
- Write everything (commits, PRs, issues, code comments) in English.
- Commits must be GPG-signed.

`main` is protected by a ruleset that requires signed commits and a linear history. Rebase onto `main`, do not merge it into your branch.
