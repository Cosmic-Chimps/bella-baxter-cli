using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Shell;

// ─── Settings ────────────────────────────────────────────────────────────────

public class ShellInitSettings : CommandSettings
{
    [CommandArgument(0, "[shell]")]
    [System.ComponentModel.Description(
        "Shell or prompt framework: bash, zsh, oh-my-zsh, powerlevel10k, oh-my-posh, starship, fish, powershell"
    )]
    public string? Shell { get; init; }
}

// ─── bella shell init ─────────────────────────────────────────────────────────
// Outputs ready-to-paste shell integration snippets for each framework.

public class ShellInitCommand : Command<ShellInitSettings>
{
    public override int Execute(
        CommandContext context,
        ShellInitSettings settings,
        CancellationToken ct
    )
    {
        var shell = settings.Shell?.ToLowerInvariant().Replace("-", "").Replace("_", "");

        if (shell == null || shell == "list")
        {
            AnsiConsole.MarkupLine("[bold]Available shells / prompt frameworks:[/]");
            AnsiConsole.MarkupLine("  [cyan]bash[/]          – Plain Bash (no framework)");
            AnsiConsole.MarkupLine("  [cyan]zsh[/]           – Plain Zsh (no framework)");
            AnsiConsole.MarkupLine("  [cyan]oh-my-zsh[/]     – Oh My Zsh (uses prompt_segment)");
            AnsiConsole.MarkupLine("  [cyan]powerlevel10k[/] – Powerlevel10k / p10k");
            AnsiConsole.MarkupLine("  [cyan]oh-my-posh[/]    – Oh My Posh");
            AnsiConsole.MarkupLine("  [cyan]starship[/]      – Starship (cross-shell)");
            AnsiConsole.MarkupLine("  [cyan]fish[/]          – Fish shell");
            AnsiConsole.MarkupLine("  [cyan]powershell[/]    – PowerShell (Windows Terminal)");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Usage: [cyan]bella shell init <framework>[/]");
            AnsiConsole.MarkupLine("Example: [dim]eval \"$(bella shell init oh-my-zsh)\"[/]");
            return 0;
        }

        var snippet = shell switch
        {
            "bash" => BashSnippet(),
            "zsh" => ZshSnippet(),
            "ohmyzsh" => OhMyZshSnippet(),
            "powerlevel10k" or "p10k" => Powerlevel10kSnippet(),
            "ohmyposh" => OhMyPoshSnippet(),
            "starship" => StarshipSnippet(),
            "fish" => FishSnippet(),
            "powershell" or "pwsh" => PowerShellSnippet(),
            _ => null,
        };

        if (snippet == null)
        {
            AnsiConsole.MarkupLine(
                $"[red]Unknown shell '{settings.Shell}'.[/] Run [cyan]bella shell init[/] to see options."
            );
            return 1;
        }

        // Output raw (no markup) so eval/source works correctly
        Console.WriteLine(snippet);
        return 0;
    }

    // ─── Snippets ──────────────────────────────────────────────────────────────
    // Context sources (priority order):
    //   1. $BELLA_BAXTER_PROJECT / $BELLA_BAXTER_ENV  — session ephemeral (bella context use camino/dev)
    //      (also exports deprecated $BELLA_PROJECT / $BELLA_ENV for compatibility)
    //   2. .bella file in cwd/parents   — directory-scoped (bella context init)
    //
    // Each snippet installs:
    //   a) A prompt function showing context from either source
    //   b) A `bella` shell function wrapper so `bella context use` works
    //      (a subprocess can't set parent shell env vars; the wrapper evals the output)

    private static string BashSnippet() =>
        """
            # Bella CLI integration — Plain Bash
            # Add to ~/.bashrc, then run: source ~/.bashrc

            # Shell function wrapper — required for `bella context use` to work
            bella() {
              if [[ "$1" == "context" && "$2" == "use" ]]; then
                eval "$(command bella context use "${@:3}")"
              else
                command bella "$@"
              fi
            }

            # Prompt segment: shows context from $BELLA_BAXTER_PROJECT (fast) or .bella file
            _bella_prompt() {
              local _bp="${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}"
              local _be="${BELLA_BAXTER_ENV:-${BELLA_ENV:-?}}"
              if [[ -n "$_bp" ]]; then
                echo -n "\[\033[0;36m\]🔐 ${_bp}/${_be}\[\033[0m\] "
              elif [[ -f ".bella" ]] && command -v bella &>/dev/null; then
                local ctx; ctx=$(command bella context get --quiet 2>/dev/null) && \
                  echo -n "\[\033[0;36m\]🔐 ${ctx}\[\033[0m\] "
              fi
            }
            PS1='$(_bella_prompt)'"$PS1"
            """;

