using System;

namespace NightLightControl.Core;

public sealed class PendingSave(Action persist, Func<DateTimeOffset> now)
{
    public bool IsDirty { get; private set; }
    public Exception? LastError { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; }
    public bool IsDue => IsDirty && now() >= NextAttempt;
    public void MarkChanged() { IsDirty = true; LastError = null; NextAttempt = now().AddMilliseconds(750); }
    public bool TrySave()
    {
        try { persist(); IsDirty = false; LastError = null; return true; }
        catch (Exception ex) { IsDirty = true; LastError = ex; NextAttempt = now().AddSeconds(10); return false; }
    }
}
