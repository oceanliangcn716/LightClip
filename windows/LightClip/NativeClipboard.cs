using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LightClip;

internal sealed record ClipboardContent(Packet? Packet=null,string[]? Files=null,bool Suppressed=false);
internal static class NativeClipboard
{
    [DllImport("user32.dll", SetLastError=true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] static extern int GetObject(IntPtr obj, int size, out NativeBitmap bitmap);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern uint DragQueryFile(IntPtr drop,uint index,StringBuilder? file,uint length);
    [StructLayout(LayoutKind.Sequential)] struct NativeBitmap { public int type,width,height,widthBytes; public ushort planes,bits; public IntPtr data; }
    public static readonly uint PngFormat = RegisterClipboardFormat("PNG");
    public static readonly uint Concealed = RegisterClipboardFormat("org.nspasteboard.ConcealedType");
    public static readonly uint Origin = RegisterClipboardFormat("LightClip.Origin.v1");
    static readonly string[] Sensitive = ["concealed","transient","password","1password","keepass","lastpass","bitwarden","enpass","dashlane","roboform","clipboard viewer ignore","excludeclipboardcontentfrommonitorprocessing","excludefromclipboardcontent","canincludeinclipboardhistory","canuploadtocloudclipboard"];
    public static int Reads {get; private set;}
    public static bool Busy {get; private set;}
    static void RequireSta() { if (Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA) throw new InvalidOperationException("剪贴板需要 STA"); }
    static bool Open(IntPtr owner) { RequireSta(); Busy = !OpenClipboard(owner); return !Busy; }

    static byte[]? ReadBytes(uint format, int limit)
    {
        IntPtr handle=GetClipboardData(format); if(handle==IntPtr.Zero)return null;
        nuint size=GlobalSize(handle); if(size==0)return null;
        if(size>(nuint)limit) throw new InvalidDataException("内容超限，已跳过");
        IntPtr ptr=GlobalLock(handle); if(ptr==IntPtr.Zero) throw new IOException("剪贴板读取失败");
        try { var bytes=new byte[(int)size]; Marshal.Copy(ptr,bytes,0,bytes.Length); return bytes; }
        finally {GlobalUnlock(handle);}
    }

    public static Packet? Read(IntPtr owner, bool allowOrigin=false)=>Capture(owner,allowOrigin,false)?.Packet;
    public static ClipboardContent? Capture(IntPtr owner,bool allowOrigin=false,bool streamEnabled=true)
    {
        if(!Open(owner))return null;
        try
        {
            Reads++;
            var formats=new HashSet<uint>();
            for(uint f=EnumClipboardFormats(0);f!=0;f=EnumClipboardFormats(f))
            {
                formats.Add(f);
                if(!allowOrigin && f==Origin)return new(Suppressed:true);
                if(f>=0xc000)
                {
                    var name=new StringBuilder(256); GetClipboardFormatName(f,name,256);
                    string lower=name.ToString().ToLowerInvariant();
                    // Cloud/history opt-out formats can be present with value 1 on ordinary data.
                    if(lower is "canincludeinclipboardhistory" or "canuploadtocloudclipboard")
                    { var value=ReadBytes(f,64); if(!allowOrigin && value is {Length:>=4} && BitConverter.ToInt32(value)==0)return new(Suppressed:true); }
                    else if(Sensitive.Any(lower.Contains))return new(Suppressed:true);
                }
            }
            if(formats.Contains(15))return streamEnabled?new(Files:ReadFilePaths()):new(ReadImageFileDrop());
            uint pngFormat=new[]{PngFormat,RegisterClipboardFormat("image/png"),RegisterClipboardFormat("public.png")}.FirstOrDefault(formats.Contains);
            if(pngFormat!=0)
            {
                byte[] png=ReadBytes(pngFormat,Protocol.ImageLimit) ?? throw new InvalidDataException();
                using var check=Png.Decode(png); return new(Protocol.New(2,png));
            }
            if(formats.Contains(2))
            {
                IntPtr hb=GetClipboardData(2);
                if(GetObject(hb,Marshal.SizeOf<NativeBitmap>(),out var bm)==0 || bm.width<=0 || bm.height<=0 || (long)bm.width*bm.height>16_000_000)throw new InvalidDataException("图片尺寸超限");
                using var image=Image.FromHbitmap(hb);
                using var output=new LimitedStream(Protocol.ImageLimit); image.Save(output,ImageFormat.Png);
                return new(Protocol.New(2,output.ToArray()));
            }
            if(formats.Contains(13))
            {
                int textLimit=streamEnabled?10_485_760:Protocol.TextLimit;
                byte[] unicode=ReadBytes(13,2*(textLimit+1))??[];
                if(unicode.Length%2!=0)throw new InvalidDataException();
                int end=0; while(end+1<unicode.Length && (unicode[end]!=0 || unicode[end+1]!=0))end+=2;
                if(end+1>=unicode.Length)throw new InvalidDataException();
                string text=new UnicodeEncoding(false,false,true).GetString(unicode,0,end);
                if(Protocol.Utf8.GetByteCount(text)>textLimit)throw new InvalidDataException("文字超限，已跳过");
                return new(Protocol.New(1,Protocol.Utf8.GetBytes(text)));
            }
            return null;
        }
        finally{CloseClipboard();}
    }
    static string[] ReadFilePaths()
    {
        IntPtr drop=GetClipboardData(15);uint count=DragQueryFile(drop,uint.MaxValue,null,0);
        if(count is <1 or >32)throw new InvalidDataException();var paths=new string[count];ulong total=0;
        for(uint i=0;i<count;i++)
        {
            uint length=DragQueryFile(drop,i,null,0);if(length is 0 or >32767)throw new InvalidDataException();
            var buffer=new StringBuilder((int)length+1);if(DragQueryFile(drop,i,buffer,length+1)!=length)throw new InvalidDataException();
            string path=buffer.ToString();ValidateLocalFile(path);var info=new FileInfo(path);
            total=checked(total+(ulong)info.Length);if((ulong)info.Length>5_368_709_120UL||total>5_368_709_120UL)throw new InvalidDataException();paths[i]=path;
        }
        return paths;
    }
    internal static void ValidateLocalFile(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal)||new DriveInfo(Path.GetPathRoot(path)!).DriveType==DriveType.Network)throw new InvalidDataException();
        var info=new FileInfo(path);if(!info.Exists||(info.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new InvalidDataException();
        for(var parent=info.Directory;parent!=null;parent=parent.Parent)if((parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
    }
    static Packet? ReadImageFileDrop()
    {
        IntPtr drop=GetClipboardData(15);if(drop==IntPtr.Zero||DragQueryFile(drop,uint.MaxValue,null,0)!=1)return null;
        uint length=DragQueryFile(drop,0,null,0);if(length is 0 or >32767)return null;
        var pathBuffer=new StringBuilder((int)length+1);if(DragQueryFile(drop,0,pathBuffer,length+1)!=length)return null;
        return ImageFilePacket(pathBuffer.ToString());
    }
    internal static Packet? ImageFilePacket(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal))return null;
        string extension=Path.GetExtension(path).ToLowerInvariant();if(extension is not(".png" or ".jpg" or ".jpeg"))return null;
        if(new DriveInfo(Path.GetPathRoot(path)!).DriveType==DriveType.Network)return null;
        var info=new FileInfo(path);if(!info.Exists||(info.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||info.Length is <=0 or >67_108_864)return null;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
        if(file.Length!=info.Length)return null;byte[] source=new byte[(int)file.Length];file.ReadExactly(source);if(file.Length!=source.Length)return null;
        var (w,h)=ImageDimensions(source,extension==".png");if(w<=0||h<=0||(long)w*h>16_000_000)return null;
        if(extension==".png"&&source.Length<=Protocol.ImageLimit){using var checkedImage=Png.Decode(source);return Protocol.New(2,source);}
        using var stream=new MemoryStream(source,false);using var decoded=Image.FromStream(stream,false,true);
        if(decoded.Width!=w||decoded.Height!=h)return null;
        using var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(bitmap)){graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;graphics.DrawImage(decoded,0,0,w,h);}
        using var output=new LimitedStream(Protocol.ImageLimit);bitmap.Save(output,ImageFormat.Png);byte[] png=output.ToArray();Png.Validate(png);return Protocol.New(2,png);
    }
    public static bool WriteFiles(IntPtr owner,IReadOnlyList<string> paths,uint expectedSequence,CancellationToken cancellation)
    {
        RequireSta();if(paths.Count is <1 or >32)throw new InvalidDataException();
        Packet? image=null;if(paths.Count==1){try{image=ImageFilePacket(paths[0]);}catch(Exception e)when(e is not OutOfMemoryException){}}
        using Bitmap? bitmap=image==null?null:Png.Decode(image.Body);
        cancellation.ThrowIfCancellationRequested();if(GetClipboardSequenceNumber()!=expectedSequence)throw new OperationCanceledException();
        if(!Open(owner))return false;
        try
        {
            cancellation.ThrowIfCancellationRequested();if(GetClipboardSequenceNumber()!=expectedSequence)throw new OperationCanceledException();
            if(!EmptyClipboard())throw new IOException();
            Put(Origin,Guid.NewGuid().ToByteArray(bigEndian:true));
            Put(RegisterClipboardFormat("CanIncludeInClipboardHistory"),new byte[4]);Put(RegisterClipboardFormat("CanUploadToCloudClipboard"),new byte[4]);
            byte[] names=Encoding.Unicode.GetBytes(string.Join('\0',paths)+"\0\0");byte[] drop=new byte[20+names.Length];
            BinaryPrimitives.WriteInt32LittleEndian(drop,20);BinaryPrimitives.WriteInt32LittleEndian(drop.AsSpan(16),1);names.CopyTo(drop,20);Put(15,drop);
            Put(RegisterClipboardFormat("Preferred DropEffect"),BitConverter.GetBytes(1));
            if(bitmap!=null){Put(PngFormat,image!.Body);Put(17,Dib(bitmap,true));Put(8,Dib(bitmap,false));}
            return true;
        }
        finally{CloseClipboard();}
    }
    internal static (int Width,int Height) ImageDimensions(ReadOnlySpan<byte> data,bool png)
    {
        if(png)
        {
            if(data.Length<33||!data[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||BinaryPrimitives.ReadUInt32BigEndian(data[8..])!=13||!data.Slice(12,4).SequenceEqual("IHDR"u8))throw new InvalidDataException();
            uint w=BinaryPrimitives.ReadUInt32BigEndian(data[16..]),h=BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            if(w>int.MaxValue||h>int.MaxValue)throw new InvalidDataException();return((int)w,(int)h);
        }
        if(data.Length<4||data[0]!=255||data[1]!=216)throw new InvalidDataException();int offset=2;
        while(offset+1<data.Length)
        {
            if(data[offset++]!=255)throw new InvalidDataException();while(offset<data.Length&&data[offset]==255)offset++;
            if(offset>=data.Length)break;byte marker=data[offset++];if(marker is 217 or 218)break;
            if(marker==1||marker is >=208 and <=215)continue;
            if(offset+2>data.Length)break;int size=BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);if(size<2||size>data.Length-offset)break;
            if(marker is >=192 and <=207 && marker is not(196 or 200 or 204))
            {if(size<7)break;return(BinaryPrimitives.ReadUInt16BigEndian(data[(offset+5)..]),BinaryPrimitives.ReadUInt16BigEndian(data[(offset+3)..]));}
            offset+=size;
        }
        throw new InvalidDataException();
    }
    static void Put(uint format,byte[] bytes)
    {
        IntPtr mem=GlobalAlloc(0x42,(nuint)Math.Max(1,bytes.Length)); if(mem==IntPtr.Zero)throw new OutOfMemoryException();
        try
        {
            IntPtr ptr=GlobalLock(mem); if(ptr==IntPtr.Zero)throw new IOException();
            try{Marshal.Copy(bytes,0,ptr,bytes.Length);}finally{GlobalUnlock(mem);}
            if(SetClipboardData(format,mem)==IntPtr.Zero)throw new IOException("剪贴板写入失败");
            mem=IntPtr.Zero;
        }
        finally{if(mem!=IntPtr.Zero)GlobalFree(mem);}
    }
    static byte[] Dib(Bitmap bitmap,bool alpha)
    {
        int header=alpha?124:40, pixels=checked(bitmap.Width*bitmap.Height*4);
        var result=new byte[header+pixels];
        BinaryPrimitives.WriteInt32LittleEndian(result,header);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4),bitmap.Width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8),-bitmap.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12),1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14),32);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20),pixels);
        if(alpha)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16),3);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40),0x00ff0000);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44),0x0000ff00);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48),0x000000ff);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52),0xff000000);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56),0x73524742);
        }
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{for(int y=0;y<bitmap.Height;y++)Marshal.Copy(data.Scan0+y*data.Stride,result,header+y*bitmap.Width*4,bitmap.Width*4);}
        finally{bitmap.UnlockBits(data);}
        if(!alpha) for(int i=header;i<result.Length;i+=4)
        {int a=result[i+3];for(int c=0;c<3;c++)result[i+c]=(byte)((result[i+c]*a+255*(255-a))/255);result[i+3]=0;}
        return result;
    }

    public static bool Write(IntPtr owner,Packet packet,bool remote=true,bool concealed=false,CancellationToken cancellation=default,uint? expectedSequence=null)
    {
        RequireSta();
        // Decode every pixel before opening or changing the real clipboard.
        using Bitmap? bitmap=packet.Kind==2?Png.Decode(packet.Body):null;
        string? text=packet.Kind==1?Protocol.Utf8.GetString(packet.Body):null;
        if(text?.Contains('\0')==true)throw new InvalidDataException("Windows 文本剪贴板不支持嵌入 NUL");
        cancellation.ThrowIfCancellationRequested();if(!Open(owner))return false;
        try
        {
            cancellation.ThrowIfCancellationRequested();if(expectedSequence.HasValue&&GetClipboardSequenceNumber()!=expectedSequence.Value)throw new OperationCanceledException();
            if(!EmptyClipboard())throw new IOException();
            if(remote)Put(Origin,packet.Id.ToByteArray(bigEndian:true));
            if(concealed)Put(Concealed,[1]);
            // Prevent the Windows clipboard history/cloud services from persisting received data.
            if(remote || concealed)
            {Put(RegisterClipboardFormat("CanIncludeInClipboardHistory"),new byte[4]);Put(RegisterClipboardFormat("CanUploadToCloudClipboard"),new byte[4]);}
            if(text!=null)Put(13,Encoding.Unicode.GetBytes(text+"\0"));
            else if(bitmap!=null){Put(PngFormat,packet.Body);Put(17,Dib(bitmap,true));Put(8,Dib(bitmap,false));}
            else throw new InvalidDataException();
            return true;
        }
        finally{CloseClipboard();}
    }
}

internal sealed class LimitedStream(int limit):MemoryStream
{
    public override void Write(byte[] buffer,int offset,int count){if(Position+count>limit)throw new InvalidDataException("图片超过 8 MiB");base.Write(buffer,offset,count);}
    public override void Write(ReadOnlySpan<byte> buffer){if(Position+buffer.Length>limit)throw new InvalidDataException("图片超过 8 MiB");base.Write(buffer);}
    public override void WriteByte(byte value){if(Position+1>limit)throw new InvalidDataException();base.WriteByte(value);}
}
