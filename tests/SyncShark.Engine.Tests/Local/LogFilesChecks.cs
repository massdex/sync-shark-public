using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the LogFiles adapter against observed outcomes.</summary>
public static class LogFilesChecks
{
    public static Task RunAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            ILogFiles logs=new L.LogFiles(); var path=logs.Resolve(dir.FullName,"pair","run")!;
            Check.Require(logs.Resolve(dir.FullName,"../outside","run") is null && logs.Read(path,(_,_)=>{},1,default,out _)==ILogFiles.ReadStatus.NotFound,"Inspection created or escaped a log");
            using(var writer=logs.Open(path,128))
            {
                writer.Write("one\r\n"); writer.Flush(); var seen=new List<string>();
                Check.Require(logs.Read(path,(text,ok)=>seen.Add(ok?text:"!"),1,default,out var tail)==ILogFiles.ReadStatus.Found && !tail && seen.SequenceEqual(["one"]),"Active log not readable");
            }
            using(var append=new FileStream(path,FileMode.Append)) append.Write([0xff,10,65]);
            var records=new List<bool>(); logs.Read(path,(_,ok)=>records.Add(ok),2,default,out var incomplete);
            Check.Require(incomplete && records.SequenceEqual([true,false]),"Malformed and trailing records not distinguished");
            var sliced=logs.Resolve(dir.FullName,"boundary","run")!;
            File.WriteAllText(sliced,"ab\ncd\n");
            var lines=new List<string>(); logs.Read(sliced,(text,_)=>lines.Add(text),3,default,out var cut);
            Check.Require(!cut && lines.SequenceEqual(["ab","cd"]),"A newline on a buffer boundary did not end its record");
            File.WriteAllText(sliced,"abcd\nef\n");
            lines.Clear(); logs.Read(sliced,(text,_)=>lines.Add(text),2,default,out cut);
            Check.Require(!cut && lines.SequenceEqual(["abcd","ef"]),"A record spanning reads was not reassembled");
            var keep=Path.Combine(dir.FullName,"other.txt"); File.WriteAllText(keep,"keep");
            logs.Prune(dir.FullName,TimeSpan.FromDays(1),DateTimeOffset.UtcNow.AddDays(2));
            Check.Require(!File.Exists(path) && File.Exists(keep),"Prune did not distinguish log files");
        }
        finally { dir.Delete(true); }
        return Task.CompletedTask;
    }
}
