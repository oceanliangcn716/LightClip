using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace LightClip;
internal sealed record Config(string Peer="",bool Enabled=false,bool Startup=false);
internal static class Store
{
    public static string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LightClip");
    public static Config Load(){try{return JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(Root,"settings.json")))??new();}catch{return new();}}
    [StructLayout(LayoutKind.Sequential)] struct Blob {public int size;public IntPtr data;}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr mem);
    internal static byte[] Protect(byte[] bytes,bool decrypt)
    {
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var input=new Blob{size=bytes.Length,data=pinned.AddrOfPinnedObject()}; Blob output=default;
        try
        {
            bool ok=decrypt?CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptProtectData(ref input,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new CryptographicException("DPAPI 失败");
            var result=new byte[output.size];Marshal.Copy(output.data,result,0,result.Length);return result;
        }
        finally{if(output.data!=IntPtr.Zero)LocalFree(output.data);pinned.Free();}
    }
    public static byte[]? LoadKey(){try{byte[] key=Protect(File.ReadAllBytes(Path.Combine(Root,"pairing.dpapi")),true);if(key.Length==32)return key;CryptographicOperations.ZeroMemory(key);return null;}catch{return null;}}
    public static void Save(Config config,byte[] key,bool setStartup=true)
    {
        Directory.CreateDirectory(Root);
        Atomic("pairing.dpapi",Protect(key,false));
        SaveConfig(config);
        if(setStartup)
        {
            using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(config.Startup)run.SetValue("LightClip","\""+Environment.ProcessPath+"\"");else run.DeleteValue("LightClip",false);
        }
    }
    public static void SaveConfig(Config config){Directory.CreateDirectory(Root);Atomic("settings.json",JsonSerializer.SerializeToUtf8Bytes(config));}
    static void Atomic(string name,byte[] bytes){string file=Path.Combine(Root,name);File.WriteAllBytes(file+".tmp",bytes);File.Move(file+".tmp",file,true);}
    public static IPAddress ParseIp(string text)
    {
        text=text.Trim();
        if(!text.Contains(':') && (text.Split('.').Length!=4 || text.Split('.').Any(s=>s.Length==0 || s.Any(c=>c<'0'||c>'9') || !byte.TryParse(s,out _))))throw new FormatException();
        if(!IPAddress.TryParse(text,out var ip)||IPAddress.Any.Equals(ip)||IPAddress.IPv6Any.Equals(ip)||ip.IsIPv6Multicast || (ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes()[0]>=224))throw new FormatException();
        return ip;
    }
    public static byte[] ParseKey(string text){byte[] key=Convert.FromBase64String(text.Trim());if(key.Length!=32 || Convert.ToBase64String(key)!=text.Trim()){CryptographicOperations.ZeroMemory(key);throw new FormatException();}return key;}
}
