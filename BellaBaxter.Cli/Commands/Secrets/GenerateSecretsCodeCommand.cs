using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BellaCli.Infrastructure;
using BellaCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BellaCli.Commands.Secrets;

// ============================================
// REQUEST / RESPONSE DTOs (mirrors API)
// ============================================

internal record ManifestItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("description")] string? Description
);

internal record SecretsManifest(
    [property: JsonPropertyName("projectSlug")] string ProjectSlug,
    [property: JsonPropertyName("environmentSlug")] string EnvironmentSlug,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("secrets")] List<ManifestItem> Secrets
);

// ============================================
// SETTINGS
// ============================================

public class GenerateSecretsCodeSettings : CommandSettings
{
    [CommandArgument(0, "<language>")]
    public string Language { get; init; } = string.Empty;

    [CommandOption("-p|--project <SLUG>")]
    public string? Project { get; init; }

    [CommandOption("-e|--environment <SLUG>")]
    public string? Environment { get; init; }

    [CommandOption("-o|--output <FILE>")]
    public string? OutputFile { get; init; }

    [CommandOption("--class-name <NAME>")]
    public string? ClassName { get; init; }

    [CommandOption("--namespace <NS>")]
    public string? Namespace { get; init; }

    [CommandOption("--dry-run")]
    public bool DryRun { get; init; }

    [CommandOption("--types")]
    [System.ComponentModel.Description(
        "Generate a bella-secrets.ts TypeScript module augmentation file for @bella-baxter/config SDK (enables app.bella.DATABASE_URL typed property access). Exports BELLA_COERCIONS for runtime type coercion."
    )]
    public bool Types { get; init; }
}

// ============================================
// COMMAND
// ============================================

