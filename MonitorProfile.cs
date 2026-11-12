// MonitorProfile.exe - save/restore complete Windows display topologies in one atomic call.
// Uses QueryDisplayConfig/SetDisplayConfig (the same CCD API the Settings app and
// DisplayFusion use), so a profile captures: which monitors are active, primary monitor,
// resolution, refresh rate, rotation, and positions. Applying a profile activates and
// deactivates monitors in a single SetDisplayConfig call - no intermediate states and
// no "Keep these display settings?" dialog.
//
// Commands:
//   MonitorProfile save <name>      capture the CURRENT layout as a profile
//   MonitorProfile load <name>      apply a saved profile (exit 0=ok, 2=apply failed,
//                                   3=applied but verification mismatch, 4=not found,
//                                   5=adapter missing)
//   MonitorProfile validate <name>  dry-run a profile without changing anything
//   MonitorProfile show <name>      print what a saved profile contains
//   MonitorProfile current          print "MATCH <name>" for the saved profile that matches
//                                   the current layout (or "NONE"), then the active displays
//   MonitorProfile status           show currently active displays
//   MonitorProfile list             list saved profiles
//
// Two layouts "match" when the same monitors are active with the same resolution,
// refresh rate (within 1 Hz) and primary display. Profiles whose names start with
// "_" are internal snapshots (e.g. the revert point) and are hidden from list/current.
//
// Profiles survive reboots: adapter LUIDs change per boot, so the profile stores the
// stable adapter device path and remaps LUIDs at load time.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MonitorProfileTool
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public long AsLong() { return ((long)HighPart << 32) | (long)LowPart; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string adapterDevicePath;
    }

    public static class Native
    {
        public const uint QDC_ALL_PATHS = 1;
        public const uint QDC_ONLY_ACTIVE_PATHS = 2;

        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        public const uint SDC_VALIDATE = 0x40;
        public const uint SDC_APPLY = 0x80;
        public const uint SDC_SAVE_TO_DATABASE = 0x200;
        public const uint SDC_ALLOW_CHANGES = 0x400;

        public const uint DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint DEVICE_INFO_GET_ADAPTER_NAME = 4;

        public const uint MODE_IDX_INVALID = 0xFFFFFFFF;

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPaths,
            [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModes,
            [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint numPaths, DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModes, DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int DisplayConfigGetDeviceInfo_Target(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int DisplayConfigGetDeviceInfo_Adapter(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);
    }

    class ProfileData
    {
        public DISPLAYCONFIG_PATH_INFO[] Paths;
        public DISPLAYCONFIG_MODE_INFO[] Modes;
        public Dictionary<long, string> Adapters;
        public List<string[]> Targets; // per path: { monitorDevicePath, friendlyName }
    }

    class DisplayEntry
    {
        public string Key;
        public string Name;
        public bool HasSource;
        public uint Width;
        public uint Height;
        public int X;
        public int Y;
        public double Hz;
        public bool Primary { get { return HasSource && X == 0 && Y == 0; } }
    }

    class Program
    {
        const int FILE_MAGIC = 0x4D4F4E50; // "PNOM"
        const int FILE_VERSION = 1;

        static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        static string ProfileDir { get { return Path.Combine(BaseDir, "profiles"); } }

        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { Usage(); return 1; }
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "status") return Status();
                if (cmd == "list") return ListProfiles();
                if (cmd == "current") return Current();
                if (args.Length >= 2)
                {
                    if (!ValidName(args[1]))
                    {
                        Console.Error.WriteLine("Profile names may only contain letters, digits, '_' and '-'.");
                        return 1;
                    }
                    if (cmd == "save") return Save(args[1]);
                    if (cmd == "load") return Load(args[1], true);
                    if (cmd == "validate") return Load(args[1], false);
                    if (cmd == "show") return Show(args[1]);
                }
                Usage();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.Message);
                return 10;
            }
        }

        static void Usage()
        {
            Console.WriteLine("Usage: MonitorProfile <save|load|validate|show> <name> | current | status | list");
        }

        static bool ValidName(string name)
        {
            return Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,40}$");
        }

        // ---------- native helpers ----------

        static void Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            uint np, nm;
            int err = Native.GetDisplayConfigBufferSizes(flags, out np, out nm);
            if (err != 0) throw new Exception("GetDisplayConfigBufferSizes failed: " + WinErr(err));
            paths = new DISPLAYCONFIG_PATH_INFO[np];
            modes = new DISPLAYCONFIG_MODE_INFO[nm];
            err = Native.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (err != 0) throw new Exception("QueryDisplayConfig failed: " + WinErr(err));
            if (np != paths.Length) Array.Resize(ref paths, (int)np);
            if (nm != modes.Length) Array.Resize(ref modes, (int)nm);
        }

        static bool GetTargetName(LUID adapter, uint targetId, out string friendly, out string devicePath)
        {
            DISPLAYCONFIG_TARGET_DEVICE_NAME req = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            req.header.type = Native.DEVICE_INFO_GET_TARGET_NAME;
            req.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
            req.header.adapterId = adapter;
            req.header.id = targetId;
            int err = Native.DisplayConfigGetDeviceInfo_Target(ref req);
            friendly = req.monitorFriendlyDeviceName ?? "";
            devicePath = req.monitorDevicePath ?? "";
            return err == 0;
        }

        static bool GetAdapterPath(LUID adapter, out string path)
        {
            DISPLAYCONFIG_ADAPTER_NAME req = new DISPLAYCONFIG_ADAPTER_NAME();
            req.header.type = Native.DEVICE_INFO_GET_ADAPTER_NAME;
            req.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_ADAPTER_NAME));
            req.header.adapterId = adapter;
            req.header.id = 0;
            int err = Native.DisplayConfigGetDeviceInfo_Adapter(ref req);
            path = req.adapterDevicePath ?? "";
            return err == 0;
        }

        static string WinErr(int code)
        {
            return code + " (" + new Win32Exception(code).Message + ")";
        }

        static List<string[]> CurrentTargets(DISPLAYCONFIG_PATH_INFO[] paths)
        {
            List<string[]> targets = new List<string[]>();
            foreach (DISPLAYCONFIG_PATH_INFO p in paths)
            {
                string f, d;
                GetTargetName(p.targetInfo.adapterId, p.targetInfo.id, out f, out d);
                targets.Add(new string[] { d, f });
            }
            return targets;
        }

        // ---------- layout comparison ----------

        static List<DisplayEntry> BuildEntries(DISPLAYCONFIG_PATH_INFO[] paths,
            DISPLAYCONFIG_MODE_INFO[] modes, List<string[]> targets)
        {
            List<DisplayEntry> list = new List<DisplayEntry>();
            for (int i = 0; i < paths.Length; i++)
            {
                DisplayEntry e = new DisplayEntry();
                string devPath = i < targets.Count ? targets[i][0] : "";
                e.Name = i < targets.Count && targets[i][1].Length > 0 ? targets[i][1] : "(unnamed display)";
                e.Key = devPath.Length > 0 ? devPath.ToLowerInvariant() : "name:" + e.Name.ToLowerInvariant();

                uint sIdx = paths[i].sourceInfo.modeInfoIdx;
                if (sIdx != Native.MODE_IDX_INVALID && sIdx < modes.Length)
                {
                    DISPLAYCONFIG_SOURCE_MODE sm = modes[sIdx].sourceMode;
                    e.HasSource = true;
                    e.Width = sm.width;
                    e.Height = sm.height;
                    e.X = sm.position.x;
                    e.Y = sm.position.y;
                }

                uint tIdx = paths[i].targetInfo.modeInfoIdx;
                if (tIdx != Native.MODE_IDX_INVALID && tIdx < modes.Length)
                {
                    DISPLAYCONFIG_RATIONAL v = modes[tIdx].targetMode.targetVideoSignalInfo.vSyncFreq;
                    if (v.Denominator != 0) e.Hz = (double)v.Numerator / v.Denominator;
                }
                if (e.Hz == 0 && paths[i].targetInfo.refreshRate.Denominator != 0)
                    e.Hz = (double)paths[i].targetInfo.refreshRate.Numerator / paths[i].targetInfo.refreshRate.Denominator;

                list.Add(e);
            }
            return list;
        }

        static bool SameLayout(List<DisplayEntry> actual, List<DisplayEntry> expected,
            out int positionMatches, out string why)
        {
            positionMatches = 0;
            why = "";
            if (actual.Count != expected.Count)
            {
                why = expected.Count + " display(s) expected, " + actual.Count + " active";
                return false;
            }
            Dictionary<string, DisplayEntry> byKey = new Dictionary<string, DisplayEntry>();
            foreach (DisplayEntry e in expected) byKey[e.Key] = e;
            foreach (DisplayEntry a in actual)
            {
                DisplayEntry x;
                if (!byKey.TryGetValue(a.Key, out x))
                {
                    why = a.Name + " is active but not part of this layout";
                    return false;
                }
                if (a.Width != x.Width || a.Height != x.Height)
                {
                    why = a.Name + " is " + a.Width + "x" + a.Height + ", expected " + x.Width + "x" + x.Height;
                    return false;
                }
                if (a.Hz > 0 && x.Hz > 0 && Math.Abs(a.Hz - x.Hz) > 1.0)
                {
                    why = a.Name + " is at " + FormatHz(a.Hz) + " Hz, expected " + FormatHz(x.Hz) + " Hz";
                    return false;
                }
                if (a.Primary != x.Primary)
                {
                    why = "a different display is primary";
                    return false;
                }
                if (a.X == x.X && a.Y == x.Y) positionMatches++;
            }
            return true;
        }

        static string FormatHz(double hz)
        {
            return hz.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static string Describe(DisplayEntry e)
        {
            string desc = e.Name;
            if (e.HasSource)
            {
                desc += "  " + e.Width + "x" + e.Height + " at (" + e.X + "," + e.Y + ")";
                if (e.Primary) desc += "  [PRIMARY]";
            }
            if (e.Hz > 0) desc += "  " + FormatHz(e.Hz) + " Hz";
            return desc;
        }

        static List<DisplayEntry> CurrentEntries()
        {
            DISPLAYCONFIG_PATH_INFO[] paths;
            DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
            return BuildEntries(paths, modes, CurrentTargets(paths));
        }

        static void PrintEntries(List<DisplayEntry> entries)
        {
            foreach (DisplayEntry e in entries) Console.WriteLine("  - " + Describe(e));
        }

        // ---------- struct (de)serialization ----------

        static void WriteStruct<T>(BinaryWriter bw, T value)
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] buf = new byte[size];
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { Marshal.StructureToPtr(value, h.AddrOfPinnedObject(), false); }
            finally { h.Free(); }
            bw.Write(buf);
        }

        static T ReadStruct<T>(BinaryReader br)
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] buf = br.ReadBytes(size);
            if (buf.Length != size) throw new Exception("Profile file is corrupt or truncated.");
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { return (T)Marshal.PtrToStructure(h.AddrOfPinnedObject(), typeof(T)); }
            finally { h.Free(); }
        }

        static string ProfilePath(string name)
        {
            return Path.Combine(ProfileDir, name + ".mon");
        }

        static ProfileData ReadProfile(string file)
        {
            ProfileData p = new ProfileData();
            p.Adapters = new Dictionary<long, string>();
            p.Targets = new List<string[]>();
            using (BinaryReader br = new BinaryReader(File.OpenRead(file)))
            {
                if (br.ReadInt32() != FILE_MAGIC) throw new Exception("Not a profile file: " + file);
                if (br.ReadInt32() != FILE_VERSION) throw new Exception("Unsupported profile version.");
                int nPaths = br.ReadInt32();
                int nModes = br.ReadInt32();
                int nAdapters = br.ReadInt32();
                for (int i = 0; i < nAdapters; i++)
                {
                    long key = br.ReadInt64();
                    p.Adapters[key] = br.ReadString();
                }
                int nTargets = br.ReadInt32();
                for (int i = 0; i < nTargets; i++)
                {
                    string d = br.ReadString();
                    string f = br.ReadString();
                    p.Targets.Add(new string[] { d, f });
                }
                p.Paths = new DISPLAYCONFIG_PATH_INFO[nPaths];
                p.Modes = new DISPLAYCONFIG_MODE_INFO[nModes];
                for (int i = 0; i < nPaths; i++) p.Paths[i] = ReadStruct<DISPLAYCONFIG_PATH_INFO>(br);
                for (int i = 0; i < nModes; i++) p.Modes[i] = ReadStruct<DISPLAYCONFIG_MODE_INFO>(br);
            }
            return p;
        }

        // ---------- commands ----------

        static int Save(string name)
        {
            DISPLAYCONFIG_PATH_INFO[] paths;
            DISPLAYCONFIG_MODE_INFO[] modes;
            Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);

            // Map every adapter LUID in the snapshot to its stable device path.
            Dictionary<long, LUID> luids = new Dictionary<long, LUID>();
            foreach (DISPLAYCONFIG_PATH_INFO p in paths)
            {
                luids[p.sourceInfo.adapterId.AsLong()] = p.sourceInfo.adapterId;
                luids[p.targetInfo.adapterId.AsLong()] = p.targetInfo.adapterId;
            }
            foreach (DISPLAYCONFIG_MODE_INFO m in modes)
                luids[m.adapterId.AsLong()] = m.adapterId;

            Dictionary<long, string> adapterPaths = new Dictionary<long, string>();
            foreach (KeyValuePair<long, LUID> kv in luids)
            {
                string ap;
                if (!GetAdapterPath(kv.Value, out ap) || ap.Length == 0)
                    throw new Exception("Could not resolve adapter device path.");
                adapterPaths[kv.Key] = ap;
            }

            List<string[]> targets = CurrentTargets(paths);

            Directory.CreateDirectory(ProfileDir);
            string file = ProfilePath(name);
            string tmp = file + ".tmp";
            using (BinaryWriter bw = new BinaryWriter(File.Create(tmp)))
            {
                bw.Write(FILE_MAGIC);
                bw.Write(FILE_VERSION);
                bw.Write(paths.Length);
                bw.Write(modes.Length);
                bw.Write(adapterPaths.Count);
                foreach (KeyValuePair<long, string> kv in adapterPaths)
                {
                    bw.Write(kv.Key);
                    bw.Write(kv.Value);
                }
                bw.Write(targets.Count);
                foreach (string[] t in targets) { bw.Write(t[0]); bw.Write(t[1]); }
                foreach (DISPLAYCONFIG_PATH_INFO p in paths) WriteStruct(bw, p);
                foreach (DISPLAYCONFIG_MODE_INFO m in modes) WriteStruct(bw, m);
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);

            Console.WriteLine("Saved profile '" + name + "' (" + paths.Length + " active display(s)):");
            PrintEntries(BuildEntries(paths, modes, targets));
            Console.WriteLine("File: " + file);
            return 0;
        }

        static int Load(string name, bool apply)
        {
            string file = ProfilePath(name);
            if (!File.Exists(file))
            {
                Console.Error.WriteLine("Profile not found: " + file);
                return 4;
            }

            ProfileData prof = ReadProfile(file);
            DISPLAYCONFIG_PATH_INFO[] paths = prof.Paths;
            DISPLAYCONFIG_MODE_INFO[] modes = prof.Modes;
            List<DisplayEntry> expected = BuildEntries(paths, modes, prof.Targets);

            // Adapter LUIDs change every boot; remap saved LUIDs to the current ones
            // by matching the stable adapter device path.
            DISPLAYCONFIG_PATH_INFO[] curPaths;
            DISPLAYCONFIG_MODE_INFO[] curModes;
            Query(Native.QDC_ALL_PATHS, out curPaths, out curModes);

            Dictionary<string, LUID> currentByPath = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            foreach (DISPLAYCONFIG_PATH_INFO p in curPaths)
            {
                CollectAdapter(currentByPath, seen, p.sourceInfo.adapterId);
                CollectAdapter(currentByPath, seen, p.targetInfo.adapterId);
            }

            Dictionary<long, LUID> remap = new Dictionary<long, LUID>();
            foreach (KeyValuePair<long, string> kv in prof.Adapters)
            {
                LUID cur;
                if (!currentByPath.TryGetValue(kv.Value, out cur))
                {
                    Console.Error.WriteLine("Adapter from profile is not present now: " + kv.Value);
                    return 5;
                }
                remap[kv.Key] = cur;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                paths[i].sourceInfo.adapterId = Remap(remap, paths[i].sourceInfo.adapterId);
                paths[i].targetInfo.adapterId = Remap(remap, paths[i].targetInfo.adapterId);
            }
            for (int i = 0; i < modes.Length; i++)
                modes[i].adapterId = Remap(remap, modes[i].adapterId);

            uint flags = Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES
                | (apply ? (Native.SDC_APPLY | Native.SDC_SAVE_TO_DATABASE) : Native.SDC_VALIDATE);

            int err = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
            if (err != 0)
            {
                Console.Error.WriteLine((apply ? "Apply" : "Validate") + " failed: " + WinErr(err));
                return 2;
            }

            if (!apply)
            {
                Console.WriteLine("Profile '" + name + "' validated OK - Windows accepts this configuration.");
                return 0;
            }

            // Verify the full layout (monitors, resolution, refresh, primary), giving the
            // driver a few seconds to settle before calling it a mismatch.
            List<DisplayEntry> now = null;
            string why = "";
            bool match = false;
            for (int attempt = 0; attempt < 5 && !match; attempt++)
            {
                System.Threading.Thread.Sleep(attempt == 0 ? 1500 : 1000);
                now = CurrentEntries();
                int pos;
                match = SameLayout(now, expected, out pos, out why);
            }

            Console.WriteLine("Applied profile '" + name + "'. Active displays now:");
            PrintEntries(now);
            if (!match)
            {
                Console.Error.WriteLine("WARNING: layout does not match the profile: " + why);
                return 3;
            }
            Console.WriteLine("Verified: active layout matches the profile.");
            return 0;
        }

        static void CollectAdapter(Dictionary<string, LUID> byPath, Dictionary<long, bool> seen, LUID luid)
        {
            long key = luid.AsLong();
            if (seen.ContainsKey(key)) return;
            seen[key] = true;
            string p;
            if (GetAdapterPath(luid, out p) && p.Length > 0 && !byPath.ContainsKey(p))
                byPath[p] = luid;
        }

        static LUID Remap(Dictionary<long, LUID> remap, LUID old)
        {
            LUID n;
            if (remap.TryGetValue(old.AsLong(), out n)) return n;
            throw new Exception("Internal error: adapter LUID missing from remap table.");
        }

        static int Show(string name)
        {
            string file = ProfilePath(name);
            if (!File.Exists(file))
            {
                Console.Error.WriteLine("Profile not found: " + file);
                return 4;
            }
            ProfileData p = ReadProfile(file);
            List<DisplayEntry> entries = BuildEntries(p.Paths, p.Modes, p.Targets);
            Console.WriteLine("Profile '" + name + "' (saved " + File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm")
                + ", " + entries.Count + " display(s)):");
            PrintEntries(entries);
            return 0;
        }

        static int Current()
        {
            List<DisplayEntry> cur = CurrentEntries();
            string best = null;
            int bestScore = -1;
            if (Directory.Exists(ProfileDir))
            {
                string[] files = Directory.GetFiles(ProfileDir, "*.mon");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name.StartsWith("_")) continue;
                    ProfileData p;
                    try { p = ReadProfile(f); }
                    catch { continue; }
                    int score;
                    string why;
                    if (SameLayout(cur, BuildEntries(p.Paths, p.Modes, p.Targets), out score, out why)
                        && score > bestScore)
                    {
                        best = name;
                        bestScore = score;
                    }
                }
            }
            Console.WriteLine(best != null ? "MATCH " + best : "NONE");
            Console.WriteLine("Active displays (" + cur.Count + "):");
            PrintEntries(cur);
            return 0;
        }

        static int Status()
        {
            List<DisplayEntry> cur = CurrentEntries();
            Console.WriteLine("Active displays (" + cur.Count + "):");
            PrintEntries(cur);
            return 0;
        }

        static int ListProfiles()
        {
            List<string> names = new List<string>();
            if (Directory.Exists(ProfileDir))
                foreach (string f in Directory.GetFiles(ProfileDir, "*.mon"))
                    if (!Path.GetFileName(f).StartsWith("_")) names.Add(f);
            if (names.Count == 0) { Console.WriteLine("No profiles saved yet."); return 0; }
            Console.WriteLine("Saved profiles:");
            foreach (string f in names)
                Console.WriteLine("  - " + Path.GetFileNameWithoutExtension(f) +
                    "  (saved " + File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm") + ")");
            return 0;
        }
    }
}
