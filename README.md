# BorgStudio Server

Self-hosted server for [BorgStudio](https://github.com/cuban8IO/borgstudio), the all-in-one app to manage BorgBackup
repositories.

> **Status:** sign-in, users, settings, packages, repositories and borg access over SSH are in place; the BorgStudio
> app's support for the server follows.

## Goals

- **Central overview**: every machine running BorgStudio reports its backups, checks and storage to the server –
  one shared dashboard and notifications for all of them.
- **Managed backup targets**: the server creates borg repositories and restricted SSH keys per machine, so BorgStudio
  can use it as a backup target – like a hosted service, but run by yourself.

## Installation

Each [release](https://github.com/cuban8IO/borgstudio-server/releases) has self-contained packages for Linux (x64,
arm64) and Windows (x64) with the .NET runtime included, and a container image.

| Where | How |
| --- | --- |
| Linux | Unpack `BorgStudio.Server-<version>-linux-x64.tar.gz` (or `-linux-arm64`), then `sudo ./borgstudio-server/install.sh`. Installs to `/opt/borgstudio-server` as the systemd service `borgstudio-server`, running as the user `borgstudio`. |
| Windows | Unpack `BorgStudio.Server-<version>-win-x64.zip`, then run `.\borgstudio-server\install.ps1` in an elevated PowerShell. Installs to `C:\Program Files\BorgStudio Server` as the Windows service `BorgStudioServer`. |
| LXC or VM (Debian 12/13, Ubuntu 22.04/24.04) | `curl -fsSL https://github.com/cuban8IO/borgstudio-server/releases/latest/download/lxc-install.sh \| bash` (as root) |
| Proxmox VE host | `bash -c "$(curl -fsSL https://github.com/cuban8IO/borgstudio-server/releases/latest/download/proxmox-create-ct.sh)"` creates a Debian container and installs the server in it. |
| Docker | `docker run -d -p 8080:8080 -v borgstudio-data:/app/data ghcr.io/cuban8io/borgstudio-server` – see [`deploy/docker/compose.yaml`](deploy/docker/compose.yaml). |

Running the installer of a newer release updates the server; its data and configuration stay. In an LXC
container or VM set up with `lxc-install.sh`, the command `update` does that: it installs the latest release and
does nothing when the server is up to date (from the Proxmox host: `pct exec <id> -- update`). `uninstall.sh` /
`uninstall.ps1` remove it (`--purge` / `-Purge` also deletes the data).

What changed in each version is in [`CHANGELOG.md`](CHANGELOG.md) and on the server's *Changelog* page. Once a day
the server asks GitHub for a newer release and tells administrators about it, with the update command for the way it
is installed (can be switched off under *Settings*).

**HTTPS:** installed servers listen on HTTPS port 8443 with a self-signed certificate until you upload your own under
*Settings* or name one in `borgstudio-server.json`. The container listens on HTTP port 8080 for a reverse proxy.
Ports, certificate files and proxy handling are set in `borgstudio-server.json` next to the program (see
[`deploy/borgstudio-server.example.json`](deploy/borgstudio-server.example.json)) or with environment variables
such as `BorgStudio__Http__Enabled=true`.

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
- **Locked out?** Run `BorgStudio.Server reset-admin [user name]` on the server, as the account the server runs under
  (Linux: `runuser -u borgstudio -- /opt/borgstudio-server/BorgStudio.Server reset-admin`, Windows: elevated, Docker:
  `docker exec <container> /app/BorgStudio.Server reset-admin`).
  It prints a temporary password, turns two-factor authentication off, lifts the lockout and enables the account.

## Repositories and datastores

- **Repositories** belong to one user and can be shared with others, with *Full* access or *Append only* (new
  backups and restoring, nothing can be deleted). Every user may create repositories unless an administrator
  forbids it; administrators manage all of them. The data is encrypted by the BorgStudio app, the server never sees
  its contents.
- **Datastores** are the folders repositories live in, e.g. a second disk or a mounted NAS share; a new server has one
  in `data/repositories`. A repository is the folder `<datastore>/<owner>/<name>`. Administrators add datastores,
  set them read-only for maintenance and move repositories between them. In Docker, mount a volume for every
  additional datastore.
- **External datastores:** a Hetzner Storage Box (user name and the password once; the server installs its own key
  and trusts only Hetzner's published host keys) or any SSH server with borg, e.g. a NAS or rsync.net (confirm its
  host key). Clients still connect to this server, which passes borg through, so access rights, quotas and
  statistics work the same. There a repository is a folder named by its id in the account's home – best use a
  (sub-)account of its own. Moves to and from them use rsync.
- **Quotas** can be set per repository (enforced by borg) and per user (the space of all their repositories,
  measured every 15 minutes).
- **Deleted** repositories stay in the trash for 7 days and can be restored until then; the repositories of a deleted
  user go there too, so an administrator can hand them on.
- Backups into the repositories need a server on Linux (LXC, VM, Linux service or Docker); on Windows they can only be
  managed.

## Backing up with borg

- **SSH for borg:** the server runs its own OpenSSH service for borg on port 2222 (`BorgStudio:Ssh` in
  `borgstudio-server.json`), under its own account and without touching the system's SSH service. It only lets in
  the keys it knows, and every key can run nothing but `borg serve` for its one repository – with *Append only* and
  the quotas applied. The install script sets up OpenSSH and downloads the official borg 1.4 build from the
  [borg project](https://github.com/borgbackup/borg/releases) (checked against a fixed checksum); the container
  image uses Debian's borg and needs port 2222 published.
- **BorgStudio app:** it pairs with the server through the browser (the app shows a code, you confirm it after
  signing in), then lists and creates repositories and registers its keys by itself. Paired apps and API tokens are
  listed under *Devices*, where they can be revoked.
- **Other borg clients** (scripts, borgmatic, Vorta): add a public key on the repository's page and use the address
  shown there, e.g. `borg init --encryption=repokey-blake2 ssh://borgstudio@backup.example.org:2222/./3f2a…`.
- **Interface:** JSON at `/api/v1` with `Authorization: Bearer <token>` – `GET server`, `GET datastores`,
  `GET|POST repositories`, `GET repositories/{id}` (usage, quota, last access, who else uses it),
  `PUT repositories/{id}/ssh-key`, `POST repositories/{id}/reports` (a run of the app: backup, check, prune or
  compact with borg's figures); pairing at `POST pairing` and `POST pairing/complete`. It can only do what backing
  up needs: deleting, sharing and quotas stay in the web interface.

## Statistics

- **Overview:** stored data, savings, success of the last 30 days, which repository, user quota or datastore fills up
  first, and one line per repository with its state; administrators also see the load of the system.
- **Per repository:** storage over time with a forecast of when the quota is full, growth and traffic per connection,
  the last 30 days, an activity calendar – and, from the BorgStudio app's reports, archives, savings, deduplication,
  compression and results (the server cannot read the encrypted repositories itself).
- **Datastores and system** (administrators): use of every datastore over time with a forecast, traffic per day,
  and CPU, memory, disk I/O and network of the container or computer (Linux), kept for 24 hours per minute, 30 days
  per hour and a year per day. Storage history: one point per day for two years; connections and reports: one year
  and two years.

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
