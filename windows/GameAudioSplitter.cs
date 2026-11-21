// Game Audio Splitter (Windows) - plays one app's (or one device's) sound on extra output devices too.
// Audio: pure WASAPI, 32-bit float pass-through, per-output volume / balance, Windows' own resampler when rates differ.
// UI: WPF (ships with Windows), layout in MainWindow.xaml which is embedded into the .exe.
// Build: double-click build.bat - uses the C# compiler that comes with Windows. No SDK, no installs.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Game Audio Splitter")]
[assembly: AssemblyProduct("Game Audio Splitter")]
[assembly: AssemblyCompany("BlackWolf")]
[assembly: AssemblyTrademark("BlackWolf\u2122")]
[assembly: AssemblyCopyright("\u00A9 2026 BlackWolf")]
[assembly: AssemblyDescription("Plays an app's sound on extra output devices")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace GameAudioSplitter
{
    // ------------------------------------------------------------------ COM interop
    enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int frames, int flags);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public int Hr;
        public object Result;
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            int inner; object o;
            int hr = op.GetActivateResult(out inner, out o);
            Hr = hr < 0 ? hr : inner;
            Result = o;
            Done.Set();
            return 0;
        }
    }

    static class Native
    {
        public const int CLSCTX_ALL = 23;
        public const int DEVICE_STATE_ACTIVE = 1;
        public const uint LOOPBACK = 0x00020000;
        public const uint EVENTCALLBACK = 0x00040000;
        public const uint AUTOCONVERTPCM = 0x80000000;
        public const uint SRC_DEFAULT_QUALITY = 0x08000000;
        public const int BUFFERFLAGS_SILENT = 2;
        public const int S_BUFFER_EMPTY = 0x08890001;
        public static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static Guid SUBTYPE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr count);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pv);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string task, ref int taskIndex);

        [DllImport("avrt.dll")]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public const int SYNCHRONIZE = 0x00100000;
        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what, hr);
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }

        public static string Describe(Exception ex)
        {
            COMException ce = ex as COMException;
            if (ce != null)
            {
                uint hr = (uint)ce.ErrorCode;
                if (hr == 0x88890004) return ce.Message + ": device was disconnected";
                if (hr == 0x80070490) return ce.Message + ": device not found / not connected";
                if (hr == 0x8889000A) return ce.Message + ": device is in use by another app in exclusive mode";
                if (hr == 0x88890008) return ce.Message + ": audio format not supported";
                return ce.Message + string.Format(" (0x{0:X8})", hr);
            }
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ Devices
    class DeviceInfo
    {
        public string Id;
        public string Name;
        public bool Active = true;
        public string Label;   // what the UI shows (name + hints)
        public Mix Mix;        // per-output volume / balance (shared with the UI)
        public override string ToString() { return Label ?? Name; }
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
    }

    // Windows calls this when any audio device connects / disconnects - no polling needed.
    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    class DeviceWatcher : IMMNotificationClient
    {
        readonly Action changed;
        public DeviceWatcher(Action changed) { this.changed = changed; }
        public int OnDeviceStateChanged(string id, int newState) { changed(); return 0; }
        public int OnDeviceAdded(string id) { changed(); return 0; }
        public int OnDeviceRemoved(string id) { changed(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string id) { if (flow == EDataFlow.eRender) changed(); return 0; }
        public int OnPropertyValueChanged(string id, PROPERTYKEY key) { return 0; }
    }

    static class Devices
    {
        static PROPERTYKEY FriendlyNameKey = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        public static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        }

        public static string FriendlyName(IMMDevice dev)
        {
            IPropertyStore ps;
            if (dev.OpenPropertyStore(0, out ps) < 0) return "Unknown device";
            try
            {
                PROPVARIANT v;
                PROPERTYKEY k = FriendlyNameKey;
                if (ps.GetValue(ref k, out v) < 0) return "Unknown device";
                string s = v.p1 != IntPtr.Zero ? Marshal.PtrToStringUni(v.p1) : "Unknown device";
                Native.PropVariantClear(ref v);
                return s;
            }
            finally { Native.Release(ps); }
        }

        public static List<DeviceInfo> ListOutputs() { return ListOutputs(null); }

        // Connected outputs, plus any disconnected ones listed in alsoShow (e.g. your saved Bluetooth buds).
        public static List<DeviceInfo> ListOutputs(ICollection<string> alsoShow)
        {
            List<DeviceInfo> list = new List<DeviceInfo>();
            IMMDeviceEnumerator en = NewEnumerator();
            IMMDeviceCollection col;
            int mask = alsoShow != null && alsoShow.Count > 0 ? (1 | 4 | 8) : Native.DEVICE_STATE_ACTIVE;   // active | not present | unplugged
            Native.Check(en.EnumAudioEndpoints(EDataFlow.eRender, mask, out col), "List devices");
            int count; col.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IMMDevice d;
                if (col.Item(i, out d) < 0) continue;
                string id; d.GetId(out id);
                int state; d.GetState(out state);
                bool active = state == Native.DEVICE_STATE_ACTIVE;
                if (active || alsoShow.Contains(id))
                    list.Add(new DeviceInfo { Id = id, Name = FriendlyName(d), Active = active });
                Native.Release(d);
            }
            Native.Release(col);
            Native.Release(en);
            list.Sort(delegate(DeviceInfo a, DeviceInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static DeviceInfo DefaultOutput(IMMDeviceEnumerator en)
        {
            IMMDevice d;
            Native.Check(en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out d), "Find default output");
            string id; d.GetId(out id);
            DeviceInfo info = new DeviceInfo { Id = id, Name = FriendlyName(d) };
            Native.Release(d);
            return info;
        }

        public static IAudioClient Activate(IMMDeviceEnumerator en, string id)
        {
            IMMDevice d;
            Native.Check(en.GetDevice(id, out d), "Open device");
            try
            {
                int state; d.GetState(out state);
                if (state != Native.DEVICE_STATE_ACTIVE) throw new COMException("Open device", unchecked((int)0x80070490));
                object o; Guid iid = Native.IID_IAudioClient;
                Native.Check(d.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out o), "Activate device");
                return (IAudioClient)o;
            }
            finally { Native.Release(d); }
        }

        // Windows 10 2004+ : capture only one process tree, wherever it is playing.
        public static IAudioClient ActivateProcessLoopback(int pid, WaitHandle cancel)
        {
            IntPtr pParams = Marshal.AllocHGlobal(12);
            IntPtr pPv = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PROPVARIANT)));
            try
            {
                Marshal.WriteInt32(pParams, 0, 1);   // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(pParams, 4, pid);
                Marshal.WriteInt32(pParams, 8, 0);   // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                PROPVARIANT pv = new PROPVARIANT();
                pv.vt = 65;                           // VT_BLOB
                pv.p1 = (IntPtr)12;
                pv.p2 = pParams;
                Marshal.StructureToPtr(pv, pPv, false);

                ActivationHandler h = new ActivationHandler();
                IActivateAudioInterfaceAsyncOperation op;
                Guid iid = Native.IID_IAudioClient;
                Native.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pPv, h, out op);
                int w = WaitHandle.WaitAny(new WaitHandle[] { h.Done, cancel }, 5000);
                if (w == 1) throw new OperationCanceledException();
                if (w == WaitHandle.WaitTimeout) throw new Exception("Timed out connecting to the app's audio");
                GC.KeepAlive(op);
                Native.Check(h.Hr, "Capture app audio");
                return (IAudioClient)h.Result;
            }
            finally
            {
                Marshal.FreeHGlobal(pPv);
                Marshal.FreeHGlobal(pParams);
            }
        }
    }

    // ------------------------------------------------------------------ Device volume (Windows' own per-device volume / balance / mute)
    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(int ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(int ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out int step, out int count);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out int mask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float stepDb);
    }

    // One device's Windows volume. The app never keeps its own copy of the level: it reads and writes
    // the same value as the Windows volume mixer, so the two can never disagree. Windows calls OnNotify
    // when anything else (keyboard keys, Bluetooth buds, the mixer) changes it - no polling.
    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    class EndpointVolume : IAudioEndpointVolumeCallback, IDisposable
    {
        static Guid Self = Guid.NewGuid();       // tags our own changes so we can ignore their echo
        IAudioEndpointVolume ep;
        readonly Action changed;
        public int Channels;

        EndpointVolume(Action changed) { this.changed = changed; }

        public static EndpointVolume Open(string deviceId, Action changed)
        {
            IMMDeviceEnumerator en = Devices.NewEnumerator();
            IMMDevice d = null;
            try
            {
                if (en.GetDevice(deviceId, out d) < 0) return null;
                int state; d.GetState(out state);
                if (state != Native.DEVICE_STATE_ACTIVE) return null;
                object o; Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (d.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out o) < 0) return null;
                EndpointVolume v = new EndpointVolume(changed);
                v.ep = (IAudioEndpointVolume)o;
                v.ep.GetChannelCount(out v.Channels);
                v.ep.RegisterControlChangeNotify(v);
                return v;
            }
            catch { return null; }
            finally { Native.Release(d); Native.Release(en); }
        }

        public int OnNotify(IntPtr data)
        {
            Guid ctx = (Guid)Marshal.PtrToStructure(data, typeof(Guid));
            if (ctx != Self) changed();
            return 0;
        }

        public float Level { get { float v = 0; if (ep != null) ep.GetMasterVolumeLevelScalar(out v); return v; } }       // 0..1, same number Windows shows
        public float LevelDb { get { float v = -96; if (ep != null) ep.GetMasterVolumeLevel(out v); return v; } }
        public bool Muted { get { bool m = false; if (ep != null) ep.GetMute(out m); return m; } }
        public void SetLevel(float v) { Guid g = Self; if (ep != null) ep.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, v)), ref g); }
        public void SetMuted(bool m) { Guid g = Self; if (ep != null) ep.SetMute(m, ref g); }

        float ChannelDb(int ch) { float v = 0; ep.GetChannelVolumeLevel(ch, out v); return v; }

        // -1 = left only .. +1 = right only, derived from Windows' own left / right levels.
        public float Balance
        {
            get
            {
                if (ep == null || Channels < 2) return 0f;
                float l = ChannelDb(0), r = ChannelDb(1);
                if (Math.Abs(l - r) < 0.05f) return 0f;
                double ratio = Math.Pow(10, -Math.Abs(l - r) / 20.0);   // quieter / louder, as amplitude
                return l < r ? (float)(1 - ratio) : (float)(ratio - 1);
            }
        }

        public void SetBalance(float b)
        {
            if (ep == null || Channels < 2) return;
            float l = ChannelDb(0), r = ChannelDb(1), top = Math.Max(l, r);
            float min, max, step;
            ep.GetVolumeRange(out min, out max, out step);
            float a = Math.Abs(b);
            float quiet = a >= 0.999f ? min : Math.Max(min, top + (float)(20 * Math.Log10(1 - a)));
            Guid g = Self;
            ep.SetChannelVolumeLevel(0, b > 0 ? quiet : top, ref g);
            ep.SetChannelVolumeLevel(1, b < 0 ? quiet : top, ref g);
        }

        // Actual left / right loudness as linear gain (for the level meters).
        public void Amps(out float l, out float r)
        {
            l = r = 0f;
            if (ep == null || Muted) return;
            if (Channels < 2) { l = r = (float)Math.Pow(10, LevelDb / 20.0); return; }
            l = (float)Math.Pow(10, ChannelDb(0) / 20.0);
            r = (float)Math.Pow(10, ChannelDb(1) / 20.0);
        }

        public void Dispose()
        {
            if (ep == null) return;
            try { ep.UnregisterControlChangeNotify(this); } catch { }
            Native.Release(ep);
            ep = null;
        }
    }

    // ------------------------------------------------------------------ Audio format
    class Fmt
    {
        public IntPtr Ptr;    // CoTaskMem
        public int Channels, Rate, Block, Bits;
        public bool IsFloat;

        public static Fmt Parse(IntPtr p)
        {
            Fmt f = new Fmt();
            f.Ptr = p;
            int tag = Marshal.ReadInt16(p, 0) & 0xFFFF;
            f.Channels = Marshal.ReadInt16(p, 2);
            f.Rate = Marshal.ReadInt32(p, 4);
            f.Block = Marshal.ReadInt16(p, 12);
            f.Bits = Marshal.ReadInt16(p, 14);
            int cb = Marshal.ReadInt16(p, 16);
            f.IsFloat = tag == 3;
            if (tag == 0xFFFE && cb >= 22)
            {
                byte[] g = new byte[16];
                Marshal.Copy(p + 24, g, 0, 16);
                f.IsFloat = new Guid(g) == Native.SUBTYPE_FLOAT;
            }
            return f;
        }

        public static Fmt Make(bool isFloat, int rate, int channels)
        {
            int bits = isFloat ? 32 : 16;
            int block = channels * bits / 8;
            IntPtr p = Marshal.AllocCoTaskMem(18);
            Marshal.WriteInt16(p, 0, (short)(isFloat ? 3 : 1));
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * block);
            Marshal.WriteInt16(p, 12, (short)block);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 0);
            return Parse(p);
        }

        public void Free()
        {
            if (Ptr != IntPtr.Zero) { Marshal.FreeCoTaskMem(Ptr); Ptr = IntPtr.Zero; }
        }

        public override string ToString()
        {
            return string.Format("{0} Hz, {1} ch, {2}", Rate, Channels, IsFloat ? "32-bit float" : Bits + "-bit");
        }
    }

    // ------------------------------------------------------------------ Per-output mix
    // Shared by the UI (writes) and the audio thread (reads). Plain volatile fields, no locks near the audio path.
    class Mix
    {
        public volatile float Volume = 1f;     // 0..1
        public volatile float Balance = 0f;    // -1 = left only .. +1 = right only
        public volatile bool Muted;
        public volatile string State = "";     // written by the engine, shown under the device name

        public void Gains(out float l, out float r)
        {
            float v = Muted ? 0f : Volume, b = Balance;
            l = v * Math.Min(1f, 1f - b);
            r = v * Math.Min(1f, 1f + b);
        }
    }

    // ------------------------------------------------------------------ Engine
    class Target
    {
        public string Id, Name;
        public Mix Mix;
        public IAudioClient Client;
        public IAudioRenderClient Render;
        public int BufferFrames;
        public bool Ok, Skip;
        public DateTime RetryAt;
        public int Dropouts;
        public float CurL, CurR;    // gains applied to the last packet; ramped towards Mix to avoid clicks
    }

    class Settings
    {
        public bool AppMode = true;
        public string ProcessName = "";
        public string ProcessTitle = "";
        public string SourceDeviceId = "";          // "" = default output
        public List<string> TargetIds = new List<string>();
        public HashSet<string> FollowIds = new HashSet<string>();   // outputs that follow the main volume
        public int BufferMs = 40;
        public bool AutoStart = false;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameAudioSplitter", "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "AppMode") s.AppMode = v == "1";
                    else if (k == "ProcessName") s.ProcessName = v;
                    else if (k == "ProcessTitle") s.ProcessTitle = v;
                    else if (k == "SourceDeviceId") s.SourceDeviceId = v;
                    else if (k == "Targets") s.TargetIds = new List<string>(v.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                    else if (k == "Follow") s.FollowIds = new HashSet<string>(v.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                    else if (k == "BufferMs") int.TryParse(v, out s.BufferMs);
                    else if (k == "AutoStart") s.AutoStart = v == "1";
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                List<string> lines = new List<string> {
                    "AppMode=" + (AppMode ? "1" : "0"),
                    "ProcessName=" + ProcessName,
                    "ProcessTitle=" + ProcessTitle,
                    "SourceDeviceId=" + SourceDeviceId,
                    "Targets=" + string.Join("|", TargetIds.ToArray()),
                    "Follow=" + string.Join("|", FollowIds.ToArray()),
                    "BufferMs=" + BufferMs,
                    "AutoStart=" + (AutoStart ? "1" : "0"),
                };
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }

    }

    class Engine
    {
        public const int Stopped = 0, Starting = 1, Waiting = 2, Live = 3, Problem = 4;
        public volatile int Phase = Stopped;
        public volatile string Detail = "";
        public volatile bool Sound;
        public volatile float PeakL, PeakR;     // source level, for the UI meters
        public volatile int BufferMs = 40;
        public long Packets;

        public string Status
        {
            get
            {
                string[] names = { "Stopped", "Starting", "Waiting", "Live", "Problem" };
                return names[Phase] + (Detail.Length > 0 ? ": " + Detail : "");
            }
        }

        volatile bool stop;
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        Thread thread;
        bool appMode;
        string processName, appTitle, sourceDeviceId;
        List<DeviceInfo> targetDevices;

        Fmt fmt;
        byte[] sides;           // per channel: 0 = left, 1 = right, 2 = both (mono / centre / LFE)
        IMMDeviceEnumerator en;
        List<Target> targets = new List<Target>();

        public bool Running { get { return thread != null && thread.IsAlive; } }

        public void Start(bool appMode, string processName, string appTitle, string sourceDeviceId, List<DeviceInfo> targetDevices)
        {
            if (Running) Stop();
            this.appMode = appMode;
            this.processName = processName;
            this.appTitle = string.IsNullOrEmpty(appTitle) ? processName : appTitle;
            this.sourceDeviceId = sourceDeviceId;
            this.targetDevices = targetDevices;
            stop = false;
            stopEvent.Reset();
            Phase = Starting;
            Detail = "";
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            // Every wait in the audio thread also watches stopEvent, so it exits within milliseconds.
            // If a driver ever hangs, the thread is a background thread and cannot keep the app alive.
            stop = true;
            stopEvent.Set();
            if (thread != null) thread.Join(3000);
            thread = null;
            Phase = Stopped;
            Detail = "";
            PeakL = PeakR = 0;
        }

        void Run()
        {
            int taskIndex = 0;
            IntPtr mmcss = IntPtr.Zero;
            try { mmcss = Native.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex); } catch { }
            while (!stop)
            {
                try { RunOnce(); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!stop) { Phase = Problem; Detail = Native.Describe(ex) + ". Retrying automatically."; }
                }
                stopEvent.WaitOne(2000);   // sleeps, but wakes instantly on Stop
            }
            if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
        }

        static int FindProcess(string name)
        {
            Process[] ps = Process.GetProcessesByName(name);
            int pid = 0;
            foreach (Process p in ps)
            {
                bool hasWindow = false;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                if (pid == 0 || hasWindow) pid = p.Id;
                if (hasWindow) break;
            }
            foreach (Process p in ps) p.Dispose();
            return pid;
        }

        static bool ProcessAlive(string name, int pid)
        {
            Process[] ps = Process.GetProcessesByName(name);
            bool alive = false;
            foreach (Process p in ps) { if (p.Id == pid) alive = true; p.Dispose(); }
            return alive;
        }

        // Cheap exit check: one kernel call on a handle instead of listing every process.
        static bool HandleAlive(IntPtr h)
        {
            return Native.WaitForSingleObject(h, 0) != 0;   // 0 = WAIT_OBJECT_0 = process ended
        }

        public static string Short(Exception ex)
        {
            COMException ce = ex as COMException;
            if (ce == null) return ex.Message;
            uint hr = (uint)ce.ErrorCode;
            if (hr == 0x88890004 || hr == 0x80070490) return "Not connected";
            if (hr == 0x8889000A) return "Busy: another app has exclusive use";
            if (hr == 0x88890008) return "Audio format not supported";
            return string.Format("Error 0x{0:X8}", hr);
        }

        string FormatText()
        {
            string ch = fmt.Channels == 2 ? "stereo" : fmt.Channels == 1 ? "mono" : fmt.Channels + " ch";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#} kHz · {1} · {2}", fmt.Rate / 1000.0, ch, fmt.IsFloat ? "32-bit float" : fmt.Bits + "-bit");
        }

        void MakeSides()
        {
            int ch = fmt.Channels;
            sides = new byte[ch];
            for (int c = 0; c < ch; c++)
            {
                bool centre = ch == 1 || ((ch == 6 || ch == 8) && (c == 2 || c == 3));   // 5.1 / 7.1: FC + LFE
                sides[c] = (byte)(centre ? 2 : c % 2);
            }
        }

        void RunOnce()
        {
            en = Devices.NewEnumerator();
            IAudioClient capClient = null;
            IAudioCaptureClient cap = null;
            AutoResetEvent ev = new AutoResetEvent(false);
            string sourceName;
            string skipId = null;
            int pid = 0;
            IntPtr hProc = IntPtr.Zero;
            WaitHandle[] waits = { ev, stopEvent };
            try
            {
                // ---- open the source
                if (appMode)
                {
                    pid = FindProcess(processName);
                    if (pid == 0) { Phase = Waiting; Detail = "Waiting for " + appTitle + " to start. Leave this open and launch it."; return; }
                    hProc = Native.OpenProcess(Native.SYNCHRONIZE, false, pid);
                    sourceName = appTitle;
                    capClient = OpenProcessCapture(pid);
                }
                else
                {
                    DeviceInfo src = string.IsNullOrEmpty(sourceDeviceId) ? Devices.DefaultOutput(en) : null;
                    string id = src != null ? src.Id : sourceDeviceId;
                    sourceName = src != null ? src.Name : NameOf(id);
                    skipId = id;
                    capClient = Devices.Activate(en, id);
                    IntPtr mix;
                    Native.Check(capClient.GetMixFormat(out mix), "Read device format");
                    fmt = Fmt.Parse(mix);
                    Native.Check(capClient.Initialize(0, Native.LOOPBACK | Native.EVENTCALLBACK, 1000000, 0, fmt.Ptr, IntPtr.Zero), "Capture device audio");
                }
                MakeSides();
                Native.Check(capClient.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "Set capture event");
                object o; Guid g = Native.IID_IAudioCaptureClient;
                Native.Check(capClient.GetService(ref g, out o), "Get capture service");
                cap = (IAudioCaptureClient)o;

                // ---- open the outputs
                targets.Clear();
                foreach (DeviceInfo d in targetDevices)
                {
                    Target t = new Target { Id = d.Id, Name = d.Name, Mix = d.Mix ?? new Mix() };
                    targets.Add(t);
                    if (d.Id == skipId) { t.Skip = true; t.Mix.State = "Skipped: this is the source"; continue; }
                    TryOpen(t);
                }

                Native.Check(capClient.Start(), "Start capture");
                string formatText = FormatText();
                Phase = Live;
                Detail = sourceName + " · " + formatText;

                // ---- pump
                DateTime nextHouse = DateTime.UtcNow;
                DateTime nextProcScan = DateTime.UtcNow.AddSeconds(3);
                DateTime lastSound = DateTime.MinValue, lastPacket = DateTime.UtcNow;
                while (!stop)
                {
                    // Sleeps until Windows signals new audio (~every 10 ms while sound plays).
                    // With no sound it only wakes 4x/second for housekeeping. No busy loop.
                    if (WaitHandle.WaitAny(waits, 250) == 1) break;
                    while (true)
                    {
                        int next;
                        int hr = cap.GetNextPacketSize(out next);
                        Native.Check(hr, "Read audio");
                        if (next == 0) break;
                        IntPtr data; int frames, flags; long devPos, qpc;
                        hr = cap.GetBuffer(out data, out frames, out flags, out devPos, out qpc);
                        Native.Check(hr, "Read audio");
                        if (hr == Native.S_BUFFER_EMPTY) break;
                        bool silent = (flags & Native.BUFFERFLAGS_SILENT) != 0;
                        if (silent) { PeakL *= 0.8f; PeakR *= 0.8f; }
                        else if (ScanPeaks(data, frames)) lastSound = DateTime.UtcNow;
                        foreach (Target t in targets) Feed(t, data, frames, silent);
                        cap.ReleaseBuffer(frames);
                        Packets++;
                        lastPacket = DateTime.UtcNow;
                    }

                    DateTime now = DateTime.UtcNow;
                    if ((now - lastPacket).TotalMilliseconds > 300) { PeakL = 0; PeakR = 0; }
                    if (now >= nextHouse)
                    {
                        nextHouse = now.AddSeconds(1);
                        bool gone = false;
                        if (appMode && hProc != IntPtr.Zero) gone = !HandleAlive(hProc);
                        else if (appMode && now >= nextProcScan)
                        {
                            nextProcScan = now.AddSeconds(3);
                            gone = !ProcessAlive(processName, pid);
                        }
                        if (gone)
                        {
                            Phase = Waiting;
                            Detail = appTitle + " closed. Waiting for it to start again.";
                            return;
                        }
                        foreach (Target t in targets)
                        {
                            if (!t.Ok && !t.Skip && now >= t.RetryAt) TryOpen(t);
                            if (t.Ok) t.Mix.State = t.Dropouts > 1 ? "Playing · " + (t.Dropouts - 1) + " dropouts" : "Playing";
                        }
                        Sound = (now - lastSound).TotalSeconds < 1.5;
                    }
                }
            }
            finally
            {
                if (capClient != null) capClient.Stop();
                foreach (Target t in targets) { Close(t); t.Mix.State = ""; }
                Native.Release(cap);
                Native.Release(capClient);
                Native.Release(en);
                if (fmt != null) { fmt.Free(); fmt = null; }
                if (hProc != IntPtr.Zero) Native.CloseHandle(hProc);
                ev.Close();
                PeakL = PeakR = 0;
                Sound = false;
            }
        }

        IAudioClient OpenProcessCapture(int pid)
        {
            // float first (lossless pass-through), 16-bit as a fallback
            bool[] floats = { true, true, false };
            long[] durations = { 1000000, 200000, 200000 };
            Exception last = null;
            for (int i = 0; i < floats.Length; i++)
            {
                IAudioClient c = null;
                Fmt f = Fmt.Make(floats[i], 48000, 2);
                try
                {
                    c = Devices.ActivateProcessLoopback(pid, stopEvent);
                    Native.Check(c.Initialize(0, Native.LOOPBACK | Native.EVENTCALLBACK | Native.AUTOCONVERTPCM, durations[i], 0, f.Ptr, IntPtr.Zero), "Capture app audio");
                    fmt = f;
                    return c;
                }
                catch (OperationCanceledException) { Native.Release(c); f.Free(); throw; }
                catch (Exception ex) { last = ex; Native.Release(c); f.Free(); }
            }
            throw last;
        }

        string NameOf(string id)
        {
            foreach (DeviceInfo d in targetDevices) if (d.Id == id) return d.Name;
            try
            {
                IMMDevice dev;
                if (en.GetDevice(id, out dev) >= 0) { string n = Devices.FriendlyName(dev); Native.Release(dev); return n; }
            }
            catch { }
            return "selected device";
        }

        // Peak of the first two channels, every 2nd frame (plenty for a meter). Returns true if not silent.
        unsafe bool ScanPeaks(IntPtr data, int frames)
        {
            int ch = fmt.Channels, rOff = ch > 1 ? 1 : 0;
            float pl = 0, pr = 0;
            if (fmt.IsFloat && fmt.Bits == 32)
            {
                float* s = (float*)data;
                for (int f = 0; f < frames; f += 2)
                {
                    float a = s[f * ch], b = s[f * ch + rOff];
                    if (a < 0) a = -a; if (b < 0) b = -b;
                    if (a > pl) pl = a; if (b > pr) pr = b;
                }
            }
            else if (fmt.Bits == 16)
            {
                short* s = (short*)data;
                for (int f = 0; f < frames; f += 2)
                {
                    int a = s[f * ch], b = s[f * ch + rOff];
                    if (a < 0) a = -a; if (b < 0) b = -b;
                    if (a > pl) pl = a; if (b > pr) pr = b;
                }
                pl /= 32768f; pr /= 32768f;
            }
            else { PeakL = PeakR = 0; return true; }
            float dl = PeakL * 0.85f, dr = PeakR * 0.85f;
            PeakL = pl > dl ? pl : dl;
            PeakR = pr > dr ? pr : dr;
            return pl > 0.0005f || pr > 0.0005f;
        }

        void TryOpen(Target t)
        {
            Close(t);
            IAudioClient c = null;
            try
            {
                t.Mix.State = "Connecting";
                c = Devices.Activate(en, t.Id);
                // Same format as the source -> no conversion. If the device runs at a different
                // rate/channel count, Windows' built-in high-quality resampler handles it.
                Native.Check(c.Initialize(0, Native.AUTOCONVERTPCM | Native.SRC_DEFAULT_QUALITY, 2000000, 0, fmt.Ptr, IntPtr.Zero), "Open output");
                int frames; Native.Check(c.GetBufferSize(out frames), "Open output");
                object o; Guid g = Native.IID_IAudioRenderClient;
                Native.Check(c.GetService(ref g, out o), "Open output");
                t.Client = c;
                t.Render = (IAudioRenderClient)o;
                t.BufferFrames = frames;
                t.Ok = true;
                t.Mix.Gains(out t.CurL, out t.CurR);
                t.Mix.State = "Playing";
                WriteSilence(t, TargetLevel());
                Native.Check(c.Start(), "Start output");
            }
            catch (Exception ex)
            {
                if (t.Client == null) Native.Release(c);
                Close(t);
                t.Mix.State = Short(ex);
                t.RetryAt = DateTime.UtcNow.AddSeconds(2);
            }
        }

        void Close(Target t)
        {
            if (t.Client != null) { try { t.Client.Stop(); } catch { } }
            Native.Release(t.Render);
            Native.Release(t.Client);
            t.Render = null;
            t.Client = null;
            t.Ok = false;
        }

        void Fail(Target t, int hr)
        {
            Close(t);
            t.Mix.State = "Reconnecting (" + Short(new COMException("", hr)) + ")";
            t.RetryAt = DateTime.UtcNow.AddSeconds(2);
        }

        int TargetLevel()
        {
            return Math.Max(fmt.Rate * BufferMs / 1000, fmt.Rate / 100);
        }

        bool WriteSilence(Target t, int frames)
        {
            frames = Math.Min(frames, t.BufferFrames);
            if (frames <= 0) return true;
            IntPtr dst;
            int hr = t.Render.GetBuffer(frames, out dst);
            if (hr < 0) { Fail(t, hr); return false; }
            hr = t.Render.ReleaseBuffer(frames, Native.BUFFERFLAGS_SILENT);
            if (hr < 0) { Fail(t, hr); return false; }
            return true;
        }

        // Keeps each output's buffer near the chosen level. Clock drift between devices is
        // corrected one sample-frame at a time (inaudible) instead of dropping chunks.
        void Feed(Target t, IntPtr src, int frames, bool silent)
        {
            if (!t.Ok) return;
            int pad;
            int hr = t.Client.GetCurrentPadding(out pad);
            if (hr < 0) { Fail(t, hr); return; }

            int level = TargetLevel();
            int n = frames, extra = 0;
            if (pad == 0)
            {
                if (!WriteSilence(t, level - frames)) return;
                pad = Math.Max(level - frames, 0);
                t.Dropouts++;
            }
            else if (pad > level + level / 2 && frames > 1) n = frames - 1;
            else if (pad < level / 4) extra = 1;

            int free = t.BufferFrames - pad;
            if (n + extra > free) { n = free; extra = 0; }
            if (n <= 0) return;

            float gl, gr;
            t.Mix.Gains(out gl, out gr);
            // Muted (and already faded out) = hand Windows a "silent" buffer: no copy, no maths.
            bool quiet = silent || (gl == 0f && gr == 0f && t.CurL == 0f && t.CurR == 0f);

            IntPtr dst;
            hr = t.Render.GetBuffer(n + extra, out dst);
            if (hr < 0) { Fail(t, hr); return; }
            if (!quiet)
            {
                CopyGain(t, dst, src, n, gl, gr);
                if (extra == 1) Native.CopyMemory(dst + n * fmt.Block, dst + (n - 1) * fmt.Block, (IntPtr)fmt.Block);
            }
            else { t.CurL = gl; t.CurR = gr; }
            hr = t.Render.ReleaseBuffer(n + extra, quiet ? Native.BUFFERFLAGS_SILENT : 0);
            if (hr < 0) Fail(t, hr);
        }

        // Copies one packet applying this output's left/right gain. At 100% / centre it is a
        // bit-exact memcpy. When the gain changes it ramps across the packet (~10 ms) so slider
        // moves never click. Cost: one multiply per sample, a few thousand per packet.
        unsafe void CopyGain(Target t, IntPtr dst, IntPtr src, int frames, float gl, float gr)
        {
            float l = t.CurL, r = t.CurR;
            t.CurL = gl; t.CurR = gr;
            bool ramp = l != gl || r != gr;
            if (!ramp && gl == 1f && gr == 1f)
            {
                Native.CopyMemory(dst, src, (IntPtr)(frames * fmt.Block));
                return;
            }
            float dl = ramp ? (gl - l) / frames : 0f, dr = ramp ? (gr - r) / frames : 0f;
            if (!ramp) { l = gl; r = gr; }
            int ch = fmt.Channels;

            if (fmt.IsFloat && fmt.Bits == 32)
            {
                float* s = (float*)src, d = (float*)dst;
                if (ch == 2)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        l += dl; r += dr;
                        d[0] = s[0] * l; d[1] = s[1] * r;
                        s += 2; d += 2;
                    }
                }
                else
                {
                    for (int f = 0; f < frames; f++)
                    {
                        l += dl; r += dr;
                        float c = (l + r) * 0.5f;
                        for (int i = 0; i < ch; i++)
                        {
                            byte side = sides[i];
                            d[i] = s[i] * (side == 0 ? l : side == 1 ? r : c);
                        }
                        s += ch; d += ch;
                    }
                }
            }
            else if (fmt.Bits == 16)
            {
                short* s = (short*)src, d = (short*)dst;
                for (int f = 0; f < frames; f++)
                {
                    l += dl; r += dr;
                    float c = (l + r) * 0.5f;
                    for (int i = 0; i < ch; i++)
                    {
                        byte side = sides[i];
                        float v = s[i] * (side == 0 ? l : side == 1 ? r : c);
                        d[i] = (short)(v > 32767f ? 32767f : v < -32768f ? -32768f : v);
                    }
                    s += ch; d += ch;
                }
            }
            else Native.CopyMemory(dst, src, (IntPtr)(frames * fmt.Block));   // unusual format: pass through untouched
        }

        // ---- headless check used by --selftest (makes no sound)
        public static string SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            Action<string> log = delegate(string s) { sb.AppendLine(s); };
            try
            {
                List<DeviceInfo> outs = Devices.ListOutputs();
                log("Outputs: " + outs.Count);
                foreach (DeviceInfo d in outs) log("  " + d.Name);
                IMMDeviceEnumerator en = Devices.NewEnumerator();
                DeviceInfo def = Devices.DefaultOutput(en);
                log("Default: " + def.Name);

                IAudioClient lc = Devices.Activate(en, def.Id);
                IntPtr mix; Native.Check(lc.GetMixFormat(out mix), "mix");
                Fmt f = Fmt.Parse(mix);
                log("Mix format: " + f);
                Native.Check(lc.Initialize(0, Native.LOOPBACK | Native.EVENTCALLBACK, 1000000, 0, f.Ptr, IntPtr.Zero), "loopback init");
                log("Device loopback: OK");

                foreach (DeviceInfo d in outs)
                {
                    try
                    {
                        IAudioClient rc = Devices.Activate(en, d.Id);
                        Native.Check(rc.Initialize(0, Native.AUTOCONVERTPCM | Native.SRC_DEFAULT_QUALITY, 2000000, 0, f.Ptr, IntPtr.Zero), "render init");
                        int bf; rc.GetBufferSize(out bf);
                        log("  Render open OK: " + d.Name + " (" + bf + " frames)");
                        Native.Release(rc);
                    }
                    catch (Exception ex) { log("  Render FAIL: " + d.Name + ": " + Native.Describe(ex)); }
                }
                Native.Release(lc);
                f.Free();

                int pid = Process.GetCurrentProcess().Id;
                Engine e = new Engine();
                IAudioClient pc = e.OpenProcessCapture(pid);
                log("Process loopback: OK [" + e.fmt + "]");
                Native.Release(pc);
                e.fmt.Free();
                Native.Release(en);
            }
            catch (Exception ex) { log("FAILED: " + Native.Describe(ex)); }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ UI: Win32 helpers
    static class Win
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        public static int Build
        {
            get
            {
                try { int b; return int.TryParse(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0") as string, out b) ? b : 0; }
                catch { return 0; }
            }
        }

        public static string ImagePath(int pid)
        {
            IntPtr h = Native.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION: works for most games too
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { Native.CloseHandle(h); }
        }

        // Hands unused memory back to Windows while minimised (it comes back on its own when needed).
        public static void TrimMemory()
        {
            try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }
    }

    // ------------------------------------------------------------------ UI: theme (Fluent tokens, follows Windows light/dark + accent)
    class Theme
    {
        public bool Dark = true;
        public readonly Dictionary<string, string> T = new Dictionary<string, string>();

        static string Hex(Color c, byte a) { return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", a, c.R, c.G, c.B); }

        public static Theme Detect()
        {
            Theme th = new Theme();
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
                th.Dark = !(v is int) || (int)v == 0;
            }
            catch { }
            // Windows stores 8 shades of the user's accent colour; Fluent uses "light 2" on dark, "dark 1" on light.
            Color accent = th.Dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
            try
            {
                byte[] p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
                if (p != null && p.Length >= 32) { int i = (th.Dark ? 1 : 4) * 4; accent = Color.FromRgb(p[i], p[i + 1], p[i + 2]); }
            }
            catch { }

            Dictionary<string, string> t = th.T;
            t["Accent"] = Hex(accent, 0xFF);
            t["AccentHover"] = Hex(accent, 0xE0);
            if (th.Dark)
            {
                t["OnAccent"] = "#FF000000"; t["Text1"] = "#FFFFFFFF"; t["Text2"] = "#C8FFFFFF"; t["Text3"] = "#8CFFFFFF";
                t["Card"] = "#0DFFFFFF"; t["CardStroke"] = "#14FFFFFF"; t["Ctrl"] = "#0FFFFFFF"; t["CtrlHover"] = "#17FFFFFF";
                t["CtrlPressed"] = "#08FFFFFF"; t["CtrlStroke"] = "#12FFFFFF"; t["SegOn"] = "#1CFFFFFF"; t["Track"] = "#40FFFFFF";
                t["MeterBg"] = "#14FFFFFF"; t["ThumbOuter"] = "#FF454545"; t["Flyout"] = "#FF2C2C2C"; t["SwitchOff"] = "#C8FFFFFF";
                t["Warn"] = "#FFFCE100"; t["Bad"] = "#FFFF99A4"; t["Good"] = "#FF6CCB5F"; t["SolidBg"] = "#FF202020";
            }
            else
            {
                t["OnAccent"] = "#FFFFFFFF"; t["Text1"] = "#E4000000"; t["Text2"] = "#9E000000"; t["Text3"] = "#72000000";
                t["Card"] = "#B3FFFFFF"; t["CardStroke"] = "#0F000000"; t["Ctrl"] = "#B3FFFFFF"; t["CtrlHover"] = "#80F9F9F9";
                t["CtrlPressed"] = "#4DF9F9F9"; t["CtrlStroke"] = "#14000000"; t["SegOn"] = "#FFFFFFFF"; t["Track"] = "#40000000";
                t["MeterBg"] = "#0F000000"; t["ThumbOuter"] = "#FFFFFFFF"; t["Flyout"] = "#FFF9F9F9"; t["SwitchOff"] = "#9E000000";
                t["Warn"] = "#FF9D5D00"; t["Bad"] = "#FFC42B1C"; t["Good"] = "#FF0F7B0F"; t["SolidBg"] = "#FFF3F3F3";
            }
            return th;
        }

        public string Apply(string xaml)
        {
            foreach (KeyValuePair<string, string> kv in T) xaml = xaml.Replace("%" + kv.Key + "%", kv.Value);
            return xaml;
        }
    }

    // ------------------------------------------------------------------ UI: view models
    class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    class AppItem
    {
        public string Name { get; set; }        // process name, e.g. VALORANT-Win64-Shipping
        public string Title { get; set; }       // friendly name from the exe
        public ImageSource Icon { get; set; }
        public bool IsRunning { get; set; }
        public string Note { get { return IsRunning ? "" : "not running"; } }
    }

    class SourceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Glyph { get; set; }
    }

    class OutputVM : Observable
    {
        readonly MainVM owner;
        public readonly Mix Mix = new Mix();
        public OutputVM(MainVM owner, string id) { this.owner = owner; Id = id; }

        public string Id { get; private set; }

        string name = "";
        public string Name { get { return name; } set { if (Set(ref name, value)) Raise("Glyph"); } }
        public string Glyph { get { return MainVM.GlyphFor(name); } }

        bool connected = true;
        public bool Connected { get { return connected; } set { if (Set(ref connected, value)) { Raise("CanEnable"); Raise("Subtitle"); Attach(); } } }

        bool isMain;
        public bool IsMain { get { return isMain; } set { if (Set(ref isMain, value)) { RefreshDerived(); Raise("ShowFollow"); ApplyFollow(); } } }

        bool enabled;
        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (!Set(ref enabled, value)) return;
                if (!value) { MeterL = 0; MeterR = 0; }
                RefreshDerived();
                owner.OutputsChanged();
            }
        }
        public bool CanEnable { get { return connected || enabled; } }

        // ---- volume / balance / mute = this device's own Windows settings (two-way sync, never a second copy)
        EndpointVolume ep;
        bool syncing;
        float ampL = 1f, ampR = 1f;

        public void Attach()
        {
            Detach();
            if (connected) ep = EndpointVolume.Open(Id, delegate { owner.Post(SyncFromWindows); });
            SyncFromWindows();
            Raise("HasVolume");
        }

        public void Detach()
        {
            if (ep != null) { ep.Dispose(); ep = null; Raise("HasVolume"); }
        }

        public bool HasVolume { get { return ep != null; } }

        public void SyncFromWindows()
        {
            if (ep == null) return;
            syncing = true;
            try
            {
                Volume = Math.Round(ep.Level * 100);
                Muted = ep.Muted;
                Balance = Math.Round(ep.Balance * 100);
                ep.Amps(out ampL, out ampR);
            }
            finally { syncing = false; }
        }

        double volume = 100;
        public double Volume
        {
            get { return volume; }
            set
            {
                value = Math.Round(Math.Max(0, Math.Min(100, value)));
                if (!Set(ref volume, value)) return;
                Raise("VolumeText");
                if (!syncing && ep != null) { ep.SetLevel((float)(value / 100)); ep.Amps(out ampL, out ampR); }
            }
        }
        public string VolumeText { get { return muted ? "Muted" : volume.ToString("0") + "%"; } }

        double balance;
        public double Balance
        {
            get { return balance; }
            set
            {
                double v = Math.Round(Math.Max(-100, Math.Min(100, value)));
                if (!syncing && Math.Abs(v) <= 3) v = 0;   // gentle centre detent
                if (Set(ref balance, v))
                {
                    Raise("BalanceText");
                    if (!syncing && ep != null) { ep.SetBalance((float)(v / 100)); ep.Amps(out ampL, out ampR); }
                }
                if (v != value) owner.Post(delegate { Raise("Balance"); });   // snap the knob to the detent
            }
        }
        public string BalanceText
        {
            get { return balance == 0 ? "Centre" : balance < 0 ? "L " + (-balance).ToString("0") : "R " + balance.ToString("0"); }
        }

        bool muted;
        public bool Muted
        {
            get { return muted; }
            set
            {
                if (!Set(ref muted, value)) return;
                Raise("VolumeText");
                if (!syncing && ep != null) { ep.SetMuted(value); ep.Amps(out ampL, out ampR); }
            }
        }

        // ---- optional: this copy also follows the main output's volume (e.g. the keyboard volume keys)
        bool followMain;
        public bool FollowMain
        {
            get { return followMain; }
            set { if (Set(ref followMain, value)) { ApplyFollow(); owner.SaveSoon(); } }
        }
        public bool ShowFollow { get { return !isMain; } }

        public void ApplyFollow()
        {
            // The only gain the engine ever applies: 1.0 (bit-exact copy) unless following the main volume.
            bool follow = followMain && !isMain;
            Mix.Volume = follow ? owner.MainGain : 1f;
            Mix.Muted = follow && owner.MainMuted;
        }

        public void MeterGains(out float l, out float r)
        {
            float gl, gr;
            Mix.Gains(out gl, out gr);
            l = gl * (ep != null ? ampL : 1f);
            r = gr * (ep != null ? ampR : 1f);
        }

        string state = "";
        public string State { get { return state; } set { if (Set(ref state, value ?? "")) Raise("Subtitle"); } }

        public string Subtitle
        {
            get
            {
                if (!connected) return isMain ? "Main output · Not connected" : "Not connected";
                string s = owner.Running && enabled && state.Length > 0 ? state : enabled ? "On" : "Off";
                return isMain ? "Main output · " + s : s;
            }
        }

        public string Warning
        {
            get
            {
                if (!enabled) return null;
                if (owner.AppMode && isMain) return "The app already plays here, so you would hear it twice.";
                if (owner.IsSourceDevice(Id)) return "This is the source device, so it will be skipped.";
                return null;
            }
        }
        public bool HasWarning { get { return !string.IsNullOrEmpty(Warning); } }

        double meterL, meterR;
        public double MeterL { get { return meterL; } set { Set(ref meterL, value); } }
        public double MeterR { get { return meterR; } set { Set(ref meterR, value); } }

        public void Init(bool follow, bool on)
        {
            followMain = follow;
            enabled = on;
        }

        public void RefreshDerived() { Raise("Subtitle"); Raise("Warning"); Raise("HasWarning"); }
    }

    class MainVM : Observable
    {
        public ObservableCollection<AppItem> Apps { get; private set; }
        public ObservableCollection<SourceItem> Sources { get; private set; }
        public ObservableCollection<OutputVM> Outputs { get; private set; }

        readonly Window window;
        readonly Settings settings;
        readonly Engine engine = new Engine();
        readonly DispatcherTimer tick, restart, save, devDebounce;
        readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        readonly Brush bGood, bWarn, bBad, bIdle;
        IMMDeviceEnumerator watchEnum;
        DeviceWatcher watcher;
        EndpointVolume mainVolume;      // the main (default) output, for "Follow main volume"
        string defaultId, mainVolumeId;

        public float MainGain { get; private set; }
        public bool MainMuted { get; private set; }

        void SyncMainVolume()
        {
            MainGain = mainVolume != null ? (float)Math.Pow(10, mainVolume.LevelDb / 20.0) : 1f;   // what the main output really hears
            MainMuted = mainVolume != null && mainVolume.Muted;
            foreach (OutputVM o in Outputs) o.ApplyFollow();
        }
        bool loading = true, suppress;

        static readonly HashSet<string> HiddenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchHost", "SearchApp", "LockApp", "explorer", "GameAudioSplitter", "Widgets", "WidgetBoard",
            "PhoneExperienceHost", "CrossDeviceResume", "NVIDIA Overlay", "ShellHost" };

        public MainVM(Window window, Settings settings)
        {
            this.window = window;
            this.settings = settings;
            Apps = new ObservableCollection<AppItem>();
            Sources = new ObservableCollection<SourceItem>();
            Outputs = new ObservableCollection<OutputVM>();
            bGood = (Brush)window.FindResource("bGood");
            bWarn = (Brush)window.FindResource("bWarn");
            bBad = (Brush)window.FindResource("bBad");
            bIdle = (Brush)window.FindResource("bIdle");

            MainGain = 1f;
            appMode = settings.AppMode;
            bufferMs = Math.Max(20, Math.Min(200, settings.BufferMs));
            autoStart = settings.AutoStart;

            // UI refresh while running: 20 fps for the meters, only while the window is visible.
            tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            tick.Tick += delegate { Tick(); };
            restart = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            restart.Tick += delegate { restart.Stop(); if (Running) Start(); };
            save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            save.Tick += delegate { save.Stop(); Collect(); settings.Save(); };
            devDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            devDebounce.Tick += delegate { devDebounce.Stop(); RefreshDevices(); };

            RefreshApps();
            RefreshDevices();
            loading = false;
            ShowIdle();

            // Windows tells us when buds / dongles connect or disconnect - no polling.
            try
            {
                watchEnum = Devices.NewEnumerator();
                watcher = new DeviceWatcher(delegate
                {
                    try { window.Dispatcher.BeginInvoke((Action)delegate { devDebounce.Stop(); devDebounce.Start(); }); } catch { }
                });
                watchEnum.RegisterEndpointNotificationCallback(watcher);
            }
            catch { watcher = null; }
        }

        // ---------------- bound properties
        bool appMode;
        public bool AppMode
        {
            get { return appMode; }
            set
            {
                if (!Set(ref appMode, value)) return;
                Raise("DeviceMode"); Raise("ModeHint");
                foreach (OutputVM o in Outputs) o.RefreshDerived();
                Changed(true);
            }
        }
        public bool DeviceMode { get { return !appMode; } set { if (value) AppMode = false; } }

        public string ModeHint
        {
            get
            {
                return appMode
                    ? "Only this app's sound is copied. Calls, music and everything else stay where they are."
                    : "Everything playing on this device is copied.";
            }
        }

        AppItem selectedApp;
        public AppItem SelectedApp
        {
            get { return selectedApp; }
            set
            {
                if (suppress || value == null) return;
                if (Set(ref selectedApp, value)) Changed(true);
            }
        }

        SourceItem selectedSource;
        public SourceItem SelectedSource
        {
            get { return selectedSource; }
            set
            {
                if (suppress || value == null) return;
                if (!Set(ref selectedSource, value)) return;
                foreach (OutputVM o in Outputs) o.RefreshDerived();
                Changed(true);
            }
        }

        double bufferMs;
        public double BufferMs
        {
            get { return bufferMs; }
            set
            {
                if (!Set(ref bufferMs, Math.Round(value))) return;
                engine.BufferMs = (int)bufferMs;   // applies live, no restart
                Raise("BufferText");
                SaveSoon();
            }
        }
        public string BufferText { get { return bufferMs.ToString("0") + " ms"; } }

        bool autoStart;
        public bool AutoStart { get { return autoStart; } set { if (Set(ref autoStart, value)) SaveSoon(); } }

        bool running;
        public bool Running { get { return running; } private set { if (Set(ref running, value)) { Raise("NotRunning"); foreach (OutputVM o in Outputs) o.RefreshDerived(); } } }
        public bool NotRunning { get { return !running; } }

        string statusTitle = "", statusDetail = "";
        Brush statusBrush;
        public string StatusTitle { get { return statusTitle; } set { Set(ref statusTitle, value); } }
        public string StatusDetail { get { return statusDetail; } set { Set(ref statusDetail, value); } }
        public Brush StatusBrush { get { return statusBrush; } set { Set(ref statusBrush, value); } }

        bool noOutputs, hasHandsFree;
        public bool NoOutputs { get { return noOutputs; } set { Set(ref noOutputs, value); } }
        public bool HasHandsFree { get { return hasHandsFree; } set { Set(ref hasHandsFree, value); } }

        // ---------------- helpers used by OutputVM
        public void Post(Action a) { window.Dispatcher.BeginInvoke(a, DispatcherPriority.Background); }
        public void SaveSoon() { if (!loading) { save.Stop(); save.Start(); } }
        public void OutputsChanged() { Changed(true); }

        public bool IsSourceDevice(string id)
        {
            if (appMode) return false;
            string src = selectedSource != null ? selectedSource.Id : "";
            return src.Length == 0 ? id == defaultId : id == src;
        }

        void Changed(bool restartIfRunning)
        {
            if (loading) return;
            SaveSoon();
            if (restartIfRunning && Running) { restart.Stop(); restart.Start(); }
        }

        public static string GlyphFor(string n)
        {
            string s = (n ?? "").ToLowerInvariant();
            if (s.Contains("headphone") || s.Contains("headset") || s.Contains("earphone") || s.Contains("buds") ||
                s.Contains("airpods") || s.Contains("iem") || s.Contains("hands-free")) return "";
            if (s.Contains("hdmi") || s.Contains("display") || s.Contains("monitor") || s.Contains("nvidia")) return "";
            if (s.Contains("cable") || s.Contains("virtual") || s.Contains("voicemeeter") || s.Contains("stream")) return "";
            return "";
        }

        // ---------------- apps
        public void RefreshApps()
        {
            string keep = selectedApp != null ? selectedApp.Name : settings.ProcessName;
            Dictionary<string, AppItem> byName = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                    string n = p.ProcessName;
                    if (HiddenApps.Contains(n) || byName.ContainsKey(n)) continue;
                    AppItem a = new AppItem { Name = n, Title = n, IsRunning = true };
                    string path = Win.ImagePath(p.Id);
                    if (path != null)
                    {
                        try
                        {
                            string d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                            if (!string.IsNullOrWhiteSpace(d) && d.Trim().Length < 60) a.Title = d.Trim();
                        }
                        catch { }
                        a.Icon = IconFor(path);
                    }
                    byName[n] = a;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (!string.IsNullOrEmpty(keep) && !byName.ContainsKey(keep))
                byName[keep] = new AppItem { Name = keep, Title = string.IsNullOrEmpty(settings.ProcessTitle) ? keep : settings.ProcessTitle, IsRunning = false };

            List<AppItem> list = byName.Values.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            suppress = true;
            Apps.Clear();
            foreach (AppItem a in list) Apps.Add(a);
            suppress = false;
            selectedApp = list.FirstOrDefault(a => a.Name.Equals(keep ?? "", StringComparison.OrdinalIgnoreCase));
            Raise("SelectedApp");
        }

        ImageSource IconFor(string path)
        {
            ImageSource img;
            if (iconCache.TryGetValue(path, out img)) return img;
            try
            {
                using (System.Drawing.Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (ic != null)
                    {
                        BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        img = bs;
                    }
                }
            }
            catch { }
            iconCache[path] = img;
            return img;
        }

        // ---------------- devices
        public void RefreshDevices()
        {
            HashSet<string> enabledIds = new HashSet<string>(loading ? settings.TargetIds : Outputs.Where(o => o.Enabled).Select(o => o.Id));
            List<DeviceInfo> devs;
            string defId = null, defName = null;
            try
            {
                devs = Devices.ListOutputs(enabledIds);
                IMMDeviceEnumerator en = Devices.NewEnumerator();
                try { DeviceInfo def = Devices.DefaultOutput(en); defId = def.Id; defName = def.Name; } catch { }
                Native.Release(en);
            }
            catch (Exception ex)
            {
                devs = new List<DeviceInfo>();
                StatusDetail = "Could not list audio devices: " + ex.Message;
            }
            bool defaultChanged = defaultId != null && defId != defaultId;
            defaultId = defId;

            Dictionary<string, OutputVM> old = Outputs.ToDictionary(o => o.Id);
            List<OutputVM> next = new List<OutputVM>();
            foreach (DeviceInfo d in devs)
            {
                OutputVM vm;
                bool isNew = !old.TryGetValue(d.Id, out vm);
                if (isNew)
                {
                    vm = new OutputVM(this, d.Id);
                    vm.Init(settings.FollowIds.Contains(d.Id), enabledIds.Contains(d.Id));
                }
                vm.Name = d.Name;
                vm.Connected = d.Active;
                vm.IsMain = d.Id == defId;
                if (isNew) vm.Attach();
                next.Add(vm);
            }
            foreach (OutputVM o in Outputs) if (!next.Contains(o)) o.Detach();
            if (defId != (mainVolume != null ? mainVolumeId : null))
            {
                if (mainVolume != null) mainVolume.Dispose();
                mainVolume = defId != null ? EndpointVolume.Open(defId, delegate { Post(SyncMainVolume); }) : null;
                mainVolumeId = defId;
            }
            next.Sort(delegate(OutputVM a, OutputVM b)
            {
                if (a.IsMain != b.IsMain) return a.IsMain ? -1 : 1;
                if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            if (!next.SequenceEqual(Outputs))
            {
                Outputs.Clear();
                foreach (OutputVM o in next) Outputs.Add(o);
            }
            foreach (OutputVM o in Outputs) o.RefreshDerived();
            SyncMainVolume();

            string keepSrc = selectedSource != null ? selectedSource.Id : settings.SourceDeviceId;
            List<SourceItem> srcs = new List<SourceItem>();
            srcs.Add(new SourceItem { Id = "", Name = defName != null ? "Default output (" + defName + ")" : "Default output", Glyph = "" });
            foreach (DeviceInfo d in devs) if (d.Active) srcs.Add(new SourceItem { Id = d.Id, Name = d.Name, Glyph = GlyphFor(d.Name) });
            suppress = true;
            Sources.Clear();
            foreach (SourceItem s in srcs) Sources.Add(s);
            suppress = false;
            selectedSource = srcs.FirstOrDefault(s => s.Id == keepSrc) ?? srcs[0];
            Raise("SelectedSource");

            NoOutputs = Outputs.Count == 0;
            HasHandsFree = Outputs.Any(o => o.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0);
            if (defaultChanged && Running && !appMode && selectedSource.Id.Length == 0) { restart.Stop(); restart.Start(); }
        }

        // ---------------- start / stop
        void ShowIdle()
        {
            StatusTitle = "Off";
            StatusBrush = bIdle;
            StatusDetail = "Pick what to copy and where to play it, then press Start.";
        }

        void ShowHint(string title, string detail)
        {
            StatusTitle = title;
            StatusBrush = bWarn;
            StatusDetail = detail;
        }

        public void Start()
        {
            List<DeviceInfo> targets = Outputs.Where(o => o.Enabled)
                .Select(o => new DeviceInfo { Id = o.Id, Name = o.Name, Mix = o.Mix }).ToList();
            string problem = null, detail = null;
            if (appMode && selectedApp == null) { problem = "Pick an app"; detail = "Choose the game or app under \"Copy sound from\". Start it first if it is not in the list."; }
            else if (targets.Count == 0) { problem = "Pick an output"; detail = "Switch on at least one device under \"Also play it on\"."; }
            if (problem != null)
            {
                if (Running) Stop();
                ShowHint(problem, detail);
                return;
            }

            Collect();
            settings.Save();
            engine.BufferMs = (int)bufferMs;
            string proc = null, title = null;
            if (appMode)
            {
                proc = selectedApp.Name;
                if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc.Substring(0, proc.Length - 4);
                title = selectedApp.Title;
            }
            engine.Start(appMode, proc, title, settings.SourceDeviceId, targets);
            Running = true;
            if (window.WindowState != WindowState.Minimized) tick.Start();
            Tick();
        }

        public void Stop()
        {
            restart.Stop();
            tick.Stop();
            engine.Stop();
            Running = false;
            foreach (OutputVM o in Outputs) { o.MeterL = 0; o.MeterR = 0; o.State = ""; }
            ShowIdle();
        }

        static double Meter(float peak)
        {
            if (peak < 0.002f) return 0;
            double db = 20 * Math.Log10(peak);           // -54 dB .. 0 dB  ->  0 .. 1
            return Math.Round(Math.Max(0, Math.Min(1, (db + 54) / 54)), 2);
        }

        void Tick()
        {
            if (!Running) return;
            int phase = engine.Phase;
            string detail = engine.Detail;
            if (phase == Engine.Live)
            {
                StatusTitle = "Live";
                StatusBrush = bGood;
                if (!engine.Sound) detail += " · nothing playing right now";
            }
            else if (phase == Engine.Waiting) { StatusTitle = "Waiting"; StatusBrush = bWarn; }
            else if (phase == Engine.Problem) { StatusTitle = "Problem"; StatusBrush = bBad; }
            else { StatusTitle = "Starting"; StatusBrush = bIdle; }
            StatusDetail = detail;

            float pl = phase == Engine.Live ? engine.PeakL : 0f, pr = phase == Engine.Live ? engine.PeakR : 0f;
            foreach (OutputVM o in Outputs)
            {
                if (!o.Enabled) continue;
                o.State = o.Mix.State;
                float gl, gr;
                o.MeterGains(out gl, out gr);
                o.MeterL = Meter(pl * gl);
                o.MeterR = Meter(pr * gr);
            }
        }

        public void OnWindowState()
        {
            if (window.WindowState == WindowState.Minimized)
            {
                tick.Stop();            // nothing to draw: zero UI work while minimised
                Win.TrimMemory();
            }
            else if (Running) { tick.Start(); Tick(); }
        }

        void Collect()
        {
            settings.AppMode = appMode;
            if (selectedApp != null) { settings.ProcessName = selectedApp.Name; settings.ProcessTitle = selectedApp.Title; }
            settings.SourceDeviceId = selectedSource != null ? selectedSource.Id : "";
            settings.TargetIds = Outputs.Where(o => o.Enabled).Select(o => o.Id).ToList();
            foreach (OutputVM o in Outputs)
            {
                if (o.FollowMain) settings.FollowIds.Add(o.Id); else settings.FollowIds.Remove(o.Id);
            }
            settings.BufferMs = (int)bufferMs;
            settings.AutoStart = autoStart;
        }

        public void Shutdown()
        {
            tick.Stop(); restart.Stop(); save.Stop(); devDebounce.Stop();
            if (watcher != null) { try { watchEnum.UnregisterEndpointNotificationCallback(watcher); } catch { } }
            Native.Release(watchEnum);
            foreach (OutputVM o in Outputs) o.Detach();
            if (mainVolume != null) mainVolume.Dispose();
            Collect();
            settings.Save();
            engine.Stop();          // returns within milliseconds; audio devices are released
        }
    }

    // ------------------------------------------------------------------ Program
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
            {
                string result = null;
                Thread t = new Thread(delegate() { result = Engine.SelfTest(); });
                t.SetApartmentState(ApartmentState.MTA);
                t.Start(); t.Join();
                if (args.Length > 1) File.WriteAllText(args[1], result); else Console.Write(result);
                return 0;
            }
            if (args.Length > 2 && args[0] == "--voltest")
            {
                File.WriteAllText(args[2], VolTest(args[1]));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--bench")
            {
                string r = Bench(args[1], args[2], int.Parse(args[3]), args.Length > 5 ? args[5] == "gain" : false);
                if (args.Length > 4) File.WriteAllText(args[4], r); else Console.Write(r);
                return 0;
            }

            // Only one copy at a time (two copies would fight over the same devices).
            bool created;
            using (Mutex single = new Mutex(true, "Local\\GameAudioSplitter_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Game Audio Splitter is already running.", "Game Audio Splitter");
                    return 1;
                }
                RunUi(args.Length > 0 && args[0] == "--screenshot" ? args[1] : null);
                GC.KeepAlive(single);
            }
            return 0;
        }

        static string ReadResource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static void RunUi(string screenshotPath)
        {
            Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show("Unexpected error: " + e.Exception.Message, "Game Audio Splitter");
                e.Handled = true;
            };

            Theme theme = Theme.Detect();
            Window w = (Window)XamlReader.Parse(theme.Apply(ReadResource("MainWindow.xaml")));
            w.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.T["SolidBg"]));
            // Fit any screen / scaling: never open taller or wider than the usable desktop.
            // Everything inside is fluid (star columns, trimming, scrolling list, fixed footer).
            Rect wa = SystemParameters.WorkArea;
            w.Width = Math.Min(w.Width, wa.Width - 24);
            w.Height = Math.Min(w.Height, wa.Height - 24);
            w.MinWidth = Math.Min(w.MinWidth, w.Width);
            w.MinHeight = Math.Min(w.MinHeight, w.Height);
            if (screenshotPath != null && screenshotPath.Contains("|"))
            {
                string[] p = screenshotPath.Split('|');        // file|width|height for layout tests
                screenshotPath = p[0];
                w.MinWidth = 0; w.MinHeight = 0;
                w.Width = double.Parse(p[1], CultureInfo.InvariantCulture);
                w.Height = double.Parse(p[2], CultureInfo.InvariantCulture);
            }
            if (screenshotPath != null)
            {
                // Headless: render far off-screen, never take focus, no taskbar button,
                // so a layout check can never interrupt whatever is on screen (e.g. a game).
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -32000; w.Top = -32000;
                w.ShowActivated = false;
                w.ShowInTaskbar = false;
            }
            WindowChrome.SetWindowChrome(w, new WindowChrome
            {
                CaptionHeight = 40,
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = true,
                CornerRadius = new CornerRadius(0),
            });
            w.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                int dark = theme.Dark ? 1 : 0;
                Win.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);          // dark title-bar buttons
                if (Win.Build >= 22621)
                {
                    // Mica: Windows 11's own frosted backdrop. Rendered by the system compositor from a
                    // blurred snapshot of the wallpaper, so it costs nothing extra while the app runs.
                    int mica = 2;
                    if (Win.DwmSetWindowAttribute(hwnd, 38, ref mica, 4) == 0)
                    {
                        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                        w.Background = Brushes.Transparent;
                    }
                }
            };

            try
            {
                using (System.Drawing.Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    w.Icon = bs;
                    ((Image)w.FindName("AppIcon")).Source = bs;
                }
            }
            catch { }

            Settings settings = Settings.Load();
            MainVM vm = new MainVM(w, settings);
            w.DataContext = vm;

            ((Button)w.FindName("StartButton")).Click += delegate { vm.Start(); };
            ((Button)w.FindName("StopButton")).Click += delegate { vm.Stop(); };
            ((ComboBox)w.FindName("AppCombo")).PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (!((ComboBox)s).IsDropDownOpen) vm.RefreshApps();   // fresh list each time it opens
            };
            // double-click a slider: volume -> 100 %, balance -> centre
            EventManager.RegisterClassHandler(typeof(Slider), Control.MouseDoubleClickEvent, new MouseButtonEventHandler(delegate(object s, MouseButtonEventArgs e)
            {
                Slider sl = (Slider)s;
                string tag = sl.Tag as string;
                if (tag == "volume") sl.SetCurrentValue(RangeBase.ValueProperty, 100.0);
                else if (tag == "balance") sl.SetCurrentValue(RangeBase.ValueProperty, 0.0);
            }));

            FrameworkElement root = (FrameworkElement)w.FindName("Root");
            w.StateChanged += delegate
            {
                root.Margin = w.WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
                vm.OnWindowState();
            };
            w.Closing += delegate { vm.Shutdown(); };
            bool first = true;
            w.ContentRendered += delegate
            {
                if (!first) return;
                first = false;
                if (screenshotPath != null) { Screenshot(w, screenshotPath); return; }
                if (settings.AutoStart) vm.Start();
            };
            app.Run(w);
        }

        // --screenshot <file.png>: renders the window to a PNG and exits (used to check the design).
        static void Screenshot(Window w, string path)
        {
            DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += delegate
            {
                t.Stop();
                FrameworkElement root = (FrameworkElement)w.Content;
                double dpi = VisualTreeHelper.GetDpi(w).PixelsPerInchX;
                RenderTargetBitmap rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi / 96), (int)(root.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                DrawingVisual dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                    dc.DrawRectangle((Brush)new BrushConverter().ConvertFromString("#FF202020"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                rtb.Render(dv);
                rtb.Render(root);
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(path)) enc.Save(fs);
                w.Close();
            };
            t.Start();
        }

        // Round-trips volume / balance / mute through Windows for one device, then restores it.
        static string VolTest(string match)
        {
            StringBuilder sb = new StringBuilder();
            DeviceInfo d = Devices.ListOutputs().Find(delegate(DeviceInfo x) { return x.Name.Contains(match); });
            int notified = 0;
            EndpointVolume v = EndpointVolume.Open(d.Id, delegate { Interlocked.Increment(ref notified); });
            float lv = v.Level, bal = v.Balance; bool mu = v.Muted;
            sb.AppendLine(string.Format("{0}: level {1:0.00}, balance {2:0.00}, muted {3}, channels {4}", d.Name, lv, bal, mu, v.Channels));
            v.SetLevel(0.37f); Thread.Sleep(100);
            sb.AppendLine(string.Format("set level 0.37 -> read {0:0.00}", v.Level));
            v.SetBalance(0.4f); Thread.Sleep(100);
            sb.AppendLine(string.Format("set balance +0.40 -> read {0:0.00}, level now {1:0.00}", v.Balance, v.Level));
            v.SetBalance(-1f); Thread.Sleep(100);
            sb.AppendLine(string.Format("set balance -1.00 -> read {0:0.00}", v.Balance));
            v.SetBalance(0f); Thread.Sleep(100);
            sb.AppendLine(string.Format("set balance 0 -> read {0:0.00}, level {1:0.00}", v.Balance, v.Level));
            v.SetMuted(true); Thread.Sleep(100);
            sb.AppendLine("set mute -> read " + v.Muted);
            sb.AppendLine("change notifications for our own writes (should be 0): " + notified);
            // an outside change, written with a different context, must notify
            v.SetMuted(mu); v.SetBalance(bal); v.SetLevel(lv); Thread.Sleep(100);
            sb.AppendLine(string.Format("restored: level {0:0.00}, balance {1:0.00}, muted {2}", v.Level, v.Balance, v.Muted));
            v.Dispose();
            return sb.ToString();
        }

        // Silent benchmark: plays digital silence into one device, mirrors that device to another,
        // and reports CPU / memory / stop time of this process. "gain" exercises the volume/balance path.
        static string Bench(string srcMatch, string dstMatch, int seconds, bool gain)
        {
            StringBuilder sb = new StringBuilder();
            List<DeviceInfo> devs = Devices.ListOutputs();
            DeviceInfo src = devs.Find(delegate(DeviceInfo d) { return d.Name.Contains(srcMatch); });
            DeviceInfo dst = devs.Find(delegate(DeviceInfo d) { return d.Name.Contains(dstMatch); });
            sb.AppendLine("Source: " + src.Name + "  ->  Target: " + dst.Name + (gain ? "  (volume 80%, balance R 20)" : "  (100%, centre)"));
            dst.Mix = new Mix();
            if (gain) { dst.Mix.Volume = 0.8f; dst.Mix.Balance = 0.2f; }

            bool feederStop = false;
            Thread feeder = new Thread(delegate()
            {
                IMMDeviceEnumerator en = Devices.NewEnumerator();
                IAudioClient c = Devices.Activate(en, src.Id);
                IntPtr mix; c.GetMixFormat(out mix);
                AutoResetEvent ev = new AutoResetEvent(false);
                Native.Check(c.Initialize(0, Native.EVENTCALLBACK, 200000, 0, mix, IntPtr.Zero), "feeder");
                c.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                int bf; c.GetBufferSize(out bf);
                object o; Guid g = Native.IID_IAudioRenderClient; c.GetService(ref g, out o);
                IAudioRenderClient r = (IAudioRenderClient)o;
                int blk = Marshal.ReadInt16(mix, 12);
                c.Start();
                while (!feederStop)
                {
                    ev.WaitOne(100);
                    int pad; c.GetCurrentPadding(out pad);
                    int n = bf - pad; if (n <= 0) continue;
                    IntPtr p; r.GetBuffer(n, out p);
                    byte[] z = new byte[n * blk];      // real zeros (not the SILENT flag) so the pipeline does full work
                    Marshal.Copy(z, 0, p, z.Length);
                    r.ReleaseBuffer(n, 0);
                }
                c.Stop();
                Native.Release(r); Native.Release(c); Marshal.FreeCoTaskMem(mix);
            });
            feeder.SetApartmentState(ApartmentState.MTA);
            feeder.Start();
            Thread.Sleep(500);

            Engine e = new Engine();
            e.Start(false, null, null, src.Id, new List<DeviceInfo> { dst });
            Thread.Sleep(1500);   // warm-up

            Process me = Process.GetCurrentProcess();
            me.Refresh();
            TimeSpan cpu0 = me.TotalProcessorTime;
            long pk0 = e.Packets;
            Stopwatch sw = Stopwatch.StartNew();
            Thread.Sleep(seconds * 1000);
            me.Refresh();
            double cpuMs = (me.TotalProcessorTime - cpu0).TotalMilliseconds;
            double wallMs = sw.Elapsed.TotalMilliseconds;
            long packets = e.Packets - pk0;
            sb.AppendLine("Status: " + e.Status + " | target: " + dst.Mix.State);
            sb.AppendLine(string.Format("Packets mirrored: {0} in {1:F1}s ({2:F0}/s)", packets, wallMs / 1000, packets * 1000 / wallMs));
            sb.AppendLine(string.Format("CPU: {0:F2}% of one core, {1:F3}% of whole CPU ({2} cores) - includes the test's own silence player",
                cpuMs * 100 / wallMs, cpuMs * 100 / wallMs / Environment.ProcessorCount, Environment.ProcessorCount));
            sb.AppendLine(string.Format("Memory: {0:F1} MB working set, {1:F1} MB private", me.WorkingSet64 / 1048576.0, me.PrivateMemorySize64 / 1048576.0));

            Stopwatch st = Stopwatch.StartNew();
            e.Stop();
            sb.AppendLine(string.Format("Stop took: {0} ms, audio thread still alive: {1}", st.ElapsedMilliseconds, e.Running));

            feederStop = true;
            feeder.Join(2000);
            return sb.ToString();
        }
    }
}
