# BorgStudio Server

Self-hosted server for [BorgStudio](https://github.com/cuban8IO/borgstudio), the all-in-one app to manage BorgBackup
repositories.

> **Status:** early planning. There is no usable release yet.

## Goals

- **Central overview**: every machine running BorgStudio reports its backups, checks and storage to the server –
  one shared dashboard and notifications for all of them.
- **Managed backup targets**: the server creates borg repositories and restricted SSH keys per machine, so BorgStudio
  can use it as a backup target – like a hosted service, but run by yourself.

## First start and sign-in

- **Data**: everything the server stores (SQLite database, cookie keys) lives in a `data` folder next to the program.
  Set `BorgStudio__DataDirectory` to move it. Keep it when updating, and back it up.
- **Setup**: on the first start the server has no users. It writes a one-time setup code to its log and to
  `data/setup-code.txt`; the web interface asks for it, then for the server name and the first administrator.
- **Sign-in**: user name and password, optionally with an authenticator app (TOTP). Five wrong attempts lock an account
  for 15 minutes. Without "stay signed in", a session ends after 60 minutes without activity.
- **Recovery codes**: every user gets ten, shown once. One code replaces password and authenticator; afterwards a new
  password is due, a lost authenticator has to be set up again, and new codes are shown.
- **Users**: administrators add users with the role *Administrator* or *User* (read only), either with a temporary
  password or with a one-time invitation link (valid for 1, 7 or 30 days). Under *Settings*, a public address makes
  invitation links work behind a reverse proxy. Administrators can reset passwords, turn off two-factor
  authentication, lift lockouts, sign users out everywhere, disable and delete accounts; the last active
  administrator is protected.
- **Locked out?** Run `BorgStudio.Server reset-admin [user name]` on the server, as the account the server runs under.
  It prints a temporary password, turns two-factor authentication off, lifts the lockout and enables the account.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The solution follows Clean Architecture:

| Project | Role |
| --- | --- |
| `BorgStudio.Server.Domain` | Business types, no dependencies |
| `BorgStudio.Server.Application` | Commands and queries with their handlers, interfaces for the outer layers |
| `BorgStudio.Server.Infrastructure` | Implementations of those interfaces: database (EF Core, SQLite), accounts (ASP.NET Core Identity), later borg |
| `BorgStudio.Server.Web` | Blazor Server interface with [MudBlazor](https://mudblazor.com/), host of the server |

```sh
dotnet run --project src/BorgStudio.Server.Web   # http://localhost:5280
dotnet test
```

Database changes are EF Core migrations; the server applies them on start.

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/BorgStudio.Server.Infrastructure \
  --startup-project src/BorgStudio.Server.Web --output-dir Persistence/Migrations
```

## Related

- [BorgStudio](https://github.com/cuban8IO/borgstudio) – the desktop app
- [Product page](https://borgstudio.cuban8io.net/)

## License

[MIT](LICENSE) © 2026 Niklas Fischbach
