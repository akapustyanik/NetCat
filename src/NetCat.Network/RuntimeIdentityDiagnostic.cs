using System.Security.Cryptography;
using System.Text;
using NetCat.Core;

namespace NetCat.Network;

public static class RuntimeIdentityDiagnostic
{
    public static string Alias(Guid? id)=>!id.HasValue||id.Value==Guid.Empty ? "none" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.Value.ToString("N"))))[..12];
    public static string Owner(OpenVpnLink? link)=>$"controlRevision={link?.Generation??0} routeOwner={Alias(link?.RouteOwnerId)} profileAlias={Alias(link?.ProfileId)}";
}
