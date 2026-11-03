using System.Net;
using System.Net.Sockets;

namespace StockSentinel;

public static class NetworkSafety
{
    public static async Task<bool> IsPublicHost(Uri uri,CancellationToken ct)
    {
        if(uri.HostNameType!=UriHostNameType.Dns)return false;
        var addresses=await Dns.GetHostAddressesAsync(uri.DnsSafeHost,ct);
        return addresses.Length>0&&addresses.All(IsPublicAddress);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        var b=address.GetAddressBytes();
        if(address.AddressFamily==AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0]<224 &&
                !(b[0]==100&&b[1]>=64&&b[1]<=127) && !(b[0]==169&&b[1]==254) &&
                !(b[0]==172&&b[1]>=16&&b[1]<=31) && !(b[0]==192&&(b[1]==168 || b[1]==0&&(b[2]==0||b[2]==2) || b[1]==88&&b[2]==99)) &&
                !(b[0]==198&&(b[1] is 18 or 19 || b[1]==51&&b[2]==100)) && !(b[0]==203&&b[1]==0&&b[2]==113);
        // Global unicast only; exclude special-purpose, documentation and transition
        // ranges that can embed private IPv4 destinations (including NAT64/6to4).
        return address.AddressFamily==AddressFamily.InterNetworkV6&&address.ScopeId==0&&(b[0]&0xe0)==0x20 &&
            !(b[0]==0x20&&b[1]==0x01&&(b[2]<2 || b[2]==0x0d&&b[3]==0xb8)) &&
            !(b[0]==0x20&&b[1]==0x02) && !(b[0]==0x3f&&b[1]==0xff&&(b[2]&0xf0)==0);
    }

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect=false,
        UseCookies=false,
        UseProxy=false,
        ConnectCallback=(context,ct)=>ConnectPublicAsync(context.DnsEndPoint,ct)
    };

    public static async ValueTask<Stream> ConnectPublicAsync(DnsEndPoint endpoint,CancellationToken ct,
        Func<string,CancellationToken,Task<IPAddress[]>>? resolve=null,
        Func<IPEndPoint,CancellationToken,ValueTask<Stream>>? connect=null)
    {
        var addresses=await (resolve ?? Dns.GetHostAddressesAsync)(endpoint.Host,ct);
        if(addresses.Length==0||addresses.Any(ip=>!IsPublicAddress(ip)))
            throw new HttpRequestException("Destination must resolve exclusively to public internet addresses");
        Exception? last=null;
        foreach(var address in addresses)
        {
            ct.ThrowIfCancellationRequested();
            try {return await (connect ?? ConnectSocketAsync)(new IPEndPoint(address,endpoint.Port),ct);}
            catch(SocketException ex){last=ex;}
        }
        throw new HttpRequestException("Could not connect to the public destination",last);
    }
    static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint,CancellationToken ct)
    {
        var socket=new Socket(endpoint.AddressFamily,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
        try {await socket.ConnectAsync(endpoint,ct);return new NetworkStream(socket,ownsSocket:true);}
        catch {socket.Dispose();throw;}
        // SocketsHttpHandler performs normal TLS/SNI validation using the original hostname.
    }
}
