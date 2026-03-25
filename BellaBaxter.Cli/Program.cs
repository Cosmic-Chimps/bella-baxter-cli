using BellaCli.Commands;
using BellaCli.Commands.Agent;
using BellaCli.Commands.Auth;
using BellaCli.Commands.Config;
using BellaCli.Commands.Environments;
using BellaCli.Commands.Generate;
using BellaCli.Commands.Mcp;
using BellaCli.Commands.Me;
using BellaCli.Commands.Orgs;
using BellaCli.Commands.Projects;
using BellaCli.Commands.Providers;
using BellaCli.Commands.Exec;
using BellaCli.Commands.Issue;
using BellaCli.Commands.Run;
using BellaCli.Commands.Secrets;
using BellaCli.Commands.Shell;
using BellaCli.Commands.Ssh;
using BellaCli.Commands.Totp;
using BellaCli.Commands.Upgrade;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

// ── Dependency injection ─────────────────────────────────────────────────────
var services = new ServiceCollection();

services.AddSingleton<GlobalSettings>();
services.AddSingleton<ConfigService>();
services.AddSingleton<CredentialStore>();
services.AddSingleton<BellaClientProvider>();
services.AddSingleton<ContextService>();
services.AddHttpClient<AuthService>();
services.AddHttpClient<WorkloadIdentityService>();

// OutputMode is resolved at runtime based on auth type / --json flag / TTY
services.AddSingleton<IOutputWriter>(sp =>
{
    var gs = sp.GetRequiredService<GlobalSettings>();
    var creds = sp.GetRequiredService<CredentialStore>();

    // Auto-detect: API key → JSON, stdout redirected → JSON
    if (creds.IsApiKeyMode() || Console.IsOutputRedirected)
        gs.OutputMode = OutputMode.Json;

    return gs.IsJsonMode ? new JsonOutputWriter() : new HumanOutputWriter();
});

// Auth commands
services.AddTransient<LoginCommand>();
services.AddTransient<LogoutCommand>();
services.AddTransient<AuthStatusCommand>();
services.AddTransient<AuthRefreshCommand>();
services.AddTransient<KeyContextService>();

// Project commands
services.AddTransient<ListProjectsCommand>();
services.AddTransient<GetProjectCommand>();
services.AddTransient<CreateProjectCommand>();
services.AddTransient<UpdateProjectCommand>();
services.AddTransient<DeleteProjectCommand>();

// Environment commands
services.AddTransient<ListEnvironmentsCommand>();
services.AddTransient<GetEnvironmentCommand>();
services.AddTransient<CreateEnvironmentCommand>();
services.AddTransient<UpdateEnvironmentCommand>();
services.AddTransient<DeleteEnvironmentCommand>();

// Provider commands
services.AddTransient<ListProvidersCommand>();
services.AddTransient<GetProviderCommand>();
services.AddTransient<CreateProviderCommand>();
services.AddTransient<DeleteProviderCommand>();

// Secret commands
services.AddTransient<ListSecretsCommand>();
services.AddTransient<GetSecretsCommand>();
services.AddTransient<SetSecretCommand>();
services.AddTransient<DeleteSecretCommand>();
services.AddTransient<PushSecretsCommand>();
services.AddTransient<GenerateSecretsCodeCommand>();
services.AddTransient<PullCommand>();

// Config commands
services.AddTransient<ConfigShowCommand>();
services.AddTransient<ConfigSetServerCommand>();

// Generate + Run + Exec + Issue + Upgrade
services.AddTransient<GenerateCommand>();
services.AddTransient<RunCommand>();
services.AddTransient<ExecCommand>();
services.AddTransient<IssueCommand>();
services.AddTransient<UpgradeCommand>();

// Context + Shell commands
services.AddTransient<ContextCommand>();
services.AddTransient<ContextShowCommand>();
services.AddTransient<ContextInitCommand>();
services.AddTransient<ContextClearCommand>();
services.AddTransient<ContextUseCommand>();
services.AddTransient<ShellInitCommand>();

// Me command
services.AddTransient<WhoAmICommand>();

// Org commands
services.AddTransient<OrgCurrentCommand>();
services.AddTransient<OrgListCommand>();
services.AddTransient<OrgSwitchCommand>();

// MCP command
services.AddTransient<McpCommand>();
services.AddTransient<AgentCommand>();

// SSH commands
services.AddTransient<ConfigureSshCommand>();
services.AddTransient<GetSshCaKeyCommand>();
services.AddTransient<ListSshRolesCommand>();
services.AddTransient<CreateSshRoleCommand>();
services.AddTransient<DeleteSshRoleCommand>();
services.AddTransient<SignSshKeyCommand>();
services.AddTransient<ConnectSshCommand>();

