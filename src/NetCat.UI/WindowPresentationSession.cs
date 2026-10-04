using NetCat.Core;

namespace NetCat.UI;

// User intent is persisted separately from a temporary recovery window.
public sealed class WindowPresentationSession(SettingsStore store, bool isAutostart, bool minimizeToTray)
{
    public WindowPresentationState PreferredState { get; private set; } = store.LoadPresentationState(isAutostart);
    public bool TrayAvailable { get; set; }
    public bool RecoveryWindow { get; private set; }
    public bool SessionEnding { get; private set; }
    public bool StartHidden => isAutostart && minimizeToTray && TrayAvailable &&
        PreferredState == WindowPresentationState.HiddenToTray;

    public void BeginTrayRecovery() => RecoveryWindow = true;
    public void UserActivated() => RecoveryWindow = false;

    public void SaveActualState(WindowPresentationState actual)
    {
        if (SessionEnding) return;
        if (!RecoveryWindow) PreferredState = actual;
        store.SavePresentationState(PreferredState);
    }

    public void UserRequestedHide()
    {
        PreferredState = WindowPresentationState.HiddenToTray;
        RecoveryWindow = !TrayAvailable;
        store.SavePresentationState(PreferredState);
    }

    public void EndSession(WindowPresentationState actual)
    {
        SaveActualState(actual);
        SessionEnding = true;
    }
}
