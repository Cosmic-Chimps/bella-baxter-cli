# homebrew-bella-baxter-cli

Homebrew tap for [Bella Baxter CLI](https://bella-baxter.io) — the command-line interface for managing and consuming secrets from Bella Baxter.

## Install

```sh
brew tap cosmic-chimps/bella-baxter-cli
brew install bella
```

## Usage

```sh
# Authenticate with your Bella Baxter instance
bella login

# Pull secrets for your project into a .env file
bella pull --project my-project --env dev

# Check connection status
bella status
```

## Upgrade

```sh
brew upgrade bella
```

## Uninstall

```sh
brew uninstall bella
brew untap cosmic-chimps/bella-baxter-cli
```

## Other install methods

| Platform | Command |
|---|---|
| **Linux / macOS** (curl) | `curl -sSfL https://raw.githubusercontent.com/cosmic-chimps/bella-baxter-cli/main/scripts/install-bella.sh \| bash` |
| **Windows** (PowerShell) | `irm https://raw.githubusercontent.com/cosmic-chimps/bella-baxter-cli/main/scripts/install-bella.ps1 \| iex` |
| **WinGet** | `winget install CosmicChimps.BellaBaxterCli` |
| **Docker** | `docker run ghcr.io/cosmic-chimps/bella-baxter-cli:latest` |

## Links

- [Bella Baxter website](https://bella-baxter.io)
- [Bella CLI GitHub repository](https://github.com/cosmic-chimps/bella-baxter-cli)
- [Releases](https://github.com/cosmic-chimps/bella-baxter-cli/releases)
