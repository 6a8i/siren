<h1 align="center">Siren</h1>
<h3 align="center">A personal media server, forked from Jellyfin</h3>

---

Siren is a media server backend and API. It started as a fork of [Jellyfin](https://github.com/jellyfin/jellyfin) and now follows its own path: it is maintained for personal use and as a place to experiment with improvements that would not fit the upstream project. It is not synchronized with Jellyfin and does not aim to stay compatible with future Jellyfin releases.

Siren keeps the Jellyfin codebase, namespaces, and API, so existing Jellyfin clients (web, mobile, TV) and plugins continue to work with it. Everything that was true about running a Jellyfin server is still true here.

## License and attribution

Siren is free software licensed under the [GNU GPL v2.0](LICENSE), the same license as Jellyfin. It contains code written by the Jellyfin project and its [contributors](CONTRIBUTORS.md), which in turn descends from Emby 3.5.2. Jellyfin is a trademark of the Jellyfin project; Siren is not affiliated with or endorsed by it.

## Development

### Prerequisites

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet). Any IDE with .NET support works; recent versions of [Visual Studio](https://visualstudio.microsoft.com/downloads/) (2022 or newer), [JetBrains Rider](https://www.jetbrains.com/rider/), and [Visual Studio Code](https://code.visualstudio.com/Download) are all fine.

[ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) is required for transcoding and media probing.

### Cloning the repository

```bash
git clone https://github.com/6a8i/siren.git
```

### Web client

The server hosts the static files of the web client by default, but they are not part of this repository. Either build [jellyfin-web](https://github.com/jellyfin/jellyfin-web) from source or copy the `jellyfin-web` folder from an existing Jellyfin installation. For frontend work it is usually easier to [host the web client separately](#hosting-the-web-client-separately).

### Running the server

#### Visual Studio or Rider

Open `Jellyfin.sln` and run the `Jellyfin.Server` project.

#### Visual Studio Code

Open the repository folder (or `siren.code-workspace`), install the recommended workspace extensions, and press `F5`.

#### Command line

```bash
dotnet run --project Jellyfin.Server --webdir /absolute/path/to/jellyfin-web/dist
```

Or build once and run the executable directly. Pass `--help` to list all command line options.

```bash
dotnet build
cd Jellyfin.Server/bin/Debug/net10.0
```

On Linux and macOS run `./jellyfin`; on Windows run `jellyfin.exe`.

#### Accessing the server

With the web client hosted, open `http://localhost:8096`. The API documentation is available at `http://localhost:8096/api-docs/swagger/index.html`.

### Running the tests

```bash
dotnet test
```

Tests also run in GitHub Actions on every push and pull request, together with `dotnet format --verify-no-changes` and CodeQL analysis.

### Hosting the web client separately

To run the server without hosting the web content, pass `--nowebclient` on the command line or set the environment variable `JELLYFIN_NOWEBCONTENT=true`. A launch profile named `Jellyfin.Server (nowebcontent)` is available in Visual Studio for this scenario.

**Note:** the setup wizard cannot be run when the web client is hosted separately.

### Dev container

A [dev container](.devcontainer/devcontainer.json) configuration is included for GitHub Codespaces and VS Code. It installs the .NET SDK and, optionally, ffmpeg.

## Versioning

Siren continues from Jellyfin 13.0.0, the version at the point of divergence. The `bump_version` script updates the shared assembly version and the NuGet package versions.
