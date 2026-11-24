// Private display ABI based on Gradual Night Light, Fenryr (MIT).
// No gamma ramps, injected code, services, or CloudStore writes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace NightLightControl.Native;

public sealed unsafe class DisplayEngine : IDisposable
{
    private sealed record Target(nint Display, string Source, string Name, bool Capable);
    private readonly List<Target> targets = new();
    private bool modern;
    private nint library;
    private bool initialized;
    public bool HasApplied { get; private set; }
    public string Description => string.Join(", ", targets.Where(t => t.Capable).Select(t => t.Name));
    public string Backend => modern ? "Servicio de pantalla de Windows" : "Motor de color nativo de Windows";
    private static nint* V(nint p) => *(nint**)p;
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private static void Release(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)V(p)[2])(p); }
    private static nint Query(nint p, Guid iid) { nint result = 0; Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)V(p)[0])(p, &iid, &result)); return result; }
    private static nint HString(string value) { Check(WindowsCreateString(value, (uint)value.Length, out var p)); return p; }
    private static byte Property(nint p, int slot) { byte b = 0; Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)V(p)[slot])(p, &b)); return b; }

    public void Refresh()
    {
        if (!initialized) { Check(RoInitialize(0)); initialized = true; }
        nint name = HString("Windows.Internal.Graphics.Display.DisplayEnhancementManagement.DisplayEnhancementManagement");
        nint factory = 0;
        var fresh = new List<Target>(); bool nextModern = modern;
        try
        {
            Guid statics = new("22771028-1658-4e5e-ab77-303691a86fda");
            Check(RoGetActivationFactory(name, ref statics, out factory));
            foreach (var path in ActivePaths())
            {
                var target = new TargetName { Header = new Header { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                CheckWin(DisplayConfigGetDeviceInfo(ref target));
                if (string.IsNullOrEmpty(target.Path)) continue;
                nint display = Open(factory, target.Path);
                try
                {
                    bool capable = Property(display, 6) != 0;
                    nextModern |= Property(display, 11) != 0 | Property(display, 12) != 0;
                    var source = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id } };
                    CheckWin(DisplayConfigGetDeviceInfo(ref source));
                    fresh.Add(new(display, source.Name, string.IsNullOrWhiteSpace(target.Name) ? "Pantalla " + (fresh.Count + 1) : target.Name, capable)); display = 0;
                }
                finally { Release(display); }
            }
            if (!fresh.Any(t => t.Capable)) throw new InvalidOperationException("Ninguna pantalla activa admite Luz nocturna.");
            if (HasApplied && nextModern && !modern) ApplyLegacy(fresh, 6500);
            foreach (var target in targets) Release(target.Display);
            targets.Clear(); targets.AddRange(fresh); fresh.Clear(); modern = nextModern;
        }
        finally { foreach (var t in fresh) Release(t.Display); Release(factory); WindowsDeleteString(name); }
    }

    private static nint Open(nint factory, string path)
    {
        nint id = HString(path), operation = 0, info = 0, inspectable = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)V(factory)[6])(factory, id, &operation));
            info = Query(operation, new("00000036-0000-0000-c000-000000000046"));
            var timeout = Stopwatch.StartNew(); int status = 0;
            do
            {
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)V(info)[7])(info, &status));
                if (status != 0) break;
                if (timeout.ElapsedMilliseconds > 1800) { ((delegate* unmanaged[Stdcall]<nint, int>)V(info)[9])(info); throw new TimeoutException("Windows tardó demasiado en abrir la pantalla."); }
                Thread.Sleep(15);
            } while (true);
            if (status != 1) throw new InvalidOperationException("Windows no pudo abrir la pantalla.");
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(operation)[8])(operation, &inspectable));
            return Query(inspectable, new("cda29a3e-9e7e-4b86-8f5f-368aa0710008"));
        }
        finally { Release(inspectable); Release(info); Release(operation); WindowsDeleteString(id); }
    }

    public void Apply(int kelvin)
    {
        if (kelvin is < 1200 or > 6500) throw new ArgumentOutOfRangeException(nameof(kelvin));
        if (targets.Count == 0) throw new InvalidOperationException("Motor de pantalla sin inicializar.");
        if (!modern) { ApplyLegacy(targets, kelvin); return; }
        Exception? error = null;
        foreach (var target in targets.Where(t => t.Capable))
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, float, double, int, int>)V(target.Display)[8])(target.Display, kelvin, 0, 0);
            if (hr < 0) error = Marshal.GetExceptionForHR(hr); else HasApplied = true;
        }
        if (error != null) throw error;
    }

    private void ApplyLegacy(IEnumerable<Target> displays, int kelvin)
    {
        if (library == 0) library = NativeLibrary.Load(System.IO.Path.Combine(Environment.SystemDirectory, "mscms.dll"));
        var apply = (delegate* unmanaged[Stdcall]<nint, float, uint, uint, int>)NativeLibrary.GetExport(library, "InternalSetDeviceTemperature");
        bool failed = false;
        foreach (var target in displays.Where(t => t.Capable).DistinctBy(t => t.Source))
        {
            nint dc = CreateDC("DISPLAY", target.Source, null, 0);
            try { if (dc == 0 || apply(dc, kelvin, 0, 0) == 0) failed = true; else HasApplied = true; }
            finally { if (dc != 0) DeleteDC(dc); }
        }
        if (failed) throw new InvalidOperationException("No se pudo aplicar la calidez en todas las pantallas.");
    }

    public void Dispose()
    {
        foreach (var target in targets) Release(target.Display); targets.Clear();
        if (library != 0) { NativeLibrary.Free(library); library = 0; }
        if (initialized) { RoUninitialize(); initialized = false; }
    }
    private static void CheckWin(int result) { if (result != 0) throw new System.ComponentModel.Win32Exception(result); }
    private static PathInfo[] ActivePaths()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            CheckWin(GetDisplayConfigBufferSizes(2, out uint pc, out uint mc));
            var paths = new PathInfo[pc]; nint modes = Marshal.AllocHGlobal(checked((int)mc * 64));
            try { int result = QueryDisplayConfig(2, ref pc, paths, ref mc, modes, 0); if (result == 122) continue; CheckWin(result); return paths.Take((int)pc).ToArray(); }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("La configuración de pantallas está cambiando.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint N, D; }
    [StructLayout(LayoutKind.Sequential)] private struct SourceInfo { public Luid Adapter; public uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct TargetInfo { public Luid Adapter; public uint Id, Mode; public int Technology, Rotation, Scaling; public Rational Rate; public int Scanline, Available; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TargetName { public Header Header; public uint Flags; public int Technology; public ushort Manufacturer, Product; public uint Connector; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; }
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, uint length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint result);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, nint modes, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)] private static extern nint CreateDC(string driver, string device, string? output, nint init);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