    private static string ZshSnippet() =>
        """
            # Bella CLI integration — Plain Zsh
            # Add to ~/.zshrc, then run: source ~/.zshrc

            # Shell function wrapper — required for `bella context use` to work
            bella() {
              if [[ "$1" == "context" && "$2" == "use" ]]; then
                eval "$(command bella context use "${@:3}")"
              else
                command bella "$@"
              fi
            }

            # Prompt segment: shows context from $BELLA_BAXTER_PROJECT (fast) or .bella file
            _bella_prompt() {
              local _bp="${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}"
              local _be="${BELLA_BAXTER_ENV:-${BELLA_ENV:-?}}"
              if [[ -n "$_bp" ]]; then
                echo -n "%F{cyan}🔐 ${_bp}/${_be}%f "
              elif [[ -f ".bella" ]] && command -v bella &>/dev/null; then
                local ctx; ctx=$(command bella context get --quiet 2>/dev/null) && \
                  echo -n "%F{cyan}🔐 ${ctx}%f "
              fi
            }
            PROMPT='$(_bella_prompt)'"$PROMPT"
            """;

    private static string OhMyZshSnippet() =>
        """
            # Bella CLI integration — Oh My Zsh
            # Add to ~/.zshrc (after oh-my-zsh is sourced), then run: source ~/.zshrc

            # Shell function wrapper — required for `bella context use` to work
            bella() {
              if [[ "$1" == "context" && "$2" == "use" ]]; then
                eval "$(command bella context use "${@:3}")"
              else
                command bella "$@"
              fi
            }

            # Prompt segment
            prompt_bella_context() {
              local _bp="${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}"
              local _be="${BELLA_BAXTER_ENV:-${BELLA_ENV:-?}}"
              if [[ -n "$_bp" ]]; then
                prompt_segment cyan black "🔐 ${_bp}/${_be}"
              elif [[ -f ".bella" ]] && which bella &>/dev/null; then
                local ctx; ctx=$(command bella context get --quiet 2>/dev/null)
                [[ -n "$ctx" ]] && prompt_segment cyan black "🔐 $ctx"
              fi
            }
            # Add to your theme's build_prompt, or:
            PROMPT='$(prompt_bella_context)'"$PROMPT"
            """;

    private static string Powerlevel10kSnippet() =>
        """
            # Bella CLI integration — Powerlevel10k
            # Add to ~/.zshrc (before [[ ! -f ~/.p10k.zsh ]] || source ~/.p10k.zsh)

            # Shell function wrapper — required for `bella context use` to work
            bella() {
              if [[ "$1" == "context" && "$2" == "use" ]]; then
                eval "$(command bella context use "${@:3}")"
              else
                command bella "$@"
              fi
            }

            # p10k segment
            function prompt_bella() {
              local _bp="${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}"
              local _be="${BELLA_BAXTER_ENV:-${BELLA_ENV:-?}}"
              if [[ -n "$_bp" ]]; then
                p10k segment -f cyan -i '🔐' -t "${_bp}/${_be}"
              elif [[ -f ".bella" ]] && command -v bella &>/dev/null; then
                local ctx; ctx=$(command bella context get --quiet 2>/dev/null)
                [[ -n "$ctx" ]] && p10k segment -f cyan -i '🔐' -t "$ctx"
              fi
            }
            # Add to ~/.p10k.zsh:
            # typeset -g POWERLEVEL9K_LEFT_PROMPT_ELEMENTS=(... bella ...)
            """;

    private static string OhMyPoshSnippet() =>
        """
            # Bella CLI integration — Oh My Posh
            # Add this JSON block to your theme file (e.g. ~/.config/oh-my-posh/theme.json)
            # inside the "segments" array of a "blocks" entry.
            #
            # Find your current theme: echo $POSH_THEME
            {
              "type": "command",
              "style": "plain",
              "foreground": "cyan",
              "template": "{{ if .Output }}🔐 {{ .Output }} {{ end }}",
              "properties": {
                "shell": "bash",
                "command": "if [ -n \"${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}\" ]; then echo \"${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}/${BELLA_BAXTER_ENV:-${BELLA_ENV:-?}}\"; elif [ -f .bella ]; then bella context get --quiet 2>/dev/null; fi"
              }
            }

            # Shell function wrapper for `bella context use` — add to your shell rc:
            # bella() {
            #   if [[ "$1" == "context" && "$2" == "use" ]]; then
            #     eval "$(command bella context use "${@:3}")"
            #   else
            #     command bella "$@"
            #   fi
            # }
            """;