// ── Spectre.Console.Cli app ──────────────────────────────────────────────────
var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("bella");
    config.SetApplicationVersion("0.1.0");
    config.ValidateExamples();

    config
        .AddCommand<LoginCommand>("login")
        .WithDescription("Log in to Bella Baxter (browser OAuth2 or API key).")
        .WithExample("login")
        .WithExample("login", "--api-key", "bax-mykey-mysecret")
        .WithExample("login", "--force");

    config
        .AddCommand<LogoutCommand>("logout")
        .WithDescription("Log out and clear stored credentials.");

    config
        .AddCommand<WhoAmICommand>("whoami")
        .WithDescription("Show the currently logged-in user.");

    config.AddBranch(
        "org",
        org =>
        {
            org.SetDescription("Organization (multi-tenancy) commands.");
            org.AddCommand<OrgCurrentCommand>("current")
                .WithDescription("Show the currently active org.")
                .WithExample("org", "current");
            org.AddCommand<OrgListCommand>("list")
                .WithDescription("List all orgs you belong to.")
                .WithExample("org", "list");
            org.AddCommand<OrgSwitchCommand>("switch")
                .WithDescription("Switch to a different org.")
                .WithExample("org", "switch", "acme-corp");
        }
    );

    config.AddBranch(
        "auth",
        auth =>
        {
            auth.SetDescription("Authentication management commands.");
            auth.AddCommand<AuthStatusCommand>("status")
                .WithDescription("Show current authentication status.");
            auth.AddCommand<AuthRefreshCommand>("refresh")
                .WithDescription("Manually refresh the OAuth2 access token.");
        }
    );

    config.AddBranch(
        "projects",
        projects =>
        {
            projects.SetDescription("Manage Bella Baxter projects.");
            projects.AddCommand<ListProjectsCommand>("list").WithDescription("List projects.");
            projects.AddCommand<GetProjectCommand>("get").WithDescription("Get project details.");
            projects
                .AddCommand<CreateProjectCommand>("create")
                .WithDescription("Create a new project.");
            projects
                .AddCommand<UpdateProjectCommand>("update")
                .WithDescription("Update a project.");
            projects
                .AddCommand<DeleteProjectCommand>("delete")
                .WithDescription("Delete a project.");
        }
    );

    config.AddBranch(
        "environments",
        envs =>
        {
            envs.SetDescription("Manage environments within projects.");
            envs.AddCommand<ListEnvironmentsCommand>("list").WithDescription("List environments.");
            envs.AddCommand<GetEnvironmentCommand>("get")
                .WithDescription("Get environment details.");
            envs.AddCommand<CreateEnvironmentCommand>("create")
                .WithDescription("Create a new environment.");
            envs.AddCommand<UpdateEnvironmentCommand>("update")
                .WithDescription("Update an environment.");
            envs.AddCommand<DeleteEnvironmentCommand>("delete")
                .WithDescription("Delete an environment.");
        }
    );

    config.AddBranch(
        "providers",
        provs =>
        {
            provs.SetDescription("Manage secret providers.");
            provs.AddCommand<ListProvidersCommand>("list").WithDescription("List providers.");
            provs.AddCommand<GetProviderCommand>("get").WithDescription("Get provider details.");
            provs
                .AddCommand<CreateProviderCommand>("create")
                .WithDescription("Create a new provider.");
            provs.AddCommand<DeleteProviderCommand>("delete").WithDescription("Delete a provider.");
        }
    );

    config.AddBranch(
        "secrets",
        secrets =>
        {
            secrets.SetDescription("Manage secrets stored in providers.");
            secrets
                .AddCommand<ListSecretsCommand>("list")
                .WithDescription("List secret keys (values masked).");
            secrets.AddCommand<GetSecretsCommand>("get").WithDescription("Download all secrets.");
            secrets
                .AddCommand<SetSecretCommand>("set")
                .WithDescription("Create or update a secret.");
            secrets.AddCommand<DeleteSecretCommand>("delete").WithDescription("Delete a secret.");
            secrets
                .AddCommand<PushSecretsCommand>("push")
                .WithDescription("Push secrets from a .env file.");
            secrets
                .AddCommand<GenerateSecretsCodeCommand>("generate")
                .WithDescription(
                    "Generate typed secrets code for dotnet, python, go, typescript, dart, php, ruby, or swift."
                )
                .WithExample("secrets", "generate", "dotnet")
                .WithExample("secrets", "generate", "python", "-p", "my-project", "-e", "dev")
                .WithExample(
                    "secrets",
                    "generate",
                    "typescript",
                    "--class-name",
                    "AppSecrets",
                    "--dry-run"
                );
        }
    );

    config.AddBranch(
        "totp",
        totp =>
        {
            totp.SetDescription("Manage TOTP/2FA keys stored in environments.");
            totp.AddCommand<ListTotpCommand>("list")
                .WithDescription("List TOTP keys in an environment.")
                .WithExample("totp", "list")
                .WithExample("totp", "list", "-p", "my-project", "-e", "prod");
            totp.AddCommand<GetTotpCodeCommand>("code")
                .WithDescription("Get the current TOTP code for a key.")
                .WithExample("totp", "code", "github")
                .WithExample("totp", "code", "github", "--quiet");
            totp.AddCommand<ImportTotpCommand>("import")
                .WithDescription("Import a TOTP key from an otpauth:// URL.")
                .WithExample("totp", "import", "github", "otpauth://totp/GitHub:user@example.com?secret=BASE32SECRET&issuer=GitHub");
            totp.AddCommand<GenerateTotpCommand>("generate")
                .WithDescription("Generate a new TOTP key.")
                .WithExample("totp", "generate", "myapp", "--issuer", "MyApp", "--account", "user@example.com");
            totp.AddCommand<DeleteTotpCommand>("delete")
                .WithDescription("Delete a TOTP key.")
                .WithExample("totp", "delete", "github")
                .WithExample("totp", "delete", "github", "--force");
        }
    );

    config.AddBranch(
        "config",
        cfg =>
        {
            cfg.SetDescription("CLI configuration (server URL).");
            cfg.AddCommand<ConfigShowCommand>("show")
                .WithDescription("Show all CLI configuration.")
                .WithAlias("view");
            cfg.AddCommand<ConfigSetServerCommand>("set-server")
                .WithDescription("Set the Bella Baxter API server URL.");
        }
    );

    config
        .AddCommand<GenerateCommand>("generate")
        .WithDescription("Generate a cryptographically secure random password or passphrase.")
        .WithExample("generate")
        .WithExample("generate", "--memorable", "--words", "6")
        .WithExample("generate", "--length", "32", "--no-symbols")
        .WithExample("generate", "--quiet");

    config
        .AddCommand<RunCommand>("run")
        .WithDescription("Inject Bella secrets as env vars and run a command.")
        .WithExample("run", "--", "node", "index.js")
        .WithExample("run", "-p", "my-project", "-e", "production", "--", "npm", "start")
        .WithExample("run", "--watch", "--", "node", "server.js");

    config
        .AddCommand<PullCommand>("pull")
        .WithDescription("Download all secrets and write to a file (default: .env). Shorthand for 'bella secrets get -o .env'.")
        .WithExample("pull")
        .WithExample("pull", "-o", ".env")
        .WithExample("pull", "-o", "secrets.json")
        .WithExample("pull", "-p", "my-project", "-e", "production", "-o", ".env");

    config
        .AddCommand<ExecCommand>("exec")
        .WithDescription("Inject Bella connection credentials (API key + URL) and run a command. The subprocess uses the Bella SDK to discover its project/environment context from the API key.")
        .WithExample("exec", "--", "node", "server.js")
        .WithExample("exec", "--", "npm", "start")
        .WithExample("exec", "--", "dotnet", "run")
        .WithExample("exec", "-p", "my-project", "-e", "production", "--", "node", "server.js");

    config
        .AddCommand<IssueCommand>("issue")
        .WithDescription("Issue a short-lived scoped API token for a specific environment.")
        .WithExample("issue", "--scope", "stripe,payment")
        .WithExample("issue", "--scope", "stripe", "--ttl", "30", "--reason", "Stripe refund agent")
        .WithExample("issue", "-s", "payment", "-p", "my-project", "-e", "production", "--output", "json");

    config
        .AddCommand<EnvCommand>("env")
        .WithDescription("Output eval-able export statements for BELLA_BAXTER_PROJECT, BELLA_BAXTER_ENV (and legacy BELLA_PROJECT/BELLA_ENV aliases). Usage: eval $(bella env)")
        .WithExample("env")
        .WithExample("env", "camino/dev")
        .WithExample("env", "--shell", "fish")
        .WithExample("env", "--unset");

    config
        .AddCommand<UpgradeCommand>("upgrade")
        .WithDescription("Upgrade bella to the latest version.")
        .WithExample("upgrade")
        .WithExample("upgrade", "--check")
        .WithExample("upgrade", "--version", "1.2.0");

    // Shortcut: `bella init` → same as `bella context init`
    config
        .AddCommand<ContextInitCommand>("init")
        .WithDescription("Create a .bella context file in the current directory (shortcut for 'bella context init').")
        .WithExample("init")
        .WithExample("init", "my-project", "dev");

    config.AddBranch(
        "context",
        ctx =>
        {
            ctx.SetDescription(
                "Directory-aware Bella project/environment context (for shell prompts)."
            );
            ctx.AddCommand<ContextCommand>("get")
                .WithDescription("Output current project/environment (for use in shell prompts).")
                .WithAlias("show-raw")
                .WithExample("context", "get")
                .WithExample("context", "get", "--quiet")
                .WithExample("context", "get", "--json");
            ctx.AddCommand<ContextShowCommand>("show")
                .WithDescription("Show active context with source information.");
            ctx.AddCommand<ContextInitCommand>("init")
                .WithDescription("Create a .bella file in the current directory (like git init).")
                .WithExample("context", "init")
                .WithExample("context", "init", "myproject", "dev");
            ctx.AddCommand<ContextClearCommand>("clear")
                .WithDescription("Remove the .bella file from the current directory.");
            ctx.AddCommand<ContextUseCommand>("use")
                .WithDescription("Set an ephemeral session context (no file written). Requires shell function from 'bella shell init'.")
                .WithExample("context", "use", "camino/dev")
                .WithExample("context", "use");
        }
    );

    config.AddBranch(
        "shell",
        sh =>
        {
            sh.SetDescription("Shell prompt integration helpers.");
            sh.AddCommand<ShellInitCommand>("init")
                .WithDescription("Output shell integration snippet for your prompt framework.")
                .WithExample("shell", "init", "oh-my-zsh")
                .WithExample("shell", "init", "starship")
                .WithExample("shell", "init", "fish")
                .WithExample("shell", "init", "bash");
            sh.AddCommand<ShellOpenCommand>("open")
                .WithDescription("Spawn an interactive subshell with BELLA_API_KEY, BELLA_PROJECT, BELLA_ENV pre-set.")
                .WithExample("shell", "open")
                .WithExample("shell", "open", "camino/dev")
                .WithExample("shell", "open", "--shell", "/bin/zsh");
        }
    );

    config
        .AddCommand<McpCommand>("mcp")
        .WithDescription(
            "Start the Bella Baxter MCP server (stdio) for Claude Desktop / VS Code Copilot."
        )
        .WithExample("mcp")
        .WithExample("mcp", "--print-config")
        .WithExample("mcp", "--api-url", "https://my-bella.example.com");

    config.AddBranch(
        "ssh",
        ssh =>
        {
            ssh.SetDescription("Manage SSH certificates via Vault SSH CA.");
            ssh.AddCommand<ConfigureSshCommand>("configure")
                .WithDescription("Configure SSH CA for an environment (admin only).")
                .WithExample("ssh", "configure")
                .WithExample("ssh", "configure", "-e", "prod");
            ssh.AddCommand<GetSshCaKeyCommand>("ca-key")
                .WithDescription("Show the SSH CA public key (for sshd_config setup).")
                .WithExample("ssh", "ca-key")
                .WithExample("ssh", "ca-key", "--output", "trusted-ca.pub");
            ssh.AddBranch(
                "roles",
                roles =>
                {
                    roles.SetDescription("Manage SSH signing roles.");
                    roles
                        .AddCommand<ListSshRolesCommand>("list")
                        .WithDescription("List SSH roles for an environment.")
                        .WithExample("ssh", "roles", "list");
                    roles
                        .AddCommand<CreateSshRoleCommand>("create")
                        .WithDescription("Create an SSH signing role.")
                        .WithExample(
                            "ssh",
                            "roles",
                            "create",
                            "--name",
                            "ops",
                            "--allowed-users",
                            "ec2-user,ubuntu"
                        );
                    roles
                        .AddCommand<DeleteSshRoleCommand>("delete")
                        .WithDescription("Delete an SSH signing role.")
                        .WithExample("ssh", "roles", "delete", "--name", "ops");
                }
            );
            ssh.AddCommand<SignSshKeyCommand>("sign")
                .WithDescription("Sign your SSH public key and write the certificate.")
                .WithExample("ssh", "sign")
                .WithExample("ssh", "sign", "--role", "ops", "--ttl", "1h")
                .WithExample("ssh", "sign", "--key", "~/.ssh/id_ed25519.pub");
            ssh.AddCommand<ConnectSshCommand>("connect")
                .WithDescription("Sign key (if needed) and open an SSH connection.")
                .WithExample("ssh", "connect", "ec2-user@10.0.0.1")
                .WithExample("ssh", "connect", "ubuntu@myserver.example.com", "--role", "ops");
        }
    );

    config
        .AddCommand<AgentCommand>("agent")
        .WithDescription("Long-running sidecar: polls secrets, writes sinks, signals your process.")
        .WithExample("agent")
        .WithExample("agent", "--config", "bella-agent.yaml")
        .WithExample("agent", "--init");
});

return await app.RunAsync(args);
