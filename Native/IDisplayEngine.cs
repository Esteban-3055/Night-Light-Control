using System;

namespace NightLightControl.Native;

public interface IDisplayEngine : IDisposable
{
    bool HasApplied { get; }
    string Description { get; }
    string Backend { get; }
    void Refresh();
    void Apply(int kelvin);
}
