using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LightClip;
internal static class Program
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    internal static readonly uint ShowMessage=RegisterWindowMessage("LightClip.Group.Show.v2");
    internal static readonly uint QuitMessage=RegisterWindowMessage("LightClip.Group.Quit.v3");
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string? className,string title);
    [STAThread]static void Main(string[] args)
    {
        if(args.Contains("--quit")){SendMessageTimeout(new IntPtr(0xffff),QuitMessage,IntPtr.Zero,IntPtr.Zero,2,1000,out _);return;}
        if(args.Contains("--stop-previous")){var hwnd=FindWindow(null,"LightClip group message window");if(hwnd!=IntPtr.Zero)SendMessageTimeout(hwnd,0x10,IntPtr.Zero,IntPtr.Zero,2,1000,out _);return;}
        if(args.Length==2 && args[0]=="--dpapi-group-probe")
        {
            try{GroupStore.Root=args[1];GroupStore.IsTest=true;var p=GroupStore.Load();using var g=GroupStore.LoadGroup(p)??throw new IOException();File.WriteAllBytes(Path.Combine(args[1],"probe.frame"),Protocol.Encode(Protocol.New(0),g.Key));Environment.ExitCode=0;}catch{Environment.ExitCode=1;}return;
        }
        bool streamTest=args.Length==2&&(args[0]=="--stream-self-test"||args[0]=="--stream-clipboard-test"),uiFixture=args.Contains("--ui-clipboard-fixture");
        bool test=args.Length==2&&args[0]=="--self-test"||streamTest||uiFixture;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using var mutex=new Mutex(true,"Local\\LightClip.v1."+Environment.UserName,out bool first);
        if(!test&&!first){SendMessageTimeout(new IntPtr(0xffff),ShowMessage,IntPtr.Zero,IntPtr.Zero,2,1000,out _);return;}
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=(_,_)=>MessageBox.Show("轻剪遇到错误，请重新打开。配对数据已保留。","轻剪",MessageBoxButtons.OK,MessageBoxIcon.Error);
        try
        {
            using var host=new GroupHost(test,args.Contains("--background"));
            if(test)host.BeginInvoke((Action)(async()=>{if(uiFixture){string p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LightClipBuild","ExplorerPasteFixture","synthetic-lightclip.png");NativeClipboard.WriteFiles(host.Handle,new[]{p},NativeClipboard.GetClipboardSequenceNumber(),CancellationToken.None);}else if(streamTest)await StreamTests.Run(host,args[1],args[0]=="--stream-self-test");else await GroupTests.Run(host,args[1]);host.ExitApp();}));
            Application.Run(host);
        }
        catch{MessageBox.Show("轻剪启动失败：请检查配置、端口或文件权限。已有配对数据没有清空。","轻剪");}
    }
}
internal sealed class GroupHost:Form
{
    public Profile Profile{get;private set;}=null!;
    public SyncGroup? Group{get;private set;}
    public GroupEngine Engine{get;}
    public GroupStreams Streams{get;}
    public Discovery? Discovery{get;private set;}
    public PairingService? Pairing{get;private set;}
    public string Status{get;private set;}="尚未加入群组";
    public readonly List<string> Evidence=[];
    public readonly Dictionary<IPAddress,bool> ConnectionState=[];
    public event Action? Changed;
    public Icon AppIcon{get;}
    readonly bool test;NotifyIcon? tray;GroupForm? settings;
    ToolStripMenuItem? statusItem,pauseItem;bool exiting,suspendedActive;uint sequence;int readGeneration,discoveryQueued;
    CancellationTokenSource? joining;
    public GroupHost(bool test,bool background=false)
    {
        this.test=test;Text="LightClip group message window";ShowInTaskbar=false;_=Handle;Engine=new(this);Streams=new(this);
        AppIcon=new Icon(Path.Combine(AppContext.BaseDirectory,"LightClip.ico"));
        Engine.Status+=s=>Ui(()=>{Status=s;RefreshStatus();});Engine.Evidence+=s=>Ui(()=>{if(Evidence.Count==100)Evidence.RemoveAt(0);Evidence.Add(s);Changed?.Invoke();});
        Engine.PeerState+=(ip,connected)=>Ui(()=>{ConnectionState[ip]=connected;Changed?.Invoke();});
        Engine.ClipboardWritten+=()=>RecordRemoteWrite();
        if(test)return;
        Profile=GroupStore.Load();
        try{Group=GroupStore.LoadGroup(Profile);}catch{Status="已保存群组暂无法解密，原文件保留；请使用原 Windows 账户";}
        var menu=new ContextMenuStrip();statusItem=new(Status){Enabled=false};menu.Items.Add(statusItem);
        pauseItem=new("暂停 / 恢复",null,(_,_)=>Toggle());menu.Items.Add(pauseItem);
        menu.Items.Add("测试群组连接",null,async(_,_)=>await TestConnection());
        menu.Items.Add("发送当前剪贴板",null,async(_,_)=>await ReadAndSend(true));
        menu.Items.Add("取消当前传输",null,(_,_)=>CancelTransfers());
        menu.Items.Add("打开接收文件夹",null,(_,_)=>OpenFolder());
        menu.Items.Add("打开设置…",null,(_,_)=>ShowSettings());menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出轻剪",null,(_,_)=>ExitApp());
        tray=new(){Icon=AppIcon,Visible=true,Text="轻剪 / LightClip 0.3.0",ContextMenuStrip=menu};tray.DoubleClick+=(_,_)=>ShowSettings();
        BeginInvoke((Action)(()=>
        {
            GroupStore.ConfigureStartup(Profile);StartDiscovery();if(Group!=null){StartPairing();if(!Profile.Paused)Resume();else Status="已暂停 · 群组已保存";}
            else if(Profile.GroupId==null)Status=GroupStore.HasLegacy?"旧配对已保留；请选择附近设备，用新配对码加入群组":"请选择附近设备加入群组";
            RefreshStatus();if(!background||Group==null)ShowSettings();
        }));
    }
    protected override void SetVisibleCore(bool value)=>base.SetVisibleCore(false);
    public void Ui(Action action){if(exiting||IsDisposed)return;if(InvokeRequired){try{BeginInvoke(action);}catch(InvalidOperationException){}}else action();}
    public Task UiAsync(Action action,CancellationToken ct=default)
    {
        if(!InvokeRequired){ct.ThrowIfCancellationRequested();action();return Task.CompletedTask;}
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try{BeginInvoke((Action)(()=>{try{ct.ThrowIfCancellationRequested();action();done.TrySetResult();}catch(Exception e){done.TrySetException(e);}}));}catch(Exception e){done.TrySetException(e);}return done.Task.WaitAsync(ct);
    }
    public void Report(string s){Status=s;RefreshStatus();}
    public void Trace(string s)=>Ui(()=>{if(Evidence.Count==100)Evidence.RemoveAt(0);Evidence.Add($"{DateTimeOffset.Now:HH:mm:ss.fff} {s}");Changed?.Invoke();});
    public void RecordRemoteWrite(bool cancelReceivers=true){sequence=NativeClipboard.GetClipboardSequenceNumber();readGeneration++;if(cancelReceivers)Streams.CancelTransfers();else Streams.CancelOutgoing();Engine.CancelSending();}
    public void CancelTransfers(){readGeneration++;Streams.CancelTransfers();Engine.CancelSending();Report("已取消当前传输 · 不补发旧内容");}
    public void OpenFolder(){try{Streams.OpenFolder();}catch{Report("接收文件夹暂不可用，请检查本机文件权限");}}
    void RefreshStatus()
    {
        if(statusItem!=null)statusItem.Text=Status;if(pauseItem!=null)pauseItem.Text=Engine.Active?"暂停同步":"恢复同步";
        if(tray!=null){string text="轻剪 · "+Status;tray.Text=text[..Math.Min(63,text.Length)];}Changed?.Invoke();
    }
    void StartDiscovery()
    {
        Discovery?.Dispose();Discovery=new(Profile.DeviceId,()=> (Profile.Name,Profile.GroupId));
        Discovery.Changed+=_=>
        {
            if(Interlocked.Exchange(ref discoveryQueued,1)==0)Ui(()=>{Interlocked.Exchange(ref discoveryQueued,0);UpdatePeers();Changed?.Invoke();});
        };
        Discovery.Error+=s=>Ui(()=>Report(s));
        try{Discovery.Start();}catch{Report("发现服务不可用：检查 UDP 49286 是否占用或被阻止");}
    }
    void StartPairing()
    {
        Pairing?.Dispose();if(Group==null)return;
        Pairing=new(Profile.DeviceId,()=>Profile.Name);Pairing.Status+=s=>Ui(()=>Report(s));Pairing.InvitationClosed+=()=>Ui(()=>Changed?.Invoke());
        try{Pairing.Start(Group);}catch{Report("配对邀请服务不可用：检查 TCP 49288 是否占用");}
    }
    public void UpdatePeers()
    {
        if(Group==null)return;var groupPeers=(Discovery?.Devices??[]).Where(d=>d.GroupId==Group.Id).Take(16).ToArray();Engine.UpdatePeers(groupPeers.Select(d=>new IPEndPoint(d.Host,Protocol.Port)));Streams.UpdatePeers(groupPeers.Select(d=>new IPEndPoint(d.Host,LightClip.Windows.StreamProtocol.Port)));
        var currentAddresses=(Discovery?.Devices??[]).Select(d=>d.Host).ToHashSet();foreach(var old in ConnectionState.Keys.Where(ip=>!currentAddresses.Contains(ip)).ToArray())ConnectionState.Remove(old);
    }
    public void SavePreferences(string name,bool autoStart)
    {
        if(!GroupStore.ValidName(name))throw new InvalidDataException();Profile=Profile with{Name=name,AutoStart=autoStart};GroupStore.SaveProfile(Profile);GroupStore.ConfigureStartup(Profile);Discovery?.Announce();RefreshStatus();
    }
    public void CreateGroup()
    {
        if(Group!=null||Profile.GroupId!=null)throw new InvalidOperationException();
        using var created=new SyncGroup(Guid.NewGuid(),RandomNumberGenerator.GetBytes(32));Persist(created);StartPairing();Discovery?.Announce();Resume();
    }
    void Persist(SyncGroup group)
    {
        var paired=GroupStore.SaveGroup(Profile,group);Group?.Dispose();Group=new(group.Id,group.Key.ToArray());Profile=paired;
    }
    public async Task Join(NearbyDevice target,string code)
    {
        if(Group!=null||Profile.GroupId!=null)throw new InvalidOperationException();joining?.Cancel();joining?.Dispose();joining=new();
        Report("正在加密配对…");bool saved=false;
        try
        {
            await PairingService.Join(target,code,Profile.DeviceId,Profile.Name,async(g,ct)=>{await UiAsync(()=>{Persist(g);saved=true;},ct);},joining.Token);
            StartPairing();Discovery?.Announce();Resume();Report("已加入群组 · 等待新复制内容");
        }
        catch
        {
            if(saved){StartPairing();Discovery?.Announce();Resume();Report("群组已安全保存；对端保存确认未完成，可测试连接");}
            else Report("配对未完成：请核对配对码、2 分钟有效期及网络");
        }
    }
    public void LeaveGroup()
    {
        joining?.Cancel();Pause(false);Pairing?.Dispose();Pairing=null;Group?.Dispose();Group=null;Profile=GroupStore.Leave(Profile);ConnectionState.Clear();Discovery?.Announce();Report("已退出群组 · 旧版配对文件仍保留");
    }
    public void Pause(bool persist=true)
    {readGeneration++;Engine.Stop();Streams.Stop();if(persist){Profile=Profile with{Paused=true};if(!test)GroupStore.SaveProfile(Profile);}Report("已暂停 · 群组保留，不读取剪贴板");}
    public void Resume()
    {
        if(Group==null){Report("请先加入或创建群组");return;}sequence=NativeClipboard.GetClipboardSequenceNumber();
        try{Streams.Start(Group.Key);Engine.Start(Group.Key);Profile=Profile with{Paused=false};if(!test)GroupStore.SaveProfile(Profile);UpdatePeers();Report("已启用 · 文字10 MiB / 文件32个、合计5 GiB");}
        catch{Engine.Stop();Streams.Stop();Report("内容监听失败：检查 TCP 49287/49289 占用、缓存权限或磁盘空间");}
    }
    public void Toggle(){if(Engine.Active)Pause();else Resume();}
    public Task<bool> TestConnection(){if(!Engine.Active){Report("请先恢复同步");return Task.FromResult(false);}return Engine.Send(Protocol.New(0));}
    public async Task ReadAndSend(bool manual=false)
    {
        if(!Engine.Active)return;int generation=++readGeneration;uint now=NativeClipboard.GetClipboardSequenceNumber();
        if(!manual&&sequence==now)return;sequence=now;Streams.CancelTransfers();Engine.CancelSending();
        try
        {
            for(int attempt=0;attempt<5;attempt++)
            {
                if(!Engine.Active||generation!=readGeneration||NativeClipboard.GetClipboardSequenceNumber()!=now)return;
                var content=NativeClipboard.Capture(Handle);if(!NativeClipboard.Busy){if(content is{Suppressed:false}){if(content.Files!=null||content.Packet is{Kind:1,Body.Length:>Protocol.TextLimit})await Streams.Send(content);else if(content.Packet!=null)await Engine.Send(content.Packet);}return;}await Task.Delay(25*(attempt+1));
            }
            Report("剪贴板繁忙，已跳过本次");
        }
        catch(Exception e)when(e is not StackOverflowException){Report("内容不是可同步图片/文字、已损坏或超限，已跳过");}
    }
    public void ShowSettings(){if(test)return;if(settings==null||settings.IsDisposed){settings=new(this);settings.FormClosed+=(_,_)=>settings=null;}settings.Show();settings.WindowState=FormWindowState.Normal;settings.Activate();}
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x31d){if(Engine.Active)_=ReadAndSend();m.Result=IntPtr.Zero;return;}
        if(m.Msg==(int)Program.ShowMessage){ShowSettings();m.Result=IntPtr.Zero;return;}
        if(m.Msg==(int)Program.QuitMessage){ExitApp();m.Result=IntPtr.Zero;return;}
        if(m.Msg==0x218)
        {if(m.WParam.ToInt32()==4){suspendedActive=Engine.Active;Pause(false);}if(m.WParam.ToInt32() is 7 or 18 && suspendedActive){suspendedActive=false;Resume();}}
        base.WndProc(ref m);
    }
    public void ExitApp()
    {
        if(exiting)return;exiting=true;readGeneration++;joining?.Cancel();joining?.Dispose();Engine.Dispose();Streams.Dispose();Pairing?.Dispose();Discovery?.Dispose();Group?.Dispose();Group=null;
        settings?.Close();if(tray!=null){tray.Visible=false;tray.Dispose();tray=null;}Close();Application.ExitThread();
    }
    protected override void Dispose(bool disposing){if(disposing){Engine.Dispose();Streams.Dispose();Pairing?.Dispose();Discovery?.Dispose();tray?.Dispose();Group?.Dispose();AppIcon.Dispose();}base.Dispose(disposing);}
}

