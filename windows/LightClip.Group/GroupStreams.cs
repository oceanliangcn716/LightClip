using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using LightClip.Windows;

namespace LightClip;
internal sealed class GroupStreams:IDisposable
{
    readonly GroupHost host;readonly object gate=new();readonly HashSet<TcpClient> clients=[];
    readonly Dictionary<Guid,CancellationTokenSource> receivers=[];List<IPEndPoint> peers=[];
    readonly SemaphoreSlim inbound=new(2,2);CancellationTokenSource? session,outgoing;
    TcpListener? listener;byte[]? key;int outgoingCount,maxOutgoing;bool pumping;
    Work? pending;sealed record Work(ClipboardContent Content,TaskCompletionSource<bool> Done,CancellationTokenSource Cancel);
    public string CacheRoot{get;set;}=Path.Combine(Store.Root,"ReceivedFiles");
    public bool Active=>session is{IsCancellationRequested:false};public int Inbound=>2-inbound.CurrentCount;
    public int Outbound=>Volatile.Read(ref outgoingCount);public int MaxOutbound=>maxOutgoing;
    public GroupStreams(GroupHost host){this.host=host;}
    public void UpdatePeers(IEnumerable<IPEndPoint> endpoints){lock(gate)peers=endpoints.Distinct().Take(16).ToList();}
    public void Start(byte[] groupKey,int port=StreamProtocol.Port,IPAddress? bind=null)
    {
        Stop();PrepareCache();StreamTransfer.CleanupAbandonedTransfers(CacheRoot);
        var l=new TcpListener(bind??IPAddress.Any,port);l.Server.ExclusiveAddressUse=true;l.Start(2);
        key=groupKey.ToArray();session=new();listener=l;byte[] listenerKey=key.ToArray();var ct=session.Token;
        _=Task.Run(()=>Accept(l,listenerKey,ct));
    }
    void PrepareCache()
    {
        string root=Path.GetFullPath(CacheRoot);Directory.CreateDirectory(root);
        for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        var user=WindowsIdentity.GetCurrent().User??throw new IOException();
        acl.AddAccessRule(new(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
    }
    async Task Accept(TcpListener l,byte[] secret,CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested){var c=await l.AcceptTcpClientAsync(ct);if(!inbound.Wait(0)){c.Dispose();continue;}lock(gate)clients.Add(c);uint version=NativeClipboard.GetClipboardSequenceNumber();_=Receive(c,secret.ToArray(),version,ct);}}
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
        finally{CryptographicOperations.ZeroMemory(secret);}
    }
    async Task Receive(TcpClient c,byte[] secret,uint version,CancellationToken stop)
    {
        Guid localId=Guid.NewGuid();using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock(gate)receivers.Add(localId,cancel);using var registration=cancel.Token.Register(()=>c.Dispose());
        try
        {
            c.NoDelay=true;host.Ui(()=>host.Report("正在接收文件或长文字…"));ulong receivedBytes=0;
            await StreamTransfer.ReceiveAsync(c,secret,CacheRoot,async received=>
            {
                receivedBytes=received.Manifest.Total;
                await Commit(received,version,cancel.Token);
                host.Trace($"STREAM RX type={received.Manifest.Type} bytes={received.Manifest.Total} items={received.Paths.Count} clipboard-write=OK");
            },cancel.Token);
            host.Trace($"STREAM ACK-TX done=OK bytes={receivedBytes}");host.Ui(()=>host.Report("已接收并写入剪贴板 · 完成 ACK 已发送"));
        }
        catch(OperationCanceledException){}
        catch{host.Ui(()=>host.Report("接收未完成：已取消、内容无效、剪贴板已变化，或磁盘/10 GiB 缓存不足"));}
        finally{lock(gate){receivers.Remove(localId);clients.Remove(c);}c.Dispose();CryptographicOperations.ZeroMemory(secret);inbound.Release();}
    }
    internal Task Commit(ReceivedStream received,uint version,CancellationToken ct)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Ui(async()=>
        {
            try
            {
                string? text=null;if(received.Manifest.Type=="text")
                {
                    if(received.Paths.Count!=1||new FileInfo(received.Paths[0]).Length>StreamProtocol.MaxTextBytes)throw new InvalidDataException();
                    byte[] bytes=await File.ReadAllBytesAsync(received.Paths[0],ct);try{text=Protocol.Utf8.GetString(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
                }
                for(int i=0;i<5;i++)
                {
                    ct.ThrowIfCancellationRequested();if(!Active||NativeClipboard.GetClipboardSequenceNumber()!=version)throw new OperationCanceledException();
                    bool written=text==null?NativeClipboard.WriteFiles(host.Handle,received.Paths,version,ct):NativeClipboard.Write(host.Handle,Protocol.New(1,Protocol.Utf8.GetBytes(text)),cancellation:ct,expectedSequence:version);
                    if(written){host.RecordRemoteWrite(false);done.TrySetResult();return;}
                    await Task.Delay(25*(i+1),ct);
                }
                throw new IOException();
            }
            catch(Exception e){done.TrySetException(e);}
        });return done.Task.WaitAsync(ct);
    }
    public Task<bool> Send(ClipboardContent content)
    {
        lock(gate)
        {
            if(!Active)return Task.FromResult(false);outgoing?.Cancel();
            if(pending!=null){pending.Cancel.Cancel();pending.Done.TrySetResult(false);pending.Cancel.Dispose();}
            var work=new Work(content,new(TaskCreationOptions.RunContinuationsAsynchronously),CancellationTokenSource.CreateLinkedTokenSource(session!.Token));pending=work;
            if(!pumping){pumping=true;_=Task.Run(Pump);}return work.Done.Task;
        }
    }
    async Task Pump()
    {
        while(true)
        {
            Work work;byte[] secret;List<IPEndPoint> targets;
            lock(gate){if(pending==null||key==null){pumping=false;return;}work=pending;pending=null;outgoing=work.Cancel;secret=key.ToArray();targets=peers.ToList();}
            int index=-1,delivered=0;
            try
            {
                if(work.Content.Files is{} paths)
                {
                    foreach(string path in paths)NativeClipboard.ValidateLocalFile(path);
                    StreamProtocol.ValidateManifest(new("files",paths.Select(p=>new StreamItem(Path.GetFileName(p),(ulong)new FileInfo(p).Length)).ToArray(),paths.Aggregate(0UL,(n,p)=>checked(n+(ulong)new FileInfo(p).Length))));
                }
                string? text=work.Content.Files==null?Protocol.Utf8.GetString(work.Content.Packet!.Body):null;
                async Task Worker()
                {
                    while(!work.Cancel.IsCancellationRequested)
                    {
                        int n=Interlocked.Increment(ref index);if(n>=targets.Count)return;var endpoint=targets[n];int active=Interlocked.Increment(ref outgoingCount);
                        int before;do{before=maxOutgoing;}while(active>before&&Interlocked.CompareExchange(ref maxOutgoing,active,before)!=before);
                        try
                        {
                            var progress=new ThrottledProgress(p=>host.Ui(()=>host.Report($"发送{(text==null?"文件":"长文字")} {p:P0}…")));
                            if(text==null)await StreamTransfer.SendFilesAsync(endpoint.Address.ToString(),endpoint.Port,secret,work.Content.Files!,progress,work.Cancel.Token);
                            else await StreamTransfer.SendTextAsync(endpoint.Address.ToString(),endpoint.Port,secret,text,progress,work.Cancel.Token);
                            Interlocked.Increment(ref delivered);host.Trace($"STREAM ACK-RX done=OK type={(text==null?"files":"text")} peer-authenticated=OK");
                        }
                        catch(OperationCanceledException){}
                        catch{host.Ui(()=>host.Report("发送未完成：对端离线、源文件已变化、内容超限，或对端缓存不足；无自动补发"));}
                        finally{Interlocked.Decrement(ref outgoingCount);}
                    }
                }
                await Task.WhenAll(Enumerable.Range(0,Math.Min(2,targets.Count)).Select(_=>Worker()));
                if(!work.Cancel.IsCancellationRequested)host.Ui(()=>host.Report(targets.Count==0?"群组已保存 · 等待其他设备上线":$"流式传输完成 {delivered}/{targets.Count} 台 · 已验证完成 ACK；无离线补发"));
                work.Done.TrySetResult(delivered>0&&!work.Cancel.IsCancellationRequested);
            }
            catch{work.Done.TrySetResult(false);host.Ui(()=>host.Report("文件或长文字不符合限制，已跳过；最多32文件/合计5 GiB/文字10 MiB"));}
            finally{CryptographicOperations.ZeroMemory(secret);lock(gate){if(ReferenceEquals(outgoing,work.Cancel))outgoing=null;}work.Cancel.Dispose();}
        }
    }
    sealed class ThrottledProgress(Action<double> action):IProgress<double>
    {long last=-1000;public void Report(double value){long now=Environment.TickCount64;if(now-last<250&&value<1)return;last=now;action(value);}}
    public void CancelOutgoing(){lock(gate){outgoing?.Cancel();if(pending!=null){pending.Cancel.Cancel();pending.Done.TrySetResult(false);pending.Cancel.Dispose();pending=null;}}}
    public void CancelTransfers(){lock(gate){CancelOutgoing();foreach(var c in receivers.Values)c.Cancel();foreach(var c in clients)c.Dispose();}host.Trace("STREAM 取消未完成传输；无自动重试/补发");}
    public void Stop(){session?.Cancel();listener?.Stop();listener=null;CancelTransfers();session?.Dispose();session=null;lock(gate)peers=[];if(key!=null){CryptographicOperations.ZeroMemory(key);key=null;}}
    public void OpenFolder(){PrepareCache();System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={CacheRoot}});}
    public void Dispose()=>Stop();
}
