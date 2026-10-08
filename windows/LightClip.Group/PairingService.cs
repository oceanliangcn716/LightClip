using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightClip.Windows;

namespace LightClip;
internal sealed class PairFrame
{
    [JsonPropertyName("v")]public int V{get;set;}=2;
    [JsonPropertyName("type")]public string Type{get;set;}="";
    [JsonPropertyName("deviceId")]public string? DeviceId{get;set;}
    [JsonPropertyName("name")]public string? Name{get;set;}
    [JsonPropertyName("groupId")]public string? GroupId{get;set;}
    [JsonPropertyName("spake")]public string? Spake{get;set;}
    [JsonPropertyName("salt")]public string? Salt{get;set;}
    [JsonPropertyName("proof")]public string? Proof{get;set;}
    [JsonPropertyName("sealed")]public string? Sealed{get;set;}
}
internal static class PairWire
{
    static readonly JsonSerializerOptions Options=new(){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
    public static async Task<PairFrame> Read(NetworkStream stream,CancellationToken ct)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,ct).ConfigureAwait(false);
        uint length=BinaryPrimitives.ReadUInt32BigEndian(header);if(length is <1 or >4096)throw new InvalidDataException();
        byte[] payload=new byte[(int)length];await stream.ReadExactlyAsync(payload,ct).ConfigureAwait(false);
        _=Protocol.Utf8.GetCharCount(payload);var m=JsonSerializer.Deserialize<PairFrame>(payload,Options)??throw new InvalidDataException();
        if(m.V!=2)throw new InvalidDataException();return m;
    }
    public static async Task Write(NetworkStream stream,PairFrame message,CancellationToken ct,bool fragmented=false)
    {
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(message,Options);if(payload.Length is <1 or >4096)throw new InvalidDataException();
        byte[] frame=new byte[4+payload.Length];BinaryPrimitives.WriteUInt32BigEndian(frame,(uint)payload.Length);payload.CopyTo(frame,4);
        if(!fragmented)await stream.WriteAsync(frame,ct).ConfigureAwait(false);
        else for(int i=0;i<frame.Length;i+=3)await stream.WriteAsync(frame.AsMemory(i,Math.Min(3,frame.Length-i)),ct).ConfigureAwait(false);
    }
    public static byte[] Base64(string? text,int length)
    {
        if(text==null)throw new InvalidDataException();byte[] b=Convert.FromBase64String(text);
        if(b.Length!=length || Convert.ToBase64String(b)!=text){CryptographicOperations.ZeroMemory(b);throw new InvalidDataException();}return b;
    }
}
internal sealed class PairingService:IDisposable
{
    public const int Port=49288;
    readonly object gate=new();readonly HashSet<TcpClient> clients=[];
    readonly Guid deviceId;readonly Func<string> name;
    TcpListener? listener;CancellationTokenSource? lifetime;
    SyncGroup? group;Invitation? invitation;int active;
    public int InboundCount{get{lock(gate)return active;}}
    public bool InvitationOpen{get{lock(gate)return invitation is{} i && Environment.TickCount64<i.Expires && i.Attempts<8;}}
    public long InvitationExpiry{get{lock(gate)return invitation?.Expires??0;}}
    public event Action<string>? Status;
    public event Action? InvitationClosed;
    public event Action<Guid>? MemberJoined;
    internal bool FragmentForTests;
    sealed class Invitation(string code,long expires){public readonly string Code=code;public readonly long Expires=expires;public int Attempts;}
    public PairingService(Guid id,Func<string> name){deviceId=id;this.name=name;}
    public void Start(SyncGroup next,int port=Port,IPAddress? bind=null)
    {
        Dispose();var l=new TcpListener(bind??IPAddress.Any,port);l.Server.ExclusiveAddressUse=true;l.Start(4);
        listener=l;group=new(next.Id,next.Key.ToArray());lifetime=new();var ct=lifetime.Token;_=Task.Run(()=>Accept(l,ct));
    }
    public string OpenInvitation(string? syntheticCode=null,int lifetimeMs=120000)
    {
        lock(gate)
        {
            if(listener==null||group==null)throw new InvalidOperationException();
            string code=syntheticCode??RandomNumberGenerator.GetInt32(100000000).ToString("D8");
            if(code.Length!=8 || code.Any(c=>c<'0'||c>'9'))throw new InvalidDataException();
            invitation=new(code,Environment.TickCount64+Math.Clamp(lifetimeMs,1,120000));return code;
        }
    }
    public void CloseInvitation(){lock(gate)invitation=null;InvitationClosed?.Invoke();}
    async Task Accept(TcpListener local,CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var c=await local.AcceptTcpClientAsync(ct).ConfigureAwait(false);Invitation? current;SyncGroup? currentGroup;
                lock(gate)
                {
                    current=invitation;currentGroup=group;
                    if(current==null||currentGroup==null||Environment.TickCount64>=current.Expires||current.Attempts>=8||active>=4){c.Dispose();continue;}
                    current.Attempts++;active++;clients.Add(c);currentGroup=new(currentGroup.Id,currentGroup.Key.ToArray());
                }
                _=Host(c,current,currentGroup,ct);
            }
        }
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    bool Current(Invitation invite){lock(gate)return ReferenceEquals(invitation,invite)&&Environment.TickCount64<invite.Expires;}
    async Task Host(TcpClient client,Invitation invite,SyncGroup hostGroup,CancellationToken stop)
    {
        byte[]? secret=null,pairKey=null;using(hostGroup)using(client)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            deadline.CancelAfter(5000);var ct=deadline.Token;
            try
            {
                client.NoDelay=true;var stream=client.GetStream();var hello=await PairWire.Read(stream,ct);
                Guid other=Discovery.Uuid(hello.DeviceId);
                if(!Current(invite)||hello.Type!="pair-hello"||other==deviceId||hello.GroupId!=hostGroup.Id.ToString("D")||!GroupStore.ValidName(hello.Name??""))throw new InvalidDataException();
                byte[] a=PairWire.Base64(hello.Spake,33);using var spake=new PairingSpake(1,invite.Code,other,deviceId,hostGroup.Id);
                byte[] b=spake.Message;secret=spake.Finish(a);byte[] salt=RandomNumberGenerator.GetBytes(16);
                byte[] transcript=PairingCrypto.Transcript(other,deviceId,hostGroup.Id,a,b,salt);pairKey=PairingCrypto.DeriveKey(secret,salt,transcript);
                await PairWire.Write(stream,new(){Type="pair-challenge",DeviceId=deviceId.ToString("D"),GroupId=hostGroup.Id.ToString("D"),Spake=Convert.ToBase64String(b),Salt=Convert.ToBase64String(salt),Proof=Convert.ToBase64String(PairingCrypto.Proof(pairKey,"server-confirm",transcript))},ct,FragmentForTests);
                var proof=await PairWire.Read(stream,ct);
                if(proof.Type!="pair-proof"||!Current(invite)||!PairingCrypto.VerifyProof(PairWire.Base64(proof.Proof,32),pairKey,"client-confirm",transcript))throw new InvalidDataException();
                lock(gate)
                {if(!ReferenceEquals(invitation,invite)||Environment.TickCount64>=invite.Expires)throw new InvalidDataException();invitation=null;}
                InvitationClosed?.Invoke();
                await PairWire.Write(stream,new(){Type="pair-result",Sealed=Convert.ToBase64String(PairingCrypto.WrapGroup(hostGroup.Id,hostGroup.Key,pairKey,transcript))},ct,FragmentForTests);
                var done=await PairWire.Read(stream,ct);
                if(done.Type!="pair-done"||!PairingCrypto.VerifyProof(PairWire.Base64(done.Proof,32),pairKey,"stored",transcript))throw new InvalidDataException();
                Status?.Invoke("新设备已完成加密配对及保存确认");MemberJoined?.Invoke(other);
            }
            catch(Exception e)when(e is not OutOfMemoryException and not StackOverflowException){if(!stop.IsCancellationRequested)Status?.Invoke("配对未完成：检查配对码、有效期和网络");}
            finally{if(secret!=null)CryptographicOperations.ZeroMemory(secret);if(pairKey!=null)CryptographicOperations.ZeroMemory(pairKey);lock(gate){clients.Remove(client);active--;}}
        }
    }
    public static async Task Join(NearbyDevice target,string code,Guid deviceId,string name,Func<SyncGroup,CancellationToken,Task> persist,CancellationToken cancellation,int port=Port,bool fragmented=false)
    {
        if(target.GroupId==null || !GroupStore.ValidName(name))throw new InvalidDataException();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(5000);var ct=deadline.Token;
        byte[]? secret=null,pairKey=null;using var context=new PairingSpake(0,code,deviceId,target.Id,target.GroupId.Value);
        using var client=new TcpClient(AddressFamily.InterNetwork){NoDelay=true};
        using var cancellationRegistration=ct.Register(()=>client.Dispose());
        try
        {
            await client.ConnectAsync(target.Host,port,ct).ConfigureAwait(false);var stream=client.GetStream();byte[] a=context.Message;
            await PairWire.Write(stream,new(){Type="pair-hello",DeviceId=deviceId.ToString("D"),Name=name,GroupId=target.GroupId.Value.ToString("D"),Spake=Convert.ToBase64String(a)},ct,fragmented);
            var challenge=await PairWire.Read(stream,ct);
            if(challenge.Type!="pair-challenge"||challenge.DeviceId!=target.Id.ToString("D")||challenge.GroupId!=target.GroupId.Value.ToString("D"))throw new InvalidDataException();
            byte[] b=PairWire.Base64(challenge.Spake,33),salt=PairWire.Base64(challenge.Salt,16);
            secret=context.Finish(b);byte[] transcript=PairingCrypto.Transcript(deviceId,target.Id,target.GroupId.Value,a,b,salt);pairKey=PairingCrypto.DeriveKey(secret,salt,transcript);
            if(!PairingCrypto.VerifyProof(PairWire.Base64(challenge.Proof,32),pairKey,"server-confirm",transcript))throw new InvalidDataException();
            await PairWire.Write(stream,new(){Type="pair-proof",Proof=Convert.ToBase64String(PairingCrypto.Proof(pairKey,"client-confirm",transcript))},ct,fragmented);
            var result=await PairWire.Read(stream,ct);if(result.Type!="pair-result")throw new InvalidDataException();
            using var verified=new SyncGroup(target.GroupId.Value,PairingCrypto.UnwrapGroup(target.GroupId.Value,PairWire.Base64(result.Sealed,76),pairKey,transcript));
            ct.ThrowIfCancellationRequested();await persist(verified,ct).WaitAsync(ct).ConfigureAwait(false);
            await PairWire.Write(stream,new(){Type="pair-done",Proof=Convert.ToBase64String(PairingCrypto.Proof(pairKey,"stored",transcript))},ct,fragmented);
        }
        finally{if(secret!=null)CryptographicOperations.ZeroMemory(secret);if(pairKey!=null)CryptographicOperations.ZeroMemory(pairKey);}
    }
    public void Dispose()
    {
        lifetime?.Cancel();listener?.Stop();listener=null;lock(gate){invitation=null;foreach(var c in clients)c.Dispose();clients.Clear();group?.Dispose();group=null;}
        lifetime?.Dispose();lifetime=null;InvitationClosed?.Invoke();
    }
}
