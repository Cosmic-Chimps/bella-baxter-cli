using System.Reflection;
using BellaCli.Commands.Secrets;

namespace BellaBaxter.Cli.Tests.Commands.Secrets;

// spec 020 (T048, US2, FR-025) — pins what a `certificate`-typed secret generates.
//
// The claim this test defends (research D4): the new secret type needs NO per-language code, because
// every one of the generators ends in a string default. If someone later removes a default arm, or
// adds a half-finished `certificate` case, these tests fail rather than the CLI silently emitting
// something odd in one of nine languages.
//
// The mapping methods are private, so they are invoked reflectively — the alternative would be
// widening production visibility purely for a test.

public class GenerateSecretsCodeTypeMappingTests
{
    private static readonly (string Method, string ExpectedType)[] Generators =
    [
        ("GetDotnetProperty", "string"),
        ("GetPythonProperty", "str"),
        ("GetGoMethod", "string"),
        ("GetTypeScriptGetter", "string"),
        ("GetDartGetter", "String"),
        ("GetPhpGetter", "string"),
        ("GetSwiftProperty", "String"),
    ];

    [Fact]
    public void Certificate_generates_a_string_accessor_in_every_language()
    {
        foreach (var (method, expectedType) in Generators)
        {
            var mapped = Invoke(method, "MY_CERT", "certificate");

            Assert.Equal(expectedType, mapped);
        }
    }

    [Fact]
    public void Certificate_maps_the_same_way_a_json_secret_does()
    {
        // Certificates are structured values; matching Json's treatment is the documented
        // behaviour rather than an accident.
        foreach (var (method, _) in Generators)
        {
            Assert.Equal(Invoke(method, "K", "json"), Invoke(method, "K", "certificate"));
        }
    }

    [Fact]
    public void An_unknown_type_also_degrades_to_a_string_accessor()
    {
        // This is the default arm the claim depends on.
        foreach (var (method, expectedType) in Generators)
        {
            Assert.Equal(expectedType, Invoke(method, "K", "someFutureType"));
        }
    }

    [Fact]
    public void A_recognised_scalar_type_still_maps_to_its_own_type()
    {
        // Guards against a change that makes EVERYTHING a string and passes the tests above.
        Assert.Equal("int", Invoke("GetDotnetProperty", "PORT", "int"));
        Assert.Equal("bool", Invoke("GetDotnetProperty", "FLAG", "bool"));
    }

    private static string Invoke(string methodName, string key, string type)
    {
        var method =
            typeof(GenerateSecretsCodeCommand).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException($"{methodName} not found — did it get renamed?");

        var result =
            method.Invoke(null, [key, type])
            ?? throw new InvalidOperationException($"{methodName} returned null.");

        // Every generator returns a (type, body) tuple; the first field is the emitted type.
        var typeField =
            result.GetType().GetFields()[0].GetValue(result)?.ToString()
            ?? throw new InvalidOperationException($"{methodName} returned no type.");
        return typeField;
    }
}
