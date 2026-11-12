using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DA300Display {

[StructLayout(LayoutKind.Sequential)] struct PKEY { public Guid fmtid; public int pid; public PKEY(Guid g, int p) { fmtid = g; pid = p; } }
[StructLayout(LayoutKind.Explicit, Size = 24)] struct PV { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p8; [FieldOffset(16)] public IntPtr p16; }
[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyStore { [PreserveSig] int GetCount(out uint c); [PreserveSig] int GetAt(uint i, out PKEY k); [PreserveSig] int GetValue(ref PKEY k, out PV v); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); [PreserveSig] int OpenPropertyStore(int a, out IPropertyStore s); [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id); [PreserveSig] int GetState(out int st); }
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceCollection { [PreserveSig] int GetCount(out uint c); [PreserveSig] int Item(uint i, out IMMDevice d); }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c); [PreserveSig] int GetDefaultAudioEndpoint(int f, int r, out IMMDevice d); [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d); }
[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioMeterInformation { [PreserveSig] int GetPeakValue(out float p); }
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioClient {
    [PreserveSig] int Initialize(int mode, uint flags, long dur, long per, IntPtr fmt, IntPtr session); [PreserveSig] int GetBufferSize(out uint n); [PreserveSig] int GetStreamLatency(out long l);
    [PreserveSig] int GetCurrentPadding(out uint p); [PreserveSig] int IsFormatSupported(int mode, IntPtr fmt, out IntPtr closest); [PreserveSig] int GetMixFormat(out IntPtr fmt); }
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPolicyConfig {
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr f); [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr f);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id); [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr ep, IntPtr mix); }
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDevEnumCom { }

class Status {
    public bool Connected; public int Rate, Bits, ValidBits, Channels; public bool Playing, Exclusive; public int HidEvents;
    public bool Asio; public string AsioApp; public int AsioRate; public string AsioBits; public SourceInfo Src; public bool ExtMeter;
    public int EffRate() { return Asio && AsioRate > 0 ? AsioRate : Rate; }
    public bool AsioDsd() { return Asio && ((AsioBits ?? "").IndexOf("DSD", StringComparison.OrdinalIgnoreCase) >= 0 || AsioRate >= 2822400); }
    public int AsioDsdRate() { if (AsioRate >= 2822400) return AsioRate; if (Src != null && Src.Dsd && Src.Rate > 0) return Src.Rate; return AsioRate > 0 ? AsioRate * 64 : 2822400; }
    public bool AsioDop() { return Asio && !AsioDsd() && Src != null && Src.Dsd && Src.Rate == 2822400 && AsioRate == 176400; }
    public bool OutDsd() { return AsioDsd() || AsioDop(); }
    public string PopKey() { return Connected + "|" + Rate + "|" + ValidBits + "|" + Asio + "|" + AsioRate + "|" + AsioBits + "|" + AsioApp + "|" + Exclusive + "|" + (Src == null ? "" : Src.Key()); }
    public string ShowKey() { return PopKey() + "|" + Playing + "|" + HidEvents + "|" + ExtMeter; }
    public string RateStr() { int r = EffRate(); if (r <= 0) return "? kHz"; return r % 1000 == 0 ? (r / 1000) + " kHz" : (r / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " kHz"; }
    public string BitsStr() { if (Asio) return string.IsNullOrEmpty(AsioBits) ? "" : AsioBits.Replace(" bits", "-bit").Replace(" bit", "-bit"); return ValidBits + "-bit"; }
}

static class Audio {
    static PKEY kName = new PKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static PKEY kFmt = new PKEY(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PV pv);
    static IMMDeviceEnumerator en; static string id; public static string DeviceId { get { return id; } }
    static string Name(IMMDevice d) {
        IPropertyStore s; if (d.OpenPropertyStore(0, out s) != 0) return "";
        PV v; string n = ""; if (s.GetValue(ref kName, out v) == 0) { if (v.vt == 31) n = Marshal.PtrToStringUni(v.p8); PropVariantClear(ref v); }
        Marshal.ReleaseComObject(s); return n ?? "";
    }
    static string Find() {
        IMMDeviceCollection col; en.EnumAudioEndpoints(0, 1, out col); uint n; col.GetCount(out n); string found = null;
        for (uint i = 0; i < n && found == null; i++) {
            IMMDevice d; col.Item(i, out d); string nm = Name(d);
            if (nm.IndexOf("Denon", StringComparison.OrdinalIgnoreCase) >= 0 || nm.IndexOf("DA-300", StringComparison.OrdinalIgnoreCase) >= 0) d.GetId(out found);
            Marshal.ReleaseComObject(d);
        }
        Marshal.ReleaseComObject(col); return found;
    }
    public static void Poll(Status s) {
        s.Connected = false;
        try {
            if (en == null) en = (IMMDeviceEnumerator)new MMDevEnumCom();
            if (id == null) id = Find();
            if (id == null) return;
            IMMDevice d; if (en.GetDevice(id, out d) != 0) { id = null; return; }
            int st; d.GetState(out st);
            if (st != 1) { Marshal.ReleaseComObject(d); id = null; return; }
            s.Connected = true;
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) == 0) {
                PV v;
                if (ps.GetValue(ref kFmt, out v) == 0) {
                    if (v.vt == 65 && v.p16 != IntPtr.Zero) {
                        int tag = Marshal.ReadInt16(v.p16, 0) & 0xFFFF; s.Channels = Marshal.ReadInt16(v.p16, 2); s.Rate = Marshal.ReadInt32(v.p16, 4);
                        s.Bits = Marshal.ReadInt16(v.p16, 14); int cbx = Marshal.ReadInt16(v.p16, 16);
                        s.ValidBits = (tag == 0xFFFE && cbx >= 22) ? Marshal.ReadInt16(v.p16, 18) : s.Bits;
                    }
                    PropVariantClear(ref v);
                }
                Marshal.ReleaseComObject(ps);
            }
            object o; Guid gm = typeof(IAudioMeterInformation).GUID;
            if (d.Activate(ref gm, 23, IntPtr.Zero, out o) == 0) { var m = (IAudioMeterInformation)o; float pk; m.GetPeakValue(out pk); s.Playing = pk > 0.00001f; Marshal.ReleaseComObject(o); }
            Marshal.ReleaseComObject(d);
        } catch { s.Connected = false; id = null; }
    }
    public static bool Exclusive() {
        if (en == null || id == null) return false; IMMDevice d; if (en.GetDevice(id, out d) != 0) return false;
        bool ex = false; object o; Guid g = typeof(IAudioClient).GUID;
        try {
            if (d.Activate(ref g, 23, IntPtr.Zero, out o) == 0) {
                var ac = (IAudioClient)o; IntPtr f;
                if (ac.GetMixFormat(out f) == 0) { int hr = ac.Initialize(0, 0, 2000000, 0, f, IntPtr.Zero); ex = hr == unchecked((int)0x8889000A); Marshal.FreeCoTaskMem(f); }
                Marshal.ReleaseComObject(o);
            }
        } catch { } finally { Marshal.ReleaseComObject(d); }
        return ex;
    }
    static IntPtr WithRate(IntPtr f, int rate, out int size) {
        int tag = Marshal.ReadInt16(f, 0) & 0xFFFF; int cbx = tag == 1 ? 0 : Marshal.ReadInt16(f, 16); size = 18 + cbx;
        var buf = new byte[size]; Marshal.Copy(f, buf, 0, size); int block = BitConverter.ToUInt16(buf, 12);
        BitConverter.GetBytes(rate).CopyTo(buf, 4); BitConverter.GetBytes(rate * block).CopyTo(buf, 8);
        IntPtr n = Marshal.AllocHGlobal(size); Marshal.Copy(buf, 0, n, size); return n;
    }
    public static int SetRate(int rate) {
        if (id == null) return -1; IPolicyConfig pc = null; IntPtr dev = IntPtr.Zero, mix = IntPtr.Zero, nd = IntPtr.Zero, nm = IntPtr.Zero;
        try {
            pc = (IPolicyConfig)new PolicyConfigCom(); int hr = pc.GetDeviceFormat(id, 0, out dev); if (hr != 0) return hr;
            hr = pc.GetMixFormat(id, out mix); int sz;
            nd = WithRate(dev, rate, out sz); nm = hr == 0 ? WithRate(mix, rate, out sz) : nd;
            return pc.SetDeviceFormat(id, nd, nm);
        } catch (Exception ex) { return Marshal.GetHRForException(ex); }
        finally {
            if (dev != IntPtr.Zero) Marshal.FreeCoTaskMem(dev); if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            if (nm != IntPtr.Zero && nm != nd) Marshal.FreeHGlobal(nm); if (nd != IntPtr.Zero) Marshal.FreeHGlobal(nd); if (pc != null) Marshal.ReleaseComObject(pc);
        }
    }
}
static class AsioWatch {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint acc, bool inh, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] mods, uint cb, out uint needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleBaseName(IntPtr h, IntPtr mod, StringBuilder name, uint size);
    public static volatile string App; static int lastPid = -1;
    public static void Start() { var t = new Thread(Loop); t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start(); }
    static bool HasAsio(int pid) {
        IntPtr h = OpenProcess(0x0410, false, pid); if (h == IntPtr.Zero) return false;
        try {
            var mods = new IntPtr[1024]; uint need;
            if (!EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out need, 3)) return false;
            int n = Math.Min((int)(need / IntPtr.Size), mods.Length); var sb = new StringBuilder(260);
            for (int i = 0; i < n; i++) { sb.Length = 0; GetModuleBaseName(h, mods[i], sb, 260); if (sb.ToString().StartsWith("de_usbaudio_asio", StringComparison.OrdinalIgnoreCase)) return true; }
            return false;
        } finally { CloseHandle(h); }
    }
    static void Loop() {
        int self = Process.GetCurrentProcess().Id;
        while (true) {
            try {
                string found = null;
                if (lastPid > 0) { try { var lp = Process.GetProcessById(lastPid); if (HasAsio(lastPid)) found = lp.ProcessName; } catch { } }
                if (found == null) {
                    lastPid = -1;
                    foreach (var p in Process.GetProcesses()) {
                        try { if (p.Id == self || p.Id <= 4 || p.ProcessName.Equals("de_usbaudio_cpl", StringComparison.OrdinalIgnoreCase)) continue;
                              if (HasAsio(p.Id)) { found = p.ProcessName; lastPid = p.Id; break; } }
                        catch { } finally { p.Dispose(); }
                    }
                }
                App = found;
            } catch { }
            Thread.Sleep(2000);
        }
    }
}

