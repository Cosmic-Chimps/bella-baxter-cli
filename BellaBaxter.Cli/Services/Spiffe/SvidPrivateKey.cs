using System.Security.Cryptography;
using Google.Protobuf;

namespace BellaCli.Services.Spiffe;

// Backlog §2.31 / issue #710 — the SVID's private key, held as bytes that can be cleared.
//
// WHY A TYPE AND NOT A BARE byte[]. The agent rotates the SVID while Workload API streams may still be
// sending the one it replaced: a stream can take the previous SVID off its channel a moment before the
// rotation lands and then build its response from it. Zeroing a bare array under that stream would push
// a key of zeros — a well-formed protobuf a client reads as a corrupt key. So the buffer lives behind a
// lock, a copy out of it and its destruction are mutually exclusive, and a copy attempted after
// destruction says so instead of reading zeros. The stream then skips that SVID, whose replacement is
// already waiting on its channel (the agent offers the new SVID BEFORE it destroys the old one).
//
// WHAT IS HELD. The PKCS#8 DER the Workload API puts on the wire, converted ONCE when the SVID arrives.
// The per-push PEM → DER conversion the agent used to do produced a fresh un-cleared copy of the key on
// every push to every stream; with the DER held here, a push copies straight from this buffer into the
// response and makes no intermediate copy of its own to clear.
//
// WHAT CANNOT BE CLEARED — Constitution II says "where the platform allows", and these are where it does
// not. They are stated so nobody mistakes this type for a guarantee it cannot give:
//   • The attestation response's PEM. Bella's /attest answers JSON, and System.Text.Json materialises
//     the key as a .NET `string`: immutable, possibly interned, moved by the GC. It is unreachable once
//     HttpSvidSource returns, and that is all that can be said for it. Same for the HTTP stack's own
//     (pooled) receive buffers.
//   • The protobuf copy each push hands to gRPC. `ByteString` is immutable by contract and owns its
//     bytes; gRPC then serialises the message into its own pooled frame buffers. Neither is ours to clear.
//   • The key inside RSA/ECDsa while SvidWireFormat re-exports it. Those objects are disposed at once and
//     the platform's own implementation clears its key blobs; we hold no reference to them.
//   • Whatever a Workload API CLIENT does with the key it receives — that process owns its copy.
// What IS cleared: the one long-lived copy the agent itself holds, when the SVID is superseded and when
// the agent is disposed.

/// <summary>
/// An SVID private key as PKCS#8 DER bytes the agent can clear. See the file comment for what can and
/// cannot be zeroed.
/// </summary>
public sealed class SvidPrivateKey
{
    private readonly object _gate = new();
    private byte[]? _pkcs8Der;

    private SvidPrivateKey(byte[] pkcs8Der) => _pkcs8Der = pkcs8Der;

    /// <summary>
    /// Wraps <paramref name="pkcs8Der"/> WITHOUT copying it. The caller hands the buffer over and must
    /// not keep using it: <see cref="Destroy"/> clears exactly that array, which is also how a test
    /// observes the clearing without asserting anything about the garbage collector.
    /// </summary>
    public static SvidPrivateKey TakeOwnership(byte[] pkcs8Der)
    {
        ArgumentNullException.ThrowIfNull(pkcs8Der);
        return new SvidPrivateKey(pkcs8Der);
    }

    /// <summary>
    /// Converts a PEM key (PKCS#8, PKCS#1 or SEC1) to the PKCS#8 DER the Workload API serves, once.
    /// </summary>
    /// <remarks>The <paramref name="privateKeyPem"/> string itself cannot be cleared — see the file comment.</remarks>
    public static SvidPrivateKey FromPem(string privateKeyPem) =>
        new(SvidWireFormat.PrivateKeyPkcs8Der(privateKeyPem));

    /// <summary>True once <see cref="Destroy"/> has cleared the key.</summary>
    public bool IsDestroyed
    {
        get { lock (_gate) { return _pkcs8Der is null; } }
    }

    /// <summary>
    /// Copies the key into a protobuf <see cref="ByteString"/> for one Workload API push, or returns false
    /// when the key has already been destroyed (the SVID was superseded while a stream held it).
    /// </summary>
    /// <remarks>
    /// The copy is made from the held buffer directly, under the lock <see cref="Destroy"/> takes, so it
    /// can never observe a half-cleared key. The resulting <see cref="ByteString"/> is protobuf-owned and
    /// immutable, and cannot be cleared.
    /// </remarks>
    public bool TryCopyToByteString(out ByteString copy)
    {
        lock (_gate)
        {
            if (_pkcs8Der is null)
            {
                copy = ByteString.Empty;
                return false;
            }

            copy = ByteString.CopyFrom(_pkcs8Der);
            return true;
        }
    }

    /// <summary>Clears the held key bytes. Idempotent.</summary>
    public void Destroy()
    {
        lock (_gate)
        {
            if (_pkcs8Der is { } held)
            {
                CryptographicOperations.ZeroMemory(held);
                _pkcs8Der = null;
            }
        }
    }

    /// <summary>Never renders key material, so a record's generated ToString cannot leak it into a log.</summary>
    public override string ToString() => IsDestroyed ? "[private key: destroyed]" : "[private key]";
}
