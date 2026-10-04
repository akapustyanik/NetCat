using System.Windows.Threading;

namespace NetCat.UI;

// Capture the creating dispatcher even without Application/MainWindow (STA tests).
internal sealed class UiProjection
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Action<Exception> failed;
    public UiProjection(Action<Exception> failed) => this.failed = failed;
    public bool CheckAccess() => dispatcher.CheckAccess();
    public void Post(Action action)
    {
        void Project() { try { action(); } catch (Exception ex) { failed(ex); } }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess()) Project();
        else _ = dispatcher.BeginInvoke(Project, DispatcherPriority.DataBind);
    }
    public Task PostAsync(Action action)
    {
        void Project() { try { action(); } catch (Exception ex) { failed(ex); } }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return Task.CompletedTask;
        if (dispatcher.CheckAccess()) { Project(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(Project, DispatcherPriority.Normal).Task;
    }
}
