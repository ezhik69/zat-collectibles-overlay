namespace ZatCollectibles;

// Indices are serialized in each ENTI placement, not derived from its entity ID
// or map order. Both arrays belong to the game's currently loaded local profile.
internal sealed class CollectibleProgress
{
    internal const int BottleCount=60, GoldCount=75, Length=BottleCount+GoldCount;
    private readonly byte[] flags;
    private CollectibleProgress(byte[] flags) => this.flags=flags;
    internal static CollectibleProgress? Parse(ReadOnlySpan<byte> data)
    {
        if(data.Length!=Length)return null;
        foreach(var value in data)if(value>1)return null;
        return new CollectibleProgress(data.ToArray());
    }
    internal bool? IsCollected(string kind,int index)
    {
        var count=kind switch {"Бутылка"=>BottleCount,"Золото"=>GoldCount,_=>0};
        if(index<0||index>=count)return null;
        return flags[index+(kind=="Золото"?BottleCount:0)]!=0;
    }
}
