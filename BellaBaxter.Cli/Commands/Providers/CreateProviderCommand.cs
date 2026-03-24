using BellaBaxter.Client;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using SdkCreateProviderCommand = BellaBaxter.Client.Models.CreateProviderCommand;
using SdkCreateProviderConfiguration = BellaBaxter.Client.Models.CreateProviderCommand_configuration;

namespace BellaCli.Commands.Providers;

public class CreateProviderSettings : CommandSettings
{
    [CommandOption("--type <TYPE>")]
    public string? Type { get; init; }

    [CommandOption("-n|--name <NAME>")]
    public string? Name { get; init; }

    [CommandOption("-d|--description <DESC>")]
    public string? Description { get; init; }

    [CommandOption("--json")]
    public bool Json { get; init; }
}

public class CreateProviderCommand(BellaClientProvider provider, IOutputWriter output)
    : AsyncCommand<CreateProviderSettings>
{
    private static readonly string[] ProviderTypes =
    [
        "AwsSecretsManager",
        "AwsParameterStore",
        "AzureKeyVault",
        "GoogleSecretManager",
        "Vault"
    ];

    public override async Task<int> ExecuteAsync(CommandContext context, CreateProviderSettings settings, CancellationToken ct)
    {
        provider.ApplyOutputModeOverrides(settings.Json);

        BellaClient client;
        try { client = provider.CreateClient(); }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        var isNonInteractive = Console.IsOutputRedirected || output is JsonOutputWriter;

        // Resolve type
        var type = settings.Type;
        if (string.IsNullOrWhiteSpace(type))
        {
            if (isNonInteractive) { output.WriteError("--type is required in non-interactive mode."); return 1; }
            type = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select provider type:")
                    .AddChoices(ProviderTypes));
        }

        var name = settings.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            if (isNonInteractive) { output.WriteError("--name is required in non-interactive mode."); return 1; }
            name = AnsiConsole.Ask<string>("Provider name:");
        }

        var description = settings.Description;
        if (!isNonInteractive && string.IsNullOrWhiteSpace(description))
            description = AnsiConsole.Ask("Description:", defaultValue: "");

        // Type-specific configuration prompts
        var configData = new Dictionary<string, object>();
        if (!isNonInteractive)
        {
            switch (type)
            {
                case "AwsSecretsManager":
                case "AwsParameterStore":
                    configData["region"] = AnsiConsole.Ask<string>("AWS Region (e.g. us-east-1):");
                    var roleArn = AnsiConsole.Ask("Role ARN (optional):", defaultValue: "");
                    if (!string.IsNullOrWhiteSpace(roleArn)) configData["role_arn"] = roleArn;
                    break;

                case "AzureKeyVault":
                    configData["vault_uri"] = AnsiConsole.Ask<string>("Vault URI:");
                    var azureAuth = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Auth method:").AddChoices("managed_identity", "service_principal"));
                    configData["auth_method"] = azureAuth;
                    if (azureAuth == "service_principal")
                    {
                        configData["tenant_id"] = AnsiConsole.Ask<string>("Tenant ID:");
                        configData["client_id"] = AnsiConsole.Ask<string>("Client ID:");
                        configData["client_secret"] = AnsiConsole.Prompt(new TextPrompt<string>("Client Secret:").Secret());
                    }
                    break;

                case "GoogleSecretManager":
                    configData["project_id"] = AnsiConsole.Ask<string>("GCP Project ID:");
                    var gcpAuth = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Auth method:").AddChoices("workload_identity", "service_account"));
                    configData["auth_method"] = gcpAuth;
                    if (gcpAuth == "service_account")
                        configData["service_account_key_json"] = AnsiConsole.Prompt(new TextPrompt<string>("Service Account Key JSON:").Secret());
                    break;

                case "Vault":
                    configData["server_url"] = AnsiConsole.Ask<string>("Vault Server URL:");
                    var vaultAuth = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Auth method:").AddChoices("approle", "jwt"));
                    configData["auth_method"] = vaultAuth;
                    if (vaultAuth == "approle")
                    {
                        configData["role_id"] = AnsiConsole.Ask<string>("Role ID:");
                        configData["secret_id"] = AnsiConsole.Prompt(new TextPrompt<string>("Secret ID:").Secret());
                    }
                    var kvPath = AnsiConsole.Ask("KV Engine Path (optional):", defaultValue: "");
                    if (!string.IsNullOrWhiteSpace(kvPath)) configData["kv_engine_path"] = kvPath;
                    break;
            }
        }

        try
        {
            await AnsiConsole.Status().StartAsync("Creating provider...", async _ =>
            {
                await client.Api.V1.Providers.PostAsync(new SdkCreateProviderCommand
                {
                    Name = name,
                    Type = type,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    Configuration = configData.Count > 0 ? new SdkCreateProviderConfiguration { AdditionalData = configData } : null
                }, cancellationToken: ct);
            });

            output.WriteSuccess($"Provider '{name}' ({type}) created successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteError($"Failed to create provider: {ex.Message}");
            return 1;
        }
    }
}
