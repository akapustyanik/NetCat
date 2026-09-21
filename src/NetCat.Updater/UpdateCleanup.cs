using System.Text.Json;
using NetCat.Core;
namespace NetCat.Updater;

public static class UpdateCleanup
{
    public static string SecureRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"NetCat","updates");
    public static string ValidateStage(string path,string? secureRoot=null)
    {
        var root=secureRoot ?? SecureRoot; var stage=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var id=Path.GetRelativePath(root,stage);
        if(!Guid.TryParseExact(id,"N",out _) || !PortableUpdate.SafePath(root,id).Equals(stage,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Обновление находится вне защищённого хранилища.");
        return stage;
    }
    public static bool TryRemoveStage(string stage,string? secureRoot=null)
    {
        stage=ValidateStage(stage,secureRoot);
        try
        {
            if(!Directory.Exists(stage)) return true;
            var lease=Path.Combine(stage,"active.lock");
            // A helper or a download holding this lease must finish first.
            using(var test=new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) { }
            DurableUpdate.DeleteTree(secureRoot ?? SecureRoot,Path.GetFileName(stage)); return true;
        }
        catch(IOException) { return false; }
        catch(UnauthorizedAccessException) { return false; }
    }
    public static void RetainRecent(string? secureRoot=null,DateTime? utcNow=null,ISet<string>? activeStages=null,Action<string>? diagnostic=null)
    {
        var root=secureRoot ?? SecureRoot; if(!Directory.Exists(root)) return;
        foreach(var stage in Directory.GetDirectories(root))
        {
            if(!Guid.TryParseExact(Path.GetFileName(stage),"N",out _) || (File.GetAttributes(stage)&FileAttributes.ReparsePoint)!=0) continue;
            if(activeStages?.Contains(stage)==true || Directory.GetLastWriteTimeUtc(stage)>(utcNow ?? DateTime.UtcNow).AddDays(-7)) continue;
            try
            {
                var jobPath=PortableUpdate.SafePath(stage,"job.json");
                if(File.Exists(jobPath))
                {
                    var job=JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobPath),JsonSettings.Options);
                    if(job!=null && DurableUpdate.Read(job.Root) is { } journal && journal.Stage==stage) continue;
                }
                TryRemoveStage(stage,root);
            }
            catch(InvalidDataException ex) { diagnostic?.Invoke("Сохранён stage с некорректным журналом: "+stage+": "+ex.Message); }
            catch(IOException ex) { diagnostic?.Invoke("Не удалось проверить stage: "+stage+": "+ex.Message); }
            catch(UnauthorizedAccessException ex) { diagnostic?.Invoke("Нет доступа к stage: "+stage+": "+ex.Message); }
            catch(JsonException ex) { diagnostic?.Invoke("Повреждён журнал stage: "+stage+": "+ex.Message); }
            catch(ArgumentException ex) { diagnostic?.Invoke("Недопустимый путь stage: "+stage+": "+ex.Message); }
        }
    }
}
