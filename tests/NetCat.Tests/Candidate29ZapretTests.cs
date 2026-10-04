using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate29ZapretTests
{
    [Fact] public void BaselineCannotBeComparedAcrossPhysicalPathChange()
    {
        var before=new NetCat.Core.NetworkSnapshot("physical",9,"192.0.2.2","192.0.2.53",[],DefaultRoute:"192.0.2.1");
        Assert.True(ZapretProbes.SamePhysicalPath(before,before with {}));
        Assert.False(ZapretProbes.SamePhysicalPath(before,before with {Index=10}));
        Assert.False(ZapretProbes.SamePhysicalPath(before,before with {Address="192.0.2.3"}));
        Assert.False(ZapretProbes.SamePhysicalPath(before,before with {DefaultRoute="192.0.2.5"}));
        Assert.False(ZapretProbes.SamePhysicalPath(before,null));
    }
    [Fact] public void PhysicalProbeConfigCannotUseMainVpnEvenInGlobalMode()
    {
        var config=ZapretProbes.BuildPhysicalConfig(new(){Mode=NetCat.Core.RoutingMode.Global},new("physical",9,"192.0.2.2","192.0.2.53",[]),12345);
        Assert.Equal("direct",config["route"]!["final"]!.ToString());
        var outbound=config["outbounds"]!.AsArray().Single(o=>o!["tag"]!.ToString()=="direct")!;
        Assert.Equal("physical",outbound["bind_interface"]!.ToString());
        Assert.Empty(config["route"]!["rules"]!.AsArray());
    }
    private static ProbeOutcome Row(string service, string name, bool ok) => new(service, name, ok, 12, "controlled");
    [Fact] public void FailedOptionalEndpointDoesNotRejectHealthyServiceGroups()
    {
        var baseline = new[] { Row("YouTube", "HTTPS", false), Row("Discord", "HTTPS", false) };
        var enabled = new[] { Row("YouTube", "HTTPS", true), Row("YouTube", "HTTPS · Player API", false), Row("Discord", "HTTPS", true), Row("Discord", "WebSocket · Hello", false) };
        var result = ZapretStrategyValidation.Evaluate(baseline, enabled, ["YouTube", "Discord"]);
        Assert.True(result.Accepted); Assert.Equal(2, result.ImprovedTargets);
    }
    [Fact] public void SuccessInOneServiceCannotHideUnavailableOtherService()
    {
        var result = ZapretStrategyValidation.Evaluate([], [Row("YouTube", "HTTPS", true), Row("Discord", "HTTPS", false)], ["YouTube", "Discord"]);
        Assert.False(result.Accepted);
    }
    [Fact] public void HealthyBaselineDoesNotClaimConfirmedBypass()
    {
        var rows = new[] { Row("YouTube", "HTTPS", true) };
        Assert.Equal("available-bypass-unconfirmed", ZapretStrategyValidation.Evaluate(rows, rows, ["YouTube"]).Reason);
    }
    [Fact] public void EmptyOrWebsocketOnlyEvidenceCannotAcceptStrategy()
    {
        Assert.False(ZapretStrategyValidation.Evaluate([], [], []).Accepted);
        Assert.False(ZapretStrategyValidation.Evaluate([], [Row("Discord", "WebSocket · Hello", true)], ["Discord"]).Accepted);
    }
    [Fact] public void DiagnosticNeverIncludesCustomTargetOrExceptionDetail()
    {
        var line = ZapretStrategyValidation.Diagnostic("s1", 7, null, new("secret-host", "private-url", false, 20, "token=password"));
        Assert.DoesNotContain("secret-host", line); Assert.DoesNotContain("private-url", line); Assert.DoesNotContain("password", line);
        Assert.Contains("dns=unknown", line); Assert.Contains("ifIndex=7", line);
    }
}
