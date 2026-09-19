using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ZatCollectibles;

internal sealed record Target(uint Address, uint Resource, uint Id, string Kind, uint Owner=0, uint OwnerVtable=0, uint MeshOffset=0);
internal sealed record Item(uint Id, string Kind, Vector3 Position, Matrix4x4 World, float[] Bounds, int ProgressIndex=-1);
internal sealed record Camera(Matrix4x4 View, Vector3 Position, float ScaleX, float ScaleY);

internal sealed class GameReader : IDisposable
{
    // Verified against the installed Steam build; all addresses are module-relative.
    internal const string SupportedHash = "4794f61f96356906a1775a95aa9f00482a42c2dd029fb3cdfb4b541cd1ba903a";
    private readonly SafeProcessHandle handle;
    private readonly string gameDirectory;
    internal Process Process { get; }
    internal uint ModuleBase { get; }
    internal uint InstanceVtable => ModuleBase + 0x6396f4;
    private uint ResourceVtable => ModuleBase + 0x639dcc;
    internal Target[] Targets { get; private set; } = [];
    private readonly Dictionary<uint,Item> knownItems = new();
    private CollectibleProgress? progress;
    internal bool ProgressAvailable => progress is not null;
    internal string LastWarning { get; private set; } = "";
    internal object[] Tracking => knownItems.Values.Select(i=>(object)new {i.Id,i.Kind,i.ProgressIndex,
        state=progress?.IsCollected(i.Kind,i.ProgressIndex) switch {true=>"profile-collected",false=>"profile-remaining",_=>"unknown"}}).ToArray();
    private string knownMap = "";
    private string cacheKey = "";
    private static readonly string CacheDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZatCollectibles");
    private sealed record StoredItem(uint Id,string Kind,float[] World,float[] Bounds,int ProgressIndex=-1);
    internal string Map => ReadString(ModuleBase + 0x6d89c8, 160);
    internal long ScannedBytes { get; private set; }

