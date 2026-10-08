using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace LightClip;
internal sealed record Profile(Guid DeviceId,string Name,Guid? GroupId=null,bool Paused=true,bool AutoStart=true);
internal sealed record SyncGroup(Guid Id,byte[] Key):IDisposable
{public void Dispose()=>CryptographicOperations.ZeroMemory(Key);}
internal static class GroupStore
{
    public static string Root=Path.Combine(Store.Root,"group-v2");
    public static bool IsTest;
    public static bool HasLegacy=>File.Exists(Path.Combine(Store.Root,"pairing.dpapi"))||File.Exists(Path.Combine(Store.Root,"shared-key.dpapi"));
    public static bool ValidName(string name)=>!string.IsNullOrWhiteSpace(name)&&Protocol.Utf8.GetByteCount(name)<=96&&!name.Any(char.IsControl);
    public static string FriendlyName()
    {
        string[] adjectives=["甜甜的","开心的","慢悠悠的","圆滚滚的","暖暖的","会飞的","闪亮的","安静的","元气满满的","爱笑的"];
        string[] fruits=["香蕉","苹果","桃子","橘子","草莓","菠萝","芒果","西瓜","葡萄","樱桃","柠檬","椰子"];
        return adjectives[RandomNumberGenerator.GetInt32(adjectives.Length)]+fruits[RandomNumberGenerator.GetInt32(fruits.Length)];
    }
    public static Profile Load()
    {
        string file=Path.Combine(Root,"profile.json");
        if(File.Exists(file))
        {
            var p=JsonSerializer.Deserialize<Profile>(File.ReadAllText(file))??throw new InvalidDataException();
            if(p.DeviceId==Guid.Empty || !ValidName(p.Name))throw new InvalidDataException();return p;
        }
        var created=new Profile(Guid.NewGuid(),FriendlyName());SaveProfile(created);return created;
    }
    public static SyncGroup? LoadGroup(Profile p)
    {
        if(p.GroupId==null)return null;
        byte[] protectedBytes=File.ReadAllBytes(Path.Combine(Root,"group.dpapi"));
        byte[] plain=Store.Protect(protectedBytes,true);
        try
        {
            if(plain.Length!=48 || new Guid(plain.AsSpan(0,16),bigEndian:true)!=p.GroupId)throw new InvalidDataException();
            return new(p.GroupId.Value,plain.AsSpan(16,32).ToArray());
        }
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    public static Profile SaveGroup(Profile p,SyncGroup group)
    {
        if(group.Key.Length!=32)throw new InvalidDataException();
        byte[] plain=new byte[48];group.Id.TryWriteBytes(plain.AsSpan(0,16),true,out _);group.Key.CopyTo(plain,16);
        try{Atomic("group.dpapi",Store.Protect(plain,false));}finally{CryptographicOperations.ZeroMemory(plain);}
        var paired=p with{GroupId=group.Id,Paused=false};SaveProfile(paired);ConfigureStartup(paired);return paired;
    }
    public static void SaveProfile(Profile p){if(!ValidName(p.Name))throw new InvalidDataException();Atomic("profile.json",JsonSerializer.SerializeToUtf8Bytes(p));}
    static void Atomic(string name,byte[] bytes)
    {Directory.CreateDirectory(Root);string path=Path.Combine(Root,name);File.WriteAllBytes(path+".tmp",bytes);File.Move(path+".tmp",path,true);}
    public static void ConfigureStartup(Profile p)
    {
        if(IsTest)return;
        using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(p.GroupId!=null && p.AutoStart)run.SetValue("LightClip.Group","\""+Environment.ProcessPath+"\" --background");
        else run.DeleteValue("LightClip.Group",false);
        // Legacy data remains intact; retire only known LightClip startup entries on successful migration.
        if(p.GroupId!=null){run.DeleteValue("LightClip",false);run.DeleteValue("LightClip.Windows",false);}
    }
    public static Profile Leave(Profile p)
    {
        var left=p with{GroupId=null,Paused=true,AutoStart=false};SaveProfile(left);ConfigureStartup(left);
        string file=Path.Combine(Root,"group.dpapi");if(File.Exists(file))File.Delete(file);return left;
    }
}
