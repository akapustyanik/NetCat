using NetCat.Engine;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate19SecurityTests
{
    [Theory]
    [InlineData("askpass password.txt")]
    [InlineData("extra-certs extra.pem")]
    [InlineData("http-proxy-user-pass auth.txt")]
    [InlineData("secret shared.key")]
    [InlineData("tmp-dir C:/outside")]
    [InlineData("setenv OPENSSL_CONF C:/outside/config")]
    [InlineData("management-external-key")]
    [InlineData("unknown-file-option C:/outside")]
    [InlineData("<unknown>\nplugin evil.dll\n</unknown>")]
    public void UnsafeExternalOrUnknownDirectiveIsRejected(string option)
    {Assert.Throws<InvalidDataException>(()=>OpenVpnConfiguration.Validate("client\ndev tun\n"+option));}
    [Fact] public void LocalDnsDirectiveCannotBypassNetCatDnsOwnership()
    {Assert.Throws<InvalidDataException>(()=>OpenVpnConfiguration.Validate("client\ndev tun\ndns server 0 address 10.1.0.53"));}
    [Fact] public void OpenVpnCannotMutateSystemDnsFromModernPush()
    {Assert.Contains("pull-filter ignore \"dns \"",OpenVpnConfiguration.Prepare("client\ndev tun\n"));}
    [Fact] public void LegacyDhcpOptionCannotMutateSystemDnsDirectly()
    {Assert.Contains("pull-filter ignore \"dhcp-option \"",OpenVpnConfiguration.Prepare("client\ndev tun\n"));}
    [Fact] public void SupportedOrdinaryOpenVpnProfileStillImports()
    {OpenVpnConfiguration.Validate("client\ndev tun\nproto udp\nremote vpn.example.test 1194\nnobind\npersist-key\npersist-tun\nremote-cert-tls server\nauth SHA256\ndata-ciphers AES-256-GCM:AES-128-GCM\n<ca>\ncertificate\n</ca>");}
}