/// <summary>
/// bella secrets generate &lt;language&gt;
///
/// Fetches the secrets manifest from Bella (key names + type hints) and generates
/// a strongly-typed secrets accessor class for the specified language.
/// Secret VALUES are never included — each property reads from an environment variable
/// at runtime, making the generated file safe to commit.
/// </summary>
public class GenerateSecretsCodeCommand(
    BellaClientProvider provider,
    ContextService context,
    IOutputWriter output
) : AsyncCommand<GenerateSecretsCodeSettings>
{
    private static readonly string[] SupportedLanguages =
    [
        "dotnet",
        "python",
        "go",
        "typescript",
        "dart",
        "php",
        "ruby",
        "swift",
    ];

    public override async Task<int> ExecuteAsync(
        CommandContext ctx,
        GenerateSecretsCodeSettings settings,
        CancellationToken ct
    )
    {
        var lang = settings.Language.ToLowerInvariant();
        if (!Array.Exists(SupportedLanguages, l => l == lang))
        {
            output.WriteError(
                $"Unknown language '{settings.Language}'. Supported: {string.Join(", ", SupportedLanguages)}"
            );
            return 1;
        }

        BellaClientProvider.BellaClientWrapper client;
        try
        {
            client = provider.CreateClientWrapper();
        }
        catch (InvalidOperationException)
        {
            output.WriteError("Not logged in. Run 'bella login' first.");
            return 1;
        }

        SecretsManifest? manifest = null;

        try
        {
            var (projectSlug, _, _, envSlug, _, _) = await context.ResolveProjectEnvironmentAsync(
                settings.Project,
                settings.Environment,
                client.BellaClient,
                ct,
                strictJwtLocal: true,
                bootstrapBellaFromExplicit: true
            );

            await AnsiConsole
                .Status()
                .StartAsync(
                    "Fetching secrets manifest...",
                    async _ =>
                    {
                        var sdkManifest =
                            await client
                                .BellaClient.Api.V1.Projects[projectSlug]
                                .Environments[envSlug]
                                .Secrets.Manifest.GetAsync(cancellationToken: ct)
                            ?? throw new InvalidOperationException("Empty manifest response.");

                        manifest = new SecretsManifest(
                            sdkManifest.ProjectSlug ?? projectSlug,
                            sdkManifest.EnvironmentSlug ?? envSlug,
                            sdkManifest.GeneratedAt ?? DateTimeOffset.UtcNow,
                            (sdkManifest.Secrets ?? [])
                                .Select(s => new ManifestItem(
                                    s.Key ?? "",
                                    s.Type ?? "string",
                                    s.Description
                                ))
                                .ToList()
                        );
                    }
                );
        }
        catch (InvalidOperationException ex)
        {
            output.WriteError(ex.Message);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            output.WriteError($"Failed to fetch secrets manifest: {ex.Message}");
            return 1;
        }

        if (manifest is null)
        {
            output.WriteError("Failed to fetch secrets manifest.");
            return 1;
        }

        if (manifest.Secrets.Count == 0)
        {
            output.WriteWarning(
                $"No secrets found for {manifest.ProjectSlug}/{manifest.EnvironmentSlug}."
            );
            output.WriteInfo(
                "Secrets must be created via Bella (bella secrets set ...) to appear in the manifest."
            );
            return 0;
        }

        var baseSlug = ToPascalCase(manifest.ProjectSlug);
        // Avoid "AwesomeSecretsSecrets" when the slug already ends in "Secrets"
        var defaultClassName = baseSlug.EndsWith("Secrets", StringComparison.OrdinalIgnoreCase)
            ? baseSlug
            : baseSlug + "Secrets";
        var className = settings.ClassName ?? defaultClassName;
        var namespaceName = settings.Namespace;

        var code = lang switch
        {
            "dotnet" => GenerateDotnet(manifest, className, namespaceName),
            "python" => GeneratePython(manifest, className),
            "go" => GenerateGo(manifest, className, namespaceName),
            "typescript" when settings.Types => GenerateTypeScriptDeclaration(manifest),
            "typescript" => GenerateTypeScript(manifest, className),
            "dart" => GenerateDart(manifest, className),
            "php" => GeneratePhp(manifest, className),
            "ruby" => GenerateRuby(manifest, className),
            "swift" => GenerateSwift(manifest, className),
            _ => throw new InvalidOperationException($"Unsupported language: {lang}"),
        };

        var outputFile =
            settings.OutputFile ?? GetDefaultOutputFile(lang, className, settings.Types);

        if (settings.DryRun)
        {
            if (output is HumanOutputWriter)
            {
                AnsiConsole.MarkupLine($"[bold green]// Would write to:[/] [grey]{outputFile}[/]");
                AnsiConsole.MarkupLine(new Rule().RuleStyle("grey").ToString() ?? string.Empty);
                AnsiConsole.Write(code);
            }
            else
            {
                output.WriteObject(new { file = outputFile, code });
            }
            return 0;
        }

        await File.WriteAllTextAsync(outputFile, code, Encoding.UTF8, ct);

        // For TypeScript --types mode, also write bella-coercions.ts alongside the .d.ts file.
        if (lang == "typescript" && settings.Types)
        {
            var coercionsFile = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(outputFile)) ?? ".",
                "bella-coercions.ts"
            );
            var coercionsCode = GenerateTypeScriptCoercions(manifest);
            if (!settings.DryRun)
                await File.WriteAllTextAsync(coercionsFile, coercionsCode, Encoding.UTF8, ct);

            if (output is HumanOutputWriter)
            {
                AnsiConsole.MarkupLine(
                    $"[bold green]✓[/] Generated [bold]{outputFile}[/] (ambient types — add to tsconfig include)"
                );
                AnsiConsole.MarkupLine(
                    $"[bold green]✓[/] Generated [bold]{coercionsFile}[/] (runtime coercions — import in setup file)"
                );
                AnsiConsole.MarkupLine(
                    $"[dim]  {manifest.Secrets.Count} secrets | Project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}[/]"
                );
                AnsiConsole.MarkupLine(
                    $"[dim]  Generated at: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC[/]"
                );
                AnsiConsole.MarkupLine(
                    $"\n[dim]  Tip: Re-run after adding/renaming secrets to regenerate.[/]"
                );
            }
            else
            {
                output.WriteObject(
                    new
                    {
                        declarationFile = outputFile,
                        coercionsFile,
                        secretCount = manifest.Secrets.Count,
                        projectSlug = manifest.ProjectSlug,
                        environmentSlug = manifest.EnvironmentSlug,
                    }
                );
            }
            return 0;
        }

        if (output is HumanOutputWriter)
        {
            AnsiConsole.MarkupLine(
                $"[bold green]✓[/] Generated [bold]{outputFile}[/] ({manifest.Secrets.Count} secrets, language: {lang})"
            );
            AnsiConsole.MarkupLine(
                $"[dim]  Generated at: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC[/]"
            );
            AnsiConsole.MarkupLine(
                $"[dim]  Project: {manifest.ProjectSlug} / {manifest.EnvironmentSlug} [/]"
            );
            AnsiConsole.MarkupLine(
                $"\n[dim]  Tip: Re-run this command after adding/renaming secrets to regenerate.[/]"
            );
        }
        else
        {
            output.WriteObject(
                new
                {
                    file = outputFile,
                    secretCount = manifest.Secrets.Count,
                    projectSlug = manifest.ProjectSlug,
                    environmentSlug = manifest.EnvironmentSlug,
                }
            );
        }

        return 0;
    }

    // ============================================
    // ============================================
    // CODE GENERATORS
    // ============================================

    private static string GenerateDotnet(SecretsManifest manifest, string className, string? ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by: bella secrets generate dotnet");
        sb.AppendLine($"// Project:      {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// Generated at: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("// DO NOT edit manually — re-run bella secrets generate dotnet to update.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(ns))
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }
        sb.AppendLine($"public partial class {className}");
        sb.AppendLine("{");
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"    /// <summary>{EscapeXmlComment(s.Description)}</summary>");
            var (csType, body) = GetDotnetProperty(s.Key, s.Type);
            sb.AppendLine($"    public {csType} {ToPascalCase(s.Key)} {{ get {{ {body} }} }}");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (string csType, string body) GetDotnetProperty(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("int", $"return int.Parse(GetRequired(\"{key}\"));"),
            "bool" => ("bool", $"return bool.Parse(GetRequired(\"{key}\"));"),
            "uri" => ("Uri", $"return new Uri(GetRequired(\"{key}\"));"),
            "guid" => ("Guid", $"return Guid.Parse(GetRequired(\"{key}\"));"),
            "base64" => ("byte[]", $"return Convert.FromBase64String(GetRequired(\"{key}\"));"),
            _ => ("string", $"return GetRequired(\"{key}\");"),
        };

    private static string GeneratePython(SecretsManifest manifest, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# auto-generated by: bella secrets generate python");
        sb.AppendLine($"# project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"# generated at: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("# DO NOT edit manually — re-run bella secrets generate python to update.");
        sb.AppendLine("from __future__ import annotations");
        sb.AppendLine("import os");
        sb.AppendLine("from dataclasses import dataclass");
        sb.AppendLine();
        sb.AppendLine($"@dataclass");
        sb.AppendLine($"class {className}:");
        sb.AppendLine($"    @staticmethod");
        sb.AppendLine($"    def _require(key: str) -> str:");
        sb.AppendLine($"        v = os.environ.get(key)");
        sb.AppendLine($"        if v is None:");
        sb.AppendLine(
            $"            raise RuntimeError(f\"Required environment variable '{{key}}' is not set.\")"
        );
        sb.AppendLine($"        return v");
        sb.AppendLine();
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"    # {s.Description}");
            var (pyType, body) = GetPythonProperty(s.Key, s.Type);
            sb.AppendLine($"    @property");
            sb.AppendLine($"    def {ToSnakeCase(s.Key)}(self) -> {pyType}:");
            sb.AppendLine($"        {body}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static (string pyType, string body) GetPythonProperty(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("int", $"return int(self._require(\"{key}\"))"),
            "bool" => ("bool", $"return self._require(\"{key}\").lower() == \"true\""),
            "uri" => ("str", $"return self._require(\"{key}\")  # URI"),
            "guid" => ("str", $"return self._require(\"{key}\")  # UUID"),
            "base64" => (
                "bytes",
                $"import base64; return base64.b64decode(self._require(\"{key}\"))"
            ),
            _ => ("str", $"return self._require(\"{key}\")"),
        };

    private static string GenerateGo(SecretsManifest manifest, string className, string? pkg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Code generated by: bella secrets generate go");
        sb.AppendLine($"// Project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// Generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("// DO NOT edit manually — re-run bella secrets generate go to update.");
        sb.AppendLine($"package {pkg?.ToLowerInvariant() ?? "secrets"}");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"fmt\"");
        sb.AppendLine("\t\"os\"");
        if (manifest.Secrets.Any(s => s.Type.ToLowerInvariant() == "int"))
            sb.AppendLine("\t\"strconv\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine($"type {className} struct{{}}");
        sb.AppendLine();
        sb.AppendLine("func mustGetenv(key string) string {");
        sb.AppendLine("\tv := os.Getenv(key)");
        sb.AppendLine("\tif v == \"\" {");
        sb.AppendLine(
            $"\t\tpanic(fmt.Sprintf(\"Required environment variable '%s' is not set.\", key))"
        );
        sb.AppendLine("\t}");
        sb.AppendLine("\treturn v");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"// {s.Description}");
            var (goType, body) = GetGoMethod(s.Key, s.Type);
            sb.AppendLine($"func (s {className}) {ToPascalCase(s.Key)}() {goType} {{ {body} }}");
        }
        return sb.ToString();
    }

    private static (string goType, string body) GetGoMethod(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("int", $"v, _ := strconv.Atoi(mustGetenv(\"{key}\")); return v"),
            "bool" => ("bool", $"return mustGetenv(\"{key}\") == \"true\""),
            "uri" => ("string", $"return mustGetenv(\"{key}\") // URI"),
            "guid" => ("string", $"return mustGetenv(\"{key}\") // UUID"),
            "base64" => (
                "[]byte",
                $"import \"encoding/base64\"; v, _ := base64.StdEncoding.DecodeString(mustGetenv(\"{key}\")); return v"
            ),
            _ => ("string", $"return mustGetenv(\"{key}\")"),
        };

    private static string GenerateTypeScript(SecretsManifest manifest, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// auto-generated by: bella secrets generate typescript");
        sb.AppendLine($"// project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(
            "// DO NOT edit manually — re-run bella secrets generate typescript to update."
        );
        sb.AppendLine();
        sb.AppendLine("function requireEnv(key: string): string {");
        sb.AppendLine("  const v = process.env[key];");
        sb.AppendLine(
            "  if (v === undefined) throw new Error(`Required env var '${key}' is not set.`);"
        );
        sb.AppendLine("  return v;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"export const {ToCamelCase(className)} = {{");
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"  /** {s.Description} */");
            var (tsType, body) = GetTypeScriptGetter(s.Key, s.Type);
            sb.AppendLine($"  get {ToCamelCase(s.Key)}(): {tsType} {{ {body} }},");
        }
        sb.AppendLine("} as const;");
        return sb.ToString();
    }

    private static (string tsType, string body) GetTypeScriptGetter(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("number", $"return parseInt(requireEnv(\"{key}\"), 10);"),
            "bool" => ("boolean", $"return requireEnv(\"{key}\") === \"true\";"),
            "uri" => ("string", $"return requireEnv(\"{key}\"); // URI"),
            "guid" => ("string", $"return requireEnv(\"{key}\"); // UUID"),
            "base64" => ("Buffer", $"return Buffer.from(requireEnv(\"{key}\"), \"base64\");"),
            _ => ("string", $"return requireEnv(\"{key}\");"),
        };

    private static string GenerateDart(SecretsManifest manifest, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// auto-generated by: bella secrets generate dart --types");
        sb.AppendLine($"// project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(
            "// DO NOT edit manually — re-run bella secrets generate dart --types to update."
        );
        sb.AppendLine("import 'dart:io';");
        sb.AppendLine();
        sb.AppendLine($"class {className} {{");
        sb.AppendLine($"  final Map<String, String>? _map;");
        sb.AppendLine();
        sb.AppendLine(
            $"  /// Env-var mode: reads from [Platform.environment] (injected by `bella run`)."
        );
        sb.AppendLine($"  {className}() : _map = null;");
        sb.AppendLine($"  {className}._fromSecrets(this._map);");
        sb.AppendLine();
        sb.AppendLine($"  /// SDK mode: use with [BellaClient.pullSecretsAs].");
        sb.AppendLine($"  ///");
        sb.AppendLine($"  /// ```dart");
        sb.AppendLine($"  /// final client = BellaClient.fromEnv();");
        sb.AppendLine($"  /// final s = await client.pullSecretsAs({className}.fromMap);");
        sb.AppendLine($"  /// print(s.someKey); // typed");
        sb.AppendLine($"  /// ```");
        sb.AppendLine($"  factory {className}.fromMap(Map<String, String> secrets) =>");
        sb.AppendLine($"      {className}._fromSecrets(secrets);");
        sb.AppendLine();
        sb.AppendLine("  String _require(String key) {");
        sb.AppendLine("    final v = _map != null ? _map[key] : Platform.environment[key];");
        sb.AppendLine(
            "    if (v == null) throw Exception('Required secret \"$key\" is not set.');"
        );
        sb.AppendLine("    return v;");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine(
            "  /// Number of secrets in the pulled map (SDK mode only; 0 in env-var mode)."
        );
        sb.AppendLine("  int get secretCount => _map?.length ?? 0;");
        sb.AppendLine();
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"  /// {s.Description}");
            var (dartType, body) = GetDartGetter(s.Key, s.Type);
            sb.AppendLine($"  {dartType} get {ToCamelCase(s.Key)} => {body};");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (string dartType, string body) GetDartGetter(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("int", $"int.parse(_require(\"{key}\"))"),
            "bool" => ("bool", $"_require(\"{key}\") == \"true\""),
            "uri" => ("Uri", $"Uri.parse(_require(\"{key}\"))"),
            "guid" => ("String", $"_require(\"{key}\")  /* UUID */"),
            "base64" => ("String", $"_require(\"{key}\")  /* base64 */"),
            _ => ("String", $"_require(\"{key}\")"),
        };

    private static string GeneratePhp(SecretsManifest manifest, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?php");
        sb.AppendLine("// auto-generated by: bella secrets generate php");
        sb.AppendLine($"// project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("// DO NOT edit manually — re-run bella secrets generate php to update.");
        sb.AppendLine();
        sb.AppendLine($"final class {className}");
        sb.AppendLine("{");
        sb.AppendLine("    private static function require(string $key): string");
        sb.AppendLine("    {");
        sb.AppendLine("        $v = getenv($key);");
        sb.AppendLine(
            "        if ($v === false) throw new \\RuntimeException(\"Required env var '$key' is not set.\");"
        );
        sb.AppendLine("        return $v;");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"    /** {s.Description} */");
            var (phpType, body) = GetPhpGetter(s.Key, s.Type);
            sb.AppendLine(
                $"    public function {ToCamelCase(s.Key)}(): {phpType} {{ return {body}; }}"
            );
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (string phpType, string body) GetPhpGetter(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("int", $"(int) self::require(\"{key}\")"),
            "bool" => ("bool", $"self::require(\"{key}\") === \"true\""),
            "uri" => ("string", $"self::require(\"{key}\") /* URI */"),
            "guid" => ("string", $"self::require(\"{key}\") /* UUID */"),
            "base64" => ("string", $"base64_decode(self::require(\"{key}\"))"),
            _ => ("string", $"self::require(\"{key}\")"),
        };

    private static string GenerateRuby(SecretsManifest manifest, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# auto-generated by: bella secrets generate ruby");
        sb.AppendLine($"# project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"# generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("# DO NOT edit manually — re-run bella secrets generate ruby to update.");
        sb.AppendLine("# frozen_string_literal: true");
        sb.AppendLine();
        sb.AppendLine("require 'base64'");
        sb.AppendLine();
        sb.AppendLine($"module {className}");
        sb.AppendLine("  def self._require(key)");
        sb.AppendLine("    ENV.fetch(key) { raise \"Required env var '#{key}' is not set.\" }");
        sb.AppendLine("  end");
        sb.AppendLine();
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"  # {s.Description}");
            var rubyExpr = GetRubyMethod(s.Key, s.Type);
            sb.AppendLine($"  def self.{ToSnakeCase(s.Key)} = {rubyExpr}");
        }
        sb.AppendLine("end");
        return sb.ToString();
    }

    private static string GetRubyMethod(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => $"_require(\"{key}\").to_i",
            "bool" => $"_require(\"{key}\") == \"true\"",
            "uri" => $"URI.parse(_require(\"{key}\"))",
            "guid" => $"_require(\"{key}\")  # UUID",
            "base64" => $"Base64.strict_decode64(_require(\"{key}\"))",
            _ => $"_require(\"{key}\")",
        };

    private static string GenerateSwift(SecretsManifest manifest, string className)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// auto-generated by: bella secrets generate swift");
        sb.AppendLine($"// project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("// DO NOT edit manually — re-run bella secrets generate swift to update.");
        sb.AppendLine("import Foundation");
        sb.AppendLine();
        sb.AppendLine($"struct {className} {{");
        sb.AppendLine("    private func require(_ key: String) -> String {");
        sb.AppendLine("        guard let v = ProcessInfo.processInfo.environment[key] else {");
        sb.AppendLine("            fatalError(\"Required env var '\\(key)' is not set.\")");
        sb.AppendLine("        }");
        sb.AppendLine("        return v");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"    /// {s.Description}");
            var (swiftType, body) = GetSwiftProperty(s.Key, s.Type);
            sb.AppendLine($"    var {ToCamelCase(s.Key)}: {swiftType} {{ {body} }}");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static (string swiftType, string body) GetSwiftProperty(string key, string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => ("Int", $"Int(require(\"{key}\"))!"),
            "bool" => ("Bool", $"require(\"{key}\") == \"true\""),
            "uri" => ("URL", $"URL(string: require(\"{key}\"))!"),
            "guid" => ("UUID", $"UUID(uuidString: require(\"{key}\"))!"),
            "base64" => ("Data", $"Data(base64Encoded: require(\"{key}\"))!"),
            _ => ("String", $"require(\"{key}\")"),
        };

    private static string GenerateTypeScriptDeclaration(SecretsManifest manifest)
    {
        // bella-secrets.d.ts — augments the global BellaSecrets interface.
        // The file must be a MODULE (has 'export {}') for 'declare global' to be valid.
        // TypeScript picks it up project-wide when it is included in the compilation
        // via tsconfig "include" — NO import needed in route/controller files.
        var sb = new StringBuilder();
        sb.AppendLine("// auto-generated by: bella secrets generate typescript --types");
        sb.AppendLine($"// project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(
            "// DO NOT edit manually — re-run bella secrets generate typescript --types to update."
        );
        sb.AppendLine("// Safe to commit: contains key names only, never secret values.");
        sb.AppendLine("//");
        sb.AppendLine("// TypeScript picks this up automatically via tsconfig 'include'.");
        sb.AppendLine(
            "// No import needed in route/controller files — types are available project-wide."
        );
        sb.AppendLine("//");
        sb.AppendLine(
            "// ⚠️  ts-node / tsx users: add this to your entry file (e.g. main.ts / server.ts):"
        );
        sb.AppendLine("//   /// <reference path=\"./bella-secrets.d.ts\" />");
        sb.AppendLine(
            "//   (ts-node only includes imported files; tsconfig include is ignored at runtime)"
        );
        sb.AppendLine("//");
        sb.AppendLine(
            "// Runtime coercions are in bella-coercions.ts — import that once in your framework setup."
        );
        sb.AppendLine();
        sb.AppendLine("export {}; // required: makes this a module so 'declare global' is valid");
        sb.AppendLine();
        sb.AppendLine("declare global {");
        sb.AppendLine("  interface BellaSecrets {");
        foreach (var s in manifest.Secrets)
        {
            if (s.Description is not null)
                sb.AppendLine($"    /** {EscapeXmlComment(s.Description)} */");
            var tsType = GetTypeScriptDeclarationType(s.Type);
            sb.AppendLine($"    {s.Key}: {tsType};");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTypeScriptCoercions(SecretsManifest manifest)
    {
        // bella-coercions.ts — runtime coercion map.
        // Import BELLA_COERCIONS and pass as `coercions` to createBellaConfig() /
        // framework plugin once (in instrumentation.ts, app.module.ts, config/bella.ts, etc.).
        var coercions = manifest
            .Secrets.Where(s => GetTypeScriptCoercionTag(s.Type) is not null)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// auto-generated by: bella secrets generate typescript --types");
        sb.AppendLine($"// project: {manifest.ProjectSlug}/{manifest.EnvironmentSlug}");
        sb.AppendLine($"// generated: {manifest.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(
            "// DO NOT edit manually — re-run bella secrets generate typescript --types to update."
        );
        sb.AppendLine("// Safe to commit: contains key names only, never secret values.");
        sb.AppendLine("//");
        sb.AppendLine(
            "// Import BELLA_COERCIONS and pass as `coercions` to createBellaConfig() so the"
        );
        sb.AppendLine("// Proxy returns number/boolean at runtime instead of raw strings.");
        sb.AppendLine("//");
        sb.AppendLine("// Example:");
        sb.AppendLine("//   import { BELLA_COERCIONS } from './bella-coercions.js';");
        sb.AppendLine("//   await initBella({ ..., coercions: BELLA_COERCIONS });");
        sb.AppendLine();

        if (coercions.Count > 0)
        {
            sb.AppendLine("export const BELLA_COERCIONS = {");
            foreach (var s in coercions)
                sb.AppendLine($"  {s.Key}: '{GetTypeScriptCoercionTag(s.Type)}',");
            sb.AppendLine("} as const satisfies Record<string, 'number' | 'boolean'>;");
        }
        else
        {
            sb.AppendLine("// No coercions needed — all secrets are strings.");
            sb.AppendLine(
                "export const BELLA_COERCIONS: Record<string, 'number' | 'boolean'> = {};"
            );
        }

        return sb.ToString();
    }

    private static string? GetTypeScriptCoercionTag(string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => "number",
            "float" => "number",
            "bool" => "boolean",
            _ => null,
        };

    /// <summary>
    /// Maps a Bella secret type to a TypeScript type for declaration files.
    /// The SDK Proxy coerces values at runtime to match these types.
    /// </summary>
    private static string GetTypeScriptDeclarationType(string type) =>
        type.ToLowerInvariant() switch
        {
            "int" => "number",
            "float" => "number",
            "bool" => "boolean",
            "uri" => "string",
            "guid" => "string",
            "base64" => "string",
            "json" => "string", // raw JSON string — parse manually
            _ => "string",
        };

    // ============================================
    // HELPERS
    // ============================================

    private static string GetDefaultOutputFile(string lang, string className, bool types = false) =>
        lang switch
        {
            "dotnet" => $"{className}.cs",
            "python" => "secrets.py",
            "go" => "secrets.go",
            "typescript" when types => "bella-secrets.d.ts",
            "typescript" => "secrets.ts",
            "dart" => $"{ToSnakeCase(className)}.dart",
            "php" => "secrets.php",
            "ruby" => "secrets.rb",
            "swift" => $"{className}.swift",
            _ => "secrets.txt",
        };

    private static string ToPascalCase(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        return string.Concat(
            s.Split(['_', '-', ' '])
                .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..].ToLower() : w)
        );
    }

    private static string ToCamelCase(string s)
    {
        var pascal = ToPascalCase(s);
        return pascal.Length > 0 ? char.ToLower(pascal[0]) + pascal[1..] : pascal;
    }

    private static string ToSnakeCase(string s)
    {
        return string.Concat(
                s.Select(
                    (c, i) =>
                        i > 0 && char.IsUpper(c)
                            ? "_" + char.ToLower(c)
                            : char.ToLower(c).ToString()
                )
            )
            .Replace('-', '_')
            .Replace(' ', '_');
    }

    private static string EscapeXmlComment(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
