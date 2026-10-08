using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using LightClip.Windows;

var root=Path.Combine(Path.GetTempPath(),"LightClip-stream-extra-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);byte[] key=RandomNumberGenerator.GetBytes(32);int passed=0;
void Check(bool ok){if(!ok)throw new InvalidOperationException();passed++;}
async Task<StreamRecord> Read(NetworkStream s,CancellationToken ct){byte[] prefix=new byte[4];await s.ReadExactlyAsync(prefix,ct);uint len=BinaryPrimitives.ReadUInt32BigEndian(prefix);if(len<61||len>262217)throw new InvalidDataException();byte[] envelope=new byte[len];await s.ReadExactlyAsync(envelope,ct);return StreamProtocol.DecodeEnvelope(envelope,key);}
async Task Write(NetworkStream s,StreamRecord r,CancellationToken ct){var frame=StreamProtocol.EncodeFrame(r,key);for(int i=0;i<frame.Length;i+=3)await s.WriteAsync(frame.AsMemory(i,Math.Min(3,frame.Length-i)),ct);}
async Task WrongAck(string kind){using var l=new TcpListener(IPAddress.Loopback,0);l.Start();using var ct=new CancellationTokenSource(4000);string file=Path.Combine(root,"synthetic.bin");File.WriteAllBytes(file,kind=="chunk"?[1]:[]);var server=Task.Run(async()=>{using var c=await l.AcceptTcpClientAsync(ct.Token);using var s=c.GetStream();var offer=await Read(s,ct.Token);await Write(s,new(kind=="ready-id"?Guid.NewGuid():offer.Id,kind=="ready-sequence"?1u:0u,StreamRecordKind.Ready,[]),ct.Token);if(kind.StartsWith("ready"))return;var record=await Read(s,ct.Token);await Write(s,new(Guid.NewGuid(),record.Sequence,kind=="chunk"?StreamRecordKind.ChunkAck:StreamRecordKind.Done,[]),ct.Token);});bool rejected=false;try{await StreamTransfer.SendFilesAsync("127.0.0.1",((IPEndPoint)l.LocalEndpoint).Port,key,new[]{file},cancellationToken:ct.Token);}catch{rejected=true;}await server;Check(rejected);}
try{
foreach(var kind in new[]{"ready-id","ready-sequence","chunk","done"})await WrongAck(kind);
var reserve=typeof(StreamTransfer).GetMethod("ReserveCache",BindingFlags.Static|BindingFlags.NonPublic)!;
IDisposable Take(ulong bytes)=>(IDisposable)reserve.Invoke(null,new object[]{root,bytes})!;
bool Rejected(ulong bytes){try{using var r=Take(bytes);return false;}catch(TargetInvocationException e)when(e.InnerException is StreamProtocolException){return true;}}
using(var a=Take(StreamProtocol.MaxFileBytes))using(var b=Take(StreamProtocol.MaxFileBytes))Check(Rejected(1));
string complete=Path.Combine(root,".lcs2-complete-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(complete);File.WriteAllBytes(Path.Combine(complete,"synthetic"),[1]);using(var a=Take(StreamProtocol.MaxFileBytes))Check(Rejected(StreamProtocol.MaxFileBytes));
Console.WriteLine($"PASS: {passed} extra checks: fragmented wrong ready/chunk/done UUID or sequence refused; parallel 10 GiB reservations and completed cache accounted (no allocation of file bodies).");
}finally{CryptographicOperations.ZeroMemory(key);if(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(root,true);}
