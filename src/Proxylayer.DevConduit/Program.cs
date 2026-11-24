using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray;

// The tray, its elevated helper and the certificate commands have verbs of their own; the service lifecycle verbs and a
// start by the service control manager go to the service host; every other command line runs
// in the console as it always has.
return ServiceLaunch.Resolve(args) switch
{
    LaunchKind.Tray => TrayProgram.Run(args[1..]),
    LaunchKind.ServiceAdmin => ServiceAdminCommand.Run(args[1..]),
    LaunchKind.Export => ExportCommand.Run(args[1..]),
    LaunchKind.Import => ImportCommand.Run(args[1..]),
    LaunchKind.Certificates => CertsCommand.Run(args[1..]),
    LaunchKind.ServiceHost => DevConduitServiceHost.Run(args),
    _ => DevConduitApplication.RunConsole(args)
};
