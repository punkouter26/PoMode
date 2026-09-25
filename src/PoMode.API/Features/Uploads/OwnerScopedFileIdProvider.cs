using System.Security.Cryptography;
using System.Text;
using tusdotnet.Interfaces;

namespace PoMode.API.Features.Uploads;

/// <summary>
/// Upload ids that carry their owner: 16 hex characters of a hash of the owner id, then 32 random
/// ones. tusdotnet asks the provider to validate every id it is handed, so an id minted for someone
/// else fails validation and answers exactly like one that never existed — ownership is enforced on
/// every HEAD, PATCH and DELETE without a sidecar file for the expiry sweep to forget about.
/// </summary>
public sealed class OwnerScopedFileIdProvider(string? ownerId) : ITusFileIdProvider
{
    private const int PrefixLength = 16;
    private const int IdLength = PrefixLength + 32;

    /// <summary>Null for the expiry sweep, which acts on every owner's uploads.</summary>
    private readonly string? _prefix = ownerId is null ? null : PrefixOf(ownerId);

    public Task<string> CreateId(string metadata)
        => _prefix is null
            ? throw new InvalidOperationException("An upload can only be created for a signed-in owner.")
            : Task.FromResult(_prefix + Guid.NewGuid().ToString("N"));

    public Task<bool> ValidateId(string fileId)
        => Task.FromResult(
            fileId.Length == IdLength
            && fileId.All(char.IsAsciiHexDigitLower)
            && (_prefix is null || fileId.StartsWith(_prefix, StringComparison.Ordinal)));

    private static string PrefixOf(string ownerId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)))[..PrefixLength];
}
