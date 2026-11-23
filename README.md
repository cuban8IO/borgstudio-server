# BorgStudio Server

Self-hosted server for [BorgStudio](https://github.com/cuban8IO/borgstudio), the all-in-one app to manage BorgBackup
repositories.

> **Status:** early planning. There is no usable release yet.

## Goals

- **Central overview**: every machine running BorgStudio reports its backups, checks and storage to the server –
  one shared dashboard and notifications for all of them.
- **Managed backup targets**: the server creates borg repositories and restricted SSH keys per machine, so BorgStudio
  can use it as a backup target – like a hosted service, but run by yourself.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The solution follows Clean Architecture:

| Project | Role |
| --- | --- |
| `BorgStudio.Server.Domain` | Business types, no dependencies |
| `BorgStudio.Server.Application` | Commands and queries with their handlers, interfaces for the outer layers |
| `BorgStudio.Server.Infrastructure` | Implementations of those interfaces (operating system, later database and borg) |
| `BorgStudio.Server.Web` | Blazor Server interface with [MudBlazor](https://mudblazor.com/), host of the server |

```sh
dotnet run --project src/BorgStudio.Server.Web   # http://localhost:5280
dotnet test
```

## Related

- [BorgStudio](https://github.com/cuban8IO/borgstudio) – the desktop app
- [Product page](https://borgstudio.cuban8io.net/)

## License

[MIT](LICENSE) © 2026 Niklas Fischbach
