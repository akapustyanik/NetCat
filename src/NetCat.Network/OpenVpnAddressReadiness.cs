using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetCat.Core;

namespace NetCat.Network;

public enum OpenVpnAddressState { Missing, Tentative, Preferred, Duplicate, Invalid, Deprecated }
public sealed record OpenVpnAddressObservation(int InterfaceIndex,IPAddress Address,DuplicateAddressDetectionState State);

public static class OpenVpnAddressReadiness
{
    public static readonly TimeSpan DefaultTimeout=TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PollInterval=TimeSpan.FromMilliseconds(250);

    public static OpenVpnAddressState Select(int index,IPAddress address,IEnumerable<OpenVpnAddressObservation> observations)
    {
        var match=observations.FirstOrDefault(a=>a.InterfaceIndex==index && a.Address.AddressFamily==AddressFamily.InterNetwork && a.Address.Equals(address));
        return match?.State switch
        {
            null=>OpenVpnAddressState.Missing,
            DuplicateAddressDetectionState.Tentative=>OpenVpnAddressState.Tentative,
            DuplicateAddressDetectionState.Preferred=>OpenVpnAddressState.Preferred,
            DuplicateAddressDetectionState.Duplicate=>OpenVpnAddressState.Duplicate,
            DuplicateAddressDetectionState.Deprecated=>OpenVpnAddressState.Deprecated,
            _=>OpenVpnAddressState.Invalid
        };
    }
    public static OpenVpnAddressState Capture(OpenVpnLink link)
    {
        if(link.Index<=0 || !IPAddress.TryParse(link.Address,out var address) || address.AddressFamily!=AddressFamily.InterNetwork)
            return OpenVpnAddressState.Invalid;
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if(!adapter.Supports(NetworkInterfaceComponent.IPv4))continue;
                var properties=adapter.GetIPProperties();var index=properties.GetIPv4Properties().Index;
                if(index!=link.Index)continue;
                return Select(index,address,properties.UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork)
                    .Select(a=>new OpenVpnAddressObservation(index,a.Address,a.DuplicateAddressDetectionState)));
            }
            catch(NetworkInformationException){/* Adapter may disappear between enumeration and capture. */}
        }
        return OpenVpnAddressState.Missing;
    }

    public static async Task WaitAsync(OpenVpnLink link,Func<bool> current,CancellationToken ct,TimeSpan timeout,
        Func<OpenVpnLink,OpenVpnAddressState>? capture=null,TimeProvider? time=null,
        Func<TimeSpan,CancellationToken,Task>? delay=null,Action<OpenVpnAddressState>? changed=null)
    {
        if(timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
        capture??=Capture;time??=TimeProvider.System;delay??=(duration,token)=>Task.Delay(duration,time,token);
        var started=time.GetTimestamp();OpenVpnAddressState? last=null;
        void Check(){ct.ThrowIfCancellationRequested();if(!current())throw new OperationCanceledException("Obsolete OpenVPN address readiness.");}
        while(true)
        {
            Check();var state=capture(link);Check();
            if(state!=last){changed?.Invoke(state);last=state;}
            switch(state)
            {
                case OpenVpnAddressState.Preferred: Check();return;
                case OpenVpnAddressState.Duplicate:
                case OpenVpnAddressState.Invalid:
                case OpenVpnAddressState.Deprecated:
                    throw new IOException("OpenVPN local address is unusable: "+state);
            }
            var remaining=timeout-time.GetElapsedTime(started);
            if(remaining<=TimeSpan.Zero)throw new TimeoutException("OpenVPN local address readiness timed out.");
            await delay(remaining<PollInterval?remaining:PollInterval,ct).WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
