using System.Text.Json;
using ZatCollectibles;

var game=Path.GetFullPath(args[1]);
var checks=0;
void Check(bool value,string name) {if(!value)throw new Exception(name);checks++;}
var blank=new byte[CollectibleProgress.Length];
var empty=CollectibleProgress.Parse(blank)!;
Check(empty.IsCollected("Бутылка",0)==false,"fresh profile bottle");
Check(empty.IsCollected("Золото",74)==false,"fresh profile gold");
blank[0]=1;
Check(empty.IsCollected("Бутылка",0)==false,"snapshot must own its bytes");
var changed=CollectibleProgress.Parse(blank)!;
Check(changed.IsCollected("Бутылка",0)==true,"bottle change");
Check(changed.IsCollected("Золото",0)==false,"bottle must not hide same-index gold");
blank[60]=1;
Check(CollectibleProgress.Parse(blank)!.IsCollected("Золото",0)==true,"gold change");
Check(CollectibleProgress.Parse(new byte[134]) is null,"partial read is unknown");
blank[80]=2;
Check(CollectibleProgress.Parse(blank) is null,"invalid byte is unknown");
Check(empty.IsCollected("Бутылка",60) is null,"bottle bounds");
Check(empty.IsCollected("Золото",-1) is null,"missing index");
Check(empty.IsCollected("other",0) is null,"unknown type");
for(var slot=0;slot<135;slot++)
{
    var profile=new byte[135];profile[slot]=1;
    var sample=CollectibleProgress.Parse(profile)!;
    var kind=slot<60?"Бутылка":"Золото";var index=slot<60?slot:slot-60;
    Check(sample.IsCollected(kind,index)==true,"individual pickup: "+slot);
    Check(empty.IsCollected(kind,index)==false,"other player: "+slot);
    profile[slot]=0;
    Check(CollectibleProgress.Parse(profile)!.IsCollected(kind,index)==false,"profile reset: "+slot);
}
var all=CollectibleProgress.Parse(Enumerable.Repeat((byte)1,135).ToArray())!;
var seenGold=new HashSet<int>();var seenBottles=new HashSet<int>();
var mapCount=0;
foreach(var path in Directory.EnumerateFiles(game,"*.pc",SearchOption.AllDirectories))
{
    if(new FileInfo(path).Length==0)continue;
    var items=GameReader.ExtractCatalog(path).ToArray();
    if(items.Length==0||path.Contains("Horde",StringComparison.OrdinalIgnoreCase))continue;
    var alternate=GameReader.ExtractCatalog(path,65549).ToArray();
    Check(items.SequenceEqual(alternate, new PlacementComparer()),"block boundary: "+path);
    Check(items.Count(i=>i.Kind=="Золото")==5&&items.Count(i=>i.Kind=="Бутылка")==4,"map counts: "+path);
    foreach(var item in items)
    {
        Check(empty.IsCollected(item.Kind,item.ProgressIndex)==false,"new profile: "+item.Id);
        Check(all.IsCollected(item.Kind,item.ProgressIndex)==true,"completed profile: "+item.Id);
        Check((item.Kind=="Золото"?seenGold:seenBottles).Add(item.ProgressIndex),"duplicate campaign index");
    }
    mapCount++;
}
Check(mapCount==15,"15 campaign maps");
Check(seenGold.SetEquals(Enumerable.Range(0,75)),"gold full index coverage");
Check(seenBottles.SetEquals(Enumerable.Range(0,60)),"bottle full index coverage");
var first=GameReader.ExtractCatalog(Path.Combine(game,"Undead/Undead_E/M05_Undead_E.pc")).ToArray();
Check(first.All(i=>all.IsCollected(i.Kind,i.ProgressIndex)==true),"completed first map: all 9 collected");
Check(first.All(i=>empty.IsCollected(i.Kind,i.ProgressIndex)==false),"different profile: same 9 remain");
Console.WriteLine($"PASS: {checks} checks; {mapCount} maps; 75 gold + 60 bottle indices. Completed profile first map: 0 visible; new profile: 9 visible.");

sealed class PlacementComparer : IEqualityComparer<Item>
{
    public bool Equals(Item? a,Item? b)=>a is not null&&b is not null&&a.Id==b.Id&&a.Kind==b.Kind&&a.ProgressIndex==b.ProgressIndex&&a.Position==b.Position;
    public int GetHashCode(Item i)=>HashCode.Combine(i.Id,i.Kind,i.ProgressIndex);
}
