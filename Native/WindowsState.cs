// CloudStore decoding adapted from Gradual Night Light by Fenryr (MIT).
// See THIRD-PARTY-NOTICES.txt. This class only reads Windows settings.
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace NightLightControl.Native;

public record WindowsState(int Kelvin, bool Preview, bool ScheduleEnabled);

public static class WindowsSettings
{
    public static byte[] ReadRecord(bool settings)
    {
        string name = settings ? "settings" : "bluelightreductionstate";
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction." + name + @"\windows.data.bluelightreduction." + name);
        return key?.GetValue("Data") is byte[] bytes && bytes.Length is > 0 and <= 65536 ? bytes : throw new InvalidDataException("Abre Luz nocturna en Configuración de Windows una vez y vuelve a intentarlo.");
    }

    public static WindowsState Read()
        => Decode(ReadRecord(true), ReadRecord(false));

    public static WindowsState Decode(byte[] settingsBytes, byte[] stateBytes)
    {
        if (settingsBytes.Length > 65536 || stateBytes.Length > 65536) throw new InvalidDataException("Registro demasiado grande.");
        var settings = Payload(settingsBytes);
        var state = Payload(stateBytes);
        if (!Boolean(state, 30, true)) throw new InvalidDataException("El controlador de pantalla no permite Luz nocturna.");
        bool on = state.TryGetValue(0, out var enabled);
        if (on && (enabled!.Type != 16 || Number(enabled.Bytes) != 0)) throw new InvalidDataException("Estado de Luz nocturna desconocido.");
        bool preview = Boolean(settings, 70);
        ulong raw = settings.ContainsKey(40) ? Number(Get(settings, 40, 15)) : 13000;
        if ((raw & 1) != 0 || raw / 2 is < 1200 or > 6500) throw new InvalidDataException("Temperatura de Windows no reconocida.");
        return new(on || preview ? (int)(raw / 2) : 6500, preview, Boolean(settings, 0));
    }

    private record Field(int Type, byte[] Bytes);
    private static byte[] Get(Dictionary<int, Field> fields, int id, int type) => fields.TryGetValue(id, out var f) && f.Type == type ? f.Bytes : throw new InvalidDataException("Formato de Luz nocturna no reconocido.");
    private static bool Boolean(Dictionary<int, Field> fields, int id, bool fallback = false) => fields.ContainsKey(id) ? Get(fields, id, 2)[0] != 0 : fallback;
    private static ulong Number(byte[] data) { var reader = new Reader(data); ulong n = reader.Varint(); reader.End(); return n; }
    private static Dictionary<int, Field> Parse(byte[] data, bool header = false)
    {
        var reader = new Reader(data);
        if (header && (reader.Byte() != 0x43 || reader.Byte() != 0x42 || reader.Byte() != 1 || reader.Byte() != 0)) throw new InvalidDataException("CloudStore no compatible.");
        var fields = reader.Struct(0); reader.End(); return fields;
    }
    private static Dictionary<int, Field> Payload(byte[] data)
    {
        var outer = Parse(data, true);
        if (!Boolean(Parse(Get(outer, 0, 10)), 0)) throw new InvalidDataException("Metadatos CloudStore inválidos.");
        var container = Parse(Get(outer, 1, 10));
        if (container.ContainsKey(0)) Get(container, 0, 6); // Bond omits default timestamp zero.
        var wrapper = Parse(Get(container, 1, 10));
        if (!wrapper.ContainsKey(1)) return new(); // Omitted empty payload is the default off state.
        var list = new Reader(Get(wrapper, 1, 11));
        if (list.Byte() != 14) throw new InvalidDataException("Contenido CloudStore no compatible.");
        int count = checked((int)list.Varint());
        byte[] payload = list.Take(count); list.End(); return Parse(payload, true);
    }
    private sealed class Reader(byte[] data)
    {
        private int pos;
        public byte Byte() => pos < data.Length ? data[pos++] : throw new InvalidDataException("Registro truncado.");
        public void End() { if (pos != data.Length) throw new InvalidDataException("Datos adicionales en CloudStore."); }
        public byte[] Take(int n) { if (n < 0 || n > data.Length - pos) throw new InvalidDataException("Longitud inválida."); var result = data.AsSpan(pos, n).ToArray(); pos += n; return result; }
        public ulong Varint()
        {
            ulong n = 0;
            for (int shift = 0; shift <= 63; shift += 7) { byte b = Byte(); if (shift == 63 && b > 1) throw new InvalidDataException("Número demasiado grande."); n |= (ulong)(b & 127) << shift; if (b < 128) return n; }
            throw new InvalidDataException("Número inválido.");
        }
        public Dictionary<int, Field> Struct(int depth)
        {
            var fields = new Dictionary<int, Field>(); int last = -1;
            while (true)
            {
                byte h = Byte(); if (h == 0) return fields;
                int type = h & 31, id = h >> 5;
                if (id == 6) id = Byte(); else if (id == 7) id = Byte() | Byte() << 8;
                if (id <= last) throw new InvalidDataException("Campos CloudStore inválidos.");
                last = id; int start = pos; Value(type, depth); fields.Add(id, new(type, data.AsSpan(start, pos - start).ToArray()));
            }
        }
        private void Value(int type, int depth)
        {
            if (depth > 24) throw new InvalidDataException("Registro demasiado anidado.");
            switch (type)
            {
                case 2: if (Byte() > 1) throw new InvalidDataException("Booleano inválido."); break;
                case 3: case 14: Take(1); break;
                case 4: case 5: case 6: case 15: case 16: case 17: Varint(); break;
                case 7: Take(4); break;
                case 8: Take(8); break;
                case 9: case 18: int length = checked((int)Varint()); Take(checked(length * (type == 18 ? 2 : 1))); break;
                case 10: Struct(depth + 1); break;
                case 11: case 12: case 13:
                    int first = Byte(), second = type == 13 ? Byte() : 0;
                    if (first is < 2 or > 18 || type == 13 && second is < 2 or > 18) throw new InvalidDataException("Tipo inválido.");
                    ulong count = Varint(); if (count > 65536) throw new InvalidDataException("Registro demasiado grande.");
                    for (ulong i = 0; i < count; i++) { Value(first, depth + 1); if (type == 13) Value(second, depth + 1); } break;
                default: throw new InvalidDataException("Tipo CloudStore no compatible.");
            }
        }
    }
}
