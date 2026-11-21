// Game Audio Splitter - mirrors one app's (or one device's) audio to extra output devices.
// Pure WASAPI, 32-bit float pass-through, Windows' own high-quality resampler when rates differ.
// Build: csc /target:winexe /unsafe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll GameAudioSplitter.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

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

    // ------------------------------------------------------------------ Engine
    class Target
    {
        public string Id, Name;
        public IAudioClient Client;
        public IAudioRenderClient Render;
        public int BufferFrames;
        public bool Ok, Skip;
        public DateTime RetryAt;
        public string State = "starting";
        public int Dropouts;
    }

    class Settings
    {
        public bool AppMode = true;
        public string ProcessName = "VALORANT-Win64-Shipping";
        public string SourceDeviceId = "";          // "" = default output
        public List<string> TargetIds = new List<string>();
        public int Volume = 100;
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
                    else if (k == "SourceDeviceId") s.SourceDeviceId = v;
                    else if (k == "Targets") s.TargetIds = new List<string>(v.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                    else if (k == "Volume") int.TryParse(v, out s.Volume);
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
                File.WriteAllLines(FilePath, new[] {
                    "AppMode=" + (AppMode ? "1" : "0"),
                    "ProcessName=" + ProcessName,
                    "SourceDeviceId=" + SourceDeviceId,
                    "Targets=" + string.Join("|", TargetIds.ToArray()),
                    "Volume=" + Volume,
                    "BufferMs=" + BufferMs,
                    "AutoStart=" + (AutoStart ? "1" : "0"),
                });
            }
            catch { }
        }
    }

    class Engine
    {
        public volatile float Volume = 1f;
        public volatile int BufferMs = 40;
        public volatile string Status = "Stopped";

        public long Packets;

        volatile bool stop;
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        Thread thread;
        bool appMode;
        string processName, sourceDeviceId;
        List<DeviceInfo> targetDevices;

        Fmt fmt;
        IMMDeviceEnumerator en;
        List<Target> targets = new List<Target>();

        public bool Running { get { return thread != null && thread.IsAlive; } }

        public void Start(bool appMode, string processName, string sourceDeviceId, List<DeviceInfo> targetDevices)
        {
            this.appMode = appMode;
            this.processName = processName;
            this.sourceDeviceId = sourceDeviceId;
            this.targetDevices = targetDevices;
            if (Running) Stop();
            stop = false;
            stopEvent.Reset();
            Status = "Starting...";
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
            Status = "Stopped";
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
                catch (Exception ex) { if (!stop) Status = "Problem: " + Native.Describe(ex) + "\nRetrying automatically..."; }
                stopEvent.WaitOne(2000);   // sleeps, but wakes instantly on Stop
            }
            if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
            Status = "Stopped";
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
                    if (pid == 0) { Status = "Waiting for " + processName + " to start...\n(Leave this running, then launch the game.)"; return; }
                    hProc = Native.OpenProcess(Native.SYNCHRONIZE, false, pid);
                    sourceName = processName;
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
                Native.Check(capClient.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "Set capture event");
                object o; Guid g = Native.IID_IAudioCaptureClient;
                Native.Check(capClient.GetService(ref g, out o), "Get capture service");
                cap = (IAudioCaptureClient)o;

                // ---- open the outputs
                targets.Clear();
                foreach (DeviceInfo d in targetDevices)
                {
                    Target t = new Target { Id = d.Id, Name = d.Name };
                    targets.Add(t);
                    if (d.Id == skipId) { t.Skip = true; t.State = "skipped (this is the source device)"; continue; }
                    TryOpen(t);
                }

                Native.Check(capClient.Start(), "Start capture");

                // ---- pump
                DateTime nextHouse = DateTime.UtcNow;
                DateTime nextProcScan = DateTime.UtcNow.AddSeconds(3);
                DateTime lastSound = DateTime.MinValue;
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
                        if (!silent && HasSound(data, frames)) lastSound = DateTime.UtcNow;
                        foreach (Target t in targets) Feed(t, data, frames, silent);
                        cap.ReleaseBuffer(frames);
                        Packets++;
                    }

                    if (DateTime.UtcNow >= nextHouse)
                    {
                        nextHouse = DateTime.UtcNow.AddSeconds(1);
                        bool gone = false;
                        if (appMode && hProc != IntPtr.Zero) gone = !HandleAlive(hProc);
                        else if (appMode && DateTime.UtcNow >= nextProcScan)
                        {
                            nextProcScan = DateTime.UtcNow.AddSeconds(3);
                            gone = !ProcessAlive(processName, pid);
                        }
                        if (gone)
                        {
                            Status = processName + " closed. Waiting for it to start again...";
                            return;
                        }
                        foreach (Target t in targets)
                            if (!t.Ok && !t.Skip && DateTime.UtcNow >= t.RetryAt) TryOpen(t);
                        Status = BuildStatus(sourceName, (DateTime.UtcNow - lastSound).TotalSeconds < 1.5);
                    }
                }
            }
            finally
            {
                if (capClient != null) capClient.Stop();
                foreach (Target t in targets) Close(t);
                Native.Release(cap);
                Native.Release(capClient);
                Native.Release(en);
                if (fmt != null) { fmt.Free(); fmt = null; }
                if (hProc != IntPtr.Zero) Native.CloseHandle(hProc);
                ev.Close();
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

        unsafe bool HasSound(IntPtr data, int frames)
        {
            int n = frames * fmt.Channels;
            if (fmt.IsFloat && fmt.Bits == 32)
            {
                float* s = (float*)data;
                for (int i = 0; i < n; i += 7) if (s[i] > 0.0005f || s[i] < -0.0005f) return true;
                return false;
            }
            if (fmt.Bits == 16)
            {
                short* s = (short*)data;
                for (int i = 0; i < n; i += 7) if (s[i] > 16 || s[i] < -16) return true;
                return false;
            }
            return true;
        }

        void TryOpen(Target t)
        {
            Close(t);
            IAudioClient c = null;
            try
            {
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
                t.State = "playing";
                WriteSilence(t, TargetLevel());
                Native.Check(c.Start(), "Start output");
            }
            catch (Exception ex)
            {
                if (t.Client == null) Native.Release(c);
                Close(t);
                t.State = "waiting - " + Native.Describe(ex);
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
            t.State = "reconnecting - " + Native.Describe(new COMException("Output stopped", hr));
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

            IntPtr dst;
            hr = t.Render.GetBuffer(n + extra, out dst);
            if (hr < 0) { Fail(t, hr); return; }
            if (!silent)
            {
                CopyScaled(dst, src, n);
                if (extra == 1) Native.CopyMemory(dst + n * fmt.Block, dst + (n - 1) * fmt.Block, (IntPtr)fmt.Block);
            }
            hr = t.Render.ReleaseBuffer(n + extra, silent ? Native.BUFFERFLAGS_SILENT : 0);
            if (hr < 0) Fail(t, hr);
        }

        unsafe void CopyScaled(IntPtr dst, IntPtr src, int frames)
        {
            int bytes = frames * fmt.Block;
            float v = Volume;
            if (v >= 0.999f || !(fmt.IsFloat || fmt.Bits == 16))
            {
                Native.CopyMemory(dst, src, (IntPtr)bytes);   // bit-exact copy
                return;
            }
            if (fmt.IsFloat)
            {
                float* s = (float*)src; float* d = (float*)dst;
                int n = bytes / 4;
                for (int i = 0; i < n; i++) d[i] = s[i] * v;
            }
            else
            {
                short* s = (short*)src; short* d = (short*)dst;
                int n = bytes / 2;
                for (int i = 0; i < n; i++) d[i] = (short)(s[i] * v);
            }
        }

        string BuildStatus(string sourceName, bool sound)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Copying: ").Append(sourceName).Append("   [").Append(fmt).Append("]\n");
            sb.Append(sound ? "Audio: flowing" : "Audio: silent (nothing playing right now)").Append("\n");
            foreach (Target t in targets)
            {
                sb.Append(t.Ok ? "  ✔ " : "  … ").Append(t.Name).Append(" - ").Append(t.State);
                if (t.Ok && t.Dropouts > 1) sb.Append(" (" + (t.Dropouts - 1) + " refills)");
                sb.Append("\n");
            }
            return sb.ToString();
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

    // ------------------------------------------------------------------ UI
    // Built from auto-sizing layout panels, so nothing overlaps at any Windows scaling (100%, 125%, 150%...).
    class MainForm : Form
    {
        RadioButton rbApp, rbDevice;
        ComboBox cbProcess, cbSource;
        CheckedListBox clbTargets;
        TrackBar tbVolume;
        Label lblVolume, lblStatus;
        NumericUpDown nudBuffer;
        CheckBox chkAuto;
        Button btnStart, btnRefresh;
        GroupBox grpSource, grpTargets;
        System.Windows.Forms.Timer timer, refreshDebounce;
        Settings settings;
        Engine engine = new Engine();
        IMMDeviceEnumerator watchEnum;
        DeviceWatcher watcher;

        static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        }

        public MainForm()
        {
            settings = Settings.Load();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            float scale;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Func<int, int> S = delegate(int v) { return (int)(v * scale); };

            Text = "Game Audio Splitter";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(540), S(600));
            MinimumSize = new Size(S(470), S(540));

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(8)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // 1. source
            grpSource = new GroupBox { Text = "1. Copy the sound from", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            TableLayoutPanel src = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2 };
            src.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            src.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            src.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rbApp = new RadioButton { Text = "Only this app:", AutoSize = true, Anchor = AnchorStyles.Left };
            cbProcess = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            btnRefresh = new Button { Text = "Refresh", AutoSize = true };
            rbDevice = new RadioButton { Text = "Everything on:", AutoSize = true, Anchor = AnchorStyles.Left };
            cbSource = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            src.Controls.Add(rbApp, 0, 0);
            src.Controls.Add(cbProcess, 1, 0);
            src.Controls.Add(btnRefresh, 2, 0);
            src.Controls.Add(rbDevice, 0, 1);
            src.Controls.Add(cbSource, 1, 1);
            src.SetColumnSpan(cbSource, 2);
            grpSource.Controls.Add(src);

            // 2. targets
            grpTargets = new GroupBox { Text = "2. Also play it on (tick one or more)", Dock = DockStyle.Fill };
            TableLayoutPanel tg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tg.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tg.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            clbTargets = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            Label tip = MakeLabel("Bluetooth: tick \"Headphones (...)\", never \"Headset / Hands-Free\".");
            tip.ForeColor = SystemColors.GrayText;
            tg.Controls.Add(clbTargets, 0, 0);
            tg.Controls.Add(tip, 0, 1);
            grpTargets.Controls.Add(tg);

            // options
            TableLayoutPanel opt = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2 };
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            opt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbVolume = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, TickFrequency = 10, LargeChange = 10, SmallChange = 5 };
            lblVolume = MakeLabel("100%");
            lblVolume.MinimumSize = new Size(S(40), 0);
            nudBuffer = new NumericUpDown { Minimum = 20, Maximum = 200, Increment = 5, Width = S(64), Anchor = AnchorStyles.Left };
            FlowLayoutPanel bufRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            Label bufHint = MakeLabel("lower = less delay, higher = fewer dropouts");
            bufHint.ForeColor = SystemColors.GrayText;
            bufRow.Controls.Add(nudBuffer);
            bufRow.Controls.Add(bufHint);
            opt.Controls.Add(MakeLabel("Volume on extra devices:"), 0, 0);
            opt.Controls.Add(tbVolume, 1, 0);
            opt.Controls.Add(lblVolume, 2, 0);
            opt.Controls.Add(MakeLabel("Buffer (ms):"), 0, 1);
            opt.Controls.Add(bufRow, 1, 1);
            opt.SetColumnSpan(bufRow, 2);

            chkAuto = new CheckBox { Text = "Start copying automatically when this app opens", AutoSize = true, Margin = new Padding(3, 6, 3, 6) };
            btnStart = new Button { Text = "Start", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, S(5), 0, S(5)), Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            lblStatus = new Label { Text = "Stopped", AutoSize = true, Dock = DockStyle.Fill, MinimumSize = new Size(0, S(80)), Margin = new Padding(3, 8, 3, 3) };

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // source
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // targets (stretches)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // options
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // autostart
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // start
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // status
            root.Controls.Add(grpSource, 0, 0);
            root.Controls.Add(grpTargets, 0, 1);
            root.Controls.Add(opt, 0, 2);
            root.Controls.Add(chkAuto, 0, 3);
            root.Controls.Add(btnStart, 0, 4);
            root.Controls.Add(lblStatus, 0, 5);
            Controls.Add(root);

            // values
            rbApp.Checked = settings.AppMode;
            rbDevice.Checked = !settings.AppMode;
            tbVolume.Value = Math.Max(0, Math.Min(100, settings.Volume));
            nudBuffer.Value = Math.Max(20, Math.Min(200, settings.BufferMs));
            chkAuto.Checked = settings.AutoStart;
            RefreshProcesses();
            RefreshDevices();
            UpdateVolumeLabel();
            UpdateModeEnabled();

            // events
            rbApp.CheckedChanged += delegate { UpdateModeEnabled(); };
            btnRefresh.Click += delegate { RefreshProcesses(); RefreshDevices(); };
            tbVolume.ValueChanged += delegate { UpdateVolumeLabel(); engine.Volume = tbVolume.Value / 100f; };
            nudBuffer.ValueChanged += delegate { engine.BufferMs = (int)nudBuffer.Value; };
            btnStart.Click += delegate { if (engine.Running) StopEngine(); else StartEngine(); };
            timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += delegate { string s = engine.Status; if (lblStatus.Text != s) lblStatus.Text = s; };
            timer.Start();

            // live device list: Windows tells us when buds/dongles connect or disconnect
            refreshDebounce = new System.Windows.Forms.Timer { Interval = 700 };
            refreshDebounce.Tick += delegate { refreshDebounce.Stop(); RefreshDevices(); };
            try
            {
                watchEnum = Devices.NewEnumerator();
                watcher = new DeviceWatcher(delegate
                {
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke((MethodInvoker)delegate { refreshDebounce.Stop(); refreshDebounce.Start(); });
                    }
                    catch { }
                });
                watchEnum.RegisterEndpointNotificationCallback(watcher);
            }
            catch { watcher = null; }

            FormClosing += delegate
            {
                timer.Stop();
                refreshDebounce.Stop();
                if (watcher != null) { try { watchEnum.UnregisterEndpointNotificationCallback(watcher); } catch { } }
                Native.Release(watchEnum);
                CollectSettings();
                settings.Save();
                engine.Stop();          // returns within milliseconds; audio devices are released
            };
            Shown += delegate { if (settings.AutoStart) StartEngine(); };
        }

        void UpdateVolumeLabel() { lblVolume.Text = tbVolume.Value + "%"; }

        void UpdateModeEnabled()
        {
            cbProcess.Enabled = rbApp.Checked;
            cbSource.Enabled = !rbApp.Checked;
        }

        void RefreshProcesses()
        {
            string current = cbProcess.Text.Length > 0 ? cbProcess.Text : settings.ProcessName;
            SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int myPid = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try { if (p.Id != myPid && p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName); } catch { }
                p.Dispose();
            }
            cbProcess.Items.Clear();
            cbProcess.Items.Add("VALORANT-Win64-Shipping");
            foreach (string n in names) if (!n.Equals("VALORANT-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) cbProcess.Items.Add(n);
            cbProcess.Text = current;
        }

        void RefreshDevices()
        {
            List<string> checkedIds = new List<string>();
            if (clbTargets.Items.Count == 0) checkedIds.AddRange(settings.TargetIds);
            else foreach (object o in clbTargets.CheckedItems) checkedIds.Add(((DeviceInfo)o).Id);
            string srcId = cbSource.SelectedItem != null ? ((DeviceInfo)cbSource.SelectedItem).Id : settings.SourceDeviceId;

            List<DeviceInfo> devs;
            string defId = null;
            try
            {
                devs = Devices.ListOutputs(checkedIds);
                IMMDeviceEnumerator en = Devices.NewEnumerator();
                try { defId = Devices.DefaultOutput(en).Id; } catch { }
                Native.Release(en);
            }
            catch (Exception ex) { devs = new List<DeviceInfo>(); lblStatus.Text = "Could not list devices: " + ex.Message; }

            foreach (DeviceInfo d in devs)
            {
                d.Label = d.Name;
                if (d.Id == defId) d.Label += "   - Windows default (the game plays here)";
                if (!d.Active) d.Label += "   - not connected";
                if (d.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0) d.Label += "   - low quality call mode";
            }

            clbTargets.BeginUpdate();
            clbTargets.Items.Clear();
            foreach (DeviceInfo d in devs) clbTargets.Items.Add(d, checkedIds.Contains(d.Id));
            clbTargets.EndUpdate();

            cbSource.BeginUpdate();
            cbSource.Items.Clear();
            cbSource.Items.Add(new DeviceInfo { Id = "", Name = "Default output device" });
            foreach (DeviceInfo d in devs) if (d.Active) cbSource.Items.Add(d);
            cbSource.SelectedIndex = 0;
            for (int i = 0; i < cbSource.Items.Count; i++) if (((DeviceInfo)cbSource.Items[i]).Id == srcId) cbSource.SelectedIndex = i;
            cbSource.EndUpdate();
        }

        void CollectSettings()
        {
            settings.AppMode = rbApp.Checked;
            settings.ProcessName = cbProcess.Text.Trim();
            settings.SourceDeviceId = cbSource.SelectedItem != null ? ((DeviceInfo)cbSource.SelectedItem).Id : "";
            settings.TargetIds.Clear();
            foreach (object o in clbTargets.CheckedItems) settings.TargetIds.Add(((DeviceInfo)o).Id);
            settings.Volume = tbVolume.Value;
            settings.BufferMs = (int)nudBuffer.Value;
            settings.AutoStart = chkAuto.Checked;
        }

        void StartEngine()
        {
            CollectSettings();
            string proc = settings.ProcessName;
            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc.Substring(0, proc.Length - 4);
            if (settings.AppMode && proc.Length == 0) { MessageBox.Show(this, "Type or pick the app to copy (e.g. VALORANT-Win64-Shipping)."); return; }
            List<DeviceInfo> targets = new List<DeviceInfo>();
            foreach (object o in clbTargets.CheckedItems)
            {
                DeviceInfo d = (DeviceInfo)o;
                targets.Add(new DeviceInfo { Id = d.Id, Name = d.Name });
            }
            if (targets.Count == 0) { MessageBox.Show(this, "Tick at least one device under \"Also play it on\"."); return; }
            settings.Save();

            engine.Volume = tbVolume.Value / 100f;
            engine.BufferMs = (int)nudBuffer.Value;
            engine.Start(settings.AppMode, proc, settings.SourceDeviceId, targets);
            btnStart.Text = "Stop";
            grpSource.Enabled = false;
            clbTargets.Enabled = false;
        }

        void StopEngine()
        {
            engine.Stop();
            btnStart.Text = "Start";
            grpSource.Enabled = true;
            clbTargets.Enabled = true;
        }
    }

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
            if (args.Length > 0 && args[0] == "--bench")
            {
                Console.Write(Bench(args[1], args[2], int.Parse(args[3])));
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
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("Unexpected error: " + e.Exception.Message, "Game Audio Splitter");
                };
                Application.Run(new MainForm());
                GC.KeepAlive(single);
            }
            return 0;
        }

        // Silent benchmark: plays digital silence into one device, mirrors that device to another,
        // and reports CPU / memory / stop time of this process.
        static string Bench(string srcMatch, string dstMatch, int seconds)
        {
            StringBuilder sb = new StringBuilder();
            List<DeviceInfo> devs = Devices.ListOutputs();
            DeviceInfo src = devs.Find(delegate(DeviceInfo d) { return d.Name.Contains(srcMatch); });
            DeviceInfo dst = devs.Find(delegate(DeviceInfo d) { return d.Name.Contains(dstMatch); });
            sb.AppendLine("Source: " + src.Name + "  ->  Target: " + dst.Name);

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
            e.Volume = 1f;
            e.Start(false, null, src.Id, new List<DeviceInfo> { dst });
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
            sb.AppendLine("Status: " + e.Status.Replace("\n", " | "));
            sb.AppendLine(string.Format("Packets mirrored: {0} in {1:F1}s ({2:F0}/s)", packets, wallMs / 1000, packets * 1000 / wallMs));
            sb.AppendLine(string.Format("CPU: {0:F2}% of one core, {1:F3}% of whole CPU ({2} cores) - includes the test's own silence player",
                cpuMs * 100 / wallMs, cpuMs * 100 / wallMs / Environment.ProcessorCount, Environment.ProcessorCount));
            sb.AppendLine(string.Format("Memory: {0:F1} MB working set, {1:F1} MB private", me.WorkingSet64 / 1048576.0, me.PrivateMemorySize64 / 1048576.0));

            Stopwatch st = Stopwatch.StartNew();
            e.Stop();
            sb.AppendLine(string.Format("Stop took: {0} ms, audio thread still alive: {1}", st.ElapsedMilliseconds, e.Running));

            // waiting-for-game mode must also stop instantly
            Engine w = new Engine();
            w.Start(true, "NoSuchGame-Win64-Shipping", null, new List<DeviceInfo> { dst });
            Thread.Sleep(1200);
            sb.AppendLine("Waiting mode status: " + w.Status.Replace("\n", " "));
            st.Restart();
            w.Stop();
            sb.AppendLine(string.Format("Stop from waiting mode took: {0} ms", st.ElapsedMilliseconds));

            feederStop = true;
            feeder.Join(2000);
            return sb.ToString();
        }
    }
}
