using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BellaCli.Infrastructure;

/// <summary>
/// Implements the OAuth2 PKCE flow: generates challenge, starts a local callback HTTP server,
/// opens the browser, and returns the authorization code.
/// </summary>
public static class OAuth2PkceFlow
{
    private static readonly int[] CallbackPorts =
    [
        5129,
        5130,
        5131,
        5132,
        5133,
        5134,
        5135,
        5136,
        5137,
        5138,
        5139,
    ];

    public record PkceChallenge(string CodeVerifier, string CodeChallenge);

    public static PkceChallenge GenerateChallenge()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkceChallenge(verifier, challenge);
    }

    /// <summary>
    /// Starts a local HTTP listener, returns (callbackUrl, port).
    /// The listener is started on the first available port.
    /// </summary>
    public static (HttpListener listener, string callbackUrl, int port) StartCallbackListener()
    {
        foreach (var port in CallbackPorts)
        {
            try
            {
                var listener = new HttpListener();
                var url = $"http://localhost:{port}/callback";
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                return (listener, url, port);
            }
            catch (HttpListenerException)
            {
                // Port in use, try next
            }
        }

        throw new InvalidOperationException("No available port for OAuth2 callback listener.");
    }

    /// <summary>
    /// Waits for the OAuth2 callback and returns the authorization code.
    /// </summary>
    public static async Task<string> WaitForCallbackAsync(
        HttpListener listener,
        CancellationToken ct
    )
    {
        using var registration = ct.Register(listener.Stop);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];

        // Return a nice HTML page to the browser
        var html = code is not null
            ? """
                <html lang="en">
                  <head>
                    <title>Bella Baxter CLI - Authentication Success</title>
                    <meta charset="UTF-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1.0">
                    <style>
                      * { margin: 0; padding: 0; box-sizing: border-box; }
                      body {
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
                        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
                        min-height: 100vh; display: flex; align-items: center; justify-content: center;
                      }
                      .container {
                        background: white; border-radius: 16px; box-shadow: 0 20px 40px rgba(0,0,0,0.1);
                        padding: 60px 40px; text-align: center; max-width: 500px; width: 90%;
                      }
                      .success-icon { font-size: 64px; color: #4CAF50; margin-bottom: 24px; }
                      h1 { color: #2e7d32; font-size: 28px; margin-bottom: 16px; font-weight: 600; }
                      .message { color: #666; font-size: 16px; line-height: 1.5; margin-bottom: 32px; }
                      .loading {
                        display: inline-block; width: 32px; height: 32px; margin: 16px 0;
                        border: 3px solid #f3f3f3; border-top: 3px solid #4CAF50; border-radius: 50%;
                        animation: spin 1s linear infinite;
                      }
                      @keyframes spin { 0% { transform: rotate(0deg); } 100% { transform: rotate(360deg); } }
                      .cli-info {
                        background: #f8f9fa; border-radius: 8px; padding: 20px; margin-top: 24px;
                        font-family: 'Monaco', 'Menlo', 'Ubuntu Mono', monospace; font-size: 14px;
                      }
                      .close-hint { color: #999; font-size: 14px; margin-top: 16px; }
                    </style>
                  </head>
                  <body>
                    <div class="container">
                      <div class="success-icon">✅</div>
                      <h1>Authentication Successful!</h1>
                      <p class="message">
                        You have been successfully authenticated with Bella Baxter CLI.<br>
                        The authorization process is completing automatically.
                      </p>
                      <div class="loading"></div>
                      <div class="cli-info">
                        <strong>🔒 Bella Baxter CLI</strong><br>
                        Secure secret management platform<br>
                        Authentication completed successfully
                      </div>
                      <p class="close-hint">
                        You can close this window and return to your terminal.<br>
                        The CLI will complete the login process automatically.
                      </p>
                    </div>
                    <script>
                      // Auto-close after 4 seconds
                      setTimeout(() => {
                        try { window.close(); } catch(e) {}
                      }, 4000);

                      // Show completion message after 2 seconds
                      setTimeout(() => {
                        document.querySelector('.loading').style.display = 'none';
                        const newP = document.createElement('p');
                        newP.style.color = '#4CAF50';
                        newP.style.fontWeight = 'bold';
                        newP.innerHTML = '✓ CLI authentication completed';
                        document.querySelector('.message').appendChild(newP);
                      }, 2000);
                    </script>
                  </body>
                </html>
                """
            : $$"""
                <html lang="en">
                  <head>
                    <title>Bella Baxter CLI - Authentication Error</title>
                    <meta charset="UTF-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1.0">
                    <style>
                      * { margin: 0; padding: 0; box-sizing: border-box; }
                      body {
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                        background: linear-gradient(135deg, #ff6b6b 0%, #ee5a24 100%);
                        min-height: 100vh; display: flex; align-items: center; justify-content: center;
                      }
                      .container {
                        background: white; border-radius: 16px; box-shadow: 0 20px 40px rgba(0,0,0,0.1);
                        padding: 60px 40px; text-align: center; max-width: 500px; width: 90%;
                      }
                      .error-icon { font-size: 64px; color: #d32f2f; margin-bottom: 24px; }
                      h1 { color: #d32f2f; font-size: 28px; margin-bottom: 16px; font-weight: 600; }
                      .error-details {
                        background: #ffebee; border: 1px solid #ffcdd2; border-radius: 8px;
                        padding: 20px; margin: 20px 0; text-align: left;
                      }
                      .error-code { font-family: monospace; font-weight: bold; color: #c62828; }
                      .instructions { color: #666; font-size: 14px; margin-top: 24px; line-height: 1.5; }
                    </style>
                  </head>
                  <body>
                    <div class="container">
                      <div class="error-icon">❌</div>
                      <h1>Authentication Failed</h1>
                      <div class="error-details">
                        <div class="error-code">Error: {{error}}</div>
                        <div style="margin-top: 8px; color: #666;">Unknown error occurred during authentication</div>
                      </div>
                      <div class="instructions">
                        <strong>What to do next:</strong><br>
                        • Close this window<br>
                        • Return to your terminal<br>
                        • Run <code>bella login</code> to try again<br>
                        • Ensure you're using correct credentials
                      </div>
                    </div>
                  </body>
                </html>
                """;

        var response = context.Response;
        response.ContentType = "text/html";
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, ct);
        response.Close();

        listener.Stop();

        return code ?? throw new InvalidOperationException($"OAuth2 error: {error}");
    }

    /// <summary>Opens a URL in the system default browser.</summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }
            );
        }
        catch
        {
            // If browser open fails, the caller should show the URL to the user
        }
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public record AuthConfig(string KeycloakUrl, string Realm, string CliClientId);

public static class AuthConfigExtensions
{
    public static string GetAuthorizationEndpoint(this AuthConfig config) =>
        $"{config.KeycloakUrl}/realms/{config.Realm}/protocol/openid-connect/auth";

    public static string GetTokenEndpoint(this AuthConfig config) =>
        $"{config.KeycloakUrl}/realms/{config.Realm}/protocol/openid-connect/token";
}
