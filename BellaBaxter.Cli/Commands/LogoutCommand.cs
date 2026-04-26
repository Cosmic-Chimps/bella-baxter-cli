using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands;

public class LogoutSettings : CommandSettings
{
    [CommandOption("--force")]
    public bool Force { get; init; }
}

public class LogoutCommand(AuthService auth, CredentialStore credentials, DekLeaseCache dekCache, IOutputWriter output)
    : Command<LogoutSettings>
{
    protected override int Execute(CommandContext context, LogoutSettings settings, CancellationToken ct)
    {
        if (!credentials.IsAuthenticated())
        {
            output.WriteWarning("Not currently logged in.");
            return 0;
        }

        if (!settings.Force && !Console.IsInputRedirected)
        {
            var confirm = AnsiConsole.Confirm("Are you sure you want to log out?");
            if (!confirm)
            {
                output.WriteInfo("Logout cancelled.");
                return 0;
            }
        }

        auth.Logout();
        dekCache.Clear(); // evict all cached DEK leases on logout
        output.WriteSuccess("Logged out successfully.");
        return 0;
    }
}