    internal GameReader(Process process)
    {
        Process = process;
        var module = process.MainModule ?? throw new InvalidOperationException("Не удалось прочитать модуль игры.");
        using var file = File.OpenRead(module.FileName);
        var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (hash != SupportedHash) throw new InvalidOperationException("Другая версия ZAT.exe. Нужна проверка совместимости.");
        ModuleBase = checked((uint)module.BaseAddress.ToInt64());
        gameDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(module.FileName)!, ".."));
        handle = Native.OpenProcess(0x410, false, process.Id);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal byte[] Read(uint address, int size)
    {
        var data = new byte[size];
        Native.ReadProcessMemory(handle, (nint)(long)address, data, (nuint)size, out var read);
        return read == (nuint)size ? data : [];
    }
    internal uint U32(uint address)
    {
        var b = Read(address, 4);
        return b.Length == 4 ? BitConverter.ToUInt32(b) : 0;
    }
    internal string ReadString(uint address, int length)
    {
        var bytes = Read(address, length);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
    internal static float F(byte[] b, int offset) => BitConverter.ToSingle(b, offset);
    internal static uint U(byte[] b, int offset) => BitConverter.ToUInt32(b, offset);
    internal static Matrix4x4 Matrix(byte[] b, int o) => new(
        F(b,o),F(b,o+4),F(b,o+8),F(b,o+12), F(b,o+16),F(b,o+20),F(b,o+24),F(b,o+28),
        F(b,o+32),F(b,o+36),F(b,o+40),F(b,o+44), F(b,o+48),F(b,o+52),F(b,o+56),F(b,o+60));

    internal void Discover(CancellationToken token)
    {
        var map = Map;
        if (!string.Equals(map,knownMap,StringComparison.OrdinalIgnoreCase))
        {
            knownMap=map;
            Targets=[];
            knownItems.Clear();
            progress=null;
            cacheKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(map.ToLowerInvariant()))).ToLowerInvariant()[..16];
            if(!string.IsNullOrWhiteSpace(map)) { LoadCache(); LoadMapCatalog(map); }
        }
        if(string.IsNullOrWhiteSpace(map))return;
        RefreshItems();
        var found = new List<Target>();
        var owners = new Dictionary<uint,(uint Address,uint Vtable,uint Offset)>();
        var resources = new Dictionary<uint,string>();
        var buffer = new byte[1024 * 1024 + 0x240];
        ScannedBytes = 0;
        for (ulong address = 0x10000; address < 0x80000000;)
        {
            token.ThrowIfCancellationRequested();
            if (Native.VirtualQueryEx(handle, (nint)(long)address, out var r, (nuint)Marshal.SizeOf<Native.MemoryInfo>()) == 0) break;
            var start = (ulong)r.BaseAddress.ToInt64();
            var end = start + (ulong)r.RegionSize;
            if (end <= address) break;
            address = end;
            if (r.State != 0x1000 || r.Type != 0x20000 || r.Protect is not (4 or 8 or 64 or 128)) continue;
            for (var block = start; block < end; block += 1024 * 1024)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min((ulong)buffer.Length, end - block);
                Native.ReadProcessMemory(handle,(nint)(long)block,buffer,(nuint)length,out var count);
                ScannedBytes += (long)count;
                var limit = Math.Min((int)count - 0x240, 1024 * 1024);
                for (int i = 0; i <= limit; i += 4)
                {
                    var vtable=U(buffer,i);
                    // Active pickup and destructible scene objects own the render model.
                    // Detached cached models survive collection and must not be shown.
                    if(vtable==ModuleBase+0x65b410 || vtable==ModuleBase+0x654c50 || vtable==ModuleBase+0x65d750)
                    {
                        uint meshOffset=vtable==ModuleBase+0x65d750?0x60u:0x198u;
                        var mesh=U(buffer,i+(int)meshOffset);
                        if(mesh!=0)owners[mesh]=((uint)(block+(ulong)i),vtable,meshOffset);
                    }
                    if (vtable != InstanceVtable) continue;
                    var resource = U(buffer,i+0x64);
                    if (!resources.TryGetValue(resource, out var kind))
                    {
                        kind = "";
                        var model = Read(resource,0x40);
                        if (model.Length == 0x40 && U(model,0) == ResourceVtable)
                        {
                            var name = ReadString(U(model,0x30),64);
                            if (name == "Gold Ingot") kind = "Золото";
                            else if (name == "Blood_Bottle_Solid") kind = "Бутылка";
                        }
                        resources[resource] = kind;
                    }
                    if (kind.Length == 0) continue;
                    var id = U(buffer,i+0xa4);
                    var world = Matrix(buffer,i+0x1c0);
                    if (id == 0 || !ValidWorld(world)) continue;
                    found.Add(new Target((uint)(block+(ulong)i),resource,id,kind));
                }
                Thread.Yield();
            }
        }
        // Discard a scan spanning a level transition. Never mix IDs from two maps.
        if(!string.Equals(Map,map,StringComparison.OrdinalIgnoreCase))return;
        var discovered = found.Select(t=>owners.TryGetValue(t.Address,out var owner)
            ? t with {Owner=owner.Address,OwnerVtable=owner.Vtable,MeshOffset=owner.Offset}
            : t).ToArray();
        // One current binding per map ID; stale addresses cannot overwrite a live one.
        // Unloaded placements survive in the catalog, not in the address list.
        Targets=discovered.GroupBy(t=>(t.Kind,t.Id)).Select(g=>g
            .OrderByDescending(t=>t.OwnerVtable==ModuleBase+0x65b410 || t.OwnerVtable==ModuleBase+0x65d750)
            .ThenByDescending(t=>t.Owner!=0).First()).ToArray();
        RefreshItems();
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && p.LengthSquared() < 1e10f;
    private static bool ValidWorld(Matrix4x4 m) => Finite(m.Translation) && Math.Abs(m.M44-1)<.001f;
    private static bool UsableWorld(Matrix4x4 m) => ValidWorld(m) && !(m.Translation.LengthSquared()<1e-6f &&
        Math.Abs(m.M11-1)<.001f && Math.Abs(m.M22-1)<.001f && Math.Abs(m.M33-1)<.001f);

    internal Camera? ReadCamera()
    {
        var pointer = U32(ModuleBase+0x6d775c);
        if (pointer == 0) return null;
        var b = Read(pointer,0xb0);
        if (b.Length != 0xb0) return null;
        var view = Matrix(b,0);
        view.M44 = 1; // The engine stores 0 in its unused affine padding slot.
        var focal=F(b,0x40); var halfWidth=F(b,0x50); var halfHeight=F(b,0x54);
        if (!Matrix4x4.Invert(view,out var inverse) || !Finite(inverse.Translation) ||
            !float.IsFinite(focal) || focal<=0 || halfWidth<=0 || halfHeight<=0 ||
            focal/halfWidth is <.05f or >20 || focal/halfHeight is <.05f or >20) return null;
        return new Camera(view,inverse.Translation,focal/halfWidth,focal/halfHeight);
    }

    internal Item[] ReadItems()
    {
        RefreshItems();
        if(!string.Equals(Map,knownMap,StringComparison.OrdinalIgnoreCase))return [];
        return knownItems.Values.Where(i=>progress?.IsCollected(i.Kind,i.ProgressIndex)==false).ToArray();
    }

    internal bool NeedsLiveBinding(Camera camera,int width,int height)
    {
        if(width<100||height<100)return false;
        return knownItems.Values.Any(item=>
        {
            if(progress?.IsCollected(item.Kind,item.ProgressIndex)!=false || Targets.Any(t=>t.Id==item.Id&&t.Kind==item.Kind) ||
                Vector3.DistanceSquared(item.Position,camera.Position)>200f*200f)return false;
            var point=Overlay.Project(item.Position,camera,width,height);
            return point is {} p && p.X>=-40&&p.X<=width+40&&p.Y>=-40&&p.Y<=height+40;
        });
    }

    private void RefreshItems()
    {
        if(string.IsNullOrWhiteSpace(knownMap)||!string.Equals(Map,knownMap,StringComparison.OrdinalIgnoreCase))return;
        // The game's active profile, serialized by generalgamesettings.cld.
        // Bottle writer RVA 0x45bd40; gold writer RVA 0x45be00.
        // Never infer saved completion from a render model or an ESP cache.
        progress=CollectibleProgress.Parse(Read(ModuleBase+0x7a1794,CollectibleProgress.Length));
        if(progress is null) {LastWarning="Не удалось прочитать прогресс игрока";return;}
        if(LastWarning=="Не удалось прочитать прогресс игрока")LastWarning="";
        foreach(var t in Targets)
        {
            if(!knownItems.TryGetValue(t.Id,out var item)||item.Kind!=t.Kind||
                progress.IsCollected(item.Kind,item.ProgressIndex)!=false)continue;
            var model=Read(t.Address,0x240);
            if(model.Length!=0x240||U(model,0)!=InstanceVtable||U(model,0x64)!=t.Resource||U(model,0xa4)!=t.Id)continue;
            var world=Matrix(model,0x1c0);
            // Ignore reused/animated debris at another position. Profile state
            // alone determines availability; memory only refines the placement.
            if(!UsableWorld(world)||Vector3.DistanceSquared(world.Translation,item.Position)>1)continue;
            var bounds=Enumerable.Range(0,6).Select(i=>F(model,0x144+i*4)).ToArray();
            if(bounds.Any(x=>!float.IsFinite(x)||Math.Abs(x)>20))continue;
            knownItems[t.Id]=item with {Position=world.Translation,World=world,Bounds=bounds};
        }
    }

    private string CatalogPath => Path.Combine(CacheDirectory,$"catalog-{cacheKey}.json");
    
    private static float[] Values(Matrix4x4 m)=>[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    private static Matrix4x4 FromValues(float[] v)=>new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);

    private static readonly byte[] BottleSignatureC=Convert.FromHexString("01000000C29E01E90100000000000000000000000000000000000000000080BF00000000");
    private static bool Match(byte[] data,int offset,byte[] expected) =>
        offset>=0 && offset+expected.Length<=data.Length && data.AsSpan(offset,expected.Length).SequenceEqual(expected);

    private static Item? ReadCatalogRecord(byte[] record)
    {
        if(record.Length is 0x158 or 0x15c && U(record,0x14)==0x5c && U(record,0x18)==5 && U(record,0x1c)==1003 &&
            U(record,0x60)==1 && U(record,0x68)==10 && U(record,0x6c)==32 && U(record,0x70)==6 &&
            U(record,0x74)==0x00308060 && U(record,0x78)==0xdd8c4577 && U(record,0xc0+record.Length-0x158)==3 && U(record,0xc4+record.Length-0x158)==0x4cbd0187)
        {
            var position=new Vector3(F(record,0x20),F(record,0x24),F(record,0x28));
            if(!Finite(position))return null;
            return new Item(U(record,0x10),"Золото",position,Matrix4x4.CreateTranslation(position),
                [-.19233236f,.19233236f,-.07891275f,-.0010276549f,-.06473931f,.06473931f],checked((int)U(record,record.Length-17)));
        }
        var bottleShift=record.Length-0x90;
        if(record.Length is 0x90 or 0x94 && U(record,0x14)==0x53 && U(record,0x18)==1003 &&
            U(record,0x1c)==11 && U(record,0x20)==6 && U(record,0x33+bottleShift)==0x765a62bb &&
            Match(record,0x53+bottleShift,BottleSignatureC))
        {
            // Optional serialized reference adds four bytes. Flags and ID high bytes
            // vary per placement and are not part of the resource signature.
            var position=new Vector3(F(record,0x37+bottleShift),F(record,0x3b+bottleShift),F(record,0x3f+bottleShift));
            if(!Finite(position))return null;
            return new Item(U(record,0x10),"Бутылка",position,Matrix4x4.CreateTranslation(position),
                [-.04688589f,.04695793f,-.41861147f,.00024479628f,-.046935957f,.046907876f],checked((int)U(record,record.Length-5)));
        }
        return null;
    }

    private void LoadMapCatalog(string map)
    {
        try
        {
            var mapFile=Path.GetFullPath(Path.Combine(gameDirectory,map));
            if(!mapFile.StartsWith(gameDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !File.Exists(mapFile))return;
            var catalog=ExtractCatalog(mapFile).ToArray();
            knownItems.Clear();
            foreach(var item in catalog)knownItems[item.Id]=item;
            SaveCatalog();
        }
        catch(Exception ex) { LastWarning=ex.Message; }
    }
    internal static IEnumerable<Item> ExtractCatalog(string mapFile,int blockSize=1024*1024)
    {
            using var file=File.OpenRead(mapFile);
            var header=new byte[20];
            file.ReadExactly(header);
            if(!header.AsSpan(0,8).SequenceEqual("AsuraZlb"u8))throw new InvalidDataException("Unknown map compression");
            using var zlib=new ZLibStream(file,CompressionMode.Decompress);
            const int maximumRecord=0x15c;
            var buffer=new byte[blockSize+maximumRecord];
            var carry=0;
            while(true)
            {
                var read=zlib.Read(buffer,carry,buffer.Length-carry);
                var length=carry+read;
                var final=read==0;
                var processEnd=final?length:Math.Max(0,length-maximumRecord);
                for(var i=0;i<processEnd && i+8<=length;i++)
                {
                    if(buffer[i]!='E'||buffer[i+1]!='N'||buffer[i+2]!='T'||buffer[i+3]!='I')continue;
                    var size=(int)U(buffer,i+4);
                    if(size is not (0x90 or 0x94 or 0x158 or 0x15c) || i+size>length)continue;
                    var record=buffer.AsSpan(i,size).ToArray();
                    if(ReadCatalogRecord(record) is {} item)yield return item;
                    i+=size-1;
                }
                if(final)break;
                carry=length-processEnd;
                Buffer.BlockCopy(buffer,processEnd,buffer,0,carry);
            }
    }
    private void LoadCache()
    {
        try
        {
            if(File.Exists(CatalogPath))
                foreach(var s in JsonSerializer.Deserialize<StoredItem[]>(File.ReadAllText(CatalogPath))??[])
                    if(s.World.Length==16&&s.Bounds.Length==6)
                    {
                        var world=FromValues(s.World);
                        if(UsableWorld(world))knownItems[s.Id]=new Item(s.Id,s.Kind,world.Translation,world,s.Bounds,s.ProgressIndex);
                    }

        }
        catch(Exception ex) { LastWarning=ex.Message; }
    }
    private void SaveCatalog()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var snapshot=knownItems.Values.Select(i=>new StoredItem(i.Id,i.Kind,Values(i.World),i.Bounds,i.ProgressIndex));
            var temporary=CatalogPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(snapshot));
            File.Move(temporary,CatalogPath,true);
        }
        catch(Exception ex) { LastWarning=ex.Message; }
    }
    public void Dispose() { handle.Dispose(); Process.Dispose(); }
}
