using System.Text.RegularExpressions;
using WixToolset.Dtf.WindowsInstaller;

namespace Proxylayer.DevConduit.Installer.CustomActions
{
    /// <summary>
    /// Checks the <c>ENVIRONMENT</c> property against the rule the app's elevated helper applies
    /// (<c>ServiceAdminCommand.IsAcceptedEnvironmentName</c>): letters, digits, dot, dash and
    /// underscore, at most 64 characters. The result goes to <c>ENVIRONMENT_ACCEPTED</c>, which
    /// the launch condition and the role dialog test.
    /// </summary>
    public static class EnvironmentNameActions
    {
        public const string EnvironmentProperty = "ENVIRONMENT";

        public const string AcceptedProperty = "ENVIRONMENT_ACCEPTED";

        private const int MaxLength = 64;

        private static readonly Regex EnvironmentName =
            new(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant);

        [CustomAction]
        public static ActionResult ValidateEnvironment(Session session)
        {
            session[AcceptedProperty] = IsAccepted(session[EnvironmentProperty]) ? "1" : string.Empty;
            return ActionResult.Success;
        }

        public static bool IsAccepted(string? name) =>
            !string.IsNullOrEmpty(name) && name!.Length <= MaxLength && EnvironmentName.IsMatch(name);
    }
}
