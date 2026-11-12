// AudioSwitch.exe - set the Windows default audio output device.
// Uses the MMDevice COM API to enumerate endpoints and the (undocumented but
// long-stable) IPolicyConfig interface to change the default - the same
// mechanism the Settings app and tools like SoundSwitch use.
//
// Commands:
//   AudioSwitch list                     show all render devices and the current default
//   AudioSwitch set <substring> [secs]   make the first ACTIVE output device whose name
//                                        contains <substring> the default (Console +
//                                        Multimedia roles). Retries until the device
//                                        appears, up to [secs] (default 15) - useful
//                                        right after a display switch, when an HDMI
//                                        audio endpoint takes a moment to come alive.
// Exit codes: 0 = set+verified, 2 = no matching active device before timeout,
//             3 = SetDefaultEndpoint rejected, 4 = set but verification mismatch.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioSwitchTool
{
    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(uint dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(uint dataFlow, uint role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint props);
        [PreserveSig] int GetAt(uint prop, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    // Vtable order matters; only SetDefaultEndpoint is actually called.
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }

    class Program
    {
        const uint eRender = 0;
        const uint eConsole = 0;
        const uint eMultimedia = 1;
        const uint DEVICE_STATE_ACTIVE = 0x1;
        const uint DEVICE_STATE_ALL = 0xF;
        const ushort VT_LPWSTR = 31;

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pvar);

        static PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        struct Dev
        {
            public string Id;
            public string Name;
            public uint State;
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 1 && args[0].ToLowerInvariant() == "list") return ListDevices();
                if (args.Length >= 2 && args[0].ToLowerInvariant() == "set")
                {
                    int timeout = 15;
                    if (args.Length >= 3) int.TryParse(args[2], out timeout);
                    return SetDefault(args[1], timeout);
                }
                Console.WriteLine("Usage: AudioSwitch list | set <name substring> [timeoutSeconds]");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.Message);
                return 10;
            }
        }

        static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
        }

        static string GetName(IMMDevice dev)
        {
            IPropertyStore store;
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != 0) return "";
            PROPVARIANT pv;
            if (store.GetValue(ref PKEY_FriendlyName, out pv) != 0) return "";
            string name = "";
            if (pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero)
                name = Marshal.PtrToStringUni(pv.p) ?? "";
            PropVariantClear(ref pv);
            return name;
        }

        static List<Dev> GetDevices(uint stateMask)
        {
            List<Dev> result = new List<Dev>();
            IMMDeviceEnumerator en = CreateEnumerator();
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(eRender, stateMask, out col) != 0) return result;
            uint count;
            col.GetCount(out count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice dev;
                if (col.Item(i, out dev) != 0) continue;
                Dev d = new Dev();
                dev.GetId(out d.Id);
                dev.GetState(out d.State);
                d.Name = GetName(dev);
                result.Add(d);
            }
            return result;
        }

        static string GetDefaultId()
        {
            IMMDeviceEnumerator en = CreateEnumerator();
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev) != 0) return "";
            string id;
            dev.GetId(out id);
            return id ?? "";
        }

        static string StateName(uint s)
        {
            if (s == 0x1) return "active";
            if (s == 0x2) return "disabled";
            if (s == 0x4) return "not present";
            if (s == 0x8) return "unplugged";
            return "state " + s;
        }

        static int ListDevices()
        {
            string def = GetDefaultId();
            List<Dev> devs = GetDevices(DEVICE_STATE_ALL);
            Console.WriteLine("Audio output devices (" + devs.Count + "):");
            foreach (Dev d in devs)
            {
                string mark = string.Equals(d.Id, def, StringComparison.OrdinalIgnoreCase) ? " [DEFAULT]" : "";
                Console.WriteLine("  - " + d.Name + "  (" + StateName(d.State) + ")" + mark);
            }
            return 0;
        }

        static int SetDefault(string substring, int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                List<Dev> devs = GetDevices(DEVICE_STATE_ACTIVE);
                foreach (Dev d in devs)
                {
                    if (d.Name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    IPolicyConfig pc = (IPolicyConfig)(object)new PolicyConfigClient();
                    int hr1 = pc.SetDefaultEndpoint(d.Id, eConsole);
                    int hr2 = pc.SetDefaultEndpoint(d.Id, eMultimedia);
                    if (hr1 != 0 || hr2 != 0)
                    {
                        Console.Error.WriteLine("SetDefaultEndpoint failed (hr=0x" + hr1.ToString("X8") + "/0x" + hr2.ToString("X8") + ")");
                        return 3;
                    }
                    Thread.Sleep(300);
                    if (string.Equals(GetDefaultId(), d.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Default audio output is now: " + d.Name);
                        return 0;
                    }
                    Console.Error.WriteLine("Set '" + d.Name + "' but Windows reports a different default.");
                    return 4;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    Console.Error.WriteLine("No active audio device matching '" + substring + "' appeared within " + timeoutSeconds + "s.");
                    Console.Error.WriteLine("Active devices:");
                    foreach (Dev d in GetDevices(DEVICE_STATE_ACTIVE))
                        Console.Error.WriteLine("  - " + d.Name);
                    return 2;
                }
                Thread.Sleep(500);
            }
        }
    }
}
