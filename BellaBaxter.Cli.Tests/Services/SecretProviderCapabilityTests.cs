using BellaBaxter.Client.Models;
using BellaCli.Services;

namespace BellaBaxter.Cli.Tests.Services;

// #1617 — "does this provider store secrets?" has ONE source: the capability the server publishes
// (supportsSecretsManagement). Meta-type is only the fallback for an older server that does not send it.
//
// The two types where the old meta-type rule and the server disagree:
//   VaultDatabase — meta-type Secrets, NO adapter: offered as a destination, then the server threw.
//   HttpRest      — meta-type Generic, adapter exists: refused though the server writes to it.
// Spec 020 FR-029/030/033 and SC-010 are the requirements these tests hold.

public class SecretProviderCapabilityTests
{
    /// <summary>
    /// Every provider type the API defines (<c>ProviderType</c>), by the name the API publishes. The CLI cannot
    /// reference the API's enum, and the OpenAPI document types the field as a plain string, so the list is
    /// repeated here. The assertions below do NOT depend on which names are listed (the published capability
    /// decides, whatever the name), so a type added on the server and missing here is not a hole; this list only
    /// makes the sweep broad.
    /// </summary>
    private static readonly string[] AllProviderTypes =
    [
        "AwsSecretsManager", "AwsParameterStore", "AzureKeyVault", "GoogleSecretManager", "Vault", "HttpRest",
        "VaultDatabase", "Gigamon", "DigiCert", "CertStorage", "BellaBaxterSecretsSource", "Acme", "GitScripts", "Dns",
    ];

    private static readonly string[] MetaTypes = ["Secrets", "Generic", "CertificateRotation"];