static class HidWatch {
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA di);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA di, IntPtr det, int sz, out int req, IntPtr dd);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] b, int n, out int r, IntPtr o);
    public static volatile int Events;
    public static void Start() { var t = new Thread(Loop); t.IsBackground = true; t.Start(); }
    static string FindPath() {
        Guid g; HidD_GetHidGuid(out g); IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); string found = null;
        for (int i = 0; found == null; i++) {
            var di = new SP_DEVICE_INTERFACE_DATA(); di.cbSize = Marshal.SizeOf(di);
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref di)) break;
            int req; SetupDiGetDeviceInterfaceDetail(set, ref di, IntPtr.Zero, 0, out req, IntPtr.Zero);
            IntPtr buf = Marshal.AllocHGlobal(req); Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (SetupDiGetDeviceInterfaceDetail(set, ref di, buf, req, out req, IntPtr.Zero)) {
                string p = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                if (p.IndexOf("vid_154e&pid_1003", StringComparison.OrdinalIgnoreCase) >= 0) found = p;
            }
            Marshal.FreeHGlobal(buf);
        }
        SetupDiDestroyDeviceInfoList(set); return found;
    }
    static void Loop() {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DA300Display");
        Directory.CreateDirectory(dir); string log = Path.Combine(dir, "hid.log");
        while (true) {
            try {
                string path = FindPath();
                if (path != null) using (var h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                    if (!h.IsInvalid) { var b = new byte[3]; int r; while (ReadFile(h, b, 3, out r, IntPtr.Zero)) { Events++; File.AppendAllText(log, DateTime.Now.ToString("o") + " " + BitConverter.ToString(b, 0, r) + Environment.NewLine); } }
                }
            } catch { }
            Thread.Sleep(3000);
        }
    }
}

