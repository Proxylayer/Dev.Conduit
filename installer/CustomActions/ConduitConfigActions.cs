using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using WixToolset.Dtf.WindowsInstaller;

namespace Proxylayer.DevConduit.Installer.CustomActions
{
    /// <summary>
    /// The optional <c>CONDUITCONFIG</c> property of a Workstation install: a <c>.conduit</c>
    /// file, or a legacy <c>.proxyconfig</c> exported by POC.DevProxy, whose server the install
    /// imports. <see cref="ValidateConduitConfig"/> checks it early,
    /// as the user who started the install, and sets <c>CONDUITCONFIG_ACCEPTED</c> or
    /// <c>CONDUITCONFIG_PROBLEM</c> for the launch condition and the role dialog. The import itself
    /// runs later, deferred, as LocalSystem, which cannot read the user's network drives or
    /// shares, so the file must be on a local disk. <see cref="BrowseConduitConfig"/> is the role
    /// dialog's Browse button.
    /// </summary>
    public static class ConduitConfigActions
    {
        public const string PathProperty = "CONDUITCONFIG";

        public const string AcceptedProperty = "CONDUITCONFIG_ACCEPTED";

        public const string ProblemProperty = "CONDUITCONFIG_PROBLEM";

        private const string RoleProperty = "ROLE";

        /// <summary>The extensions the import accepts: the current one, then the legacy one.</summary>
        private static readonly string[] Extensions = { ".conduit", ".proxyconfig" };

        private static readonly Regex LocalDrivePath = new Regex(@"\A[A-Za-z]:\\", RegexOptions.CultureInvariant);

        [CustomAction]
        public static ActionResult ValidateConduitConfig(Session session)
        {
            var path = Clean(session[PathProperty]);
            if (path.Length == 0)
            {
                session[AcceptedProperty] = string.Empty;
                session[ProblemProperty] = string.Empty;
                return ActionResult.Success;
            }

            var problem = Problem(path, session[RoleProperty], File.Exists, IsNetworkDrive);
            session[AcceptedProperty] = problem is null ? "1" : string.Empty;
            session[ProblemProperty] = problem ?? string.Empty;
            if (problem is null)
            {
                session[PathProperty] = path;
            }
            else
            {
                session.Log("CONDUITCONFIG refused: " + problem);
            }

            return ActionResult.Success;
        }

        /// <summary>Opens a file dialog over the installer's window and puts the chosen file in <c>CONDUITCONFIG</c>.</summary>
        [CustomAction]
        public static ActionResult BrowseConduitConfig(Session session)
        {
            var current = Clean(session[PathProperty]);
            var owner = GetForegroundWindow();
            string? chosen = null;
            Exception? failure = null;

            // A common dialog needs a single-threaded apartment, which the custom action host's thread is not.
            var thread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new OpenFileDialog())
                    {
                        dialog.Title = "Choose the .conduit file from your Dev.Conduit server";
                        dialog.Filter = "Dev.Conduit client configuration (*.conduit, *.proxyconfig)|*.conduit;*.proxyconfig|All files (*.*)|*.*";
                        dialog.CheckFileExists = true;
                        dialog.RestoreDirectory = true;
                        var folder = current.Length > 0 ? SafeDirectory(current) : null;
                        dialog.InitialDirectory = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                        if (dialog.ShowDialog(new OwnerWindow(owner)) == DialogResult.OK)
                        {
                            chosen = dialog.FileName;
                        }
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure is not null)
            {
                session.Log("The file dialog could not be shown: " + failure.Message);
            }
            else if (chosen is not null)
            {
                session[PathProperty] = chosen;
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// Why <paramref name="path"/> cannot be imported, or null. Pure apart from the two
        /// lookups passed in.
        /// </summary>
        public static string? Problem(string path, string? role, Func<string, bool> fileExists, Func<string, bool> isNetworkDrive)
        {
            if (!string.Equals(role, "Workstation", StringComparison.OrdinalIgnoreCase))
            {
                return "CONDUITCONFIG applies to a Workstation install only. Leave it out for ROLE=Server.";
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return "CONDUITCONFIG names a network location (" + path + "). The import runs as the local system account, "
                    + "which cannot read it: copy the file to a local folder and give that path.";
            }

            if (!LocalDrivePath.IsMatch(path))
            {
                return "CONDUITCONFIG must be the full path of the file, for example C:\\Users\\you\\Downloads\\server.conduit: " + path;
            }

            if (!HasAcceptedExtension(path))
            {
                return "CONDUITCONFIG must name a .conduit file (or a .proxyconfig file from POC.DevProxy): " + path;
            }

            if (isNetworkDrive(path))
            {
                return "CONDUITCONFIG is on a network drive (" + path + "). The import runs as the local system account, "
                    + "which cannot read it: copy the file to a local folder and give that path.";
            }

            if (!fileExists(path))
            {
                return "CONDUITCONFIG names a file that does not exist: " + path;
            }

            return null;
        }

        /// <summary>True when <paramref name="path"/> ends in an extension the import accepts, in any case.</summary>
        public static bool HasAcceptedExtension(string path)
        {
            foreach (var extension in Extensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The path as typed, without surrounding spaces or quotes.</summary>
        public static string Clean(string? value) => (value ?? string.Empty).Trim().Trim('"').Trim();

        private static bool IsNetworkDrive(string path)
        {
            try
            {
                return new DriveInfo(path.Substring(0, 1)).DriveType == DriveType.Network;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string? SafeDirectory(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                return directory is not null && Directory.Exists(directory) ? directory : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private sealed class OwnerWindow : IWin32Window
        {
            public OwnerWindow(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }
    }
}
