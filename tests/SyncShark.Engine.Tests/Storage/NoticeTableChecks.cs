using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises notice ownership, replacement, membership and mapped side footprints.</summary>
public static class NoticeTableChecks
{
    /// <summary>A stale close cannot settle the replacement, and another kind cannot erase a hidden PutBack.</summary>
    public static async Task RunAsync()
    {
        using var db=PairTableChecks.Open(); var notices=new NoticeTable();
        var first=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,"same",new([new(Side.Dropbox,"cloud/parent","original")],null),DateTimeOffset.UnixEpoch,false,true);
        await notices.SaveNoticeAsync(db,"p",first,default);
        var old=await At(NoticeKind.PutBack);
        await notices.SaveNoticeAsync(db,"p",first with {Kind=NoticeKind.Failure,Ignored=false},default);
        if(!(await At(NoticeKind.PutBack)).Ignored) throw new InvalidOperationException("Failure displaced hidden PutBack");
        var replacement=first with {Subject=new([new(Side.Dropbox,"cloud/parent","replacement")],null)};
        await notices.SaveNoticeAsync(db,"p",replacement,default);
        await notices.CloseNoticeAsync(db,"p",old,default);
        var current=await At(NoticeKind.PutBack);
        if(current.Subject.Items.Single().Id!="replacement") throw new InvalidOperationException("Stale close changed replacement");
        int hits=0; await foreach(var n in notices.NoticesUnderAsync(db,"p",new("unrelated","cloud/parent/child"),default)) hits++;
        if(hits!=2) throw new InvalidOperationException("Missing covering ancestor");
        await foreach(var n in notices.NoticesUnderAsync(db,"p",new("cloud/parent","unrelated"),default)) throw new InvalidOperationException("Wrong side matched mapped footprint");
        await notices.CloseNoticeAsync(db,"p",current,default);
        if(await notices.NoticeAsync(db,"p",current.Id,default)!=null) throw new InvalidOperationException("Exact close did not settle slot");
        var link=new NoticeItem(Side.Local,"dir/link","7"); var next=link with {Id="8"};
        await notices.UpdateLinkMemberAsync(db,"p",link,true,default);
        await notices.UpdateLinkMemberAsync(db,"p",next,true,default);
        await notices.UpdateLinkMemberAsync(db,"p",link,false,default);
        var survivors=new List<NoticeItem>();
        await foreach(var n in notices.NoticesAtAsync(db,"p","",default)) survivors.AddRange(n.Subject.Items);
        if(!survivors.SequenceEqual(new[]{next})) throw new InvalidOperationException("Old link retirement removed the replacement");
        await notices.UpdateLinkMemberAsync(db,"p",next,false,default);
        await foreach(var n in notices.NoticesAtAsync(db,"p","",default)) throw new InvalidOperationException("Empty link summary retained");
        async Task<Notice> At(NoticeKind kind)
        {
            await foreach(var n in notices.NoticesAtAsync(db,"p",PathNames.Key("same"),default)) if(n.Kind==kind) return n;
            throw new InvalidOperationException("Missing notice kind");
        }
    }
}
