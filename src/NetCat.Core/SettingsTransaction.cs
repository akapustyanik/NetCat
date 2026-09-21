namespace NetCat.Core;

/// <summary>Runtime first, durable settings second, UI publication last. Inputs are detached snapshots.</summary>
public sealed class SettingsTransaction
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<AppSettings> ExecuteAsync(AppSettings current, AppSettings desired,
        Func<AppSettings,CancellationToken,Task> validate,
        Func<AppSettings,CancellationToken,Task>? apply,
        Func<AppSettings,Task> save,
        Func<AppSettings,CancellationToken,Task>? rollback,
        Func<Task> stop, CancellationToken ct = default, Func<bool>? canCommit = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var old=JsonSettings.Clone(current); var next=JsonSettings.Clone(desired);
            await validate(next,ct);
            bool applyAttempted=false;
            try
            {
                if(apply!=null) {applyAttempted=true;await apply(next,ct);}
                ct.ThrowIfCancellationRequested();
                if(canCommit?.Invoke()==false) throw new OperationCanceledException("Настройки изменены во время проверки.");
                await save(next);
            }
            catch(Exception saveError)
            {
                if(applyAttempted && rollback!=null)
                {
                    try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20)); await rollback(old,timeout.Token); }
                    catch(Exception rollbackError)
                    {
                        try { await stop(); }
                        catch(Exception stopError) { throw new AggregateException("Сохранение, восстановление и остановка подключения завершились ошибкой.",saveError,rollbackError,stopError); }
                        throw new AggregateException("Настройки не сохранены; восстановление подключения не удалось. Подключение остановлено.",saveError,rollbackError);
                    }
                }
                throw;
            }
            return next;
        }
        finally { gate.Release(); }
    }
}