    public static TheoryData<string, string> EveryTypeWithEveryMetaType()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in AllProviderTypes)
        foreach (var meta in MetaTypes)
            data.Add(type, meta);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTypeWithEveryMetaType))]
    public void A_published_true_makes_a_provider_a_destination_whatever_its_meta_type(string type, string meta)
    {
        var candidate = new SecretProviderCandidate("p", type, meta, SupportsSecretsManagement: true);

        Assert.True(candidate.StoresSecrets);

        var decision = SecretProviderSelection.Decide([candidate], explicitSlug: null, canPrompt: false);
        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("p", decision.Slug);

        var named = SecretProviderSelection.Decide([candidate], explicitSlug: "p", canPrompt: false);
        Assert.Equal(SecretProviderDecisionKind.Resolved, named.Kind);
    }

    [Theory]
    [MemberData(nameof(EveryTypeWithEveryMetaType))]
    public void A_published_false_refuses_a_provider_whatever_its_meta_type(string type, string meta)
    {
        var candidate = new SecretProviderCandidate("p", type, meta, SupportsSecretsManagement: false);

        Assert.False(candidate.StoresSecrets);

        var decision = SecretProviderSelection.Decide([candidate], explicitSlug: null, canPrompt: true);
        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);

        // FR-033: --provider naming a non-store is refused, never aimed at.
        var named = SecretProviderSelection.Decide([candidate], explicitSlug: "p", canPrompt: true);
        Assert.Equal(SecretProviderDecisionKind.Failed, named.Kind);
        Assert.Contains("does not store", named.Error!);
    }

    [Theory]
    [MemberData(nameof(EveryTypeWithEveryMetaType))]
    public void With_no_published_capability_the_meta_type_decides(string type, string meta)
    {
        // An older server: the property is absent, so the rule falls back to the meta-type.
        var candidate = new SecretProviderCandidate("p", type, meta, SupportsSecretsManagement: null);

        Assert.Equal(meta == "Secrets", candidate.StoresSecrets);
    }

    [Fact]
    public void VaultDatabase_with_meta_type_Secrets_is_not_a_candidate_when_the_server_says_it_has_no_adapter()
    {
        var attached = new[]
        {
            new SecretProviderCandidate("db", "VaultDatabase", "Secrets", false),
            new SecretProviderCandidate("openbao", "Vault", "Secrets", true),
        };

        // FR-030: non-interactive, resolves to the real store without prompting and without ambiguity.
        var decision = SecretProviderSelection.Decide(attached, explicitSlug: null, canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("openbao", decision.Slug);
    }

    [Fact]
    public void VaultDatabase_alone_is_refused_not_offered()
    {
        var attached = new[] { new SecretProviderCandidate("db", "VaultDatabase", "Secrets", false) };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.Failed, decision.Kind);
        Assert.Contains("No secrets provider", decision.Error!);
    }

    [Fact]
    public void HttpRest_with_meta_type_Generic_is_a_destination_when_the_server_says_it_has_an_adapter()
    {
        var attached = new[] { new SecretProviderCandidate("rest", "HttpRest", "Generic", true) };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: false);

        Assert.Equal(SecretProviderDecisionKind.Resolved, decision.Kind);
        Assert.Equal("rest", decision.Slug);
    }

    [Fact]
    public void Two_stores_still_ask_and_a_non_store_is_not_among_the_choices()
    {
        var attached = new[]
        {
            new SecretProviderCandidate("openbao", "Vault", "Secrets", true),
            new SecretProviderCandidate("rest", "HttpRest", "Generic", true),
            new SecretProviderCandidate("db", "VaultDatabase", "Secrets", false),
        };

        var decision = SecretProviderSelection.Decide(attached, null, canPrompt: true);

        Assert.Equal(SecretProviderDecisionKind.NeedsChoice, decision.Kind);
        Assert.Equal(["openbao", "rest"], decision.Choices.Select(c => c.Slug).ToArray());
    }

    // ── the ONE predicate shared with `secrets list` / `secrets push` ────────────────────────────────────

    private static EnvironmentProviderResponse Response(string type, string? meta, bool? capability) =>
        new()
        {
            ProviderSlug = "p",
            ProviderType = type,
            ProviderMetaType = meta,
            SupportsSecretsManagement = capability,
        };

    [Theory]
    [MemberData(nameof(EveryTypeWithEveryMetaType))]
    public void The_shared_predicate_agrees_with_the_candidate_for_a_response(string type, string meta)
    {
        foreach (bool? capability in new bool?[] { true, false, null })
        {
            var candidate = new SecretProviderCandidate("p", type, meta, capability);

            Assert.Equal(candidate.StoresSecrets, SecretProviderCapability.StoresSecrets(Response(type, meta, capability)));
        }
    }

    [Fact]
    public void The_shared_predicate_follows_the_capability_for_the_two_disagreeing_types()
    {
        Assert.False(SecretProviderCapability.StoresSecrets(Response("VaultDatabase", "Secrets", false)));
        Assert.True(SecretProviderCapability.StoresSecrets(Response("HttpRest", "Generic", true)));
    }

    [Theory]
    [InlineData("Vault", true)]
    [InlineData("AwsSecretsManager", true)]
    [InlineData("AwsParameterStore", true)]
    [InlineData("AzureKeyVault", true)]
    [InlineData("GoogleSecretManager", true)]
    [InlineData("Gigamon", false)]
    [InlineData("HttpRest", false)]
    [InlineData("VaultDatabase", false)]
    public void With_neither_capability_nor_meta_type_the_legacy_type_list_decides(string type, bool expected)
    {
        Assert.Equal(expected, SecretProviderCapability.StoresSecrets(Response(type, null, null)));
    }

    [Fact]
    public void A_capability_outranks_a_meta_type_and_the_legacy_list()
    {
        // Vault is in the legacy list and meta-type Secrets, but the server said no: no.
        Assert.False(SecretProviderCapability.StoresSecrets(Response("Vault", "Secrets", false)));
    }

    [Fact]
    public void No_command_keeps_a_private_copy_of_the_predicate()
    {
        // Three copies of one rule was the defect. The predicate lives in SecretProviderCapability and nowhere else.
        var root = CliSourceRoot();
        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("SecretProviderResolver.cs", StringComparison.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("IsSecretsProvider(", StringComparison.Ordinal)
                    || text.Contains("ProviderMetaType == \"Secrets\"", StringComparison.Ordinal)
                    || text.Contains("ProviderMetaType != \"Secrets\"", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files decide 'stores secrets' themselves instead of calling SecretProviderCapability.StoresSecrets: "
                + string.Join(", ", offenders)
        );
    }

    private static string CliSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BellaBaxter.Cli")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "BellaBaxter.Cli");
    }
}
