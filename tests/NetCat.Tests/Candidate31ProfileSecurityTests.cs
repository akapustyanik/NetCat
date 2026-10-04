using NetCat.Core;
using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate31ProfileSecurityTests
{
    private const string Client = "client\ndev tun\nremote vpn.example 1194\n";
    [Theory]
    [InlineData("")]
    [InlineData("# remote-cert-tls server")]
    [InlineData("remote-cert-tls client")]
    [InlineData("remote-cert-tls server\nremote-cert-tls client")]
    [InlineData("verify-x509-name \"\" name-prefix")]
    [InlineData("peer-fingerprint invalid")]
    [InlineData("<ca>\nremote-cert-tls server\n</ca>")]
    public void NewOpenVpnImportRequiresServerAuthentication(string extra)
    {
        var result=ProfileImporter.ParseForImport(Client+extra);
        Assert.Empty(result.Profiles); Assert.NotEmpty(result.Errors);
    }
    [Theory]
    [InlineData("remote-cert-tls server")]
    [InlineData("verify-x509-name vpn.example name")]
    [InlineData("verify-x509-name \"CN=vpn.example\"")]
    [InlineData("peer-fingerprint 00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F")]
    public void ExplicitServerAuthenticationAllowsNewImport(string extra)
    {var result=ProfileImporter.ParseForImport(Client+extra);Assert.Empty(result.Errors);Assert.Single(result.Profiles);}
    [Fact] public void ExistingLegacyProfileRemainsUnmodifiedAndShowsMigrationWarning()
    {
        var p=new Profile{Protocol="openvpn",OpenVpnConfig=Client};
        Assert.Contains("миграция",p.SecurityWarning); Assert.Null(OpenVpnConfiguration.MigrationDiagnostic(p));
        OpenVpnConfiguration.Validate(p.OpenVpnConfig); Assert.Equal(Client,p.OpenVpnConfig);
    }
    [Fact] public void ServerAuthenticationDoesNotAuthorizeTlsVerifyScript()
    {Assert.Throws<InvalidDataException>(()=>OpenVpnConfiguration.ValidateNewImport(Client+"remote-cert-tls server\ntls-verify malicious.cmd"));}
    [Theory]
    [InlineData("{\"tls\":{\"insecure\":true}}")]
    [InlineData("{\"streamSettings\":{\"tlsSettings\":{\"allowInsecure\":true}}}")]
    [InlineData("{\"tls\":{\"skip-cert-verify\":true}}")]
    public void InsecureCertificateValidationIsVisible(string json)
    {var p=new Profile{OutboundJson=json};Assert.True(ProfileSecurity.CertificateValidationDisabled(p));Assert.Contains("TLS",p.SecurityWarning);}
    [Fact] public void SecureSubscriptionCannotSilentlyAddInsecureReplacement()
    {
        var subscription=new Subscription();
        var old=new Profile{SubscriptionId=subscription.Id,Name="Example",Host="vpn.example",OutboundJson="{\"type\":\"trojan\",\"tls\":{\"enabled\":true}}"};
        var settings=new AppSettings{Subscriptions=[subscription],Profiles=[old]};
        var incoming=JsonSettings.Clone(old);incoming.OutboundJson="{\"type\":\"trojan\",\"tls\":{\"enabled\":true,\"insecure\":true}}";
        Assert.Throws<SubscriptionSecurityApprovalRequiredException>(()=>SubscriptionMerge.Prepare(settings,subscription.Id,[incoming]));
        Assert.False(ProfileSecurity.CertificateValidationDisabled(settings.Profiles[0]));Assert.Null(subscription.UpdatedAt);
        var approved=SubscriptionMerge.Prepare(settings,subscription.Id,[incoming],approveSecurityChanges:true);
        Assert.Contains(approved.Profiles,ProfileSecurity.CertificateValidationDisabled);
        Assert.False(ProfileSecurity.CertificateValidationDisabled(settings.Profiles[0]));
    }
}
