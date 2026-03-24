# Bella CLI

The official command-line interface for [Bella Baxter](https://bella-baxter.io) — a unified secret management gateway.

Bella CLI is a **self-contained binary** with zero runtime dependencies. No Node.js, no Python, no .NET runtime required on the target machine.

---

## Table of Contents

- [Bella CLI](#bella-cli)
  - [Table of Contents](#table-of-contents)
  - [Installation](#installation)
    - [Linux / macOS (one-liner)](#linux--macos-one-liner)
    - [Windows (PowerShell)](#windows-powershell)
    - [Manual download](#manual-download)
    - [Self-update](#self-update)
  - [Quick Start](#quick-start)
  - [Authentication & Billing](#authentication--billing)
  - [Shell Prompt Integration](#shell-prompt-integration)
    - [The `.bella` file](#the-bella-file)
    - [Which prompt do I have?](#which-prompt-do-i-have)
    - [Plain Bash](#plain-bash)
    - [Plain Zsh](#plain-zsh)
    - [Oh My Zsh](#oh-my-zsh)
    - [Powerlevel10k](#powerlevel10k)
    - [Oh My Posh](#oh-my-posh)
    - [Starship](#starship)
    - [Fish Shell](#fish-shell)
    - [PowerShell (Windows)](#powershell-windows)
  - [Commands Reference](#commands-reference)
  - [`bella run` vs `bella exec`](#bella-run-vs-bella-exec)
  - [Workload Identity (GitHub Actions / Kubernetes)](#workload-identity-github-actions--kubernetes)
  - [Issuing Scoped Tokens (`bella issue`)](#issuing-scoped-tokens-bella-issue)
  - [Shell Credential Export (`bella env`)](#shell-credential-export-bella-env)
  - [Interactive Subshell (`bella shell open`)](#interactive-subshell-bella-shell-open)
  - [Ephemeral Context (`bella context use`)](#ephemeral-context-bella-context-use)
  - [SSH Certificate Authority](#ssh-certificate-authority)
    - [How it works](#how-it-works)
    - [Quick start (admin)](#quick-start-admin)
    - [Developer workflow](#developer-workflow)
    - [Commands](#commands)
  - [Agent Sidecar](#agent-sidecar)
    - [How it works](#how-it-works-1)
    - [Quick start](#quick-start-1)
    - [Config file (`bella-agent.yaml`)](#config-file-bella-agentyaml)
    - [Works with `bella run --watch`](#works-with-bella-run---watch)
  - [MCP Server](#mcp-server)
    - [Usage](#usage)
  - [Typed Secret Code Generation](#typed-secret-code-generation)
    - [Quick examples](#quick-examples)
    - [Supported languages](#supported-languages)
    - [Options](#options)
    - [Works with `bella watch`](#works-with-bella-watch)
  - [Development](#development)
    - [Step 1 — Install the CLI globally (once, or after every code change)](#step-1--install-the-cli-globally-once-or-after-every-code-change)
    - [Step 2 — Point at the local API](#step-2--point-at-the-local-api)
    - [Step 3 — Start the local API (Aspire)](#step-3--start-the-local-api-aspire)
    - [Step 4 — Full dev loop in practice](#step-4--full-dev-loop-in-practice)
    - [PATH note](#path-note)
    - [dotnet run (no install required)](#dotnet-run-no-install-required)
    - [Debugging in VS Code / Rider](#debugging-in-vs-code--rider)
  - [Configuration](#configuration)
  - [License](#license)

---

## Installation

### Linux / macOS (one-liner)

```bash
curl -sSfL https://raw.githubusercontent.com/cosmic-chimps/bella-baxter/main/scripts/install-bella.sh | bash
```

To install a specific version:
```bash
curl -sSfL https://raw.githubusercontent.com/cosmic-chimps/bella-baxter/main/scripts/install-bella.sh | bash -s -- --version 1.2.3
```

To install to a custom directory:
```bash
BELLA_INSTALL_DIR="$HOME/.local/bin" curl -sSfL .../install-bella.sh | bash
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/cosmic-chimps/bella-baxter/main/scripts/install-bella.ps1 | iex
```

### Manual download

Download the binary for your platform from the [Releases page](https://github.com/cosmic-chimps/bella-baxter/releases), make it executable, and place it on your `$PATH`:

| Platform | File |
|---|---|
| Windows x64 | `cli-win-x64.exe` |
| Windows ARM64 | `cli-win-arm64.exe` |
| macOS Intel | `cli-osx-x64` |
| macOS Apple Silicon | `cli-osx-arm64` |
| Linux x64 | `cli-linux-x64` |
| Linux ARM64 | `cli-linux-arm64` |
| Linux (musl/Alpine) | `cli-linux-musl-x64` |

```bash
# Example for Linux x64
curl -Lo bella https://github.com/cosmic-chimps/bella-baxter/releases/latest/download/cli-linux-x64
chmod +x bella
sudo mv bella /usr/local/bin/
```

### Self-update

Once installed, bella can update itself:

```bash
bella upgrade
bella upgrade --check   # check without installing
```

---

## Quick Start

```bash
# 1. Log in (opens browser for OAuth2)
bella login

# 2. Log in with an API key (CI/CD, no browser)
bella login --api-key bax-mykeyid-mysigningsecret

# 3. List your projects
bella projects list

# 4. Navigate to your project directory and create a .bella context file
cd my-project/
bella context init    # select project + environment interactively

# 5. List secrets in the current project/environment
bella secrets list

# 6. Run a command with secrets injected as environment variables
bella run -- node server.js
```

---

## Authentication & Billing

Bella supports two login methods. **Which one you should use depends on who is running the command.**

### OAuth — for humans (recommended)

```bash
bella login          # opens a browser window
```

- Authenticates via your Keycloak account (same as the web app)
- The token does **not** encode a specific project or environment
- **Not billed** under the pay-as-you-go model — ideal for developers working interactively
- Requires a `.bella` file (or `-p`/`-e` flags) for commands that need project/environment context

> **Human users should always use OAuth.** It keeps your usage outside the billing meter and integrates naturally with the web app's identity.

### API keys — for machines

```bash
bella login --api-key bax-<keyid>-<secret>
```

- Authenticates with a pre-issued API key (issued from the Bella web app)
- The key **encodes the project slug and environment slug** — no `.bella` file needed
- **Billed** per API call under the pay-as-you-go model
- Designed for CI/CD pipelines, automated deployments, and non-interactive workloads

### The `.bella` file and auth context

| Auth method | `.bella` file required? | Notes |
|---|---|---|
| **OAuth** | **Yes** | Token has no project/env context — Bella reads it from `.bella` or `-p`/`-e` flags |
| **API key** | No | Project + env are encoded in the key itself |

When you run `bella login --api-key bax-…`, Bella **automatically creates a `.bella` file** in the current directory with the project and environment extracted from the key. This is why you'll see a `.bella` file appear after API key login — it is generated for you.

You can commit the `.bella` file to share context with your team, or add it to `.gitignore` to keep it personal.

---

## Shell Prompt Integration

Bella can show your current project and environment context directly in your terminal prompt — like how modern prompts show the Node.js or .NET version.

```
❯ bella: myproject/dev  ~/src/myapp  main*
```

The segment **only appears** when a `.bella` file exists in the current directory (or a parent directory). In any other directory, the segment is hidden. No network calls are made — it reads a local file, so it's instant.

### The `.bella` file

Create a `.bella` file in your project directory to associate it with a Bella project and environment:

```bash
# Interactive
bella context init

# Direct
bella context init myproject dev

# Check what context is active
bella context get

# Remove the .bella file
bella context clear
```

The `.bella` file is a simple text file:
```
project = "myproject"
environment = "dev"
```

You can commit it to your repository to share the default context with your team, or add it to `.gitignore` if it's personal.

> **Note:** When using OAuth, the `.bella` file is **required** because the token has no project/environment context built in. When using an API key, the file is optional (the key already encodes the project/env) and is auto-created by `bella login --api-key`. See [Authentication & Billing](#authentication--billing) for details.

---

### Which prompt do I have?

Not sure which prompt framework you're using? Run these:

```bash
# Check your shell
echo $SHELL           # /bin/zsh or /bin/bash or /usr/bin/fish

# Check for Oh My Zsh
echo $ZSH             # non-empty if Oh My Zsh is installed

# Check for Powerlevel10k (zsh)
echo $POWERLEVEL9K_MODE  # non-empty if p10k is active
# or check your .zshrc for: source ~/powerlevel10k/...

# Check for Starship
starship --version    # works if starship is installed

# Check for Oh My Posh
oh-my-posh --version  # works if oh-my-posh is installed

# Check for Fish
fish --version        # if you're in a fish session
```

---

### Plain Bash

**Config file:** `~/.bashrc`

Add these lines to your `~/.bashrc`:

```bash
_bella_context() {
  [ -f ".bella" ] || return
  command -v bella &>/dev/null || return
  local ctx
  ctx=$(bella context get --quiet 2>/dev/null) || return
  echo -n "\[\033[0;36m\]bella: ${ctx}\[\033[0m\] "
}
PS1='$(_bella_context)'"$PS1"
```

Apply: `source ~/.bashrc`

Or let bella generate it for you:
```bash
bella shell init bash >> ~/.bashrc && source ~/.bashrc
```

---

### Plain Zsh

**Config file:** `~/.zshrc`

Add these lines to your `~/.zshrc`:

```zsh
_bella_context() {
  [ -f ".bella" ] || return
  command -v bella &>/dev/null || return
  local ctx
  ctx=$(bella context get --quiet 2>/dev/null) || return
  echo -n "%F{cyan}bella: ${ctx}%f "
}
PROMPT='$(_bella_context)'"$PROMPT"
```

Apply: `source ~/.zshrc`

Or let bella generate it:
```bash
bella shell init zsh >> ~/.zshrc && source ~/.zshrc
```

---

### Oh My Zsh

**What it is:** [Oh My Zsh](https://ohmyz.sh/) is the most popular Zsh framework. It provides themes and plugins. If you have `$ZSH` set, you're using it.

**Config file:** `~/.zshrc` (after the `source $ZSH/oh-my-zsh.sh` line)

Add this function to your `~/.zshrc`:

```zsh
prompt_bella_context() {
  local bellafile
  bellafile=$(find . -maxdepth 1 -name '.bella' -print -quit)
  if [[ -n "$bellafile" ]]; then
    if which bella &>/dev/null; then
      local ctx
      ctx=$(bella context get --quiet 2>/dev/null)
      if [[ -n "$ctx" ]]; then
        prompt_segment cyan black "bella: $ctx"
      fi
    fi
  fi
}
```

Then add `prompt_bella_context` to your theme's `build_prompt` function, or prepend it to `PROMPT`:

```zsh
# After sourcing oh-my-zsh, add this:
PROMPT='$(prompt_bella_context)'"$PROMPT"
```

Apply: `source ~/.zshrc`

Or let bella generate it:
```bash
bella shell init oh-my-zsh >> ~/.zshrc && source ~/.zshrc
```

> **Note:** `prompt_segment` is a function provided by the `agnoster` and many other Oh My Zsh themes. If your theme doesn't provide it, use the plain zsh snippet instead.

---

### Powerlevel10k

**What it is:** [Powerlevel10k](https://github.com/romkatv/powerlevel10k) is a fast, feature-rich Zsh theme. If your prompt has segments for git, time, etc. and was configured with `p10k configure`, you're using it.

**Config files:** `~/.zshrc` + `~/.p10k.zsh`

**Step 1:** Add the segment function to `~/.zshrc` (before the `source ~/.p10k.zsh` line):

```zsh
function prompt_bella() {
  local bellafile
  bellafile=$(find . -maxdepth 1 -name '.bella' -print -quit)
  if [[ -n "$bellafile" ]]; then
    command -v bella &>/dev/null || return
    local ctx
    ctx=$(bella context get --quiet 2>/dev/null) || return
    p10k segment -f cyan -i '🔐' -t "bella: $ctx"
  fi
}
```

**Step 2:** Add `bella` to the elements list in `~/.p10k.zsh`:

```zsh
# Find this line in ~/.p10k.zsh and add 'bella' where you want it:
typeset -g POWERLEVEL9K_LEFT_PROMPT_ELEMENTS=(
  # ... existing elements ...
  bella           # ← add this
  # ... more elements ...
)
```

Apply: `source ~/.zshrc`

Or let bella generate the snippet:
```bash
bella shell init powerlevel10k
# Then follow the two-step manual instructions above
```

---

### Oh My Posh

**What it is:** [Oh My Posh](https://ohmyposh.dev/) is a prompt engine for any shell. It uses a JSON/TOML/YAML theme file. Available on Windows, macOS, and Linux.

**Find your theme file:**
```bash
echo $POSH_THEME     # prints the path to your active theme
# e.g. ~/.config/oh-my-posh/theme.json
```

**Add this JSON segment** to your theme file inside a `segments` array:

```json
{
  "type": "command",
  "style": "plain",
  "foreground": "cyan",
  "template": "{{ if .Output }}bella: {{ .Output }} {{ end }}",
  "properties": {
    "shell": "bash",
    "command": "[ -f .bella ] && bella context get --quiet 2>/dev/null || true"
  }
}
```

**Full example** — adding it to a block:
```json
{
  "blocks": [
    {
      "type": "prompt",
      "alignment": "left",
      "segments": [
        {
          "type": "command",
          "style": "plain",
          "foreground": "cyan",
          "template": "{{ if .Output }}bella: {{ .Output }} {{ end }}",
          "properties": {
            "shell": "bash",
            "command": "[ -f .bella ] && bella context get --quiet 2>/dev/null || true"
          }
        }
        // ... your other segments
      ]
    }
  ]
}
```

Reload: restart your shell or run `exec $SHELL`.

Or let bella generate the snippet:
```bash
bella shell init oh-my-posh
```

---

### Starship

**What it is:** [Starship](https://starship.rs/) is a cross-shell prompt written in Rust. Works with bash, zsh, fish, PowerShell, and more. Very popular and fast.

**Check if installed:** `starship --version`

**Config file:** `~/.config/starship.toml`

Add this block to your `~/.config/starship.toml`:

```toml
[custom.bella]
command = "bella context get --quiet"
detect_files = [".bella"]
symbol = "🔐 "
style = "bold cyan"
format = "[$symbol$output]($style) "
```

> The `detect_files = [".bella"]` key means Starship **only runs the command when `.bella` exists** in the current directory. In other directories, there is zero overhead.

Apply: Starship picks up config changes immediately on next prompt draw.

Or let bella generate it:
```bash
bella shell init starship >> ~/.config/starship.toml
```

If you haven't set up Starship yet, add the init hook to your shell first:
```bash
# For bash — add to ~/.bashrc:
eval "$(starship init bash)"

# For zsh — add to ~/.zshrc:
eval "$(starship init zsh)"

# For fish — add to ~/.config/fish/config.fish:
starship init fish | source
```

---

### Fish Shell

**What it is:** [Fish](https://fishshell.com/) is a smart, user-friendly shell. It has a different syntax from bash/zsh and configures itself via functions rather than a startup file.

**Config file:** `~/.config/fish/config.fish`  
(Or create a new file: `~/.config/fish/conf.d/bella.fish`)

Add this to your fish config:

```fish
function _bella_context
  test -f .bella; or return
  command -v bella &>/dev/null; or return
  set -l ctx (bella context get --quiet 2>/dev/null)
  test -n "$ctx"; or return
  set_color cyan
  echo -n "bella: $ctx "
  set_color normal
end

# Wrap the default fish_prompt to prepend the bella segment:
functions -c fish_prompt _bella_original_prompt
function fish_prompt
  _bella_context
  _bella_original_prompt
end
```

Apply: `source ~/.config/fish/config.fish` or open a new terminal.

Or let bella generate it:
```bash
bella shell init fish >> ~/.config/fish/conf.d/bella.fish
```

> **Note:** If you already have a custom `fish_prompt` function, just call `_bella_context` at the start of it instead of using the `functions -c` wrapper.

---

### PowerShell (Windows)

**Config file:** `$PROFILE` (usually `~\Documents\PowerShell\Microsoft.PowerShell_profile.ps1`)

Open your profile: `notepad $PROFILE`

Add this to your PowerShell profile:

```powershell
$__originalPrompt = if (Get-Command prompt -ErrorAction SilentlyContinue) {
  $Function:prompt
} else {
  { "PS $($executionContext.SessionState.Path.CurrentLocation)> " }
}

function prompt {
  if (Test-Path ".bella") {
    $ctx = bella context get --quiet 2>$null
    if ($ctx) {
      Write-Host "bella: $ctx " -NoNewline -ForegroundColor Cyan
    }
  }
  & $__originalPrompt
}
```

Apply: `. $PROFILE` or open a new terminal.

Or let bella generate it:
```powershell
bella shell init powershell | Add-Content $PROFILE
```

---

## Commands Reference

```
bella login                   Log in (browser OAuth2 or API key)
bella logout                  Log out
bella whoami                  Show the currently logged-in user

bella auth status             Show authentication status
bella auth refresh             Refresh OAuth2 token

bella projects list           List projects
bella projects get <slug>     Get project details
bella projects create         Create a project
bella projects update         Update a project
bella projects delete         Delete a project

bella environments list       List environments
bella environments get        Get environment details
bella environments create     Create an environment
bella environments update     Update an environment
bella environments delete     Delete an environment

bella providers list          List secret providers
bella providers get           Get provider details
bella providers create        Create a provider
bella providers delete        Delete a provider

bella secrets list            List secret keys (values masked)
bella secrets get             Download all secrets as .env / JSON
bella secrets set <key>       Create or update a secret
bella secrets delete <key>    Delete a secret
bella secrets push            Push secrets from a .env file
bella secrets generate <lang> Generate a typed secrets accessor class

bella init                    Create a .bella context file (shortcut for context init)
bella context show            Show active context (source: local .bella or session env var)
bella context init            Create .bella in current directory
bella context get --quiet     Output project/env string (for prompt use)
bella context clear           Remove .bella from current directory
bella context use <p>/<e>     Set ephemeral session context (no file written)

bella shell init <framework>  Output shell prompt integration snippet
bella shell open              Spawn a subshell with Bella credentials pre-set
bella env                     Output eval-able export statements (eval $(bella env))

bella config show             Show CLI configuration
bella config set-server <url> Set API server URL

bella generate                Generate a secure random password
bella generate --memorable    Generate a memorable passphrase
bella generate --quiet        Output raw value only (for piping/clipboard)

bella run -- <command>        Run a command with secrets injected as env vars
bella run --watch -- <cmd>    Re-run on secret changes

bella exec -- <command>       Inject API key only; SDK inside the app fetches secrets

bella issue --scope <names>   Issue a short-lived scoped API token for a task/agent

bella upgrade                 Upgrade to the latest version
bella upgrade --check         Check for updates without installing

bella ssh configure           Configure Vault SSH CA for an environment
bella ssh ca-key              Show CA public key (for server trusted-ca setup)
bella ssh roles list          List SSH signing roles
bella ssh roles create        Create a signing role
bella ssh roles delete        Delete a signing role
bella ssh sign                Sign your SSH public key (get a short-lived cert)
bella ssh connect <user@host> Sign + SSH in one step

bella agent                   Run sidecar: poll secrets → write sinks → signal process
bella agent --init            Scaffold a starter bella-agent.yaml
bella mcp                     Start an MCP server (AI agent tool integration)
bella mcp --print-config      Print Claude Desktop / VS Code config snippets
```

---

## `bella run` vs `bella exec`

Both commands spawn a subprocess with Bella credentials available. They differ in **who fetches the secrets** — the CLI or the app itself.

### `bella run` — CLI fetches secrets, injects as env vars

```bash
bella run -p myproject -e production -- node server.js
```

1. CLI authenticates and calls `GET /projects/{slug}/environments/{slug}/secrets`
2. All secrets are fetched and injected as individual environment variables (`DATABASE_URL`, `STRIPE_KEY`, etc.)
3. The child process starts with secrets already in its environment
4. **The child has no knowledge of Bella** — it just sees plain env vars

Options:
- `--project / -p` — project slug or ID (required unless a `.bella` file is present)
- `--environment / -e` — environment slug or ID (required unless a `.bella` file is present)
- `--provider` — target a specific provider if the environment has multiple
- `--watch` — poll for secret changes and restart the process when they change
- `--poll-interval <seconds>` — how often to poll (default: `30`)
- `--signal restart|sighup` — how to reload: `restart` (default) kills and respawns; `sighup` sends SIGHUP

```bash
# Basic use
bella run -p myapp -e staging -- python manage.py runserver

# Watch for changes + restart automatically
bella run -p myapp -e dev --watch --poll-interval 10 -- node server.js

# Live reload via SIGHUP (useful for nginx, gunicorn, etc.)
bella run -p myapp -e prod --watch --signal sighup -- gunicorn app:app
```

**Good for:** Scripts, legacy apps, any process that isn't Bella-aware. The simplest option — no SDK required in the child.

---

### `bella exec` — inject API key only, SDK fetches secrets inside the app

```bash
bella exec -- node server.js
```

1. CLI resolves the stored API key (`bax-…` token)
2. Injects **only two variables** into the child process:
   - `BELLA_BAXTER_API_KEY` — the full API key token
   - `BELLA_BAXTER_URL` — the Bella API base URL
3. The child process starts immediately — no secrets are fetched yet
4. **The Bella SDK inside the app** calls `GET /api/v1/keys/me` to discover its project + environment from the key, then fetches secrets at runtime

```bash
# Typical usage — app uses the Bella SDK
bella exec -- node server.js

# The API key can also be supplied via env var (bella exec respects it too)
BELLA_BAXTER_API_KEY=bax-xxx bella exec -- ./deploy.sh
```

**Good for:** Apps that use the Bella SDK (`bella-js`, `bella-dotnet`, etc.). The app controls when and how secrets are fetched — enabling patterns like deferred loading, per-request secret refresh, and automatic retries.

---

### Which one should I use?

| | `bella run` | `bella exec` |
|---|---|---|
| **Bella SDK required in child** | No | Yes |
| **What gets injected** | All secrets as individual env vars | Only `BELLA_BAXTER_API_KEY` + `BELLA_BAXTER_URL` |
| **Secrets ever in CLI process** | Yes (fetched before spawn) | Never |
| **Watch / auto-reload** | ✅ `--watch` flag | ❌ |
| **Project/env flags needed** | Yes (or `.bella` file) | No — encoded in the API key¹ |
| **Best for** | Any process, no SDK needed | SDK-powered apps, long-running services |

**Rule of thumb:** Use `bella run` for scripts and legacy apps. Use `bella exec` when your app already integrates the Bella SDK and you want the app to own its own secret lifecycle.

> ¹ `bella exec` is designed for API key auth — the SDK inside the app calls `/api/v1/keys/me` to resolve project + environment directly from the key. Human developers using OAuth should use `bella run` with a `.bella` file instead.

---

## Workload Identity (GitHub Actions / Kubernetes)

When running inside **GitHub Actions** (with `id-token: write` permission) or a **Kubernetes Pod**, `bella exec` and `bella run` automatically exchange the platform-issued OIDC token for a short-lived Bella API key — no stored credentials required.

### GitHub Actions

```yaml
jobs:
  deploy:
    permissions:
      id-token: write   # required for OIDC
      contents: read
    steps:
      - uses: actions/checkout@v6
      - name: Deploy with Bella secrets
        run: bella exec -p my-project -e production -- ./deploy.sh
```

No `bella login` step is needed. When `ACTIONS_ID_TOKEN_REQUEST_URL` and `ACTIONS_ID_TOKEN_REQUEST_TOKEN` are set, the CLI requests a GitHub OIDC token and exchanges it for a `bax-…` key scoped to the specified project and environment.

### Kubernetes

Any pod that mounts a ServiceAccount token (`/var/run/secrets/kubernetes.io/serviceaccount/token`) is automatically detected. The token is exchanged for a short-lived Bella API key on each invocation.

```yaml
# No extra config — the SA token is auto-detected
command: ["bella", "exec", "-p", "my-project", "-e", "production", "--", "node", "server.js"]
```

### Priority order for credentials

`bella exec` and `bella run` resolve credentials in this order:

1. **Workload identity** — GitHub Actions OIDC or Kubernetes SA token (automatic, no config)
2. **Stored API key** — from `bella login --api-key`
3. **`BELLA_BAXTER_API_KEY` env var** — explicit override
4. **Stored OAuth2 tokens** — from `bella login` (browser flow)

---

## Issuing Scoped Tokens (`bella issue`)

`bella issue` creates a **short-lived, scope-limited API token** for a specific task or agent. Unlike a stored API key, issued tokens:

- Expire automatically (1–480 minutes, default 15)
- Are restricted to named scopes (e.g. `stripe`, `payment`)
- Are logged for audit purposes

```bash
# Issue a 15-minute token scoped to the stripe and payment secrets
bella issue --scope stripe,payment

# Issue a 30-minute token for a specific reason (good for audit logs)
bella issue --scope stripe --ttl 30 --reason "Stripe refund agent"

# Output full JSON (token + metadata)
bella issue --scope stripe -o json

# Capture the token into a variable
TOKEN=$(bella issue --scope stripe,payment)
```

**Options:**

| Flag | Default | Description |
|------|---------|-------------|
| `-s, --scope <names>` | — (required) | Comma-separated scope names |
| `-t, --ttl <minutes>` | `15` | Token lifetime (1–480 min) |
| `-r, --reason <text>` | `cli-issued-token` | Audit label |
| `-p, --project <slug>` | `.bella` context | Project slug |
| `-e, --env <slug>` | `.bella` context | Environment slug |
| `-o, --output <fmt>` | `token` | `token` (raw) or `json` (full response) |

The raw token is printed to **stdout** only; the expiry hint goes to stderr — safe for `TOKEN=$(bella issue ...)` capture.

---

## Shell Credential Export (`bella env`)

`bella env` outputs **eval-able shell export statements** for all Bella connection variables. Useful when you want to activate a Bella context in your current shell session without spawning a subprocess.

```bash
# Activate the current context in your shell
eval $(bella env)

# Activate a specific project/environment
eval $(bella env camino/dev)

# Clear all Bella env vars
eval $(bella env --unset)

# Fish shell
eval (bella env --shell fish)

# PowerShell
Invoke-Expression (bella env --shell powershell)

# Output as JSON (for scripts)
bella env --json
```

Variables exported:

| Variable | Value |
|----------|-------|
| `BELLA_BAXTER_API_KEY` | Stored API key |
| `BELLA_BAXTER_PROJECT` | Project slug |
| `BELLA_BAXTER_ENV` | Environment slug |
| `BELLA_BAXTER_URL` | API base URL (only when non-default) |
| `BELLA_API_KEY` / `BELLA_PROJECT` / `BELLA_ENV` | Deprecated aliases |

> **Note:** `bella env` requires a stored API key (`bella login --api-key`). For OAuth2 sessions, use `bella shell open` instead.

---

## Interactive Subshell (`bella shell open`)

`bella shell open` spawns an **interactive subshell** with all Bella credentials pre-set as environment variables. Any tool inside the subshell that reads `BELLA_API_KEY`, `BELLA_PROJECT`, or `BELLA_ENV` works without extra configuration.

```bash
# Open a subshell for the current context
bella shell open

# Open a subshell for a specific project/environment
bella shell open camino/dev

# Force a specific shell binary
bella shell open --shell /bin/bash

# Run a non-interactive command (exit immediately after)
bella shell open -- node server.js
bella shell open camino/dev -- python app.py
```

Exit the interactive subshell with `exit` or Ctrl-D to return to the parent shell.

---

## Ephemeral Context (`bella context use`)

`bella context use` sets an **in-memory session context** without writing a `.bella` file. It requires the shell function installed by `bella shell init` (which intercepts the command and exports the variables into the current shell process).

```bash
# Set ephemeral context for this shell session
bella context use camino/dev

# Interactive picker (prompts for project/environment)
bella context use

# Clear the ephemeral context (no argument = clear)
bella context use
```

> Run `bella shell init <framework>` first to install the shell function that makes `bella context use` work. Without the shell function, the context would only affect the `bella` subprocess, not the parent shell.

---

## SSH Certificate Authority

Bella integrates with [Vault SSH Secrets Engine](https://developer.hashicorp.com/vault/docs/secrets/ssh/signed-ssh-certificates) to give your team short-lived, audited SSH certificates — no more distributing SSH keys.

### How it works

```
Admin sets up CA  →  Creates roles  →  Developer runs bella ssh sign
                                             ↓
Server trusts CA via TrustedUserCAKeys   Gets cert (~1h TTL)
                                             ↓
                                       bella ssh connect user@host
```

### Quick start (admin)

```bash
# 1. Configure the SSH CA for the production environment
bella ssh configure -p myproject -e prod

# 2. Create a role for developers to sign as ec2-user or ubuntu
bella ssh roles create \
  --name ops \
  --allowed-users "ec2-user,ubuntu" \
  -p myproject -e prod

# 3. Show the CA public key (add this to your servers' sshd_config)
bella ssh ca-key -p myproject -e prod
```

On each server, add to `/etc/ssh/sshd_config`:
```
TrustedUserCAKeys /etc/ssh/trusted-user-ca-keys.pem
```
Then paste the CA public key into that file and restart sshd.

### Developer workflow

```bash
# Sign your default public key (~/.ssh/id_ed25519.pub)
bella ssh sign --role ops -e prod

# SSH directly (sign + connect in one step)
bella ssh connect ec2-user@10.0.0.5 --role ops -e prod

# Use a specific TTL or key
bella ssh sign --role ops --ttl 4h --key ~/.ssh/id_rsa.pub
```

The signed certificate is written alongside your public key (`~/.ssh/id_ed25519-cert.pub`) and reused for 30 minutes before requesting a new one.

### Commands

| Command | Description |
|---------|-------------|
| `bella ssh configure` | Enable and configure the SSH CA in Vault for an environment |
| `bella ssh ca-key` | Print the CA public key (for server `TrustedUserCAKeys` setup) |
| `bella ssh roles list` | List available signing roles |
| `bella ssh roles create` | Create a new signing role |
| `bella ssh roles delete` | Delete a signing role |
| `bella ssh sign` | Sign your SSH public key and save the certificate locally |
| `bella ssh connect <user@host>` | Sign (or reuse cert) and open an SSH session |

All `bella ssh` commands accept `-p/--project` and `-e/--environment` to target a specific project and environment. Defaults are read from your `.bella` file.

---

## Agent Sidecar

`bella agent` is a long-running daemon that **watches your secrets for changes** and keeps local files (`.env`, JSON, YAML) in sync. When secrets change it can also signal your running process to reload config without restarting.

### How it works

```
bella agent
   └── reads bella-agent.yaml
   └── resolves project/environment/provider slugs
   └── fetches initial secrets → writes all sinks
   └── every poll-interval seconds per watch:
         GET /secrets/hash  (lightweight — only the hash changes)
         if hash changed:
           GET /secrets      (fetch all values)
           write all sinks
           send SIGHUP/SIGTERM to PID
```

### Quick start

```bash
# Scaffold a starter config
bella agent --init

# Edit bella-agent.yaml, then start the agent
bella agent

# Use a non-default config file
bella agent --config /etc/myapp/secrets.yaml
```

### Config file (`bella-agent.yaml`)

```yaml
# bella-agent.yaml
watches:
  - project: my-project          # project name or slug
    environment: production      # environment name or slug
    # provider: aws-prod         # optional — uses first provider if omitted
    poll-interval: 30            # seconds between hash checks (minimum: 5)

sinks:
  - type: dotenv                 # dotenv | json | yaml
    path: ./.env
  - type: json
    path: ./secrets.json

process:
  signal: sighup                 # sighup | sigterm | none
  pid-file: ./app.pid            # PID file to read — omit to skip signalling
```

**Sink types:**

| Type | Output format | Example value |
|------|--------------|---------------|
| `dotenv` | `KEY="value"` per line | `DATABASE_URL="postgres://..."` |
| `json` | Pretty-printed JSON object | `{ "DATABASE_URL": "..." }` |
| `yaml` | `key: "value"` per line | `DATABASE_URL: "postgres://..."` |

**Environment variable expansion:**  
Values in `bella-agent.yaml` can reference environment variables using `${VAR_NAME}` syntax:
```yaml
watches:
  - project: ${BELLA_PROJECT}
    environment: ${BELLA_ENV}
```

**Multiple watches:**  
When you have multiple watch entries, secrets are merged across all of them (later entries win on key collision). All sinks are rewritten whenever *any* watch detects a change.

**Process signalling:**

- `sighup` — sends SIGHUP (Unix/macOS only; most servers reload config on SIGHUP)
- `sigterm` — sends SIGTERM (graceful shutdown signal; works on Windows too)
- `none` — no signalling (default if `pid-file` is omitted)

On Windows, `sighup` is silently treated as `sigterm` (Windows has no SIGHUP).

### Works with `bella run --watch`

For development, `bella run --watch` is simpler — it polls and re-runs the whole process. `bella agent` is designed for **production sidecars** where you want zero-downtime secret rotation.

---

## MCP Server

`bella mcp` starts an [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) server over **stdio** that proxies tool calls to the Bella Baxter API. AI assistants (Claude, GitHub Copilot, Cursor, etc.) launch this process and communicate with it via JSON-RPC.

All secret values are protected — the MCP server enforces the same RBAC as the REST API.

### Setup

```bash
# Print the exact config snippet to paste into your AI host
bella mcp --print-config

# Start manually (AI hosts do this automatically)
bella mcp

# Use a self-hosted Bella instance
bella mcp --api-url https://my-bella.example.com
```

Add to your AI assistant's MCP configuration:

**Claude Desktop** (`~/Library/Application Support/Claude/claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "bella-baxter": {
      "command": "bella",
      "args": ["mcp"]
    }
  }
}
```

**VS Code / GitHub Copilot** (`.vscode/mcp.json` or User settings):
```json
{
  "servers": {
    "bella-baxter": {
      "type": "stdio",
      "command": "bella",
      "args": ["mcp"]
    }
  }
}
```

### Available MCP tools

| Tool | Description |
|------|-------------|
| `list_projects` | List projects you have access to |
| `list_environments` | List environments for a project |
| `list_providers` | List secret providers for an environment |
| `list_secret_keys` | List secret key names (values never exposed) |
| `get_secret` | Retrieve a specific secret value |
| `set_secret` | Create or update a secret |
| `delete_secret` | Permanently delete a secret |
| `get_totp_code` | Generate a current TOTP/2FA code |
| `list_totp_keys` | List TOTP key names |
| `sign_ssh_key` | Sign an SSH public key via Vault CA |
| `list_ssh_roles` | List available SSH CA roles |
| `bella_issue_token` | Issue a short-lived, scope-limited token for the current task |

The `bella_issue_token` tool is especially useful for AI agents that need to hand off a credential to a subprocess or tool call — the token expires automatically and is scoped to only the secrets the agent actually needs.

---

## Typed Secret Code Generation

`bella secrets generate <lang>` fetches the **secrets manifest** (key names + type hints — no values) from the Bella API and generates a strongly-typed accessor class for your language. Each generated property reads from an environment variable **at runtime** — secret values are never embedded in the generated file.

### Quick examples

**dotnet:**
```bash
bella secrets generate dotnet --namespace MyApp --output AppSecrets.g.cs
```
```csharp
public static partial class AppSecrets
{
    public static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        throw new InvalidOperationException("SECRET 'DATABASE_URL' is not set.");

    public static int Port =>
        int.Parse(Environment.GetEnvironmentVariable("PORT") ??
        throw new InvalidOperationException("SECRET 'PORT' is not set."));
}
```

**python:**
```bash
bella secrets generate python --output app_secrets.py
```
```python
class AppSecrets:
    @property
    def database_url(self) -> str:
        v = os.environ.get("DATABASE_URL")
        if v is None:
            raise RuntimeError("SECRET 'DATABASE_URL' is not set.")
        return v
```

### Supported languages

| Language | File generated | Class/module type |
|----------|---------------|-------------------|
| `dotnet` | `AppSecrets.g.cs` | `static partial class` |
| `python` | `app_secrets.py` | `class` with `@property` |
| `go` | `app_secrets.go` | `struct` with methods |
| `typescript` | `AppSecrets.ts` | `class` with getters |
| `dart` | `app_secrets.dart` | `class` with getters |
| `php` | `AppSecrets.php` | `class` with methods |
| `ruby` | `app_secrets.rb` | `module` with `self.` methods |
| `swift` | `AppSecrets.swift` | `public struct` with static vars |

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `-p, --project <slug>` | `.bella` context | Project slug |
| `-e, --environment <slug>` | `.bella` context | Environment slug |
| `--provider <slug>` | `default` | Provider slug |
| `-o, --output <path>` | auto-named | Output file path |
| `--class-name <name>` | `AppSecrets` | Class/module/struct name |
| `--namespace <ns>` | `AppSecrets` | Namespace/package (dotnet only) |
| `--dry-run` | — | Print generated code without writing |

### Works with `bella watch`

Because every generated property reads the environment variable on each access (no caching), values injected or updated by `bella watch` are picked up immediately by any code that reads config per-request.

---

## Development

This section covers the full development loop — from making a code change to running `bella` against a local API instance.

---

### Step 1 — Install the CLI globally (once, or after every code change)

The equivalent of `npm link` in .NET is installing the project as a **global tool**:

```bash
cd apps/cli-dotnet
./dev-install.sh
```

What the script does:

1. `dotnet pack` — builds the project and creates a local `.nupkg` in `BellaCli/nupkg/`
2. `dotnet tool install -g` — installs it to `~/.dotnet/tools/bella`

**After any code change**, re-run the same script. It uninstalls the old binary and installs the fresh one.

`bella` is then available from **any directory** in any terminal — no project path needed.

---

### Step 2 — Point at the local API

The CLI reads two environment variables that override stored config and credentials:

| Variable | Purpose |
|----------|---------|
| `BAXTER_URL` | API base URL — overrides `bella config set-server` and the default `https://api.bella-baxter.io` |
| `BAXTER_TOKEN` | Bearer token — skips `bella login` and the credential store entirely |

**Export them for your shell session** (put this in your terminal while developing):

```bash
export BAXTER_URL=http://localhost:5237
export BAXTER_TOKEN=<your-dev-token>
```

Or inline for a single command:

```bash
BAXTER_URL=http://localhost:5237 BAXTER_TOKEN=my-token bella projects list
```

To get a dev token, run the Aspire host and grab a token from the Scalar UI or from a `bella login` against your local Keycloak.

---

### Step 3 — Start the local API (Aspire)

```bash
cd apps/api/baxter-dotnet
dotnet run --project BellaBaxter.AppHost
```

Aspire starts:
- **BellaBaxter API** on `https://localhost:5237` (or check Aspire dashboard for the port)
- **PostgreSQL** (Docker)
- **Keycloak** (Docker)
- **OpenBao/Vault** (Docker)

The Aspire dashboard URL is printed on startup. Use it to find the exact ports.

---

### Step 4 — Full dev loop in practice

```bash
# Terminal 1 — run the API
cd apps/api/baxter-dotnet
dotnet run --project BellaBaxter.AppHost

# Terminal 2 — work on CLI code
cd apps/cli-dotnet
export BAXTER_URL=http://localhost:5237
export BAXTER_TOKEN=<token>

# Make a code change, then:
./dev-install.sh

# Now test from any directory
cd /tmp
bella projects list
bella secrets list -p myproject -e dev
bella --version
```

---

### PATH note

`~/.dotnet/tools` must appear in your `$PATH`. The .NET SDK installer adds it automatically.  
If you previously had the JS bella installed via `npm link`, it may shadow the .NET binary.  
Ensure `.dotnet/tools` comes **before** nvm/npm in your shell config:

```zsh
# ~/.zshrc — .dotnet/tools before nvm
export PATH="$HOME/.dotnet/tools:$PATH"

export NVM_DIR="$HOME/.nvm"
[ -s "$NVM_DIR/nvm.sh" ] && source "$NVM_DIR/nvm.sh"
```

The `dev-install.sh` script warns you if another `bella` binary is shadowing the .NET one.

---

### dotnet run (no install required)

If you only want to test quickly without installing, you can run directly from the project:

```bash
cd apps/cli-dotnet/BellaCli

# Pass CLI args after --
BAXTER_URL=http://localhost:5237 BAXTER_TOKEN=mytoken dotnet run -- projects list
BAXTER_URL=http://localhost:5237 BAXTER_TOKEN=mytoken dotnet run -- secrets list
```

This rebuilds on every invocation (adds ~1s) and only works from the project directory.  
Use `dev-install.sh` for a proper dev loop.

---

### Debugging in VS Code / Rider

**VS Code:**

1. Open `apps/cli-dotnet/` as the workspace root
2. Create `.vscode/launch.json`:
   ```json
   {
     "version": "0.2.0",
     "configurations": [
       {
         "name": "bella projects list",
         "type": "dotnet",
         "request": "launch",
         "projectPath": "${workspaceFolder}/BellaCli/BellaCli.csproj",
         "args": ["projects", "list"],
         "env": {
           "BAXTER_URL": "http://localhost:5237",
           "BAXTER_TOKEN": "your-dev-token"
         }
       }
     ]
   }
   ```
3. Set breakpoints, press **F5**

**Rider:**

1. Open `apps/cli-dotnet/BellaCli.slnx`
2. Edit the run configuration for `BellaCli`: set **Program arguments** (e.g. `projects list`) and **Environment variables** (`BAXTER_URL`, `BAXTER_TOKEN`)
3. Set breakpoints, hit the debug button

---

## Configuration

| File | Purpose |
|---|---|
| `~/.config/bella-cli/config.json` | Server URL |
| `~/.config/bella-cli/tokens` | Encrypted OAuth2 tokens (DataProtection) |
| `~/.config/bella-cli/apikey` | Encrypted API key (DataProtection) |
| `.bella` | Directory-local context (project + environment) |

The `.bella` file format:
```toml
project = "my-project-slug"
environment = "dev"
```

---

## License

MIT © Cosmic Chimps
