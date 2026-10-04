using System.Net.NetworkInformation;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;

public sealed class Candidate21HyperVTests
{
    [Fact]
    public void HyperVGuestSyntheticUplinkWithDefaultGatewayIsEligible()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "Ethernet",
            description: "Microsoft Hyper-V Network Adapter",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Up,
            hasUsableIpv4: true,
            hasDefaultGateway: true,
            isHyperVGuest: true);

        Assert.True(eligible);
        Assert.False(PhysicalNetwork.IsVirtualAdapter("Ethernet", "Microsoft Hyper-V Network Adapter", isHyperVGuest: true));
    }

    [Fact]
    public void HyperVGuestSyntheticUplinkWithoutDefaultGatewayIsIneligible()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "Ethernet",
            description: "Microsoft Hyper-V Network Adapter",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Up,
            hasUsableIpv4: true,
            hasDefaultGateway: false,
            isHyperVGuest: true);

        Assert.False(eligible);
    }

    [Fact]
    public void HyperVHostVirtualAdapterRemainsIneligible()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "vEthernet (VM-Direct)",
            description: "Hyper-V Virtual Ethernet Adapter",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Up,
            hasUsableIpv4: true,
            hasDefaultGateway: true,
            isHyperVGuest: false);

        Assert.False(eligible);
        Assert.True(PhysicalNetwork.IsVirtualAdapter("vEthernet (VM-Direct)", "Hyper-V Virtual Ethernet Adapter", isHyperVGuest: false));
    }

    [Fact]
    public void HyperVGuestTunAdapterRemainsIneligible()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "NetCat-TUN",
            description: "Wintun Userspace Tunnel",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Up,
            hasUsableIpv4: true,
            hasDefaultGateway: true,
            isHyperVGuest: true);

        Assert.False(eligible);
        Assert.True(PhysicalNetwork.IsVirtualAdapter("NetCat-TUN", "Wintun Userspace Tunnel", isHyperVGuest: true));
    }

    [Fact]
    public void HyperVGuestVpnAdapterRemainsIneligible()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "OpenVPN",
            description: "TAP-Windows Adapter V9",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Up,
            hasUsableIpv4: true,
            hasDefaultGateway: true,
            isHyperVGuest: true);

        Assert.False(eligible);
        Assert.True(PhysicalNetwork.IsVirtualAdapter("OpenVPN", "TAP-Windows Adapter V9", isHyperVGuest: true));
    }

    [Fact]
    public void HyperVGuestSyntheticUplinkMustBeOperational()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "Ethernet",
            description: "Microsoft Hyper-V Network Adapter",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Down,
            hasUsableIpv4: true,
            hasDefaultGateway: true,
            isHyperVGuest: true);

        Assert.False(eligible);
    }

    [Fact]
    public void HyperVGuestSyntheticUplinkMustHaveUsableIpv4()
    {
        bool eligible = PhysicalNetwork.IsEligibleUplink(
            name: "Ethernet",
            description: "Microsoft Hyper-V Network Adapter",
            type: NetworkInterfaceType.Ethernet,
            status: OperationalStatus.Up,
            hasUsableIpv4: false,
            hasDefaultGateway: true,
            isHyperVGuest: true);

        Assert.False(eligible);
    }
}
