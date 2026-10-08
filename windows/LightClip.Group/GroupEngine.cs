using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LightClip;
internal sealed class GroupEngine:IDisposable
{
    readonly Form ui;readonly ReplayWindow replay=new();readonly SemaphoreSlim incoming=new(4,4);
    readonly object gate=new();readonly HashSet<TcpClient> clients=[];
    CancellationTokenSource? session;TcpListener? listener;byte[]? key;List<IPEndPoint> peers=[];
    Batch? pending,current;bool pumping;int inFlight,maxInFlight,sent,received,rejected;
    sealed class Batch(Packet packet,List<IPEndPoint> targets,CancellationToken token):IDisposable
    {
        public readonly Packet Packet=packet;public readonly List<IPEndPoint> Targets=targets;
        public readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(token);
        public readonly TaskCompletionSource<bool> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()=>Cancel.Dispose();
    }
    public bool Active=>session is{IsCancellationRequested:false};
    public int Sent=>Volatile.Read(ref sent);public int Received=>Volatile.Read(ref received);public int Rejected=>Volatile.Read(ref rejected);
    public int InboundCount=>4-incoming.CurrentCount;public int InFlight=>Volatile.Read(ref inFlight);public int MaxInFlight=>Volatile.Read(ref maxInFlight);
    public event Action<string>? Status;public event Action<string>? Evidence;public event Action<IPAddress,bool>? PeerState;
    public event Action? ClipboardWritten;
    public GroupEngine(Form ui){this.ui=ui;}
    public void Report(string s)=>Status?.Invoke(s);
    void Trace(string s)=>Evidence?.Invoke($"{DateTimeOffset.Now:HH:mm:ss.fff} {s}");
    public void UpdatePeers(IEnumerable<IPEndPoint> endpoints)
    {lock(gate)peers=endpoints.Distinct().OrderBy(x=>x.Address.ToString(),StringComparer.Ordinal).ThenBy(x=>x.Port).Take(16).ToList();}
    public void Start(byte[] secret,int port=Protocol.Port,IPAddress? bind=null)
    {
        Stop();if(secret.Length!=32)throw new InvalidDataException();
        var l=new TcpListener(bind??IPAddress.Any,port);l.Server.ExclusiveAddressUse=true;l.Start(4);
        if(!NativeClipboard.AddClipboardFormatListener(ui.Handle)){l.Stop();throw new IOException();}
        key=secret.ToArray();listener=l;session=new();var ct=session.Token;byte[] listenerKey=key.ToArray();
        _=Task.Run(()=>Accept(l,listenerKey,ct));Trace("内容启用；保留防重放窗口");Report("已启用 · 等待群组设备");
    }
    public void Stop()
    {
        if(ui.IsHandleCreated)NativeClipboard.RemoveClipboardFormatListener(ui.Handle);
        session?.Cancel();listener?.Stop();listener=null;
        lock(gate)
        {
            if(pending!=null){pending.Cancel.Cancel();pending.Done.TrySetResult(false);pending.Dispose();pending=null;}
            current?.Cancel.Cancel();foreach(var c in clients)c.Dispose();clients.Clear();peers=[];
        }
        session?.Dispose();session=null;if(key!=null){CryptographicOperations.ZeroMemory(key);key=null;}
        Trace("内容暂停；监听/连接关闭，无补发");Report("已暂停");
    }
    async Task Accept(TcpListener l,byte[] secret,CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var c=await l.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if(!incoming.Wait(0)){c.Dispose();continue;}lock(gate)clients.Add(c);_=Receive(c,secret.ToArray(),ct);
            }
        }
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
        finally{CryptographicOperations.ZeroMemory(secret);}
    }
    async Task Receive(TcpClient c,byte[] secret,CancellationToken stop)
    {
        using(c)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            deadline.CancelAfter(5000);var ct=deadline.Token;
            try
            {
                c.NoDelay=true;var stream=c.GetStream();var packet=await Protocol.ReadAsync(stream,secret,ct).ConfigureAwait(false);
                if(packet.Kind==3||!replay.Accept(packet.Id))throw new InvalidDataException();
                if(packet.Kind!=0)await WriteOnUi(packet,ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();Trace($"RX id={packet.Id} kind={packet.Kind} bytes={packet.Body.Length} {(packet.Kind==0?"ping":"clipboard-write=OK")}");
                await stream.WriteAsync(Protocol.Encode(new(packet.Id,Protocol.Now,3,[]),secret),ct).ConfigureAwait(false);
                Interlocked.Increment(ref received);Trace($"ACK-TX id={packet.Id} encrypted=OK");
                if(c.Client.RemoteEndPoint is IPEndPoint peer)PeerState?.Invoke(peer.Address,true);
                Report(packet.Kind==0?"群组设备已连接 · 已回复加密 ACK":"已接收并写入剪贴板");
            }
            catch(Exception e)when(e is not OutOfMemoryException and not StackOverflowException)
            {Interlocked.Increment(ref rejected);Trace("RX 拒绝或取消：鉴权/格式/重放/截止时间/剪贴板未通过");}
            finally{lock(gate)clients.Remove(c);incoming.Release();CryptographicOperations.ZeroMemory(secret);}
        }
    }
    Task WriteOnUi(Packet packet,CancellationToken ct)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Work()
        {
            try
            {
                for(int i=0;i<5;i++){ct.ThrowIfCancellationRequested();if(!Active)throw new OperationCanceledException();if(NativeClipboard.Write(ui.Handle,packet,cancellation:ct)){ClipboardWritten?.Invoke();done.TrySetResult();return;}await Task.Delay(25*(i+1),ct);}
                throw new IOException();
            }
            catch(Exception e){done.TrySetException(e);}
        }
        try{ui.BeginInvoke((Action)Work);}catch(Exception e){done.TrySetException(e);}return done.Task.WaitAsync(ct);
    }
    public Task<bool> Send(Packet packet)
    {
        lock(gate)
        {
            if(!Active)return Task.FromResult(false);
            current?.Cancel.Cancel();
            if(pending!=null){pending.Cancel.Cancel();pending.Done.TrySetResult(false);pending.Dispose();}
            pending=new(packet,peers.ToList(),session!.Token);var result=pending.Done.Task;
            if(!pumping){pumping=true;_=Task.Run(Pump);}return result;
        }
    }
    async Task Pump()
    {
        while(true)
        {
            Batch b;byte[] secret;
            lock(gate){if(pending==null||key==null){pumping=false;return;}b=pending;pending=null;current=b;secret=key.ToArray();}
            int successes=0,index=-1;
            try
            {
                if(b.Targets.Count==0)Report("群组已保存 · 等待其他设备上线");
                else
                {
                    async Task Worker()
                    {
                        while(!b.Cancel.IsCancellationRequested)
                        {
                            int i=Interlocked.Increment(ref index);if(i>=b.Targets.Count)return;
                            if(await SendOne(b.Packet,b.Targets[i],secret,b.Cancel.Token).ConfigureAwait(false))Interlocked.Increment(ref successes);
                        }
                    }
                    await Task.WhenAll(Enumerable.Range(0,Math.Min(4,b.Targets.Count)).Select(_=>Worker())).ConfigureAwait(false);
                    if(!b.Cancel.IsCancellationRequested)Report(successes==b.Targets.Count?$"{(b.Packet.Kind==0?"群组连接测试":"发送")}成功 · 已验证 {successes} 台加密 ACK":$"已送达 {successes}/{b.Targets.Count} 台 · 无离线补发");
                }
                b.Done.TrySetResult(!b.Cancel.IsCancellationRequested && successes>0);
            }
            catch(Exception e)when(e is not OutOfMemoryException and not StackOverflowException){b.Done.TrySetResult(false);}
            finally{CryptographicOperations.ZeroMemory(secret);lock(gate){if(ReferenceEquals(current,b))current=null;}b.Dispose();}
        }
    }
    public void CancelSending(){lock(gate){current?.Cancel.Cancel();if(pending!=null){pending.Cancel.Cancel();pending.Done.TrySetResult(false);pending.Dispose();pending=null;}}}
    async Task<bool> SendOne(Packet packet,IPEndPoint target,byte[] secret,CancellationToken stop)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(5000);var ct=deadline.Token;
        using var c=new TcpClient(AddressFamily.InterNetwork){NoDelay=true};using var cancel=ct.Register(()=>c.Dispose());
        lock(gate)clients.Add(c);int concurrent=Interlocked.Increment(ref inFlight);
        int old;do{old=maxInFlight;}while(concurrent>old&&Interlocked.CompareExchange(ref maxInFlight,concurrent,old)!=old);
        try
        {
            ct.ThrowIfCancellationRequested();await c.ConnectAsync(target.Address,target.Port,ct).ConfigureAwait(false);var stream=c.GetStream();
            byte[] frame=Protocol.Encode(packet,secret);await stream.WriteAsync(frame,ct).ConfigureAwait(false);
            var ack=await Protocol.ReadAsync(stream,secret,ct).ConfigureAwait(false);Protocol.ValidateAck(packet,ack);
            Interlocked.Increment(ref sent);Trace($"ACK-RX id={packet.Id} kind={packet.Kind} bytes={packet.Body.Length} encrypted=OK uuid=OK");PeerState?.Invoke(target.Address,true);return true;
        }
        catch(Exception e)when(e is not OutOfMemoryException and not StackOverflowException){PeerState?.Invoke(target.Address,false);Trace($"TX 未送达/取消 id={packet.Id}，无补发");return false;}
        finally{lock(gate)clients.Remove(c);Interlocked.Decrement(ref inFlight);}
    }
    public void Dispose()=>Stop();
}
