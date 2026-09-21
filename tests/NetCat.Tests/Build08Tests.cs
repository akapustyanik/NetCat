using System.Text.Json.Nodes;
using NetCat.Core;
using NetCat.Engine;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Build08Tests
{
    [Fact]
    public void FailoverRequiresConsecutiveFailuresAndStableCandidate()
    {
        var p=new FailoverPolicy(); var active=Guid.NewGuid(); var other=Guid.NewGuid(); var now=DateTimeOffset.UtcNow; var age=TimeSpan.FromMinutes(5);
        p.Record(active,new(false,-1),now); Assert.False(p.ShouldRecover(active,2,now,age));
        p.Record(active,new(true,1500),now); Assert.False(p.ShouldRecover(active,2,now,age));
        p.Record(active,new(false,-1),now); Assert.False(p.ShouldRecover(active,2,now,age));
        p.Record(active,new(false,-1),now); Assert.True(p.ShouldRecover(active,2,now,age));
        p.Record(other,new(true,100),now); Assert.Null(p.Best([other],now,age));
        p.Record(other,new(false,-1),now); Assert.Null(p.Best([other],now,age));
        p.Record(other,new(true,120),now); Assert.Null(p.Best([other],now,age));
        p.Record(other,new(true,110),now); Assert.Equal(other,p.Best([other],now,age));
        p.Switched(now); p.Record(active,new(false,-1),now); p.Record(active,new(false,-1),now);
        Assert.False(p.ShouldRecover(active,2,now.AddSeconds(60),age));
        Assert.True(p.ShouldRecover(active,2,now.AddMinutes(3),age));
        Assert.Null(p.Best([other],now.AddMinutes(6),age));
    }
    [Theory]
    [InlineData("TLS handshake failed","Ошибка защищённого соединения (TLS)")]
    [InlineData("DNS lookup failed","Не удалось определить адрес сервера")]
    [InlineData("timeout","Сервер не ответил вовремя")]
    [InlineData("Connection refused","Сервер отклонил соединение")]
    public void ErrorSummaryKeepsDetailsSeparate(string error,string expected)
    {
        var p=new Profile(); p.SetTestResult(new(false,-1,error));
        Assert.Equal(expected,p.Result); Assert.Contains(error,p.ResultDetails);
    }
    [Fact]
    public void ZapretHistoryRestoresOnlySelectedScenario()
    {
        var a=new AppSettings {YouTube=ServiceRoute.Zapret,Discord=ServiceRoute.Vpn};
        a.ZapretResults.Add(new("general.bat",a.Scenario,DateTimeOffset.UtcNow,"Доступен","Через VPN",true,1,50,"YouTube test details"));
        a.Discord=ServiceRoute.Zapret;
        a.ZapretResults.Add(new("general.bat",a.Scenario,DateTimeOffset.UtcNow,"Доступен","Таймаут",false,1,50,"Both services test details"));
        a=JsonSettings.Clone(a);
        var strategy=new StrategyResult {File="general.bat"}; strategy.RestoreHistory(a);
        Assert.False(strategy.Passed); Assert.Equal("Таймаут",strategy.Discord);
        a.Discord=ServiceRoute.Vpn; strategy.RestoreHistory(a); Assert.True(strategy.Passed);
        Assert.Contains("Both services",strategy.Details); Assert.Contains("YouTube test",strategy.Details);
    }
    [Fact]
    public async Task HealthRuleIsLimitedToTunProbeAndAcceptedByOfficialCore()
    {
        var profile=ProfileImporter.ParseLink("socks://127.0.0.1:19900");
        var config=SingBoxConfig.Build(new AppSettings(),new("Ethernet",1,"192.168.1.10","192.168.1.1",[]),profile,null,true,healthSourcePort:19901);
        var rules=config["route"]!["rules"]!.AsArray(); var check=rules.First(n=>n?["source_port"]!=null)!;
        Assert.Equal("tun",check["inbound"]![0]!.ToString()); Assert.Equal("vpn",check["outbound"]!.ToString());
        Assert.Equal("172.29.255.1/32",check["source_ip_cidr"]![0]!.ToString());
        Assert.True(rules.IndexOf(check)<rules.IndexOf(rules.First(n=>n?["process_name"]?.ToJsonString().Contains("NetCat.exe")==true)));
        var file=Path.Combine(RoutingTests.FindRoot(),"artifacts/validation/health08.json"); await File.WriteAllTextAsync(file,config.ToJsonString());
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result=await ProcessHost.RunAsync(Path.Combine(RoutingTests.FindRoot(),"bin/sing-box/sing-box.exe"),["check","-c",file],ct.Token);
        Assert.True(result.Code==0,result.Output);
    }
}
