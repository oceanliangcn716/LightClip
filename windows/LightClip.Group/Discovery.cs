using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LightClip;
internal sealed record NearbyDevice(Guid Id,string Name,IPAddress Host,Guid? GroupId,long LastSeen);
internal sealed class Discovery:IDisposable
{
    public const int Port=49286;
    readonly Guid id;
    readonly Func<(string Name,Guid? Group)> profile;
    readonly Dictionary<Guid,NearbyDevice> nearby=[];
    readonly object gate=new();
    readonly int port;
    Socket? socket;CancellationTokenSource? lifetime;
    long lastResponse=-1000;
    public event Action<IReadOnlyList<NearbyDevice>>? Changed;
    public event Action<string>? Error;
    public Discovery(Guid id,Func<(string,Guid?)> profile,int port=Port){this.id=id;this.profile=profile;this.port=port;}
    public IReadOnlyList<NearbyDevice> Devices{get{lock(gate)return Sorted();}}
    List<NearbyDevice> Sorted()=>nearby.Values.OrderBy(x=>x.Name,StringComparer.Ordinal).ThenBy(x=>x.Id.ToString("D"),StringComparer.Ordinal).ToList();
    public static Guid Uuid(string? text)
    {if(text==null||!Guid.TryParseExact(text,"D",out var value)||value.ToString("D")!=text)throw new InvalidDataException();return value;}
    public byte[] Announcement()
    {
        var p=profile();if(!GroupStore.ValidName(p.Name))throw new InvalidDataException();
        var fields=new Dictionary<string,object>{{"v",2},{"type","lightclip-discovery"},{"deviceId",id.ToString("D")},{"name",p.Name}};
        if(p.Group!=null)fields.Add("groupId",p.Group.Value.ToString("D"));
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var addresses=new HashSet<IPAddress>();
        foreach(var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if(n.OperationalStatus!=OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)continue;
            foreach(var a in n.GetIPProperties().UnicastAddresses)
            {
                if(a.Address.AddressFamily!=AddressFamily.InterNetwork || a.PrefixLength is <=0 or >=31)continue;
                byte[] ip=a.Address.GetAddressBytes(),mask=a.IPv4Mask.GetAddressBytes();if(ip[0] is 0 or 127 || ip[0]>=224)continue;
                addresses.Add(new IPAddress(ip.Zip(mask,(b,m)=>(byte)(b|~m)).ToArray()));
            }
        }
        return addresses.ToList();
    }
    public void Start()
    {
        Dispose();var s=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp){EnableBroadcast=true,ExclusiveAddressUse=false};
        s.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
        try{s.Bind(new IPEndPoint(IPAddress.Any,port));}catch{s.Dispose();throw;}
        socket=s;lifetime=new();var ct=lifetime.Token;_=Task.Run(()=>Read(s,ct));_=Task.Run(()=>Heartbeat(ct));Announce();
    }
    public void Announce(){var s=socket;if(s==null)return;try{byte[] b=Announcement();foreach(var address in BroadcastAddresses()){try{s.SendTo(b,new IPEndPoint(address,port));}catch(SocketException){}}}catch{Error?.Invoke("局域网公告暂不可用");}}
    async Task Heartbeat(CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested){await Task.Delay(5000,ct);Expire(Environment.TickCount64);Announce();}}
        catch(OperationCanceledException){}
    }
    async Task Read(Socket s,CancellationToken ct)
    {
        byte[] buffer=new byte[1025];int count=0;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try{result=await s.ReceiveFromAsync(buffer,SocketFlags.None,new IPEndPoint(IPAddress.Any,0),ct);}
                catch(SocketException e)when(e.SocketErrorCode==SocketError.MessageSize){if(++count==64){count=0;await Task.Yield();}continue;}
                ct.ThrowIfCancellationRequested();
                if(result.ReceivedBytes<=1024 && result.RemoteEndPoint is IPEndPoint source && Process(buffer.AsSpan(0,result.ReceivedBytes),source.Address,Environment.TickCount64))
                {
                    long now=Environment.TickCount64;if(now-lastResponse>=1000){lastResponse=now;try{await s.SendToAsync(Announcement(),SocketFlags.None,new IPEndPoint(source.Address,port),ct);}catch(SocketException){}}
                }
                if(++count==64){count=0;await Task.Yield();}
            }
        }
        catch(Exception e)when(e is SocketException or ObjectDisposedException or OperationCanceledException){if(!ct.IsCancellationRequested)Error?.Invoke("局域网发现已停止，请重新打开轻剪");}
    }
    internal bool Process(ReadOnlySpan<byte> data,IPAddress source,long now)
    {
        try
        {
            if(data.Length is <1 or >1024 || source.AddressFamily!=AddressFamily.InterNetwork) return false;
            byte first=source.GetAddressBytes()[0];if(first is 0 or 127 || first>=224)return false;
            _=Protocol.Utf8.GetCharCount(data);using var json=JsonDocument.Parse(data.ToArray());var o=json.RootElement;
            if(o.GetProperty("v").GetInt32()!=2 || o.GetProperty("type").GetString()!="lightclip-discovery")return false;
            Guid other=Uuid(o.GetProperty("deviceId").GetString());if(other==id)return false;
            string name=o.GetProperty("name").GetString()??"";if(!GroupStore.ValidName(name))return false;
            Guid? group=o.TryGetProperty("groupId",out var g)?Uuid(g.GetString()):null;
            bool changed,newAddress;
            lock(gate)
            {
                if(!nearby.TryGetValue(other,out var old) && nearby.Count>=64)return false;
                newAddress=old==null || !old.Host.Equals(source);changed=newAddress || old?.Name!=name || old.GroupId!=group;
                nearby[other]=new(other,name,source,group,now);
            }
            if(changed)Changed?.Invoke(Devices);return newAddress;
        }
        catch(Exception e)when(e is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or System.Text.DecoderFallbackException){return false;}
    }
    internal void Expire(long now)
    {
        bool changed=false;lock(gate){foreach(var expired in nearby.Where(x=>now-x.Value.LastSeen>=20000).Select(x=>x.Key).ToArray()){nearby.Remove(expired);changed=true;}}
        if(changed)Changed?.Invoke(Devices);
    }
    public void Dispose(){lifetime?.Cancel();socket?.Dispose();socket=null;lifetime?.Dispose();lifetime=null;lock(gate)nearby.Clear();}
}
