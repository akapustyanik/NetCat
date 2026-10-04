using System.Text.Json.Nodes;
namespace NetCat.Core;

public static class ProfileSettingsMerge
{
    // Three-way merge per profile/field. Background learned-route updates must
    // not conflict with a user rename or overwrite a concurrent credential edit.
    public static List<Profile> Merge(IReadOnlyList<Profile> baseline,IReadOnlyList<Profile> edited,IReadOnlyList<Profile> current)
    {
        var result=current.Select(JsonSettings.Clone).ToList();
        foreach(var before in baseline)
        {
            var after=edited.FirstOrDefault(p=>p.Id==before.Id);var live=result.FirstOrDefault(p=>p.Id==before.Id);
            var b=System.Text.Json.JsonSerializer.SerializeToNode(before,JsonSettings.Options)!;
            var a=after==null?null:System.Text.Json.JsonSerializer.SerializeToNode(after,JsonSettings.Options)!;
            if(JsonNode.DeepEquals(b,a))continue;
            if(live==null)throw new OperationCanceledException("Профиль уже удалён другой операцией.");
            var c=System.Text.Json.JsonSerializer.SerializeToNode(live,JsonSettings.Options)!;
            if(after==null)
            {if(!JsonNode.DeepEquals(b,c))throw new OperationCanceledException("Профиль изменён перед удалением.");result.Remove(live);continue;}
            foreach(var field in a!.AsObject())
            {
                if(JsonNode.DeepEquals(field.Value,b[field.Key]))continue;
                if(!JsonNode.DeepEquals(c[field.Key],b[field.Key])&&!JsonNode.DeepEquals(c[field.Key],field.Value))
                    throw new OperationCanceledException("Поле профиля уже изменено другой операцией: "+field.Key);
                c[field.Key]=field.Value?.DeepClone();
            }
            result[result.IndexOf(live)]=System.Text.Json.JsonSerializer.Deserialize<Profile>(c,JsonSettings.Options)!;
        }
        foreach(var added in edited.Where(p=>baseline.All(b=>b.Id!=p.Id)))
        {if(result.Any(p=>p.Id==added.Id))throw new OperationCanceledException("Профиль с этим ID уже существует.");result.Add(JsonSettings.Clone(added));}
        return result;
    }
}
