using System.Net.NetworkInformation;

namespace LightClip;
internal sealed class GroupForm:Form
{
    readonly GroupHost host;readonly TextBox name=new(),evidence=new();readonly CheckBox startup=new();readonly Label status=new(),group=new(),codeLabel=new();
    readonly ListView nearby=new();readonly Button join=new(),create=new(),invite=new(),leave=new();readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    string? invitationCode;bool refreshing;
    public GroupForm(GroupHost host)
    {
        this.host=host;Text="轻剪 / LightClip 0.3.0 · 局域网同步群组";Icon=(Icon)host.AppIcon.Clone();Font=new Font("Microsoft YaHei UI",10);
        ClientSize=new(720,820);MinimumSize=Size;MaximumSize=Size;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Label("轻剪  ·  局域网同步群组",24,18,660,35,18);
        Label("设备名",24,70,90);name.SetBounds(110,66,385,30);name.Text=host.Profile.Name;name.AccessibleName="设备名";Controls.Add(name);
        Button("保存名字与偏好",510,64,185,()=>Save());
        Label("本机地址："+LocalIps(),24,112,670);
        group.SetBounds(24,147,670,32);Controls.Add(group);
        create.Text="创建新群组";create.SetBounds(24,188,140,36);create.Click+=(_,_)=>Safe(()=>{if(Save())host.CreateGroup();});Controls.Add(create);
        invite.Text="生成配对码";invite.SetBounds(178,188,140,36);invite.Click+=(_,_)=>Safe(()=>{invitationCode=host.Pairing?.OpenInvitation();UpdateCode();});Controls.Add(invite);
        Button("关闭配对码",332,188,135,()=>{host.Pairing?.CloseInvitation();invitationCode=null;UpdateCode();});
        leave.Text="退出群组";leave.SetBounds(481,188,135,36);leave.Click+=(_,_)=>
        {if(MessageBox.Show(this,"退出后本机停止同步；重新加入需要新的配对码。其他成员和旧版配对文件保留。","退出群组",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)==DialogResult.OK)Safe(()=>host.LeaveGroup());};Controls.Add(leave);
        codeLabel.SetBounds(24,238,670,32);codeLabel.Font=new Font(Font.FontFamily,14,FontStyle.Bold);Controls.Add(codeLabel);
        Label("附近设备（发现公告未认证；配对码完成身份确认）",24,282,670);
        nearby.SetBounds(24,312,670,174);nearby.View=View.Details;nearby.FullRowSelect=true;nearby.MultiSelect=false;nearby.HideSelection=false;
        nearby.Columns.Add("设备名",210);nearby.Columns.Add("当前地址",150);nearby.Columns.Add("群组",140);nearby.Columns.Add("连接",160);nearby.AccessibleName="附近设备";Controls.Add(nearby);
        join.Text="加入所选设备的群组";join.SetBounds(24,498,210,36);join.Click+=async(_,_)=>
        {
            if(nearby.SelectedItems.Count!=1||nearby.SelectedItems[0].Tag is not NearbyDevice device)return;
            if(device.GroupId==null){host.Report("请先让对方创建群组并显示配对码");return;}
            if(!Save())return;
            using var dialog=new PairCodeForm(host.AppIcon,device.Name);
            if(dialog.ShowDialog(this)==DialogResult.OK)
            {string code=dialog.TakeCode();join.Enabled=false;try{await host.Join(device,code);}finally{code="";RefreshState();}}
        };Controls.Add(join);
        Button("暂停 / 恢复",248,498,140,()=>host.Toggle());Button("测试群组连接",402,498,160,async()=>await host.TestConnection());
        startup.Text="登录 Windows 时自动启动并连接已保存群组";startup.AutoSize=true;startup.Location=new(24,550);startup.Checked=host.Profile.AutoStart;Controls.Add(startup);
        status.SetBounds(24,586,670,46);Controls.Add(status);
        evidence.SetBounds(24,638,670,88);evidence.Multiline=true;evidence.ReadOnly=true;evidence.ScrollBars=ScrollBars.Vertical;evidence.WordWrap=false;evidence.Font=new Font("Consolas",8);evidence.AccessibleName="轻剪通信证据";Controls.Add(evidence);
        Button("取消当前传输",24,737,150,()=>host.CancelTransfers());Button("打开接收文件夹",188,737,170,()=>host.OpenFolder());Button("退出轻剪",546,737,148,()=>host.ExitApp());Label("关闭窗口后继续在托盘运行。文字10 MiB；文件32个、合计5 GiB。",24,785,680,24,9);
        host.Changed+=RefreshState;FormClosed+=(_,_)=>{host.Changed-=RefreshState;timer.Stop();timer.Dispose();Icon?.Dispose();};timer.Tick+=(_,_)=>UpdateCode();timer.Start();RefreshState();
    }
    void Label(string s,int x,int y,int w,int h=28,int size=10)=>Controls.Add(new Label{Text=s,Location=new(x,y),Size=new(w,h),Font=new Font(Font.FontFamily,size)});
    void Button(string s,int x,int y,int w,Action click){var b=new Button{Text=s,Location=new(x,y),Size=new(w,36)};b.Click+=(_,_)=>click();Controls.Add(b);}
    bool Save(){try{host.SavePreferences(name.Text,startup.Checked);return true;}catch{host.Report("设备名不能为空、不能包含控制字符，且 UTF-8 最多 96 字节；偏好保存失败");return false;}}
    void Safe(Action a){try{a();}catch{host.Report("操作未完成：检查本机文件权限和端口状态；原数据保留");}}
    void UpdateCode()
    {
        if(host.Pairing?.InvitationOpen!=true){invitationCode=null;codeLabel.Text="未开放配对邀请";return;}
        codeLabel.Text=invitationCode==null?"配对邀请已开放；关闭后重新生成可查看":"配对码："+invitationCode+"   剩余 "+Math.Max(0,(host.Pairing.InvitationExpiry-Environment.TickCount64+999)/1000)+" 秒";
    }
    void RefreshState()
    {
        if(IsDisposed||refreshing)return;refreshing=true;
        try
        {
            bool paired=host.Profile.GroupId!=null;group.Text=paired?$"群组已保存：{host.Profile.GroupId}  ·  {(host.Engine.Active?"同步启用":"同步暂停")}":"尚未加入群组 · 不读取剪贴板、不监听内容";
            create.Enabled=!paired;join.Enabled=!paired;invite.Enabled=host.Group!=null&&host.Pairing!=null;leave.Enabled=paired;
            var selected=nearby.SelectedItems.Count==1?(nearby.SelectedItems[0].Tag as NearbyDevice)?.Id:null;
            var devices=host.Discovery?.Devices??[];
            string signature=string.Join('|',devices.Select(d=>$"{d.Id}/{d.Name}/{d.Host}/{d.GroupId}/{host.ConnectionState.GetValueOrDefault(d.Host)}"));
            if(nearby.Tag as string!=signature)
            {
                nearby.BeginUpdate();nearby.Items.Clear();foreach(var d in devices)
                {
                    bool same=paired&&d.GroupId==host.Profile.GroupId;var item=new ListViewItem(d.Name){Tag=d};item.SubItems.Add(d.Host.ToString());item.SubItems.Add(same?"当前群组":d.GroupId==null?"尚未建组":"其他群组");item.SubItems.Add(same?(host.ConnectionState.GetValueOrDefault(d.Host)?"已验证 ACK":"待测试连接"):"未认证");nearby.Items.Add(item);if(d.Id==selected)item.Selected=true;
                }
                nearby.EndUpdate();nearby.Tag=signature;
            }
            status.Text=host.Status;evidence.Text=string.Join(Environment.NewLine,host.Evidence);evidence.SelectionStart=evidence.TextLength;evidence.ScrollToCaret();UpdateCode();
        }
        finally{refreshing=false;}
    }
    static string LocalIps()=>string.Join("   ",NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!a.Address.ToString().StartsWith("169.254.")).Select(a=>a.Address.ToString()).Distinct());
}
internal sealed class PairCodeForm:Form
{
    readonly TextBox code=new();string value="";
    public PairCodeForm(Icon icon,string deviceName)
    {
        Text="加入群组";Icon=(Icon)icon.Clone();Font=new Font("Microsoft YaHei UI",10);ClientSize=new(440,205);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
        Controls.Add(new Label{Text="请输入“"+deviceName+"”显示的 8 位配对码",Location=new(20,20),Size=new(400,50)});
        code.SetBounds(20,80,400,32);code.MaxLength=8;code.UseSystemPasswordChar=true;code.AccessibleName="8 位配对码";Controls.Add(code);
        var ok=new Button{Text="加密配对",Location=new(180,140),Size=new(115,35)};ok.Click+=(_,_)=>{if(code.Text.Length!=8||code.Text.Any(c=>c<'0'||c>'9')){MessageBox.Show(this,"请输入 8 位 ASCII 数字。","配对码格式");return;}value=code.Text;code.Clear();DialogResult=DialogResult.OK;};Controls.Add(ok);
        var cancel=new Button{Text="取消",Location=new(305,140),Size=new(115,35),DialogResult=DialogResult.Cancel};Controls.Add(cancel);AcceptButton=ok;CancelButton=cancel;FormClosed+=(_,_)=>code.Clear();
    }
    public string TakeCode(){string result=value;value="";return result;}
    protected override void Dispose(bool disposing){if(disposing){value="";Icon?.Dispose();}base.Dispose(disposing);}
}

