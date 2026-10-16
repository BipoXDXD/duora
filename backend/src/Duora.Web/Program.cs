using Duora.Web;
using Duora.Web.Admin;

// "admin ..." é o comando operacional do bootstrap administrativo (spec 11.1), rodado pelo operador no host; sem
// ele, o processo é o servidor.
if (AdminCommand.IsInvocation(args))
{
    return await AdminCommand.RunAsync(args);
}

await DuoraWebApp.Build(args).RunAsync();
return 0;
