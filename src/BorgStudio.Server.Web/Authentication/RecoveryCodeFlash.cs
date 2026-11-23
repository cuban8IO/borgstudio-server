using Microsoft.Extensions.Caching.Memory;

namespace BorgStudio.Server.Web.Authentication;

/// <summary>
/// Hands freshly created recovery codes across one page reload (needed after changes that renew the
/// sign-in cookie). They stay in server memory for at most five minutes and can be taken only once.
/// </summary>
public sealed class RecoveryCodeFlash(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public void Put(string userId, IReadOnlyList<string> codes) => cache.Set(Key(userId), codes, Lifetime);

    public IReadOnlyList<string>? Take(string userId)
    {
        if (!cache.TryGetValue(Key(userId), out IReadOnlyList<string>? codes))
        {
            return null;
        }

        cache.Remove(Key(userId));
        return codes;
    }

    private static string Key(string userId) => $"recovery-codes:{userId}";
}