static class Font5 {
    public static readonly Dictionary<char, byte[]> G = new Dictionary<char, byte[]>();
    static void A(char c, params byte[] b) { G[c] = b; }
    static Font5() {
        A('0',0x3E,0x51,0x49,0x45,0x3E); A('1',0x00,0x42,0x7F,0x40,0x00); A('2',0x42,0x61,0x51,0x49,0x46); A('3',0x21,0x41,0x45,0x4B,0x31); A('4',0x18,0x14,0x12,0x7F,0x10);
        A('5',0x27,0x45,0x45,0x45,0x39); A('6',0x3C,0x4A,0x49,0x49,0x30); A('7',0x01,0x71,0x09,0x05,0x03); A('8',0x36,0x49,0x49,0x49,0x36); A('9',0x06,0x49,0x49,0x29,0x1E);
        A('A',0x7E,0x11,0x11,0x11,0x7E); A('B',0x7F,0x49,0x49,0x49,0x36); A('C',0x3E,0x41,0x41,0x41,0x22); A('D',0x7F,0x41,0x41,0x22,0x1C); A('E',0x7F,0x49,0x49,0x49,0x41);
        A('F',0x7F,0x09,0x09,0x09,0x01); A('G',0x3E,0x41,0x49,0x49,0x7A); A('H',0x7F,0x08,0x08,0x08,0x7F); A('I',0x00,0x41,0x7F,0x41,0x00); A('J',0x20,0x40,0x41,0x3F,0x01);
        A('K',0x7F,0x08,0x14,0x22,0x41); A('L',0x7F,0x40,0x40,0x40,0x40); A('M',0x7F,0x02,0x0C,0x02,0x7F); A('N',0x7F,0x04,0x08,0x10,0x7F); A('O',0x3E,0x41,0x41,0x41,0x3E);
        A('P',0x7F,0x09,0x09,0x09,0x06); A('Q',0x3E,0x41,0x51,0x21,0x5E); A('R',0x7F,0x09,0x19,0x29,0x46); A('S',0x46,0x49,0x49,0x49,0x31); A('T',0x01,0x01,0x7F,0x01,0x01);
        A('U',0x3F,0x40,0x40,0x40,0x3F); A('V',0x1F,0x20,0x40,0x20,0x1F); A('W',0x3F,0x40,0x38,0x40,0x3F); A('X',0x63,0x14,0x08,0x14,0x63); A('Y',0x07,0x08,0x70,0x08,0x07);
        A('Z',0x61,0x51,0x49,0x45,0x43); A(' ',0,0,0,0,0); A('.',0x00,0x60,0x60,0x00,0x00); A('-',0x08,0x08,0x08,0x08,0x08); A(':',0x00,0x36,0x36,0x00,0x00);
        A('/',0x20,0x10,0x08,0x04,0x02); A('>',0x7F,0x3E,0x1C,0x08,0x00); A('+',0x08,0x08,0x3E,0x08,0x08); A('?',0x02,0x01,0x51,0x09,0x06); A('(',0x00,0x1C,0x22,0x41,0x00);
        A(')',0x00,0x41,0x22,0x1C,0x00); A('_',0x40,0x40,0x40,0x40,0x40); A('\u00B7',0x00,0x00,0x08,0x00,0x00); A(',',0x00,0x50,0x30,0x00,0x00); A('%',0x23,0x13,0x08,0x64,0x62);
    }
}
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionManager2 { [PreserveSig] int GetAudioSessionControl(IntPtr a, uint f, out IntPtr c); [PreserveSig] int GetSimpleAudioVolume(IntPtr a, uint f, out IntPtr v); [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e); }
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionEnumerator { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s); }
[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioSessionControl2 {
  [PreserveSig] int GetState(out int s); [PreserveSig] int GetDisplayName(out IntPtr n); [PreserveSig] int SetDisplayName(IntPtr n, IntPtr g); [PreserveSig] int GetIconPath(out IntPtr p); [PreserveSig] int SetIconPath(IntPtr p, IntPtr g);
  [PreserveSig] int GetGroupingParam(out Guid g); [PreserveSig] int SetGroupingParam(ref Guid g, IntPtr c); [PreserveSig] int RegisterAudioSessionNotification(IntPtr n); [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
  [PreserveSig] int GetSessionIdentifier(out IntPtr s); [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s); [PreserveSig] int GetProcessId(out uint pid); [PreserveSig] int IsSystemSoundsSession(); }

class SourceInfo {
    public string Codec = "?"; public int Rate; public int Bits; public bool Dsd; public bool Float; public bool Lossy; public int Kbps; public string File; public string App;
    public bool Complete { get { if (Rate <= 0 || Codec == "?") return false; if (Dsd) return true; return Lossy ? Kbps > 0 : Bits > 0; } }
    public static string RateTxt(int hz) { return hz % 1000 == 0 ? (hz / 1000).ToString() : (hz / 1000.0).ToString("0.0", CultureInfo.InvariantCulture); }
    public static string MHz(int hz) { return (hz / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"; }
    public string RateField() { return Dsd ? MHz(Rate) : RateTxt(Rate); }
    public string BitsField() { if (Dsd) return "1"; if (Lossy) return Kbps + "K"; return Bits + (Float ? "F" : ""); }
    public string Short() { string b = BitsField(); return Codec + " " + RateField() + (b.Length > 0 ? "/" + b : ""); }
    public string Key() { return File + "|" + Rate + "|" + Bits + "|" + Kbps; }
}

static class AudioFileInfo {
    static uint BE32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
    static int Find(byte[] b, string s, int from) { var k = Encoding.ASCII.GetBytes(s); for (int i = Math.Max(0, from); i <= b.Length - k.Length; i++) { int j = 0; while (j < k.Length && b[i + j] == k[j]) j++; if (j == k.Length) return i; } return -1; }
    static int FindLast(byte[] b, string s) { var k = Encoding.ASCII.GetBytes(s); for (int i = b.Length - k.Length; i >= 0; i--) { int j = 0; while (j < k.Length && b[i + j] == k[j]) j++; if (j == k.Length) return i; } return -1; }
    static byte[] ReadAt(FileStream fs, long off, int n) {
        if (off >= fs.Length) return new byte[0]; fs.Seek(off, SeekOrigin.Begin); n = (int)Math.Min(n, fs.Length - off);
        var b = new byte[n]; int r = 0; while (r < n) { int k = fs.Read(b, r, n - r); if (k <= 0) break; r += k; } return b;
    }
    static readonly int[] Mp3Br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    static readonly int[] Mp3Br2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
    public static SourceInfo Parse(string path) {
        try {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                long len = fs.Length; var h = ReadAt(fs, 0, 1 << 20); var t = len > (2 << 20) ? ReadAt(fs, len - (1 << 20), 1 << 20) : h;
                var si = new SourceInfo { File = path }; string ext = Path.GetExtension(path).ToLowerInvariant();
                string magic = h.Length >= 4 ? Encoding.ASCII.GetString(h, 0, 4) : "";
                if (magic == "fLaC" && h.Length > 22) { si.Codec = "FLAC"; si.Rate = (h[18] << 12) | (h[19] << 4) | (h[20] >> 4); si.Bits = (((h[20] & 1) << 4) | (h[21] >> 4)) + 1; return si; }
                if (magic == "RIFF" || magic == "RF64") {
                    int o = Find(h, "fmt ", 12); if (o < 0 || o + 40 > h.Length) return null; int fmt = h[o + 8] | h[o + 9] << 8;
                    si.Codec = "WAV"; si.Rate = BitConverter.ToInt32(h, o + 12); si.Bits = h[o + 22] | h[o + 23] << 8; si.Float = fmt == 3;
                    if (fmt == 0xFFFE && (h[o + 24] | h[o + 25] << 8) >= 22) { si.Bits = h[o + 26] | h[o + 27] << 8; si.Float = h[o + 32] == 3; }
                    return si; }
                if (magic == "FORM") {
                    int o = Find(h, "COMM", 12); if (o < 0) return null; si.Codec = "AIFF"; si.Bits = h[o + 14] << 8 | h[o + 15];
                    int exp = ((h[o + 16] & 0x7F) << 8) | h[o + 17]; ulong mant = 0; for (int i = 0; i < 8; i++) mant = (mant << 8) | h[o + 18 + i];
                    si.Rate = (int)Math.Round(mant * Math.Pow(2, exp - 16383 - 63)); return si; }
                if (magic == "DSD ") { si.Codec = "DSF"; si.Dsd = true; si.Rate = BitConverter.ToInt32(h, 56); si.Bits = 1; return si; }
                if (magic == "FRM8") { int o = Find(h, "FS  ", 12); si.Codec = "DFF"; si.Dsd = true; if (o >= 0) si.Rate = (int)BE32(h, o + 12); si.Bits = 1; return si; }
                if (magic == "OggS") {
                    int lastPage = FindLast(t, "OggS"); long gran = lastPage >= 0 && lastPage + 14 <= t.Length ? BitConverter.ToInt64(t, lastPage + 6) : 0;
                    int o = Find(h, "OpusHead", 0);
                    if (o >= 0) { si.Codec = "OPUS"; si.Lossy = true; si.Rate = 48000; int pre = h[o + 10] | h[o + 11] << 8; double dur = (gran - pre) / 48000.0; if (dur > 1) si.Kbps = (int)Math.Round(len * 8 / dur / 1000); return si; }
                    o = Find(h, "\u0001vorbis", 0);
                    if (o >= 0) { si.Codec = "VORB"; si.Lossy = true; si.Rate = BitConverter.ToInt32(h, o + 12); int nom = BitConverter.ToInt32(h, o + 20);
                                  if (nom > 0) si.Kbps = (int)Math.Round(nom / 1000.0); else if (si.Rate > 0 && gran > 0) si.Kbps = (int)Math.Round(len * 8 / (gran / (double)si.Rate) / 1000); return si; }
                    o = Find(h, "\u007FFLAC", 0);
                    if (o >= 0) { int b = o + 13 + 4 + 10; si.Codec = "FLAC"; si.Rate = (h[b] << 12) | (h[b + 1] << 4) | (h[b + 2] >> 4); si.Bits = (((h[b + 2] & 1) << 4) | (h[b + 3] >> 4)) + 1; return si; }
                    return null; }
                if (ext == ".m4a" || ext == ".alac" || ext == ".mp4" || (ext == ".aac" && !magic.StartsWith("ID3"))) {
                    foreach (var buf in new[] { h, t }) {
                        int o = Find(buf, "stsd", 0); if (o < 0 || o + 52 > buf.Length) continue;
                        string fmt = Encoding.ASCII.GetString(buf, o + 16, 4);
                        if (fmt == "alac") { int a = Find(buf, "alac", o + 20 + 28); if (a > 0 && a + 32 <= buf.Length) { si.Codec = "ALAC"; si.Bits = buf[a + 13]; si.Rate = (int)BE32(buf, a + 28); return si; } }
                        if (fmt == "mp4a") {
                            si.Codec = "AAC"; si.Lossy = true; si.Rate = (int)(BE32(buf, o + 44) >> 16);
                            int e = Find(buf, "esds", o);
                            if (e > 0) for (int i = e + 8; i < Math.Min(buf.Length - 20, e + 64); i++) if (buf[i] == 0x04) {
                                int j = i + 1; int guard = 0; while ((buf[j] & 0x80) != 0 && guard++ < 4) j++; j++;
                                if (j + 13 <= buf.Length) { uint mx = BE32(buf, j + 5), avg = BE32(buf, j + 9); uint br = avg > 0 ? avg : mx; if (br > 8000 && br < 2000000) { si.Kbps = (int)Math.Round(br / 1000.0); break; } } }
                            return si; }
                    }
                    return null; }
                if (ext == ".mp3" || magic.StartsWith("ID3")) {
                    long off = 0;
                    if (magic.StartsWith("ID3") && h.Length > 10) { off = 10 + ((h[6] & 0x7F) << 21 | (h[7] & 0x7F) << 14 | (h[8] & 0x7F) << 7 | (h[9] & 0x7F)); if ((h[5] & 0x10) != 0) off += 10; }
                    var m = ReadAt(fs, off, 1 << 17);
                    for (int i = 0; i < m.Length - 200; i++) {
                        if (m[i] != 0xFF || (m[i + 1] & 0xE0) != 0xE0) continue;
                        int ver = (m[i + 1] >> 3) & 3, layer = (m[i + 1] >> 1) & 3, bri = (m[i + 2] >> 4) & 15, ri = (m[i + 2] >> 2) & 3, mode = m[i + 3] >> 6;
                        if (ver == 1 || layer != 1 || ri == 3 || bri == 0 || bri == 15) continue;
                        int[] baseR = { 44100, 48000, 32000 }; int rate = baseR[ri]; if (ver == 2) rate /= 2; else if (ver == 0) rate /= 4;
                        int kbps = ver == 3 ? Mp3Br1[bri] : Mp3Br2[bri]; int spf = ver == 3 ? 1152 : 576;
                        int side = ver == 3 ? (mode == 3 ? 17 : 32) : (mode == 3 ? 9 : 17); int x = i + 4 + side;
                        string tag = Encoding.ASCII.GetString(m, x, 4);
                        if ((tag == "Xing" || tag == "Info") && (BE32(m, x + 4) & 1) != 0) { uint frames = BE32(m, x + 8); if (frames > 0) kbps = (int)Math.Round((len - off) * 8.0 / (frames * (double)spf / rate) / 1000); }
                        else if (Encoding.ASCII.GetString(m, i + 36, 4) == "VBRI") { uint frames = BE32(m, i + 36 + 14); if (frames > 0) kbps = (int)Math.Round((len - off) * 8.0 / (frames * (double)spf / rate) / 1000); }
                        si.Codec = "MP3"; si.Lossy = true; si.Rate = rate; si.Kbps = kbps; return si;
                    }
                    return null; }
                return null;
            }
        } catch { return null; }
    }
}
static class SourceWatch {
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr sp, IntPtr sh, IntPtr tp, out IntPtr th, uint a, bool i, uint o);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetFinalPathNameByHandle(IntPtr h, StringBuilder s, uint n, uint f);
    static readonly string[] Ext = { ".flac", ".wav", ".wave", ".aif", ".aiff", ".aifc", ".dsf", ".dff", ".mp3", ".m4a", ".alac", ".aac", ".ogg", ".oga", ".opus", ".wv", ".ape", ".wma" };
    public static volatile SourceInfo Current; public static volatile string DeviceId;
    static string lastFile; static SourceInfo lastInfo;
    public static void Start() { var t = new Thread(Loop); t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.SetApartmentState(ApartmentState.MTA); t.Start(); }
    static List<uint> ActivePids(IMMDeviceEnumerator en, string id) {
        var r = new List<uint>(); IMMDevice d; if (en.GetDevice(id, out d) != 0) return r;
        try {
            object o; Guid g = typeof(IAudioSessionManager2).GUID; if (d.Activate(ref g, 23, IntPtr.Zero, out o) != 0) return r; var sm = (IAudioSessionManager2)o;
            IAudioSessionEnumerator se; if (sm.GetSessionEnumerator(out se) == 0) { int n; se.GetCount(out n);
                for (int i = 0; i < n; i++) { IAudioSessionControl2 s; if (se.GetSession(i, out s) != 0) continue; int st; s.GetState(out st); uint pid; s.GetProcessId(out pid); if (st == 1 && pid != 0) r.Add(pid); Marshal.ReleaseComObject(s); }
                Marshal.ReleaseComObject(se); }
            Marshal.ReleaseComObject(o);
        } finally { Marshal.ReleaseComObject(d); }
        return r;
    }
    public static List<string> AudioFiles(uint pid) {
        var res = new List<string>(); IntPtr proc = OpenProcess(0x40, false, pid); if (proc == IntPtr.Zero) return res;
        int len = 4 << 20; IntPtr buf; int ret, st;
        while (true) { buf = Marshal.AllocHGlobal(len); st = NtQuerySystemInformation(0x40, buf, len, out ret); if (st == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(buf); len = Math.Max(len * 2, ret + 65536); continue; } break; }
        try {
            if (st != 0) return res; long count = Marshal.ReadIntPtr(buf).ToInt64(); IntPtr p0 = IntPtr.Add(buf, 16);
            for (long i = 0; i < count; i++) {
                IntPtr e = IntPtr.Add(p0, (int)(i * 40)); if (Marshal.ReadIntPtr(e, 8).ToInt64() != pid) continue;
                IntPtr dup; if (!DuplicateHandle(proc, Marshal.ReadIntPtr(e, 16), GetCurrentProcess(), out dup, 0, false, 2)) continue;
                try { if (GetFileType(dup) != 1) continue; var sb = new StringBuilder(1024); if (GetFinalPathNameByHandle(dup, sb, 1024, 0) == 0) continue;
                      string path = sb.ToString(); if (path.StartsWith(@"\\?\UNC\")) path = @"\\" + path.Substring(8); else if (path.StartsWith(@"\\?\")) path = path.Substring(4);
                      if (Array.IndexOf(Ext, Path.GetExtension(path).ToLowerInvariant()) >= 0 && !res.Contains(path)) res.Add(path); }
                finally { CloseHandle(dup); }
            }
        } finally { Marshal.FreeHGlobal(buf); CloseHandle(proc); }
        return res;
    }
    static void Loop() {
        IMMDeviceEnumerator en = null; int self = Process.GetCurrentProcess().Id;
        while (true) {
            try {
                string id = DeviceId; SourceInfo found = null;
                if (id != null) {
                    if (en == null) en = (IMMDeviceEnumerator)new MMDevEnumCom();
                    foreach (uint pid in ActivePids(en, id)) {
                        if (pid == self) continue;
                        foreach (var f in AudioFiles(pid)) {
                            SourceInfo inf = f == lastFile ? lastInfo : AudioFileInfo.Parse(f);
                            if (inf != null) { try { inf.App = Process.GetProcessById((int)pid).ProcessName; } catch { } lastFile = f; lastInfo = inf; found = inf; break; }
                        }
                        if (found != null) break;
                    }
                }
                Current = found;
            } catch { }
            Thread.Sleep(2000);
        }
    }
}
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioCaptureClient { [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong pos, out ulong qpc); [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint n); }
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioClientFull {
    [PreserveSig] int Initialize(int mode, uint flags, long dur, long per, IntPtr fmt, IntPtr session); [PreserveSig] int GetBufferSize(out uint n); [PreserveSig] int GetStreamLatency(out long l);
    [PreserveSig] int GetCurrentPadding(out uint p); [PreserveSig] int IsFormatSupported(int mode, IntPtr fmt, out IntPtr closest); [PreserveSig] int GetMixFormat(out IntPtr fmt);
    [PreserveSig] int GetDevicePeriod(out long d, out long m); [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset(); [PreserveSig] int SetEventHandle(IntPtr h);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o); }

static class LoopMeter {
    static readonly object lk = new object(); static float pl, pr; static DateTime lastClip = DateTime.MinValue;
    public static volatile string DeviceId; public static volatile bool Active;
    public static void Start() { var t = new Thread(Loop); t.IsBackground = true; t.Priority = ThreadPriority.AboveNormal; t.SetApartmentState(ApartmentState.MTA); t.Start(); }
    public static void Read(out float l, out float r, out bool clip) { lock (lk) { l = pl; r = pr; pl = 0; pr = 0; } clip = (DateTime.Now - lastClip).TotalMilliseconds < 2000; }
    static void Loop() {
        IMMDeviceEnumerator en = null;
        while (true) {
            string id = DeviceId; if (id == null) { Active = false; Thread.Sleep(1000); continue; }
            object o = null, co = null; IMMDevice d = null; IntPtr fmt = IntPtr.Zero; IAudioClientFull ac = null;
            try {
                if (en == null) en = (IMMDeviceEnumerator)new MMDevEnumCom();
                if (en.GetDevice(id, out d) != 0) { Thread.Sleep(1000); continue; }
                Guid g = typeof(IAudioClientFull).GUID; if (d.Activate(ref g, 23, IntPtr.Zero, out o) != 0) { Thread.Sleep(1000); continue; }
                ac = (IAudioClientFull)o; if (ac.GetMixFormat(out fmt) != 0) { Thread.Sleep(1000); continue; }
                int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF, ch = Marshal.ReadInt16(fmt, 2), bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadByte(fmt, 24) == 3);
                if (ac.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero) != 0) { Thread.Sleep(2000); continue; }
                Guid cg = typeof(IAudioCaptureClient).GUID; if (ac.GetService(ref cg, out co) != 0) { Thread.Sleep(2000); continue; }
                var cc = (IAudioCaptureClient)co; if (ac.Start() != 0) { Thread.Sleep(2000); continue; } Active = true;
                float[] fb = new float[0]; short[] sb = new short[0]; int[] ib = new int[0];
                while (DeviceId == id) {
                    Thread.Sleep(10); uint n; if (cc.GetNextPacketSize(out n) != 0) break;
                    while (n > 0) {
                        IntPtr data; uint frames, flags; ulong p1, p2; if (cc.GetBuffer(out data, out frames, out flags, out p1, out p2) != 0) { n = 0; break; }
                        if ((flags & 2) == 0 && frames > 0 && ch > 0) {
                            int cnt = (int)frames * ch; float ml = 0, mr = 0;
                            if (isFloat && bits == 32) { if (fb.Length < cnt) fb = new float[cnt]; Marshal.Copy(data, fb, 0, cnt); for (int i = 0; i < cnt; i += ch) { float a = Math.Abs(fb[i]); if (a > ml) ml = a; if (ch > 1) { float b2 = Math.Abs(fb[i + 1]); if (b2 > mr) mr = b2; } } }
                            else if (bits == 16) { if (sb.Length < cnt) sb = new short[cnt]; Marshal.Copy(data, sb, 0, cnt); for (int i = 0; i < cnt; i += ch) { float a = Math.Abs(sb[i] / 32768f); if (a > ml) ml = a; if (ch > 1) { float b2 = Math.Abs(sb[i + 1] / 32768f); if (b2 > mr) mr = b2; } } }
                            else if (bits == 32) { if (ib.Length < cnt) ib = new int[cnt]; Marshal.Copy(data, ib, 0, cnt); for (int i = 0; i < cnt; i += ch) { float a = Math.Abs(ib[i] / 2147483648f); if (a > ml) ml = a; if (ch > 1) { float b2 = Math.Abs(ib[i + 1] / 2147483648f); if (b2 > mr) mr = b2; } } }
                            if (ch == 1) mr = ml;
                            lock (lk) { if (ml > pl) pl = ml; if (mr > pr) pr = mr; }
                            if (ml >= 0.9772f || mr >= 0.9772f) lastClip = DateTime.Now;
                        }
                        cc.ReleaseBuffer(frames); if (cc.GetNextPacketSize(out n) != 0) n = 0;
                    }
                }
                ac.Stop();
            } catch { }
            finally { Active = false; if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt); if (co != null) Marshal.ReleaseComObject(co); if (o != null) Marshal.ReleaseComObject(o); if (d != null) Marshal.ReleaseComObject(d); }
            Thread.Sleep(500);
        }
    }
}
static class FoobarMeter {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint acc, bool inh, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr h, uint acc, uint hi, uint lo, UIntPtr n);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();
    static IntPtr map, view; public static int Rate;
    static void Close() { if (view != IntPtr.Zero) UnmapViewOfFile(view); if (map != IntPtr.Zero) CloseHandle(map); view = map = IntPtr.Zero; }
    static float F(int off) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(view, off)), 0); }
    public static bool Read(out float l, out float r, out bool clip) {
        l = r = 0; clip = false;
        if (view == IntPtr.Zero) {
            map = OpenFileMapping(4, false, @"Local\DA300DisplayFoobarMeter"); if (map == IntPtr.Zero) return false;
            view = MapViewOfFile(map, 4, 0, 0, (UIntPtr)32); if (view == IntPtr.Zero) { Close(); return false; }
        }
        if (Marshal.ReadInt32(view, 0) != 0x44544D31) { Close(); return false; }
        for (int i = 0; i < 4; i++) {
            uint s1 = (uint)Marshal.ReadInt32(view, 4); float a = F(8), b = F(12); uint ct = (uint)Marshal.ReadInt32(view, 16), up = (uint)Marshal.ReadInt32(view, 20); int playing = Marshal.ReadInt32(view, 24);
            uint s2 = (uint)Marshal.ReadInt32(view, 4);
            if (s1 == s2 && (s1 & 1) == 0) {
                uint now = GetTickCount(); if (now - up > 3000) { Close(); return false; }
                l = a; r = b; Rate = Marshal.ReadInt32(view, 28); clip = ct != 0 && now - ct < 2000; return playing != 0 && now - up < 300;
            }
        }
        return false;
    }
}
class Popup : Form {
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const string RegKey = @"Software\DA300Display"; const int GW = 128, GH = 64;
    static readonly Color Oled = Color.FromArgb(110, 225, 255), AsioCol = Color.FromArgb(120, 255, 160), ExclCol = Color.FromArgb(255, 210, 90), Dim = Color.FromArgb(55, 95, 110);
    Color[,] px = new Color[GW, GH]; Dictionary<int, SolidBrush[]> brushes = new Dictionary<int, SolidBrush[]>(); SolidBrush offBrush = new SolidBrush(Color.FromArgb(13, 15, 19));
    System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
    public float PanelScale = 1.5f; public bool Pinned; bool hasPos; Point savedPos;
    public bool MeterOn, UseExt; Color warnCol = Color.FromArgb(255, 140, 60); Color meterCol = Color.FromArgb(110, 225, 255); float dl = -90, dr = -90, hl = -90, hr = -90; DateTime hlT, hrT; bool clipOn;
    static readonly Color LedOn = Color.FromArgb(255, 45, 45);
    const int MY0 = 44, MY1 = 52, BX0 = 3, BX1 = 115;
    static float ToX(float db) { float f = (db + 50f) / 50f; if (f < 0) f = 0; if (f > 1) f = 1; return BX0 + f * (BX1 - BX0); }
    static float Db(float v) { return v > 0.000001f ? 20f * (float)Math.Log10(v) : -90f; }
    public void FeedMeter(float l, float r, bool clip) {
        float ldb = Db(l), rdb = Db(r); var now = DateTime.Now;
        dl = Math.Max(ldb, dl - 1.5f); dr = Math.Max(rdb, dr - 1.5f);
        if (ldb >= hl) { hl = ldb; hlT = now; } else if ((now - hlT).TotalMilliseconds > 1000) hl = Math.Max(dl, hl - 1f);
        if (rdb >= hr) { hr = rdb; hrT = now; } else if ((now - hrT).TotalMilliseconds > 1000) hr = Math.Max(dr, hr - 1f);
        bool changed = clip != clipOn; clipOn = clip; if (MeterOn || changed) DrawMeter();
    }
    void Bar(float db, float hold, int y) {
        float warnX = ToX(-3f); int x1 = (int)ToX(db);
        for (int x = BX0; x < x1; x++) { Color c = x >= warnX ? warnCol : meterCol; for (int yy = y; yy < y + 3; yy++) px[x, yy] = c; }
        if (hold > -50f) { int hx = Math.Min((int)ToX(hold), BX1); Color c = hx >= warnX ? warnCol : meterCol; for (int yy = y; yy < y + 3; yy++) px[hx, yy] = c; }
    }
    void DrawMeter() {
        if (!MeterOn) return;
        for (int x = 0; x < GW; x++) for (int y = MY0; y <= MY1; y++) px[x, y] = Color.Empty;
        if (MeterOn) { Bar(dl, hl, 45); Bar(dr, hr, 49); if (clipOn) for (int x = 119; x <= 125; x++) for (int y = 45; y <= 51; y++) px[x, y] = LedOn; }
        float chh = ClientSize.Height / (float)GH; Invalidate(new Rectangle(0, (int)(MY0 * chh) - 3, ClientSize.Width, (int)((MY1 - MY0 + 1) * chh) + 6));
    }
    public Popup() {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; BackColor = Color.Black; DoubleBuffered = true;
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey)) if (k != null) {
            PanelScale = (int)k.GetValue("PanelScale", 150) / 100f; Pinned = (int)k.GetValue("PanelPinned", 0) != 0;
            if ((int)k.GetValue("PanelHasPos", 0) != 0) { hasPos = true; savedPos = new Point((int)k.GetValue("PanelX", 0), (int)k.GetValue("PanelY", 0)); }
        }
        ApplyScale();
        hide.Tick += (a, b) => { if (Pinned) { hide.Stop(); return; } if (!Bounds.Contains(Cursor.Position)) { hide.Stop(); Hide(); } };
        MouseDown += (o, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } else if (e.Button == MouseButtons.Right && !Pinned) Hide(); };
    }
    protected override void WndProc(ref Message m) { base.WndProc(ref m); if (m.Msg == 0x0232) { hasPos = true; savedPos = Location; Save(); } }
    void Save() { using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) { k.SetValue("PanelScale", (int)Math.Round(PanelScale * 100)); k.SetValue("PanelPinned", Pinned ? 1 : 0); k.SetValue("PanelHasPos", hasPos ? 1 : 0); k.SetValue("PanelX", savedPos.X); k.SetValue("PanelY", savedPos.Y); } }
    public void SetScale(float s) { PanelScale = s; ApplyScale(); Save(); }
    public void SetPinned(bool p) { Pinned = p; Save(); if (p) ShowFor(0); else { hide.Stop(); hide.Interval = 3000; hide.Start(); } }
    public void ResetPos() { hasPos = false; Save(); Place(); }
    void ApplyScale() { Size = new Size((int)(300 * PanelScale), (int)(150 * PanelScale)); if (Visible) Place(); Invalidate(); }
    void Place() {
        if (hasPos) { var r = new Rectangle(savedPos, Size); foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(r)) { Location = savedPos; return; } }
        var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8 | 0x08000000; return cp; } }
    void Set(int x, int y, Color c) { if (x >= 0 && x < GW && y >= 0 && y < GH) px[x, y] = c; }
    static int W(string s, int sc) { return s.Length * 6 * sc - sc; }
    void Txt(string s, int x, int y, int sc, Color c) {
        foreach (char ch0 in s.ToUpperInvariant()) {
            byte[] gl; if (!Font5.G.TryGetValue(ch0, out gl)) gl = Font5.G['?'];
            for (int col = 0; col < 5; col++) for (int row = 0; row < 7; row++) if (((gl[col] >> row) & 1) != 0)
                for (int dx = 0; dx < sc; dx++) for (int dy = 0; dy < sc; dy++) Set(x + col * sc + dx, y + row * sc + dy, c);
            x += 6 * sc;
        }
    }
    void TxtR(string s, int y, int sc, Color c) { Txt(s, GW - 2 - W(s, sc), y, sc, c); }
    void Row(string label, string codec, string rate, string bits, int y, Color c) {
        Txt(label, 3, y, 1, c); Txt(codec, 27, y, 1, c);
        if (string.IsNullOrEmpty(rate)) return; const int SL = 87;
        Txt(rate, SL - 6 * rate.Length, y, 1, c);
        if (!string.IsNullOrEmpty(bits)) { Txt("/", SL, y, 1, c); Txt(bits, SL + 6, y, 1, c); }
    }
    public void UpdateStatus(Status s) {
        Array.Clear(px, 0, px.Length);
        if (!s.Connected) { Txt("NO LINK", 3, 3, 2, Oled); Txt("DA-300USB", 3, 30, 1, Dim); Txt("NOT CONNECTED", 3, 40, 1, Dim); }
        else {
            Color main = s.Asio ? AsioCol : (s.Exclusive ? ExclCol : Oled);
            Txt("USB", 3, 2, 2, main);
            string st = s.Asio ? "ASIO" : s.Exclusive ? "EXCL" : s.Playing ? "> PLAY" : "IDLE";
            TxtR(st, 2, 1, main);
            if (s.Asio && s.AsioApp != null) { string a = s.AsioApp; if (a.Length > 13) a = a.Substring(0, 13); TxtR(a, 11, 1, Dim); }
            if (s.Exclusive) { if (s.Src != null && !string.IsNullOrEmpty(s.Src.App)) { string ea = s.Src.App.ToUpperInvariant(); if (ea.Length > 16) ea = ea.Substring(0, 16); Row("OUT", ea, null, null, 22, main); } }
            else if (s.AsioDsd()) Row("OUT", "DSD", SourceInfo.MHz(s.AsioDsdRate()), "1", 22, main);
            else if (s.AsioDop()) Row("OUT", "DOP", SourceInfo.MHz(2822400), "1", 22, main);
            else if (s.Asio) Row("OUT", "ASIO", s.AsioRate > 0 ? SourceInfo.RateTxt(s.AsioRate) : null, s.AsioRate > 0 ? System.Text.RegularExpressions.Regex.Match(s.AsioBits ?? "", @"\d+").Value : null, 22, main);
            else Row("OUT", "PCM", s.Rate > 0 ? SourceInfo.RateTxt(s.Rate) : null, s.ValidBits > 0 ? s.ValidBits.ToString() : null, 22, main);
            if (s.Src != null && s.Src.Complete) Row("SRC", s.Src.Codec, s.Src.RateField(), s.Src.BitsField(), 36, main);
            
            
            
            
            
            Txt("DA-300USB", 3, 56, 1, Dim);
            if (s.AsioDsd()) TxtR("NATIVE DSD", 56, 1, Dim);
            else if (s.AsioDop()) TxtR("DOP", 56, 1, Dim);
            else if (s.Src != null && s.Src.Complete && s.Src.Dsd && !s.Exclusive) TxtR("DSD>PCM", 56, 1, ExclCol);
            else if (s.Src != null && s.Src.Complete && !s.Exclusive && !s.Src.Dsd) { bool ok = s.Src.Rate == s.EffRate(); TxtR(ok ? "RATE OK" : "RESAMPLE", 56, 1, ok ? Dim : ExclCol); }
            else TxtR("V1.0.0" + (s.HidEvents > 0 ? " HID" + s.HidEvents : ""), 56, 1, Dim);
        }
        MeterOn = s.Connected && !s.OutDsd() && ((!s.Asio && !s.Exclusive) || s.ExtMeter); UseExt = s.ExtMeter; meterCol = s.Asio ? AsioCol : (s.Exclusive ? ExclCol : Oled); warnCol = Color.FromArgb(255, 140, 60); DrawMeter(); Invalidate();
    }
    SolidBrush[] B(Color c) {
        SolidBrush[] b; if (!brushes.TryGetValue(c.ToArgb(), out b)) { b = new[] { new SolidBrush(Color.FromArgb(46, c)), new SolidBrush(c) }; brushes[c.ToArgb()] = b; } return b;
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.Clear(Color.Black); float cw = ClientSize.Width / (float)GW, ch = ClientSize.Height / (float)GH;
        int x0 = Math.Max(0, (int)(e.ClipRectangle.Left / cw) - 1), x1 = Math.Min(GW - 1, (int)(e.ClipRectangle.Right / cw) + 1), y0 = Math.Max(0, (int)(e.ClipRectangle.Top / ch) - 1), y1 = Math.Min(GH - 1, (int)(e.ClipRectangle.Bottom / ch) + 1);
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) if (px[x, y].IsEmpty) g.FillRectangle(offBrush, x * cw + cw * 0.18f, y * ch + ch * 0.18f, cw * 0.64f, ch * 0.64f);
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var c = px[x, y]; if (c.IsEmpty) continue; g.FillRectangle(B(c)[0], x * cw - cw * 0.3f, y * ch - ch * 0.3f, cw * 1.6f, ch * 1.6f); }
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var c = px[x, y]; if (c.IsEmpty) continue; g.FillRectangle(B(c)[1], x * cw + cw * 0.08f, y * ch + ch * 0.08f, cw * 0.84f, ch * 0.84f); }
        using (var p = new Pen(Color.FromArgb(32, 50, 60))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
    public void ShowFor(int ms) { Place(); if (!Visible) Show(); hide.Stop(); if (!Pinned && ms > 0) { hide.Interval = ms; hide.Start(); } }
}
class App : ApplicationContext {
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    const string RegKey = @"Software\DA300Display", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    NotifyIcon ni; Popup pop = new Popup(); System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer(); System.Windows.Forms.Timer mt;
    Status s = new Status(); string popKey = null, showKey = null; DateTime lastSound = DateTime.MinValue; bool popOnChange = true, excl = false, autoMatch = false; string lastMatchKey = null; IntPtr curIcon = IntPtr.Zero; int tick = 0;
    ToolStripMenuItem miRate;
    static readonly int[] Rates = { 44100, 48000, 88200, 96000, 176400, 192000 };
    public App() {
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey)) if (k != null) { popOnChange = (int)k.GetValue("PopOnChange", 1) != 0; autoMatch = (int)k.GetValue("AutoMatch", 0) != 0; }
        
        
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("DA-300 Display 1.0.0") { Enabled = false });
        
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show", null, (a, b) => pop.ShowFor(6000));
        
        miRate = new ToolStripMenuItem("Windows output rate");
        foreach (int r in Rates) { int rr = r; miRate.DropDownItems.Add(new ToolStripMenuItem(r % 1000 == 0 ? (r / 1000) + " kHz" : (r / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " kHz", null, (a, b) => SetRate(rr)) { Tag = r }); }
        menu.Items.Add(miRate); var miAuto = new ToolStripMenuItem("Auto-match output rate to source") { Checked = autoMatch, CheckOnClick = true }; miAuto.CheckedChanged += (a, b) => { autoMatch = miAuto.Checked; lastMatchKey = null; using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("AutoMatch", autoMatch ? 1 : 0); }; menu.Items.Add(miAuto);
        var miSize = new ToolStripMenuItem("Panel size"); string[] sizeNames = { "Small", "Medium", "Large", "Extra large" }; float[] sizeVals = { 1f, 1.5f, 2f, 2.5f };
        for (int i = 0; i < sizeNames.Length; i++) { float v = sizeVals[i]; var it = new ToolStripMenuItem(sizeNames[i]) { Checked = Math.Abs(pop.PanelScale - v) < 0.01f };
            it.Click += (a, b) => { foreach (ToolStripItem x in miSize.DropDownItems) ((ToolStripMenuItem)x).Checked = false; ((ToolStripMenuItem)a).Checked = true; pop.SetScale(v); pop.ShowFor(4000); }; miSize.DropDownItems.Add(it); }
        var miPin = new ToolStripMenuItem("Always show panel") { Checked = pop.Pinned, CheckOnClick = true }; miPin.CheckedChanged += (a, b) => pop.SetPinned(miPin.Checked);
        menu.Items.Add(miSize); menu.Items.Add(miPin); menu.Items.Add("Reset panel position", null, (a, b) => { pop.ResetPos(); pop.ShowFor(4000); }); menu.Items.Add(new ToolStripSeparator());
        var miPop = new ToolStripMenuItem("Pop up on changes") { Checked = popOnChange, CheckOnClick = true };
        miPop.CheckedChanged += (a, b) => { popOnChange = miPop.Checked; using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("PopOnChange", popOnChange ? 1 : 0); };
        var miStart = new ToolStripMenuItem("Start with Windows") { Checked = IsStartup(), CheckOnClick = true }; miStart.CheckedChanged += (a, b) => SetStartup(miStart.Checked);
        menu.Items.Add(miPop); menu.Items.Add(miStart); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (a, b) => { tm.Stop(); ni.Visible = false; Application.Exit(); });
        menu.Opening += (a, b) => { foreach (ToolStripMenuItem it in miRate.DropDownItems) it.Checked = (int)it.Tag == s.Rate; miRate.Enabled = s.Connected; };
        ni = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "DA-300USB" };
        ni.MouseClick += (a, e) => {
            if (e.Button == MouseButtons.Left) { if (pop.Visible && !pop.Pinned) pop.Hide(); else pop.ShowFor(6000); }
            
        };
        HidWatch.Start(); AsioWatch.Start(); SourceWatch.Start(); LoopMeter.Start();
        mt = new System.Windows.Forms.Timer { Interval = 40 }; mt.Tick += (a, b) => { float ml, mr; bool mc; LoopMeter.Read(out ml, out mr, out mc); if (pop.UseExt) { float fl, fr; bool fc; if (FoobarMeter.Read(out fl, out fr, out fc)) { ml = fl; mr = fr; mc = fc; } else { ml = mr = 0; mc = false; } } if (pop.Visible) pop.FeedMeter(ml, mr, mc); }; mt.Start();
        tm.Interval = 250; tm.Tick += (a, b) => Tick(); tm.Start(); Tick(); if (pop.Pinned) pop.ShowFor(0);
    }
    void SetRate(int r) {
        int hr = Audio.SetRate(r);
        if (hr != 0) ni.ShowBalloonTip(4000, "DA300Display", "Couldn't switch to " + r + " Hz (0x" + hr.ToString("X8") + "). The Denon or Windows rejected that format.", ToolTipIcon.Warning);
        Tick();
    }
    static bool IsStartup() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("DA300Display") != null; }
    static void SetStartup(bool on) { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) { if (on) k.SetValue("DA300Display", "\"" + Application.ExecutablePath + "\""); else k.DeleteValue("DA300Display", false); } }
    void Tick() {
        Audio.Poll(s);
        if (s.Playing) lastSound = DateTime.Now;
        s.Playing = s.Connected && (DateTime.Now - lastSound).TotalSeconds < 1.5;
        s.HidEvents = HidWatch.Events;
        s.AsioApp = AsioWatch.App; s.Asio = s.Connected && s.AsioApp != null;
        
        if (tick % 8 == 0) excl = s.Connected && !s.Playing && !s.Asio && Audio.Exclusive();
        if (s.Playing || s.Asio) excl = false;
        s.Exclusive = excl; tick++; SourceWatch.DeviceId = Audio.DeviceId; LoopMeter.DeviceId = (s.Asio || AsioWatch.App != null) ? null : Audio.DeviceId; s.Src = s.Connected ? SourceWatch.Current : null;
        if (autoMatch && s.Connected && !s.Asio && !s.Exclusive && s.Src != null && !s.Src.Dsd && s.Src.Rate > 0) { string mk = s.Src.Key(); if (mk != lastMatchKey) { lastMatchKey = mk; if (s.Src.Rate != s.Rate && Array.IndexOf(Rates, s.Src.Rate) >= 0) SetRate(s.Src.Rate); } }
        s.AsioRate = 0; s.AsioBits = null;
        float xfl, xfr; bool xfc; bool xfresh = FoobarMeter.Read(out xfl, out xfr, out xfc);
        s.ExtMeter = xfresh && ((s.Exclusive && s.Src != null && string.Equals(s.Src.App, "foobar2000", StringComparison.OrdinalIgnoreCase)) || (s.Asio && string.Equals(s.AsioApp, "foobar2000", StringComparison.OrdinalIgnoreCase)));
        if (s.Asio && s.ExtMeter && FoobarMeter.Rate > 0) s.AsioRate = FoobarMeter.Rate;
        string sk = s.ShowKey(), pk = s.PopKey();
        if (sk != showKey) { showKey = sk; UpdateIcon(); pop.UpdateStatus(s); }
        if (pk != popKey) { bool first = popKey == null; popKey = pk; if (!first && popOnChange) pop.ShowFor(3500); }
    }
    void UpdateIcon() {
        string t; Color col;
        if (!s.Connected) { t = "--"; col = Color.Gray; }
        
        else if (s.Exclusive) { t = "EX"; col = Color.FromArgb(255, 210, 90); }
        else if (s.OutDsd()) { t = "DSD"; col = Color.FromArgb(130, 255, 170); }
        else { int r = s.EffRate(); t = r > 0 ? (r / 1000).ToString() : "?"; col = s.Asio ? Color.FromArgb(130, 255, 170) : (s.Playing ? Color.FromArgb(80, 220, 255) : Color.FromArgb(170, 170, 170)); }
        int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (var bmp = new Bitmap(sz, sz)) using (var g = Graphics.FromImage(bmp)) {
            g.Clear(Color.Transparent); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float r = sz * 0.22f;
            using (var bg = new System.Drawing.Drawing2D.GraphicsPath()) {
                bg.AddArc(0, 0, r * 2, r * 2, 180, 90); bg.AddArc(sz - 1 - r * 2, 0, r * 2, r * 2, 270, 90);
                bg.AddArc(sz - 1 - r * 2, sz - 1 - r * 2, r * 2, r * 2, 0, 90); bg.AddArc(0, sz - 1 - r * 2, r * 2, r * 2, 90, 90); bg.CloseFigure();
                using (var bb = new SolidBrush(Color.FromArgb(235, 12, 14, 18))) g.FillPath(bb, bg);
            }
            using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
                path.AddString(t, new FontFamily("Segoe UI"), (int)FontStyle.Bold, 100f, new PointF(0, 0), StringFormat.GenericTypographic);
                var b = path.GetBounds(); float pad = Math.Max(1f, sz * 0.08f), aw = sz - 2 * pad, ah = sz - 2 * pad;
                float sx = aw / b.Width, sy = ah / b.Height; if (t.Length <= 2) sx = Math.Min(sx, sy * 1.15f);
                var m = new System.Drawing.Drawing2D.Matrix(); m.Translate(pad + (aw - b.Width * sx) / 2f, pad + (ah - b.Height * sy) / 2f); m.Scale(sx, sy); m.Translate(-b.X, -b.Y);
                path.Transform(m);
                using (var br = new SolidBrush(col)) g.FillPath(br, path);
            }
            IntPtr h = bmp.GetHicon(); ni.Icon = Icon.FromHandle(h); if (curIcon != IntPtr.Zero) DestroyIcon(curIcon); curIcon = h;
        }
        string mode = s.AsioDsd() ? "DSD" : s.AsioDop() ? "DoP" : s.Asio ? "ASIO" : s.Exclusive ? "Exclusive" : "PCM";
        string tip = !s.Connected ? "DA-300USB: not connected" : string.Format("DA-300USB \u00B7 {0} {1} {2} {3}{4}", mode, s.Exclusive ? "" : s.RateStr(), s.Exclusive ? "" : s.BitsStr(), "", s.Asio ? " \u00B7 " + s.AsioApp : (s.Src != null && s.Src.Complete ? " \u00B7 src " + s.Src.Short() : (s.Playing ? " \u00B7 playing" : "")));
        ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }
}

static class Program {
    [STAThread] static void Main() {
        bool created; using (var m = new Mutex(true, "DA300Display_SingleInstance", out created)) {
            if (!created) return; Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new App());
        }
    }
}
}