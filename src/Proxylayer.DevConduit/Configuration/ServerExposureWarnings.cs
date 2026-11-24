namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The two ways a server is left wide open by default, said the same way in the startup log and
/// on the setup window's Server page: no allowed users or groups, so anyone the tenant signs in
/// may use it, and no allowed targets, so it calls any host it can reach. Blank list entries
/// count for nothing, as every reader of those lists ignores them. Pure.
/// </summary>
public static class ServerExposureWarnings
{
    public const string AnyUser = "Every user in the tenant can use this server. Add allowed users or groups to restrict.";

    public const string AnyTarget = "Any reachable host is allowed. Add allowed targets to restrict.";

    /// <summary>True when neither <paramref name="allowedUsers"/> nor <paramref name="allowedGroups"/> names anyone.</summary>
    public static bool IsOpenToEveryUser(IEnumerable<string?> allowedUsers, IEnumerable<string?> allowedGroups) =>
        !HasEntries(allowedUsers) && !HasEntries(allowedGroups);

    /// <summary>True when <paramref name="allowedTargets"/> names nothing, so every host not blocked may be called.</summary>
    public static bool IsOpenToEveryTarget(IEnumerable<string?> allowedTargets) => !HasEntries(allowedTargets);

    /// <summary>The warnings that apply, in the order the Server page shows them.</summary>
    public static IReadOnlyList<string> For(
        IEnumerable<string?> allowedUsers,
        IEnumerable<string?> allowedGroups,
        IEnumerable<string?> allowedTargets)
    {
        var warnings = new List<string>();
        if (IsOpenToEveryUser(allowedUsers, allowedGroups))
        {
            warnings.Add(AnyUser);
        }

        if (IsOpenToEveryTarget(allowedTargets))
        {
            warnings.Add(AnyTarget);
        }

        return warnings;
    }

    private static bool HasEntries(IEnumerable<string?> values) => values.Any(value => !string.IsNullOrWhiteSpace(value));
}
