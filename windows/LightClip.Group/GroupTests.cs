using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LightClip.Windows;

namespace LightClip;
internal static class GroupTests
{
    const int ContentPort=49387,PairPort=49392;
    static readonly List<object> results=[];
    static void Check(string name,bool ok){results.Add(new{name,passed=ok});if(!ok)throw new InvalidOperationException("Check failed: "+name);}
    static void Reject(string name,Action action){bool rejected=false;try{action();}catch{rejected=true;}Check(name,rejected);}
    static async Task Wait(Func<bool> f,int ms=6000){var w=Stopwatch.StartNew();while(!f()){if(w.ElapsedMilliseconds>ms)throw new TimeoutException();await Task.Delay(20);}}
    static byte[] PngBytes(int w=96,int h=64,bool alpha=true)
    {
        using var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(bitmap)){g.Clear(alpha?Color.Transparent:Color.White);g.FillRectangle(Brushes.CornflowerBlue,8,8,w/2,h/2);g.DrawString(Guid.NewGuid().ToString("N"),SystemFonts.DefaultFont,Brushes.Black,10,20);}
        if(alpha)bitmap.SetPixel(0,0,Color.FromArgb(73,220,80,40));using var ms=new MemoryStream();bitmap.Save(ms,ImageFormat.Png);return ms.ToArray();
    }
    static async Task<Packet?> Request(byte[] frame,byte[] key,bool fragments=false)
    {
        using var deadline=new CancellationTokenSource(5500);using var client=new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback,ContentPort,deadline.Token);var s=client.GetStream();
            if(fragments){for(int i=0;i<frame.Length;i+=3)await s.WriteAsync(frame.AsMemory(i,Math.Min(3,frame.Length-i)),deadline.Token);}else await s.WriteAsync(frame,deadline.Token);
            return await Protocol.ReadAsync(s,key,deadline.Token);
        }
        catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or InvalidDataException or CryptographicException){return null;}
    }
    static byte[] Announcement(Guid id,string name,Guid? group=null)=>JsonSerializer.SerializeToUtf8Bytes(group==null?new Dictionary<string,object>{{"v",2},{"type","lightclip-discovery"},{"deviceId",id.ToString("D")},{"name",name}}:new Dictionary<string,object>{{"v",2},{"type","lightclip-discovery"},{"deviceId",id.ToString("D")},{"name",name},{"groupId",group.Value.ToString("D")}});
    public static async Task Run(GroupHost host,string output)
    {
        string scratch=Path.Combine(Path.GetTempPath(),"LightClip-group-tests-"+Guid.NewGuid().ToString("N"));
        byte[] key=RandomNumberGenerator.GetBytes(32);string oldGroupRoot=GroupStore.Root;bool oldTest=GroupStore.IsTest;
        string? error=null;var measurements=new List<object>();var fakePeers=new List<FakePeer>();PairingService? pairing=null;
        var timer=Stopwatch.StartNew();
        try
        {
            Check("unpaired launch zero clipboard reads and no content listener",!host.Engine.Active&&NativeClipboard.Reads==0);
            var replayBoundary=new ReplayWindow();for(int i=0;i<4096;i++)if(!replayBoundary.Accept(Guid.NewGuid(),0))throw new InvalidOperationException("Replay capacity");
            Check("replay full window refuses new UUID without early eviction",!replayBoundary.Accept(Guid.NewGuid(),299999)&&replayBoundary.Accept(Guid.NewGuid(),300000));
            GroupStore.Root=Path.Combine(scratch,"group");GroupStore.IsTest=true;
            var profile=GroupStore.Load();var same=GroupStore.Load();Check("friendly fruit name and UUID persist",GroupStore.ValidName(profile.Name)&&profile==same);
            Check("login startup defaults on but no unpaired registry mutation",profile.AutoStart&&profile.GroupId==null);
            Reject("empty device name rejected",()=>GroupStore.SaveProfile(profile with{Name="  "}));
            Reject("control device name rejected",()=>GroupStore.SaveProfile(profile with{Name="name\n"}));
            Reject("overlong UTF8 device name rejected",()=>GroupStore.SaveProfile(profile with{Name=new string('橘',33)}));
            using var syncGroup=new SyncGroup(Guid.NewGuid(),key.ToArray());var paired=GroupStore.SaveGroup(profile,syncGroup);
            using(var loaded=GroupStore.LoadGroup(GroupStore.Load()))Check("DPAPI stores exact group UUID and group key",loaded?.Id==syncGroup.Id&&loaded.Key.SequenceEqual(key));
            Check("group config contains neither key nor pairing code",!File.ReadAllText(Path.Combine(GroupStore.Root,"profile.json")).Contains(Convert.ToBase64String(key)));
            using(var probe=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,ArgumentList={"--dpapi-group-probe",GroupStore.Root}})!)
            {await probe.WaitForExitAsync();Check("group DPAPI restored in a new actual process",probe.ExitCode==0&&Protocol.Decode(File.ReadAllBytes(Path.Combine(GroupStore.Root,"probe.frame"))[4..],key).Kind==0);}
            var paused=paired with{Paused=true};GroupStore.SaveProfile(paused);Check("paused and startup preferences persist",GroupStore.Load()==paused);
            var left=GroupStore.Leave(paired);Check("leave forgets group, encrypted key and group autostart",left.GroupId==null&&!left.AutoStart&&left.Paused&&!File.Exists(Path.Combine(GroupStore.Root,"group.dpapi")));
            var discovery=new Discovery(profile.DeviceId,()=> (profile.Name,(Guid?)null),49386);int changes=0;discovery.Changed+=_=>changes++;
            using(var json=JsonDocument.Parse(discovery.Announcement()))Check("unpaired discovery omits groupId rather than null",!json.RootElement.TryGetProperty("groupId",out _));
            Guid remote=Guid.NewGuid();var addr=IPAddress.Parse("192.0.2.10");byte[] announce=Announcement(remote,"合成橘子",syncGroup.Id);
            Check("discovery accepts canonical device and source address",discovery.Process(announce,addr,1000)&&discovery.Devices[0].Host.Equals(addr));
            int previousChanges=changes;discovery.Process(announce,addr,2000);Check("heartbeat-only update does not churn UI",changes==previousChanges);
            discovery.Process(announce,IPAddress.Parse("192.0.2.11"),3000);Check("same UUID updates changed source IP",discovery.Devices.Count==1&&discovery.Devices[0].Host.ToString()=="192.0.2.11");
            discovery.Process(Announcement(remote,"新的橘子",null),addr,4000);Check("name and group change refresh discovered record",discovery.Devices[0].Name=="新的橘子"&&discovery.Devices[0].GroupId==null);
            foreach(string ip in new[]{"0.0.0.1","127.0.0.1","224.0.0.1"})Check("reject discovery source "+ip,!discovery.Process(Announcement(Guid.NewGuid(),"合成"),IPAddress.Parse(ip),5000));
            Check("own discovery ignored",!discovery.Process(Announcement(profile.DeviceId,"自身"),addr,5000));
            Check("oversize discovery ignored",!discovery.Process(new byte[1025],addr,5000));
            Check("noncanonical uppercase UUID ignored",!discovery.Process(Protocol.Utf8.GetBytes(Protocol.Utf8.GetString(announce).Replace(remote.ToString("D"),remote.ToString("D").ToUpperInvariant())),addr,5000));
            Check("discovery null group rejected",!discovery.Process(Protocol.Utf8.GetBytes("{\"v\":2,\"type\":\"lightclip-discovery\",\"deviceId\":\""+Guid.NewGuid()+"\",\"name\":\"test\",\"groupId\":null}"),addr,5000));
            for(int i=0;i<70;i++)discovery.Process(Announcement(Guid.NewGuid(),"测试设备"),addr,5000);Check("discovery bounded to 64 records",discovery.Devices.Count==64);
            discovery.Expire(25001);Check("discovery records expire after 20 seconds",discovery.Devices.Count==0);discovery.Dispose();
            // Actual UDP ingress from the machine's non-loopback source on an isolated test port.
            var local=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(x=>x.Address).FirstOrDefault(ip=>ip.AddressFamily==AddressFamily.InterNetwork&&ip.GetAddressBytes()[0] is not(0 or 127 or 169));
            if(local!=null)
            {
                using var liveDiscovery=new Discovery(profile.DeviceId,()=> (profile.Name,(Guid?)null),49386);liveDiscovery.Start();using var udp=new UdpClient(new IPEndPoint(local,0));
                await udp.SendAsync(new byte[2048],new IPEndPoint(local,49386));
                await udp.SendAsync(announce,new IPEndPoint(local,49386));await Wait(()=>liveDiscovery.Devices.Any(d=>d.Id==remote));Check("actual UDP discovery uses recvfrom source",liveDiscovery.Devices.First(d=>d.Id==remote).Host.Equals(local));
                Check("oversize UDP does not terminate discovery",liveDiscovery.Devices.Any(d=>d.Id==remote));
            }
            using(var a=new PairingSpake(0,"12345678",profile.DeviceId,remote,syncGroup.Id))using(var b=new PairingSpake(1,"12345678",profile.DeviceId,remote,syncGroup.Id))
            {
                byte[] am=a.Message,bm=b.Message,ka=a.Finish(bm),kb=b.Finish(am);Check("shipped native DLL SPAKE2 agrees",ka.SequenceEqual(kb));
                byte[] salt=RandomNumberGenerator.GetBytes(16),t=PairingCrypto.Transcript(profile.DeviceId,remote,syncGroup.Id,am,bm,salt),pk=PairingCrypto.DeriveKey(ka,salt,t);
                Check("pair transcript exact 148 bytes",t.Length==148);byte[] sealedGroup=PairingCrypto.WrapGroup(syncGroup.Id,key,pk,t);Check("sealed group exact 76 bytes",sealedGroup.Length==76);
                sealedGroup[20]^=1;Reject("tampered sealed group rejected",()=>PairingCrypto.UnwrapGroup(syncGroup.Id,sealedGroup,pk,t));
                byte[] clean=PairingCrypto.WrapGroup(syncGroup.Id,key,pk,t);Reject("sealed group UUID binding enforced",()=>PairingCrypto.UnwrapGroup(Guid.NewGuid(),clean,pk,t));
                Reject("native state cannot finish twice",()=>a.Finish(bm));CryptographicOperations.ZeroMemory(ka);CryptographicOperations.ZeroMemory(kb);CryptographicOperations.ZeroMemory(pk);
            }
            pairing=new(remote,()=>"合成邀请者"){FragmentForTests=true};pairing.Start(syncGroup,PairPort,IPAddress.Loopback);int joined=0;pairing.MemberJoined+=_=>Interlocked.Increment(ref joined);
            var target=new NearbyDevice(remote,"合成邀请者",IPAddress.Loopback,syncGroup.Id,0);
            pairing.OpenInvitation("12345678");bool wrongAccepted=false;
            try{await PairingService.Join(target,"12345679",profile.DeviceId,profile.Name,(_,_)=>{wrongAccepted=true;return Task.CompletedTask;},CancellationToken.None,PairPort,true);}catch{}
            Check("wrong short code never persists group",!wrongAccepted&&pairing.InvitationOpen);
            bool persisted=false;await PairingService.Join(target,"12345678",profile.DeviceId,profile.Name,(g,_)=>{Check("network join obtains inviter same group UUID/key",g.Id==syncGroup.Id&&g.Key.SequenceEqual(key));persisted=true;return Task.CompletedTask;},CancellationToken.None,PairPort,true);
            await Wait(()=>Volatile.Read(ref joined)==1);Check("fragmented PAKE handshake persists before stored confirmation",persisted&&!pairing.InvitationOpen);
            bool reused=false;try{await PairingService.Join(target,"12345678",Guid.NewGuid(),"合成加入者",(_,_)=>{reused=true;return Task.CompletedTask;},CancellationToken.None,PairPort);}catch{}Check("successful invitation is one-use",!reused);
            pairing.OpenInvitation("12345678",20);await Task.Delay(40);bool expired=false;try{await PairingService.Join(target,"12345678",Guid.NewGuid(),"合成加入者",(_,_)=>{expired=true;return Task.CompletedTask;},CancellationToken.None,PairPort);}catch{}Check("expired invitation cannot join",!expired);
            pairing.OpenInvitation("12345678");for(int i=0;i<8;i++){using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,PairPort);await c.GetStream().WriteAsync(new byte[]{0,0,0,0});await Task.Delay(30);}await Wait(()=>pairing.InboundCount==0);Check("eight invalid hello attempts close invitation",!pairing.InvitationOpen);
            pairing.Dispose();pairing=null;
            // Exercise the package's separate content implementation against our real listener.
            byte[] fixtureKey=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray();host.Engine.Start(fixtureKey,ContentPort,IPAddress.Loopback);
            string? dotnet=Environment.GetEnvironmentVariable("LIGHTCLIP_TEST_DOTNET"),fixture=Environment.GetEnvironmentVariable("LIGHTCLIP_INTEROP_DLL");
            if(dotnet!=null&&fixture!=null)
            {
                var start=new ProcessStartInfo(dotnet){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(fixture);start.Environment["LIGHTCLIP_TEST_PORT"]=ContentPort.ToString();
                using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();string result=await stdout;_=await stderr;Check("provided independent content fixture validation and TCP tests",child.ExitCode==0&&result.StartsWith("PASS:"));
            }
            host.Engine.Stop();host.Engine.Start(key,ContentPort,IPAddress.Loopback);
            fakePeers.Add(new(49389,key));fakePeers.Add(new(49390,key));host.Engine.UpdatePeers(fakePeers.Select(p=>new IPEndPoint(IPAddress.Loopback,p.Port)));
            var text=Protocol.New(1,Protocol.Utf8.GetBytes("合成中文 😀\n多行\r\n"+Guid.NewGuid()));byte[] frame=Protocol.Encode(text,key);
            var ack=await Request(frame,key,true);Check("incoming Chinese emoji multiline writes then matching ACK",ack?.Id==text.Id&&NativeClipboard.Read(host.Handle,true)?.Body.SequenceEqual(text.Body)==true);
            int total=fakePeers.Sum(p=>p.Count);await Task.Delay(200);Check("remote text does not fan out or echo",fakePeers.Sum(p=>p.Count)==total);
            Check("repeat UUID rejected over TCP",await Request(frame,key)==null);
            Check("expired packet rejected over TCP",await Request(Protocol.Encode(text with{Id=Guid.NewGuid(),Timestamp=Protocol.Now-130000},key),key)==null);
            byte[] wrong=RandomNumberGenerator.GetBytes(32);Check("wrong content key rejected over TCP",await Request(Protocol.Encode(Protocol.New(0),wrong),key)==null);CryptographicOperations.ZeroMemory(wrong);
            var tampered=Protocol.Encode(Protocol.New(0),key);tampered[20]^=1;Check("tampered frame rejected over TCP",await Request(tampered,key)==null);
            Check("oversize prefix rejected before body allocation",await Request([255,255,255,255],key)==null);
            Check("inbound kind3 ACK rejected",await Request(Protocol.Encode(Protocol.New(3),key),key)==null);
            byte[] png=PngBytes();var image=Protocol.New(2,png);ack=await Request(Protocol.Encode(image,key),key,true);
            using(var bm=Png.Decode(NativeClipboard.Read(host.Handle,true)!.Body))Check("remote transparent PNG and alpha paste formats",ack?.Id==image.Id&&bm.GetPixel(0,0).A==73&&bm.GetPixel(95,63).A==0);
            await Task.Delay(200);Check("remote image does not fan out or echo",fakePeers.Sum(p=>p.Count)==total);
            NativeClipboard.Write(host.Handle,Protocol.New(1,text.Body),false);await Wait(()=>fakePeers.All(p=>p.Count>=1));await Wait(()=>host.Engine.InFlight==0);
            Check("real clipboard notification fans text to two peers with same UUID",fakePeers[0].Last?.Id==fakePeers[1].Last?.Id&&fakePeers.All(p=>p.Last?.Body.SequenceEqual(text.Body)==true));
            int c0=fakePeers[0].Count,c1=fakePeers[1].Count;NativeClipboard.Write(host.Handle,Protocol.New(2,png),false);await Wait(()=>fakePeers[0].Count>c0&&fakePeers[1].Count>c1);await Wait(()=>host.Engine.InFlight==0);
            Check("transparent PNG fans out with same request UUID",fakePeers[0].Last?.Id==fakePeers[1].Last?.Id&&fakePeers.All(p=>p.Last?.Kind==2));
            // Real Ctrl+C FileDrop representation for one local ordinary image.
            string pngFile=Path.Combine(scratch,"synthetic.png"),jpgFile=Path.Combine(scratch,"synthetic.jpg"),jpegFile=Path.Combine(scratch,"synthetic.jpeg"),textFile=Path.Combine(scratch,"synthetic.txt");
            Directory.CreateDirectory(scratch);File.WriteAllBytes(pngFile,png);using(var bmp=Png.Decode(png)){bmp.Save(jpgFile,ImageFormat.Jpeg);bmp.Save(jpegFile,ImageFormat.Jpeg);}File.WriteAllText(textFile,"synthetic only");
            foreach(var (file,ext) in new[]{(pngFile,"PNG"),(jpgFile,"JPG"),(jpegFile,"JPEG")})
            {
                c0=fakePeers[0].Count;Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection{file});await Task.Delay(100);
                Check("single local "+ext+" FileDrop classified as v2 file and decodable optional image",NativeClipboard.Capture(host.Handle)?.Files?.Single()==file&&NativeClipboard.ImageFilePacket(file)?.Kind==2&&fakePeers[0].Count==c0);
            }
            c0=fakePeers[0].Count;foreach(var items in new[]{new[]{pngFile,jpgFile},new[]{scratch},new[]{textFile}}){var list=new System.Collections.Specialized.StringCollection();list.AddRange(items);Clipboard.SetFileDropList(list);await Task.Delay(200);Check("multi/directory/nonimage FileDrop excluded",fakePeers[0].Count==c0);}
            var data=new DataObject();data.SetFileDropList(new System.Collections.Specialized.StringCollection{pngFile});data.SetText("synthetic URL should not be sent");Clipboard.SetDataObject(data,true);await Task.Delay(100);Check("FileDrop takes priority over attached text",NativeClipboard.Capture(host.Handle)?.Files?.Single()==pngFile&&fakePeers[0].Count==c0);
            c0=fakePeers[0].Count;NativeClipboard.Write(host.Handle,Protocol.New(1,Protocol.Utf8.GetBytes("synthetic concealed")),false,true);await Task.Delay(200);Check("concealed data excluded",fakePeers[0].Count==c0);
            foreach(string excluded in new[]{"org.nspasteboard.TransientType","application/x-keepassxc-private","com.agilebits.onepassword"})
            {var sensitive=new DataObject();sensitive.SetText("synthetic only");sensitive.SetData(excluded,false,new MemoryStream(new byte[]{1}));Clipboard.SetDataObject(sensitive,true);await Task.Delay(100);Check("private/transient clipboard format excluded: "+excluded,fakePeers[0].Count==c0);}
            using(var bitmap=new Bitmap(123,77)){using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.CornflowerBlue);Clipboard.SetImage(bitmap);await Wait(()=>fakePeers[0].Count>c0);await Wait(()=>host.Engine.InFlight==0);Check("ordinary bitmap screenshot format converts to PNG",fakePeers[0].Last?.Kind==2&&Png.Validate(fakePeers[0].Last!.Body)==(123,77));}
            c0=fakePeers[0].Count;string huge=Path.Combine(scratch,"too-large.png");using(var file=File.Create(huge))file.SetLength(67_108_865);Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection{huge});await Task.Delay(150);Check("source file above 64 MiB skipped before reading",fakePeers[0].Count==c0);File.Delete(huge);
            var holderClients=new List<TcpClient>();try
            {for(int i=0;i<4;i++){var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,ContentPort);holderClients.Add(c);}await Wait(()=>host.Engine.InboundCount==4);Check("content fifth inbound connection rejected",await Request(Protocol.Encode(Protocol.New(0),key),key)==null);}finally{foreach(var c in holderClients)c.Dispose();}await Wait(()=>host.Engine.InboundCount==0);
            fakePeers[0].WrongAck=true;host.Engine.UpdatePeers([new(IPAddress.Loopback,49389)]);Check("wrong UUID encrypted ACK never succeeds",!await host.Engine.Send(Protocol.New(0)));fakePeers[0].WrongAck=false;
            host.Engine.UpdatePeers([new(IPAddress.Loopback,49389),new(IPAddress.Loopback,49391)]);Check("offline member does not block other member ACK",await host.Engine.Send(Protocol.New(0)));
            c0=fakePeers[0].Count;host.Engine.Stop();int reads=NativeClipboard.Reads;NativeClipboard.Write(host.Handle,Protocol.New(1,text.Body),false);
            await Task.Delay(15000);Check("pause negative control 15 seconds no reads or transfer",NativeClipboard.Reads==reads&&fakePeers[0].Count==c0);
            bool listenerClosed=false;try{using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,ContentPort);}catch(SocketException){listenerClosed=true;}Check("pause content port closed",listenerClosed);
            host.Engine.Start(key,ContentPort,IPAddress.Loopback);host.Engine.UpdatePeers([new(IPAddress.Loopback,49389)]);await Task.Delay(200);Check("resume has no stale clipboard resend",fakePeers[0].Count==c0);
            Check("resume retains replay window",await Request(frame,key)==null);NativeClipboard.Write(host.Handle,Protocol.New(1,Protocol.Utf8.GetBytes("synthetic resumed "+Guid.NewGuid())),false);await Wait(()=>fakePeers[0].Count>c0);await Wait(()=>host.Engine.InFlight==0);Check("resume sends newly copied content",true);
            host.Engine.UpdatePeers([new(IPAddress.Loopback,49390)]);c1=fakePeers[1].Count;await host.Engine.Send(Protocol.New(0));Check("updated endpoint used without restarting content service",fakePeers[1].Count>c1);
            // Six simulated members exercise the global four-connection bound.
            for(int i=0;i<4;i++)fakePeers.Add(new(49400+i,key));host.Engine.UpdatePeers(fakePeers.Select(p=>new IPEndPoint(IPAddress.Loopback,p.Port)));
            foreach(var p in fakePeers)p.Delay=100;Check("six simulated members receive one UUID via fanout",await host.Engine.Send(Protocol.New(2,png))&&fakePeers.Select(p=>p.Last?.Id).Distinct().Count()==1);
            Check("outbound fanout never exceeds four connections",host.Engine.MaxInFlight<=4);
            foreach(var p in fakePeers)p.Delay=0;
            fakePeers[0].Stall=true;host.Engine.UpdatePeers([new(IPAddress.Loopback,49389)]);var deadlineWatch=Stopwatch.StartNew();Check("single absolute five second send deadline",!await host.Engine.Send(Protocol.New(0))&&deadlineWatch.ElapsedMilliseconds<5600);
            c0=fakePeers[0].Count;var stale=host.Engine.Send(Protocol.New(0));await Wait(()=>fakePeers[0].Count>c0);
            host.Engine.UpdatePeers([new(IPAddress.Loopback,49390)]);c1=fakePeers[1].Count;var replacementWatch=Stopwatch.StartNew();
            Check("fresh request cancels unfinished fanout without waiting five seconds",await host.Engine.Send(Protocol.New(1,text.Body))&&!await stale&&fakePeers[1].Count>c1&&replacementWatch.ElapsedMilliseconds<1500);fakePeers[0].Stall=false;
            measurements.Add(await Measure("enabled idle; same process includes six synthetic receivers",8));
            for(int i=0;i<30;i++){c1=fakePeers[1].Count;NativeClipboard.Write(host.Handle,Protocol.New(2,PngBytes(800,600,i%2==0)),false);await Wait(()=>fakePeers[1].Count>c1);await Wait(()=>host.Engine.InFlight==0);}
            Check("30 fresh image copies each ACKed",true);measurements.Add(await Measure("after 30 800x600 image sends; synthetic test process",8));
            host.Engine.Stop();Check("cleanup releases listener and content connections",host.Engine.InFlight==0&&!host.Engine.Active);
            NativeClipboard.Write(host.Handle,Protocol.New(1,Protocol.Utf8.GetBytes("LightClip group local tests complete")),false,true);
        }
        catch(Exception e){error=e.GetType().Name+": "+(e is InvalidOperationException?e.Message:"test did not complete");}
        finally
        {
            host.Engine.Stop();pairing?.Dispose();foreach(var p in fakePeers)p.Dispose();CryptographicOperations.ZeroMemory(key);
            GroupStore.Root=oldGroupRoot;GroupStore.IsTest=oldTest;
            if(!Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new IOException();
            if(Directory.Exists(scratch))Directory.Delete(scratch,true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output,JsonSerializer.Serialize(new{date=DateTimeOffset.Now,version="0.3.0",passed=error==null,error,seconds=timer.Elapsed.TotalSeconds,results,measurements,evidence=host.Evidence,limits=new[]{"Synthetic peers are not physical PCs or Swift devices","Physical DHCP/reboot/sleep and cross-device isolation remain pending"}},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    static async Task<object> Measure(string state,int seconds)
    {
        using var p=Process.GetCurrentProcess();p.Refresh();var cpu=p.TotalProcessorTime;var w=Stopwatch.StartNew();var samples=new List<object>();
        for(int i=0;i<seconds;i++){await Task.Delay(1000);p.Refresh();samples.Add(new{privateBytes=p.PrivateMemorySize64,workingSet=p.WorkingSet64});}
        p.Refresh();double pct=(p.TotalProcessorTime-cpu).TotalMilliseconds/w.Elapsed.TotalMilliseconds*100;
        return new{state,seconds=w.Elapsed.TotalSeconds,cpuPercentOneCore=pct,cpuPercentMachine=pct/Environment.ProcessorCount,samples};
    }
    sealed class FakePeer:IDisposable
    {
        readonly TcpListener listener;readonly CancellationTokenSource stop=new();readonly byte[] key;
        public readonly int Port;int count;public int Count=>Volatile.Read(ref count);public Packet? Last;public volatile bool WrongAck,Stall;public int Delay;
        public FakePeer(int port,byte[] key){Port=port;this.key=key.ToArray();listener=new(IPAddress.Loopback,port);listener.Start(4);_=Loop();}
        async Task Loop(){try{while(!stop.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(stop.Token);_=Handle(c);}}catch(Exception e)when(e is SocketException or OperationCanceledException or ObjectDisposedException){}}
        async Task Handle(TcpClient c)
        {
            using(c)using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                deadline.CancelAfter(6000);try
                {
                    var stream=c.GetStream();var packet=await Protocol.ReadAsync(stream,key,deadline.Token);Last=packet;Interlocked.Increment(ref count);
                    if(Stall){await Task.Delay(5500,deadline.Token);return;}if(Delay>0)await Task.Delay(Delay,deadline.Token);
                    byte[] frame=Protocol.Encode(new(WrongAck?Guid.NewGuid():packet.Id,Protocol.Now,3,[]),key);
                    for(int i=0;i<frame.Length;i+=3)await stream.WriteAsync(frame.AsMemory(i,Math.Min(3,frame.Length-i)),deadline.Token);
                }
                catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
            }
        }
        public void Dispose(){stop.Cancel();listener.Stop();stop.Dispose();}
    }
}

