using System.Net;
using BellaCli.Infrastructure;

namespace BellaBaxter.Cli.Tests.Infrastructure;

/// <summary>
/// <c>BELLA_BAXTER_DEBUG=1</c> prints a URL in the request line and in a redirect <c>Location</c> header. Each goes through
/// <see cref="DebugLoggingHandler.MaskUrl"/>: the value of every query parameter whose NAME is sensitive (the header rule,
/// plus <c>sig</c>) and the userinfo become <c>***</c>; everything else is left as it was. Truncated text is cut on a
/// character boundary.
/// </summary>
[Collection(DebugLoggingEnvironmentCollection.Name)]
public class DebugLoggingMasksUrlsTests
{
    [Theory]
    [InlineData("https://h.test/p?token=abc123", "https://h.test/p?token=***")]
    [InlineData("https://h.test/p?key=abc123", "https://h.test/p?key=***")]
    [InlineData("https://h.test/p?signature=abc123", "https://h.test/p?signature=***")]
    [InlineData(
        "https://b.s3.amazonaws.com/o?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIDEXAMPLE%2F20261126&X-Amz-Date=20261126T000000Z&X-Amz-Expires=300&X-Amz-Security-Token=tok123&X-Amz-SignedHeaders=host&X-Amz-Signature=sig123",
        "https://b.s3.amazonaws.com/o?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=***&X-Amz-Date=20261126T000000Z&X-Amz-Expires=300&X-Amz-Security-Token=***&X-Amz-SignedHeaders=host&X-Amz-Signature=***")]
    [InlineData(
        "https://acct.blob.core.windows.net/c/b?sv=2022-11-02&se=2026-12-01T00%3A00%3A00Z&sp=r&sig=abc%2Fdef%3D",
        "https://acct.blob.core.windows.net/c/b?sv=2022-11-02&se=2026-12-01T00%3A00%3A00Z&sp=r&sig=***")]
    [InlineData("https://h.test/p?page=2&token=abc&sort=name&api_key=k", "https://h.test/p?page=2&token=***&sort=name&api_key=***")]
    [InlineData("https://h.test/p?token=a&x=1&token=b&x=2", "https://h.test/p?token=***&x=1&token=***&x=2")]
    [InlineData("https://h.test/p?to%6Ben=abc&x%2Dkey=def&ok=1", "https://h.test/p?to%6Ben=***&x%2Dkey=***&ok=1")]
    [InlineData("https://user:pass@h.test:8443/p?a=1", "https://***@h.test:8443/p?a=1")]
    [InlineData("https://user:pass@h.test/p", "https://***@h.test/p")]
    [InlineData("https://h.test:8443/a/b", "https://h.test:8443/a/b")]
    [InlineData("https://h.test/p?token=abc#frag", "https://h.test/p?token=***#frag")]
    [InlineData("/next?token=abc&n=1", "/next?token=***&n=1")]
    [InlineData("https://h.test/p?token=&n=1", "https://h.test/p?token=***&n=1")]
    [InlineData("https://h.test/@me?n=1", "https://h.test/@me?n=1")]
    public void MaskUrl_masks_credential_named_parameters_and_userinfo_only(string input, string expected) =>
        Assert.Equal(expected, DebugLoggingHandler.MaskUrl(input));

    private sealed class RedirectStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "https://other.test/cb?code=1&token=redirect-marker&n=2");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task The_request_line_and_a_redirect_Location_are_printed_masked()
    {
        var previous = Environment.GetEnvironmentVariable("BELLA_BAXTER_DEBUG");
        var stderr = Console.Error;
        var captured = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("BELLA_BAXTER_DEBUG", "1");
            Console.SetError(captured);
            using var client = new HttpClient(new DebugLoggingHandler { InnerHandler = new RedirectStub() });
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://u:p-marker@example.test/x?token=query-marker&page=3");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable("BELLA_BAXTER_DEBUG", previous);
        }

        var output = captured.ToString();
        foreach (var leaked in new[] { "query-marker", "p-marker", "redirect-marker" })
            Assert.DoesNotContain(leaked, output);
        Assert.Contains("→ GET https://***@example.test/x?token=***&page=3", output);
        Assert.Contains("Location: https://other.test/cb?code=1&token=***&n=2", output);
    }

    [Theory]
    [InlineData("€")]
    [InlineData("😀")]
    public void Truncate_never_splits_a_character(string filler)
    {
        var text = "ab" + string.Concat(Enumerable.Repeat(filler, 10));
        for (var max = 0; max < text.Length; max++)
        {
            var cut = DebugLoggingHandler.Truncate(text, max);
            Assert.True(cut.Length <= max);
            Assert.True(cut.Length == 0 || !char.IsHighSurrogate(cut[^1]), $"max {max} ends on a lone high surrogate");
            Assert.StartsWith(cut, text, StringComparison.Ordinal);
        }
        Assert.Equal("short", DebugLoggingHandler.Truncate("short", 10));
    }
}
