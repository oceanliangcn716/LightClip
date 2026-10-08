using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LightClip.Windows;

namespace LightClip;
internal static class StreamTests
{
    const int Port=49489;static readonly List<object> checks=[];
    static void Check(string name,bool ok){checks.Add(new{name,passed=ok});if(!ok)throw new InvalidOperationException(name);}
    static async Task Wait(Func<bool> condition,int ms=10000){var watch=Stopwatch.StartNew();while(!condition()){if(watch.ElapsedMilliseconds>ms)throw new TimeoutException();await Task.Delay(25);}}
    static async Task<bool> Fails(Task task){try{await task;return false;}catch{return true;}}
    static async Task<bool> Fails(Func<Task> task){try{await task();return false;}catch{return true;}}
    public static async Task Run(GroupHost host,string output,bool largeTest=true)
    {
        string root=Path.Combine("D:/LightClipTestTemp",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string cache=Path.Combine(root,"cache"),source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        byte[] key=RandomNumberGenerator.GetBytes(32);string? error=null;object? large=null;var timer=Stopwatch.StartNew();
        var peers=new List<StreamPeer>();string phase="start private cache/listeners";
        try
        {
            host.Streams.CacheRoot=cache;host.Streams.Start(key,Port,IPAddress.Loopback);host.Engine.Start(key,49387,IPAddress.Loopback);
            phase="small file clipboard tests";string a=Path.Combine(source,"synthetic.bin"),empty=Path.Combine(source,"empty.dat"),pngFile=Path.Combine(source,"synthetic.png");
            File.WriteAllBytes(a,RandomNumberGenerator.GetBytes(2_097_169));File.WriteAllBytes(empty,[]);
            using(var bm=new Bitmap(64,48,PixelFormat.Format32bppArgb)){bm.SetPixel(0,0,Color.FromArgb(73,200,80,40));bm.Save(pngFile,ImageFormat.Png);}
            await StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{a,empty});await Wait(()=>host.Streams.Inbound==0);
            var received=Clipboard.GetFileDropList().Cast<string>().ToArray();Check("real STA FileDrop multi-file and empty file commit before done",received.Length==2&&File.ReadAllBytes(received[0]).SequenceEqual(File.ReadAllBytes(a))&&new FileInfo(received[1]).Length==0);
            Check("completed files retained and temporary directories removed",Directory.EnumerateDirectories(cache,".lcs2-complete-*").Any()&&!Directory.EnumerateDirectories(cache,".lcs2-temp-*").Any());
            await StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{pngFile});await Wait(()=>host.Streams.Inbound==0);
            received=Clipboard.GetFileDropList().Cast<string>().ToArray();var packet=NativeClipboard.Read(host.Handle,true);using(var bm=Png.Decode(packet!.Body))Check("single PNG supplies exact file plus alpha PNG and DIB formats",received.Length==1&&File.ReadAllBytes(received[0]).SequenceEqual(File.ReadAllBytes(pngFile))&&bm.GetPixel(0,0).A==73&&Clipboard.ContainsImage()&&Clipboard.ContainsData("PNG"));
            string jpg=Path.Combine(source,"synthetic.jpg");using(var bm=new Bitmap(91,53)){using var g=Graphics.FromImage(bm);g.Clear(Color.CornflowerBlue);bm.Save(jpg,ImageFormat.Jpeg);}
            await StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{jpg});await Wait(()=>host.Streams.Inbound==0);received=Clipboard.GetFileDropList().Cast<string>().ToArray();Check("single JPG preserves original bytes",File.ReadAllBytes(received.Single()).SequenceEqual(File.ReadAllBytes(jpg)));Check("single JPG supplies PNG and Windows image formats",NativeClipboard.ImageFilePacket(received.Single())?.Kind==2&&Clipboard.ContainsImage()&&Clipboard.ContainsData("PNG"));
            string seed="中文😀\n";int unit=Protocol.Utf8.GetByteCount(seed);string big=string.Concat(Enumerable.Repeat(seed,StreamProtocol.MaxTextBytes/unit))+new string('x',StreamProtocol.MaxTextBytes%unit);
            await StreamTransfer.SendTextAsync("127.0.0.1",Port,key,big);await Wait(()=>host.Streams.Inbound==0);
            Check("exact 10 MiB Chinese emoji multiline text in Windows clipboard",Clipboard.GetText()==big&&Protocol.Utf8.GetByteCount(big)==StreamProtocol.MaxTextBytes);
            Check("text staging deleted after commit",Directory.EnumerateDirectories(cache,".lcs2-complete-*").All(d=>!File.Exists(Path.Combine(d,"clipboard.txt"))));
            uint sequence=NativeClipboard.GetClipboardSequenceNumber();Check("10 MiB plus one rejected without changing clipboard",await Fails(()=>StreamTransfer.SendTextAsync("127.0.0.1",Port,key,big+"x"))&&NativeClipboard.GetClipboardSequenceNumber()==sequence);big="";
            peers.Add(new(key,Path.Combine(root,"peer1")));peers.Add(new(key,Path.Combine(root,"peer2")));host.Streams.UpdatePeers(peers.Select(p=>new IPEndPoint(IPAddress.Loopback,p.Port)));
            int count=peers[0].Count;Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection{a,empty});await Wait(()=>peers.All(p=>p.Count>0));await Wait(()=>host.Streams.Outbound==0);Check("normal FileDrop notification streams to two group members",peers.All(p=>p.Last?.Manifest.Items.Count==2));
            count=peers[0].Count;NativeClipboard.Write(host.Handle,Protocol.New(1,Protocol.Utf8.GetBytes(new string('x',Protocol.TextLimit+1))),false);await Wait(()=>peers[0].Count>count);await Wait(()=>host.Streams.Outbound==0);Check("clipboard text above v1 limit routes to stream v2",peers[0].Last?.Manifest.Type=="text");
            count=peers[0].Count;await StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{a});await Task.Delay(200);Check("remote FileDrop is not echoed",peers[0].Count==count);
            foreach(var list in new[]{new[]{source},new[]{"\\\\server\\share\\file"}}){Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection{list[0]});await Task.Delay(100);Check("directory and UNC clipboard excluded",peers[0].Count==count);}
            var holder=new List<TcpClient>();try{for(int i=0;i<2;i++){var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,Port);holder.Add(c);}await Wait(()=>host.Streams.Inbound==2);using var third=new TcpClient();await third.ConnectAsync(IPAddress.Loopback,Port);using var deadline=new CancellationTokenSource(2000);Check("third inbound stream rejected",await third.GetStream().ReadAsync(new byte[1],deadline.Token)==0);}finally{foreach(var c in holder)c.Dispose();}await Wait(()=>host.Streams.Inbound==0);
            if(largeTest){
            // Real 5 GiB Windows sender -> production wrapper receiver, all bytes encrypted/written/hashed.
            string huge=Path.Combine(source,"synthetic-five-gib.bin");using(var f=File.Create(huge))f.SetLength((long)StreamProtocol.MaxFileBytes);
            using var beforeHash=File.OpenRead(huge);byte[] expectedHash=await SHA256.HashDataAsync(beforeHash);beforeHash.Close();
            using var process=Process.GetCurrentProcess();process.Refresh();var cpu=process.TotalProcessorTime;var watch=Stopwatch.StartNew();long peakPrivate=0,peakWorking=0;var sampleCount=0;using var sampling=new CancellationTokenSource();
            var sampler=Task.Run(async()=>{try{while(!sampling.IsCancellationRequested){process.Refresh();peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);peakWorking=Math.Max(peakWorking,process.WorkingSet64);sampleCount++;await Task.Delay(100,sampling.Token);}}catch(OperationCanceledException){}});
            await StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{huge});await Wait(()=>host.Streams.Inbound==0);watch.Stop();sampling.Cancel();await sampler;process.Refresh();double usedCpu=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            received=Clipboard.GetFileDropList().Cast<string>().ToArray();using var afterHash=File.OpenRead(received.Single());byte[] actualHash=await SHA256.HashDataAsync(afterHash);afterHash.Close();Check("Windows actual 5 GiB all bytes SHA256, clipboard FileDrop and final authenticated done",new FileInfo(received.Single()).Length==(long)StreamProtocol.MaxFileBytes&&expectedHash.SequenceEqual(actualHash));
            large=new{bytes=StreamProtocol.MaxFileBytes,direction="Windows C# sender -> GroupStreams Windows receiver, same process loopback TCP 49489",sha256=Convert.ToHexString(actualHash),seconds=watch.Elapsed.TotalSeconds,peakPrivateBytes=peakPrivate,peakWorkingSet=peakWorking,cpuPercentOneCore=usedCpu/watch.Elapsed.TotalMilliseconds*100,cpuPercentMachine=usedCpu/watch.Elapsed.TotalMilliseconds*100/Environment.ProcessorCount,sampleCount,cacheBytes=Directory.EnumerateFiles(cache,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length),doneAuthenticated=true};
            File.Delete(huge); // Only owned synthetic source. Completed clipboard target remains until cleanup after the test.
            }
            // Cancellation after chunks arrive, and clipboard version changes, must leave no partial commit.
            string cancelFile=Path.Combine(source,"synthetic-cancel.bin");using(var f=File.Create(cancelFile))f.SetLength(256*1024*1024);
            var cancellationTransfer=StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{cancelFile});await Wait(()=>Directory.EnumerateDirectories(cache,".lcs2-temp-*").Any());host.CancelTransfers();Check("cancel current transfer rejects final done",await Fails(cancellationTransfer));await Wait(()=>host.Streams.Inbound==0);Check("cancel removes all partial directories",!Directory.EnumerateDirectories(cache,".lcs2-temp-*").Any());
            var changedTransfer=StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{cancelFile});await Wait(()=>Directory.EnumerateDirectories(cache,".lcs2-temp-*").Any());Clipboard.SetText("synthetic new local copy "+Guid.NewGuid());Check("new local copy cancels receiving stream",await Fails(changedTransfer));await Wait(()=>host.Streams.Inbound==0);
            var pauseTransfer=StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{cancelFile});await Wait(()=>Directory.EnumerateDirectories(cache,".lcs2-temp-*").Any());host.Engine.Stop();host.Streams.Stop();Check("pause cancels active stream",await Fails(pauseTransfer));await Wait(()=>host.Streams.Inbound==0);
            int reads=NativeClipboard.Reads;count=peers[0].Count;Clipboard.SetText("synthetic paused "+Guid.NewGuid());await Task.Delay(15000);Check("15 second pause negative no clipboard read or stream send",NativeClipboard.Reads==reads&&peers[0].Count==count);
            Check("pause partial cleanup",!Directory.EnumerateDirectories(cache,".lcs2-temp-*").Any());
            string abandoned=Path.Combine(cache,".lcs2-temp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(abandoned);File.WriteAllText(Path.Combine(abandoned,"synthetic"),"test");host.Streams.Start(key,Port,IPAddress.Loopback);Check("restart cleanup removes abandoned owned temp, retains completed",!Directory.Exists(abandoned)&&Directory.EnumerateDirectories(cache,".lcs2-complete-*").Any());
            Check("outbound stream cap remains two",host.Streams.MaxOutbound<=2);
            // Leave a small synthetic PNG on the clipboard for a separate real Explorer/UI paste check.
            await StreamTransfer.SendFilesAsync("127.0.0.1",Port,key,new[]{pngFile});await Wait(()=>host.Streams.Inbound==0);
            string uiRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LightClipBuild","ExplorerPasteFixture");Directory.CreateDirectory(uiRoot);string retained=Path.Combine(uiRoot,"synthetic-lightclip.png");File.Copy(Clipboard.GetFileDropList()[0]!,retained,true);
            NativeClipboard.WriteFiles(host.Handle,new[]{retained},NativeClipboard.GetClipboardSequenceNumber(),CancellationToken.None);host.RecordRemoteWrite(false);
            File.WriteAllBytes(Path.Combine(uiRoot,"expected.sha256"),SHA256.HashData(File.ReadAllBytes(retained)));
        }
        catch(Exception e){error=e.GetType().Name+": "+(e is InvalidOperationException?e.Message:phase);}
        finally
        {
            host.Engine.Stop();host.Streams.Stop();foreach(var p in peers)p.Dispose();CryptographicOperations.ZeroMemory(key);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,JsonSerializer.Serialize(new{date=DateTimeOffset.Now,version="0.3.0",passed=error==null,error,seconds=timer.Elapsed.TotalSeconds,checks,large,limits=new[]{"same Windows process loopback, not Mac/Wi-Fi", "Explorer GUI paste checked separately"}},new JsonSerializerOptions{WriteIndented=true}));
            if(Path.GetFullPath(root).StartsWith(Path.GetFullPath("D:/LightClipTestTemp/"),StringComparison.OrdinalIgnoreCase)&&Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    sealed class StreamPeer:IDisposable
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();readonly byte[] key;readonly string cache;int count;
        public int Port{get;}public int Count=>Volatile.Read(ref count);public ReceivedStream? Last;
        public StreamPeer(byte[] key,string cache){this.key=key.ToArray();this.cache=cache;listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;_=Loop();}
        async Task Loop(){try{while(!stop.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(stop.Token);_=Receive(c);}}catch(Exception e)when(e is OperationCanceledException or ObjectDisposedException or SocketException){}}
        async Task Receive(TcpClient c){using(c)try{await StreamTransfer.ReceiveAsync(c,key,cache,r=>{Last=r;Interlocked.Increment(ref count);return Task.CompletedTask;},stop.Token);}catch{}}
        public void Dispose(){stop.Cancel();listener.Stop();}
    }
}
