using NetCat.Core;
using NetCat.Network;
using Xunit;

namespace NetCat.Tests;
public sealed class Candidate19JournalTests
{
    [Fact] public async Task CleanupDoesNotDeleteForeignDuplicateRoute()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-journal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"routes.json");
            var link=new OpenVpnLink("test",42,"10.1.0.2","10.1.0.1","10.1.0.53",["10.2.0.0/16"]);
            await (OpenVpnRouteJournal.Empty(link) with {Prefixes=["10.2.0.0/16"]}).SaveAsync(path,default);
            string command="";
            await OpenVpnRouteJournal.CleanupAsync(path,(s,_)=>{command=s;return Task.FromResult((0,""));});
            Assert.Contains("RouteMetric -eq",command);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task TruncatedJournalIsQuarantined()
    {
        var root=Path.Combine(Path.GetTempPath(),"NetCat-C19-corrupt-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"routes.json");await File.WriteAllTextAsync(path,"{\"Schema\":");int calls=0;
            await Assert.ThrowsAnyAsync<Exception>(()=>OpenVpnRouteJournal.CleanupAsync(path,(_,_)=>{calls++;return Task.FromResult((0,""));}));
            Assert.Equal(0,calls);Assert.NotEmpty(Directory.GetFiles(root,"*.corrupt*"));
        }
        finally {Directory.Delete(root,true);}
    }
}
