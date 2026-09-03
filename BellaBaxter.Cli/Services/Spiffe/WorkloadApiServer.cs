using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BellaCli.Services.Spiffe;

// Spec 001 T022/T042 (US6) — hosting the Workload API on a Unix domain socket.
//
// Kestrel over a UDS, and no HTTPS. Both are deliberate:
//
//  * NO TLS. A Unix socket has no network peer to authenticate and no wire to intercept; the security
//    boundary is the filesystem permission (see SvidSocketPath for why that boundary is the WHOLE
//    boundary here rather than a supporting control). Adding TLS would mean the agent needs a
//    server certificate before it can serve the identity it exists to obtain — a bootstrap loop for no
//    gain. This is what SPIRE does too, and the SPIFFE spec assumes it.
//
//  * HTTP/2 WITHOUT NEGOTIATION. gRPC needs HTTP/2, and h2 over a plaintext connection cannot be
//    negotiated by ALPN (there is no TLS handshake to negotiate in). So the endpoint is pinned to
//    Http2. Leaving the default (Http1AndHttp2) makes Kestrel pick HTTP/1.1 for a plaintext
//    connection, and every gRPC client then fails with a protocol error that says nothing about the
//    cause.
//
// A STALE SOCKET FILE IS REMOVED BEFORE BINDING, but only after the path has been validated by
// SvidSocketPath — never blindly. An unclean shutdown (SIGKILL, a crashed pod) leaves the file behind
// and bind() then fails with "address already in use", which for a sidecar means it never comes back
// without manual intervention.

/// <summary>Serves the SPIFFE Workload API over a Unix domain socket.</summary>
public sealed class WorkloadApiServer(
    SvidAgent agent,
    IJwtSvidSource jwtSource,
    SvidSocketLocation location,
    ILoggerFactory? loggerFactory = null)
{
    /// <summary>The socket the server is bound to.</summary>
    public string SocketPath => location.Path;

    /// <summary>
    /// Binds the socket and serves until <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// Returns only on shutdown. The socket file is removed on the way out so the next start does not
    /// have to reason about whether a leftover file belongs to a live process.
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        // Directory permissions are established (and an over-permissive existing directory REFUSED)
        // before anything is bound. Doing this after binding would mean the socket exists, briefly,
        // under a directory whose permissions were never checked.
        SvidSocketPath.PrepareDirectory(location.Path);
        RemoveStaleSocket(location.Path);

        var builder = WebApplication.CreateSlimBuilder();

        // The agent is a sidecar: its own logs are the operator's only view. But framework request
        // logging on a stream that stays open for the process's lifetime says nothing useful, so only
        // warnings and worse from Kestrel/gRPC reach the console.
        builder.Logging.ClearProviders();
        if (loggerFactory is null)
        {
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "; });
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Grpc", LogLevel.Warning);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenUnixSocket(location.Path, listen =>
            {
                // See the file header: h2c cannot be ALPN-negotiated, so it is pinned.
                listen.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddGrpc(options =>
        {
            // A workload's own agent is not a hostile client, and hiding the reason a call was refused
            // from the process that has to act on it costs a debugging session. The messages here are
            // written for that reader (see WorkloadApiService).
            options.EnableDetailedErrors = true;
        });

        builder.Services.AddSingleton(agent);
        builder.Services.AddSingleton(jwtSource);
        builder.Services.AddSingleton<WorkloadApiService>();

        await using var app = builder.Build();
        app.MapGrpcService<WorkloadApiService>();

        await app.StartAsync(ct).ConfigureAwait(false);

        // Only NOW is the socket owner-only. Kestrel creates the file on bind, so tightening it before
        // StartAsync would chmod a path that does not exist yet. The window between bind and chmod is
        // covered by the containing directory already being 0700 — which is why the directory
        // permission, not the socket permission, is the load-bearing one.
        SvidSocketPath.SecureSocket(location.Path);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            // Not passing ct: it is already cancelled, and a graceful stop is what closes open streams
            // cleanly instead of dropping every connected workload mid-read.
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            RemoveStaleSocket(location.Path);
        }
    }

    /// <summary>
    /// Removes a leftover socket file so bind() does not fail with "address already in use".
    /// </summary>
    /// <remarks>
    /// <para>Only ever called on a path <see cref="SvidSocketPath"/> resolved and whose directory it
    /// validated. Deleting a caller-supplied path without that check would be a delete-arbitrary-file
    /// primitive driven by an environment variable.</para>
    ///
    /// <para><b>It deletes ONLY a stale socket</b>, established by connecting and reading the exact
    /// error. The three outcomes are distinguishable and were confirmed by experiment rather than
    /// assumed, because the first version of this method treated any <c>SocketException</c> as "stale"
    /// — which would have deleted a regular file, since a non-socket also throws:</para>
    /// <list type="bullet">
    ///   <item><b>Connects</b> — another agent is serving here. Refuse: taking the endpoint from a live
    ///   process would leave two agents fighting over one socket.</item>
    ///   <item><c>ConnectionRefused</c> — a real socket nobody is listening on, i.e. the unclean-shutdown
    ///   case (SIGKILL, crashed pod). Safe to remove, and removing it is the whole point: otherwise a
    ///   sidecar never restarts without manual intervention.</item>
    ///   <item><c>NotSocket</c> (or anything else) — a regular file, a directory, something else. That
    ///   is either a mistake or someone else's data, and destroying it is not this program's call.</item>
    /// </list>
    /// </remarks>
    private static void RemoveStaleSocket(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        // A symlink is refused before anything else: following one would let a link planted at the path
        // point the endpoint — and any deletion — somewhere the operator never chose.
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"'{path}' is a symbolic link. The agent will not follow one to bind its socket.");
        }

        SocketError probe;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));

            throw new InvalidOperationException(
                $"Another process is already serving the Workload API at '{path}'. Stop it first, or "
                + "pass --socket to listen elsewhere.");
        }
        catch (SocketException ex)
        {
            probe = ex.SocketErrorCode;
        }

        if (probe != SocketError.ConnectionRefused)
        {
            throw new InvalidOperationException(
                $"'{path}' exists but is not a stale socket ({probe}). Refusing to delete it — move it "
                + "aside if this is really where the agent should listen.");
        }

        File.Delete(path);
    }
}