    private static string StarshipSnippet() =>
        """
            # Bella CLI integration — Starship
            # Add to ~/.config/starship.toml
            #
            # When $BELLA_BAXTER_PROJECT is set, when_env fires instantly (no subprocess).
            # Falls back to running bella context when a .bella file is present.

            [custom.bella_env]
            description = "Bella session context (from $BELLA_BAXTER_PROJECT/$BELLA_BAXTER_ENV)"
            command = "echo \"${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}/${BELLA_BAXTER_ENV:-${BELLA_ENV:-?}}\""
            when = "[ -n \"${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}\" ]"
            symbol = "🔐 "
            style = "bold cyan"
            format = "[$symbol$output]($style) "

            [custom.bella_file]
            description = "Bella directory context (from .bella file)"
            command = "bella context get --quiet"
            detect_files = [".bella"]
            when = "[ -z \"${BELLA_BAXTER_PROJECT:-$BELLA_PROJECT}\" ]"
            symbol = "🔐 "
            style = "bold cyan"
            format = "[$symbol$output]($style) "

            # Shell function wrapper for `bella context use` — add to your shell rc:
            # bella() {
            #   if [[ "$1" == "context" && "$2" == "use" ]]; then
            #     eval "$(command bella context use "${@:3}")"
            #   else
            #     command bella "$@"
            #   fi
            # }
            """;

    private static string FishSnippet() =>
        """
            # Bella CLI integration — Fish shell
            # Add to ~/.config/fish/config.fish (or ~/.config/fish/conf.d/bella.fish)

            # Shell function wrapper — required for `bella context use` to work
            function bella
              if test "$argv[1]" = "context" -a "$argv[2]" = "use"
                eval (command bella context use $argv[3] --shell fish)
              else
                command bella $argv
              end
            end

            # Prompt segment: shows context from $BELLA_BAXTER_PROJECT (fast) or .bella file
            function _bella_prompt
              set -l _bp "$BELLA_BAXTER_PROJECT"
              if test -z "$_bp"; set _bp "$BELLA_PROJECT"; end
              set -l _be "$BELLA_BAXTER_ENV"
              if test -z "$_be"; set _be "$BELLA_ENV"; end
              if test -n "$_bp"
                set_color cyan
                echo -n "🔐 $_bp/$_be "
                set_color normal
              else if test -f .bella; and command -v bella &>/dev/null
                set -l ctx (command bella context get --quiet 2>/dev/null)
                if test -n "$ctx"
                  set_color cyan
                  echo -n "🔐 $ctx "
                  set_color normal
                end
              end
            end

            # Wrap the existing fish_prompt:
            functions -c fish_prompt _bella_original_prompt
            function fish_prompt
              _bella_prompt
              _bella_original_prompt
            end
            """;

    private static string PowerShellSnippet() =>
        """
            # Bella CLI integration — PowerShell
            # Add to your $PROFILE (run: notepad $PROFILE to open it)

            # Shell function wrapper — required for `bella context use` to work
            function bella {
              if ($args[0] -eq "context" -and $args[1] -eq "use") {
                $expr = (& (Get-Command bella -CommandType Application) context use $args[2] --shell powershell)
                Invoke-Expression $expr
              } else {
                & (Get-Command bella -CommandType Application) @args
              }
            }

            # Prompt wrapper
            $__originalPrompt = if (Get-Command prompt -ErrorAction SilentlyContinue) {
              $Function:prompt
            } else {
              { "PS $($executionContext.SessionState.Path.CurrentLocation)> " }
            }
            function prompt {
              $_bp = if ($env:BELLA_BAXTER_PROJECT) { $env:BELLA_BAXTER_PROJECT } else { $env:BELLA_PROJECT }
              $_be = if ($env:BELLA_BAXTER_ENV) { $env:BELLA_BAXTER_ENV } else { $env:BELLA_ENV }
              if ($_bp) {
                Write-Host "🔐 $($_bp)/$($_be ?? '?') " -NoNewline -ForegroundColor Cyan
              } elseif (Test-Path ".bella") {
                $ctx = command bella context get --quiet 2>$null
                if ($ctx) { Write-Host "🔐 $ctx " -NoNewline -ForegroundColor Cyan }
              }
              & $__originalPrompt
            }
            """;
}
