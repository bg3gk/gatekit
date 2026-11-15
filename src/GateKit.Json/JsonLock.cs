using System.Security.Cryptography;
using System.Text;

namespace GateKit.Json;

/// <summary>
/// One writer at a time across processes, for a scope named by a path (a ledger, or a folder of
/// ledgers written together). Held as a file in the temporary folder that nobody else may open, so
/// a process that dies lets go by dying, and nothing is left in the repository. Not re-entrant: a
/// caller that holds it passes it on (<see cref="JsonText.Append(string, IReadOnlyCollection{System.Text.Json.Nodes.JsonNode}, string?, JsonLock?, SchemaSet?)"/>).
///
/// The lock of a scope is the same file as <c>GateKit.Copies.Record.Lock</c> of the same path, so
/// both writers of the coverage record exclude each other.
/// </summary>
public sealed class JsonLock : IDisposable
{
    readonly FileStream _file;

    JsonLock(FileStream file, string scope)
    {
        _file = file;
        Scope = scope;
    }

    /// <summary>The full path the lock is for.</summary>
    public string Scope { get; }

    /// <summary>The lock file of a scope.</summary>
    public static string FileOf(string scope)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(scope).ToUpperInvariant())))[..16];
        return Path.Combine(Path.GetTempPath(), $"gatekit-record-{name}.lock");
    }

    /// <summary>Waits up to <paramref name="timeout"/> (two minutes by default) for the scope.</summary>
    public static JsonLock Take(string scope, TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromMinutes(2);
        var path = FileOf(scope);
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new JsonLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose),
                    Path.GetFullPath(scope));
            }
            // a lock file being closed (it deletes itself) answers "access denied" for a moment: held too
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new TimeoutException($"another writer has held {scope} for over {wait.TotalSeconds:0} s; nothing was written - "
                    + "try again, and if it repeats, a process died holding it open", e);
            }
        }
    }

    public void Dispose() => _file.Dispose();
}
