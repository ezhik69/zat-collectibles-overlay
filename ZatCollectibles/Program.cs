using System.Diagnostics;
using System.Text.Json;

namespace ZatCollectibles;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length==3&&args[0]=="--audit-maps")
        {
            var results=Directory.EnumerateFiles(args[1],"*.pc",SearchOption.AllDirectories).Select(path=>
            {
                try { var items=GameReader.ExtractCatalog(path).ToArray();
                    var alternate=GameReader.ExtractCatalog(path,65549).ToArray();
                    var boundaryStable=items.Select(i=>(i.Id,i.Kind,i.Position,i.ProgressIndex)).OrderBy(i=>i.Id).SequenceEqual(alternate.Select(i=>(i.Id,i.Kind,i.Position,i.ProgressIndex)).OrderBy(i=>i.Id));
                    return (object)new {path,boundaryStable,
                    gold=items.Count(i=>i.Kind=="Золото"),bottles=items.Count(i=>i.Kind=="Бутылка"),
                    duplicateIds=items.GroupBy(i=>i.Id).Where(g=>g.Count()>1).Select(g=>g.Key).ToArray(),
                    ids=items.Select(i=>new {i.Id,i.Kind,i.ProgressIndex})}; }
                catch(Exception ex) { return (object)new {path,error=ex.Message}; }
            }).ToArray();
            File.WriteAllText(args[2],JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
            return;
        }
        if(args.Length==2&&args[0]=="--diagnose")
        {
            try
            {
                var p=Process.GetProcessesByName("ZAT").FirstOrDefault()??throw new Exception("Игра не запущена.");
                using var reader=new GameReader(p);
                var watch=Stopwatch.StartNew();
                reader.Discover(CancellationToken.None);
                Thread.Sleep(200);
                reader.Discover(CancellationToken.None);
                var camera=reader.ReadCamera();var items=reader.ReadItems();
                for(var sample=0;sample<3;sample++) {Thread.Sleep(100);items=reader.ReadItems();}
                File.WriteAllText(args[1],JsonSerializer.Serialize(new {
                    pid=p.Id, map=reader.Map, scanMs=watch.ElapsedMilliseconds, scannedMB=reader.ScannedBytes/1048576,
                    camera=camera is null?null:new {position=new[]{camera.Position.X,camera.Position.Y,camera.Position.Z},camera.ScaleX,camera.ScaleY},
                    candidates=reader.Targets.Length, warning=reader.LastWarning, tracking=reader.Tracking,
                    targets=reader.Targets.Select(t=>new {t.Id,t.Kind,t.Address,t.Owner,t.OwnerVtable,t.MeshOffset,
                        currentOwnerVtable=reader.U32(t.Owner),currentOwnerMesh=reader.U32(t.Owner+t.MeshOffset),
                        stateB8=reader.U32(t.Owner+0xb8)}),
                    items=items.Select(i=>new {i.Id,i.Kind,position=new[]{i.Position.X,i.Position.Y,i.Position.Z},
                        distance=camera is null?0:System.Numerics.Vector3.Distance(camera.Position,i.Position),
                        screen=camera is null?null:Overlay.Project(i.Position,camera,2560,1440)})
                },new JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
            }
            catch(Exception ex) {File.WriteAllText(args[1],JsonSerializer.Serialize(new {error=ex.Message})); Environment.ExitCode=1;}
            return;
        }
        using var mutex=new Mutex(true,"ZatCollectibles.ExternalMenu",out var first);
        if(!first)return;
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly Label status=new(), counts=new(), details=new(), title=new();
    private readonly CheckBox enabled=new(), gold=new(), bottles=new();
    private readonly Button rescan=new(), close=new(), language=new();
    private bool english;
    private string statusRu="Ожидание запущенной игры…", statusEn="Waiting for the game…";
    private int? goldCount,bottleCount;
    private bool progressAvailable;
    private static readonly string LanguagePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZatCollectibles","language.txt");
    private readonly Overlay overlay=new();
    private readonly System.Windows.Forms.Timer connectionTimer=new() {Interval=1000};
    private readonly System.Windows.Forms.Timer frameTimer=new() {Interval=33};
    private readonly CancellationTokenSource cancellation=new();
    private GameReader? reader;
    private bool busy,closing;
    private string map="";
    private DateTime lastScan=DateTime.MinValue;
    private DateTime warmupUntil=DateTime.MinValue;
    private bool cameraWasUnavailable;
    private bool visibleItemNeedsBinding;

    internal MainForm()
    {
        Text="ZAT • Предметы";
        ClientSize=new Size(460,410);
        FormBorderStyle=FormBorderStyle.FixedSingle;
        MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(22,25,31);
        ForeColor=Color.FromArgb(228,232,240);
        Font=new Font("Segoe UI",10);
        title.Font=new Font("Segoe UI",19,FontStyle.Bold);title.AutoSize=true;title.Location=new Point(24,20);
        Controls.Add(title);
        status.SetBounds(26,70,410,45);status.Text="Ожидание запущенной игры…";Controls.Add(status);
        SetupCheck(enabled,"Подсветка предметов",26,126,true);
        SetupCheck(gold,"Золотые слитки",44,171,true);gold.ForeColor=Color.FromArgb(255,209,70);
        SetupCheck(bottles,"Бутылки крови",44,209,true);bottles.ForeColor=Color.FromArgb(255,110,120);
        counts.SetBounds(26,251,410,24);Controls.Add(counts);
        details.SetBounds(26,280,410,47);details.ForeColor=Color.FromArgb(155,165,182);details.Font=new Font("Segoe UI",9);
        Controls.Add(details);
        rescan.Text="Обновить поиск";rescan.SetBounds(26,341,185,40);rescan.FlatStyle=FlatStyle.Flat;Controls.Add(rescan);
        close.FlatStyle=FlatStyle.Flat;close.SetBounds(250,341,185,40);close.Click+=(_,_)=>Close();Controls.Add(close);
        language.SetBounds(344,24,92,32);language.FlatStyle=FlatStyle.Flat;Controls.Add(language);
        try {english=File.Exists(LanguagePath)&&File.ReadAllText(LanguagePath).Trim()=="en";} catch(IOException){} catch(UnauthorizedAccessException){}
        language.Click+=(_,_)=>
        {
            english=!english;ApplyLanguage();
            try {Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);File.WriteAllText(LanguagePath,english?"en":"ru");}
            catch(IOException){} catch(UnauthorizedAccessException){}
        };
        ApplyLanguage();
        rescan.Click+=async (_,_)=>{if(reader is not null&&!busy)await Scan();};
        connectionTimer.Tick+=async (_,_)=>await Connect();
        frameTimer.Tick+=(_,_)=>Frame();
        Shown+=async (_,_)=>{connectionTimer.Start();frameTimer.Start();await Connect();};
    }
    private string T(string ru,string en)=>english?en:ru;
    private void SetStatus(string ru,string en)
    {statusRu=ru;statusEn=en;status.Text=T(ru,en);}
    private void ApplyLanguage()
    {
        Text=T("ZAT • Предметы","ZAT • Items");
        title.Text=T("ZAT / ПРЕДМЕТЫ","ZAT / ITEMS");
        enabled.Text=T("Подсветка предметов","Item overlay");
        gold.Text=T("Золотые слитки","Gold bars");
        bottles.Text=T("Бутылки крови","Blood bottles");
        rescan.Text=T("Обновить поиск","Rescan");close.Text=T("Закрыть","Close");
        language.Text=english?"Русский":"English";
        language.AccessibleName=T("Переключить на английский","Switch to Russian");
        details.Text=T("Оверлей отображается, когда окно игры активно\nи игра запущена в оконном режиме.",
            "The overlay appears when the game window is active\nand the game is running in windowed mode.");
        status.Text=T(statusRu,statusEn);RenderCounts();
        overlay.English=english;overlay.Invalidate();
    }
    private void UpdateCounts(Item[] items)
    {
        goldCount=items.Count(i=>i.Kind=="Золото");bottleCount=items.Count(i=>i.Kind=="Бутылка");
        progressAvailable=reader?.ProgressAvailable==true;RenderCounts();
    }
    private void RenderCounts()
    {
        counts.Text=goldCount is null?"":progressAvailable?
            T($"Осталось: золото {goldCount} · бутылки {bottleCount}",$"Remaining: gold {goldCount} · bottles {bottleCount}"):
            T("Прогресс недоступен — рамки скрыты","Progress unavailable — markers hidden");
    }
    private void ShowError(Exception ex,bool read=false)
    {
        var en=ex.Message switch
        {
            "Не удалось прочитать модуль игры."=>"Could not read the game module.",
            "Другая версия ZAT.exe. Нужна проверка совместимости."=>"Unsupported ZAT.exe version. Compatibility needs to be checked.",
            _=>ex.Message
        };
        SetStatus((read?"Ошибка чтения: ":"")+ex.Message,(read?"Read error: ":"")+en);
    }

    private void SetupCheck(CheckBox c,string text,int x,int y,bool value)
    {c.Text=text;c.SetBounds(x,y,380,32);c.Checked=value;Controls.Add(c);}

    private async Task Connect()
    {
        if(busy||closing)return;
        try
        {
            if(reader is not null&&reader.Process.HasExited)
            {reader.Dispose();reader=null;overlay.Hide();goldCount=bottleCount=null;RenderCounts();}
            if(reader is null)
            {
                var processes=Process.GetProcessesByName("ZAT");
                if(processes.Length==0) {SetStatus("Ожидание запущенной игры…","Waiting for the game…");return;}
                if(processes.Length!=1) {foreach(var p in processes)p.Dispose();SetStatus("Найдено несколько ZAT.exe. Оставь один процесс.","Multiple ZAT.exe processes found. Leave only one running.");return;}
                try {reader=new GameReader(processes[0]);} catch {processes[0].Dispose();throw;}
                await Scan();
            }
            else if(reader.Map!=map ||
                DateTime.UtcNow-lastScan>TimeSpan.FromSeconds(10) ||
                DateTime.UtcNow<warmupUntil&&DateTime.UtcNow-lastScan>TimeSpan.FromSeconds(2) ||
                reader.ReadItems().Length==0&&DateTime.UtcNow-lastScan>TimeSpan.FromSeconds(20))
                await Scan();
            else if(visibleItemNeedsBinding&&DateTime.UtcNow-lastScan>TimeSpan.FromSeconds(1))
                await Scan();
            if(reader is not null&&!busy&&!closing)
            {
                var items=reader.ReadItems();
                UpdateCounts(items);
            }
        }
        catch(Exception ex) {ShowError(ex);overlay.Hide();}
    }

    private async Task Scan()
    {
        if(reader is null||busy)return;
        busy=true;rescan.Enabled=false;
        SetStatus($"Подключено к ZAT.exe · PID {reader.Process.Id}\nПоиск предметов…",$"Connected to ZAT.exe · PID {reader.Process.Id}\nScanning for items…");
        try
        {
            var nextMap=reader.Map;
            if(nextMap!=map) {warmupUntil=DateTime.UtcNow.AddSeconds(8);overlay.Items=[];}
            map=nextMap;
            var current=reader;
            await Task.Run(()=>current.Discover(cancellation.Token),cancellation.Token);
            if(closing)return;
            var items=current.ReadItems();
            UpdateCounts(items);
            SetStatus($"Подключено · PID {current.Process.Id}\n{Path.GetFileNameWithoutExtension(map)}",$"Connected · PID {current.Process.Id}\n{Path.GetFileNameWithoutExtension(map)}");
            lastScan=DateTime.UtcNow;
            if(warmupUntil==DateTime.MinValue)warmupUntil=DateTime.UtcNow.AddSeconds(8);
        }
        catch(OperationCanceledException){}
        catch(Exception ex) {if(!closing)ShowError(ex);}
        finally {busy=false;if(!closing)rescan.Enabled=true;else {reader?.Dispose();reader=null;cancellation.Dispose();}}
    }

    private void Frame()
    {
        if(reader is null||!enabled.Checked||closing) {overlay.Hide();return;}
        try
        {
            if(reader.Process.HasExited) {overlay.Hide();return;}
            var window=reader.Process.MainWindowHandle;
            if(window==0||Native.GetForegroundWindow()!=window||Native.IsIconic(window)) {overlay.Hide();return;}
            var camera=reader.ReadCamera();
            if(camera is null) {cameraWasUnavailable=true;overlay.Hide();return;}
            if(cameraWasUnavailable)
            {
                cameraWasUnavailable=false;
                warmupUntil=DateTime.UtcNow.AddSeconds(8);
            }
            overlay.Camera=camera;
            // Discovery mutates tracking on its worker; render the previous immutable
            // snapshot with a fresh camera until discovery has finished.
            if(!busy)overlay.Items=reader.ReadItems();
            overlay.Gold=gold.Checked;overlay.Bottles=bottles.Checked;
            overlay.Follow(window);
            if(!busy)visibleItemNeedsBinding=reader.NeedsLiveBinding(camera,overlay.ClientSize.Width,overlay.ClientSize.Height);
            // Refresh paints synchronously. Invalidate alone allowed the old
            // rectangle to remain visible after a collectible was removed.
            overlay.Refresh();
        }
        catch(Exception ex) {ShowError(ex,true);overlay.Hide();}
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing=true;cancellation.Cancel();connectionTimer.Stop();frameTimer.Stop();overlay.Dispose();
        if(!busy) {reader?.Dispose();cancellation.Dispose();}
        connectionTimer.Dispose();frameTimer.Dispose();
        base.OnFormClosed(e);
    }
}
