// ArsMemory.cs - one place that resolves and caches every raw CVehicle / CWheel / CHandlingData
// offset ARS uses, and does every raw read/write with null/range checks.
//
// Resolution order for CVehicle offsets (each field resolved at most once, failure cached):
//   1. SHVDN's own, already-scanned offsets: public static properties of SHVDN.NativeMemory+Vehicle
//      in the loaded "ScriptHookVDotNet" core assembly (read by reflection, so ARS does not bind to a
//      particular SHVDN build). These are the offsets SHVDN's own Vehicle.SteeringScale /
//      SteeringAngle / HandlingData / Wheels use, maintained by SHVDN for current game builds.
//   2. If SHVDN does not provide a field: one scan of the main module with the same pattern SHVDN uses.
//   3. MemoryOffsets.ini values: used only when (1)+(2) failed, or when PreferFileOffsets=1, and only
//      if they pass the sanity check. A rejected value is logged and never written to memory.
//
// This file has no dependency on the SHVDN API so it can be tested against a stub (see tests/).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace ARS
{
    public static unsafe class ArsMemory
    {
        // ---------- logging hooks (set by ARS) ----------
        public static Action<string> LogInfo = s => { };
        public static Action<string> LogError = s => { };

        // ---------- sanity limits ----------
        public const int MinVehicleOffset = 0x100;
        public const int MaxVehicleOffset = 0x4000;   // CVehicle is well under 16 KB on every known build
        public const int MaxWheels = 16;              // GTA V vehicles have at most 10 wheels
        public const int MinWheelFieldOffset = 0x100;
        public const int MaxWheelFieldOffset = 0x400;

        // ---------- CVehicle offsets (0 = not available) ----------
        public static int SteerInput { get; private set; }     // SHVDN: SteeringScaleOffset      (ARS: steer input)
        public static int SteerAngle { get; private set; }     // SHVDN: SteeringAngleOffset      (= input + 8)
        public static int Throttle { get; private set; }       // SHVDN: ThrottlePowerOffset      (= input + 0x10)
        public static int Brake { get; private set; }          // SHVDN: BrakePowerOffset         (= input + 0x14)
        public static int WheelCount { get; private set; }     // SHVDN: WheelCountOffset
        public static int WheelPtrArray { get; private set; }  // SHVDN: WheelPtrArrayOffset      (= count - 8)
        public static int HandlingData { get; private set; }   // SHVDN: HandlingDataOffset

        public static string SteerSource { get; private set; } = "none";
        public static string WheelSource { get; private set; } = "none";
        public static string HandlingSource { get; private set; } = "none";

        // ---------- CWheel field offsets (ARS's original hard-coded values) ----------
        public const int OrigWheelSpin = 0x174;  // ARS GetWheelsMaxWheelspin / AvgWheelspin
        public const int OrigTyreGrip = 0x198;   // ARS GetWheelsGrip  (TYRE_GRIP, b2245 per ikt)
        public const int OrigWetGrip = 0x19C;    // ARS GetWheelsWetgrip
        public static int WheelSpinOffset { get; private set; } = OrigWheelSpin;
        public static int TyreGripOffset { get; private set; } = OrigTyreGrip;
        public static int WetGripOffset { get; private set; } = OrigWetGrip;

        // ---------- file overrides (MemoryOffsets.ini) ----------
        static string iniSteer, iniThrottle, iniBrake;
        static bool preferFile;
        static bool useScannedWheelOffsets;

        static bool resolved;
        static readonly object sync = new object();

        /// <summary>Region the fallback scanner searches. Default: the main module. Replaceable for tests.</summary>
        public static Func<KeyValuePair<IntPtr, long>> ScanRegion = () =>
        {
            ProcessModule m = Process.GetCurrentProcess().MainModule;
            return new KeyValuePair<IntPtr, long>(m.BaseAddress, m.ModuleMemorySize);
        };

        /// <summary>Assemblies searched for SHVDN.NativeMemory+Vehicle. Replaceable for tests.</summary>
        public static Func<Assembly[]> Assemblies = () => AppDomain.CurrentDomain.GetAssemblies();

        public static void SetFileOverrides(string steer, string throttle, string brake, bool preferFileOffsets, bool scanWheelOffsets)
        {
            iniSteer = steer; iniThrottle = throttle; iniBrake = brake;
            preferFile = preferFileOffsets;
            useScannedWheelOffsets = scanWheelOffsets;
        }

        /// <summary>For tests only: forget everything.</summary>
        public static void ResetForTests()
        {
            resolved = false;
            SteerInput = SteerAngle = Throttle = Brake = WheelCount = WheelPtrArray = HandlingData = 0;
            SteerSource = WheelSource = HandlingSource = "none";
            WheelSpinOffset = OrigWheelSpin; TyreGripOffset = OrigTyreGrip; WetGripOffset = OrigWetGrip;
            iniSteer = iniThrottle = iniBrake = null; preferFile = false; useScannedWheelOffsets = false;
            ScanCount = 0;
        }

        public static int ScanCount { get; private set; }

        // =====================================================================================
        // Resolution
        // =====================================================================================
        public static void EnsureResolved()
        {
            if (resolved) return;
            lock (sync)
            {
                if (resolved) return;
                resolved = true; // set first: whatever happens below, we never try again
                try { Resolve(); }
                catch (Exception e) { LogError("[MEMORY] offset resolution threw, raw memory access disabled: " + e.Message); }
            }
        }

        static void Resolve()
        {
            // ---- 1. SHVDN's own offsets ----
            Type t = FindShvdnVehicleType();
            int sScale = 0, sAngle = 0, sThrottle = 0, sBrake = 0, sCount = 0, sArray = 0, sHandling = 0;
            if (t != null)
            {
                sScale = GetStaticInt(t, "SteeringScaleOffset");
                sAngle = GetStaticInt(t, "SteeringAngleOffset");
                sThrottle = GetStaticInt(t, "ThrottlePowerOffset");
                sBrake = GetStaticInt(t, "BrakePowerOffset");
                sCount = GetStaticInt(t, "WheelCountOffset");
                sArray = GetStaticInt(t, "WheelPtrArrayOffset");
                sHandling = GetStaticInt(t, "HandlingDataOffset");
                LogInfo(string.Format("[MEMORY] SHVDN NativeMemory.Vehicle found ({0}): SteeringScale=0x{1:X} SteeringAngle=0x{2:X} ThrottlePower=0x{3:X} BrakePower=0x{4:X} WheelCount=0x{5:X} WheelPtrArray=0x{6:X} HandlingData=0x{7:X}",
                    t.Assembly.GetName().Name + " " + t.Assembly.GetName().Version, sScale, sAngle, sThrottle, sBrake, sCount, sArray, sHandling));
            }
            else LogInfo("[MEMORY] SHVDN NativeMemory.Vehicle not found; using own pattern scans.");

            // ---- steering / throttle / brake group ----
            if (ValidVeh(sScale) && ValidVeh(sAngle) && ValidVeh(sThrottle) && ValidVeh(sBrake))
                SetSteerGroup(sScale, sAngle, sThrottle, sBrake, "SHVDN");
            if (SteerInput == 0)
            {
                // Same pattern as SHVDN NativeMemory.cs: the register byte (offset 5) is a wildcard because
                // it changed from rbx (B3) to rdi (B7) in b3095. ARS's old mask required B3 -> the bug.
                byte* p = Scan("74 0A F3 0F 11 B3 1C 09 00 00 EB 25", "xxxxx?????xx");
                int baseOff = p == null ? 0 : *(int*)(p + 6);
                if (ValidVeh(baseOff) && ValidVeh(baseOff + 0x14))
                    SetSteerGroup(baseOff, baseOff + 8, baseOff + 0x10, baseOff + 0x14, "own scan");
                else
                    LogError("[MEMORY] steering/throttle/brake offset NOT found (SHVDN and own scan). " + (p == null ? "pattern not found" : "value 0x" + baseOff.ToString("X") + " failed sanity check"));
            }

            // ---- wheels ----
            if (ValidVeh(sCount) && ValidVeh(sArray))
            {
                WheelCount = sCount; WheelPtrArray = sArray; WheelSource = "SHVDN";
            }
            else
            {
                // SHVDN NativeMemory.cs WheelCountOffset pattern (movsxd rbx, [rcx+disp32]).
                byte* p = Scan("48 63 99 00 00 00 00 45 33 C0 45 8B D0 48 85 DB", "xxx????xxxxxxxxx");
                int c = p == null ? 0 : *(int*)(p + 3);
                if (ValidVeh(c) && ValidVeh(c - 8)) { WheelCount = c; WheelPtrArray = c - 8; WheelSource = "own scan"; }
                else LogError("[MEMORY] wheel count/array offset NOT found. " + (p == null ? "pattern not found" : "value 0x" + c.ToString("X") + " failed sanity check"));
            }

            // ---- handling ----
            if (ValidVeh(sHandling)) { HandlingData = sHandling; HandlingSource = "SHVDN"; }
            else
            {
                // Same pattern as ARS original and SHVDN, displacement at +0x16.
                byte* p = Scan("3C 03 0F 85 00 00 00 00 48 8B 41 20 48 8B 88", "xxxx????xxxxxxx");
                int h = p == null ? 0 : *(int*)(p + 0x16);
                if (ValidVeh(h)) { HandlingData = h; HandlingSource = "own scan"; }
                else LogError("[MEMORY] handling data offset NOT found. " + (p == null ? "pattern not found" : "value 0x" + h.ToString("X") + " failed sanity check"));
            }

            // ---- MemoryOffsets.ini ----
            ApplyFileOverride("Steer", iniSteer, SteerInput, v => { SteerInput = v; SteerAngle = v + 8; SteerSource = "MemoryOffsets.ini"; });
            ApplyFileOverride("Throttle", iniThrottle, Throttle, v => { Throttle = v; });
            ApplyFileOverride("Brake", iniBrake, Brake, v => { Brake = v; });

            // ---- CWheel field offsets (flagged, default off) ----
            if (useScannedWheelOffsets) ResolveWheelFields();

            LogInfo(string.Format("[MEMORY] Using: SteerInput=0x{0:X} SteerAngle=0x{1:X} Throttle=0x{2:X} Brake=0x{3:X} [{4}] | WheelCount=0x{5:X} WheelPtrArray=0x{6:X} [{7}] | HandlingData=0x{8:X} [{9}] | CWheel spin=0x{10:X} grip=0x{11:X} wetgrip=0x{12:X}",
                SteerInput, SteerAngle, Throttle, Brake, SteerSource, WheelCount, WheelPtrArray, WheelSource, HandlingData, HandlingSource, WheelSpinOffset, TyreGripOffset, WetGripOffset));
        }

        static void SetSteerGroup(int input, int angle, int thr, int brk, string src)
        {
            // The four fields are contiguous in CVehicle; refuse a set that breaks the known layout.
            if (angle != input + 8 || thr != input + 0x10 || brk != input + 0x14)
            {
                LogError(string.Format("[MEMORY] {0} steering group layout unexpected (0x{1:X},0x{2:X},0x{3:X},0x{4:X}); not used.", src, input, angle, thr, brk));
                return;
            }
            SteerInput = input; SteerAngle = angle; Throttle = thr; Brake = brk; SteerSource = src;
        }

        static void ApplyFileOverride(string key, string raw, int current, Action<int> apply)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            ulong parsed;
            if (!TryParseOffset(raw, out parsed))
            {
                LogError("[MEMORY] MemoryOffsets.ini " + key + "=" + raw + " is not a number; ignored.");
                return;
            }
            if (parsed == 0) return; // 0 = "let ARS find it" (original meaning)
            bool sane = parsed % 4 == 0 && parsed >= MinVehicleOffset && parsed < MaxVehicleOffset;
            if (current != 0 && !preferFile)
            {
                if ((ulong)current != parsed)
                    LogInfo(string.Format("[MEMORY] MemoryOffsets.ini {0}={1} ignored: auto-resolved offset 0x{2:X} is used (set PreferFileOffsets=1 to force the file value).", key, raw, current));
                return;
            }
            if (!sane)
            {
                LogError(string.Format("[MEMORY] MemoryOffsets.ini {0}={1} (=0x{2:X}) rejected: must be 4-byte aligned and within 0x{3:X}..0x{4:X}.", key, raw, parsed, MinVehicleOffset, MaxVehicleOffset));
                return;
            }
            LogInfo(string.Format("[MEMORY] MemoryOffsets.ini {0}=0x{1:X} used ({2}).", key, parsed, current == 0 ? "auto-resolve failed" : "PreferFileOffsets=1, auto was 0x" + current.ToString("X")));
            apply((int)parsed);
        }

        /// <summary>Accepts "0x1A2B" (hex) or "1234" (decimal). The original ScriptSettings.GetValue&lt;ulong&gt;
        /// silently turned any "0x..." value into 0 (Convert.ChangeType throws FormatException, caught).</summary>
        public static bool TryParseOffset(string raw, out ulong value)
        {
            value = 0;
            if (raw == null) return false;
            string s = raw.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(s.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        static void ResolveWheelFields()
        {
            // Patterns from ikt's GTAVManualTransmission VehicleExtensions.cpp (tested by ikt on b2245):
            //   TYRE_GRIP:  "89 8B ? ? 00 00 E8 ? ? ? ? 0F 57 ?"  -> *(int*)(p+2)
            //   suspension compression: "45 0F 57 C9 F3 0F 11 83 60 01 00 00 F3 0F 5C" xxx?xxx???xxxxx -> *(int*)(p+8)
            // ARS's 0x174 = compression(0x160 on the build those were written for) + 0x14 (inference).
            byte* g = Scan("89 8B 00 00 00 00 E8 00 00 00 00 0F 57 00", "xx??xxx????xx?");
            byte* c = Scan("45 0F 57 C9 F3 0F 11 83 60 01 00 00 F3 0F 5C", "xxx?xxx???xxxxx");
            int grip = g == null ? 0 : *(int*)(g + 2);
            int spin = c == null ? 0 : *(int*)(c + 8) + 0x14;
            bool ok = grip >= MinWheelFieldOffset && grip < MaxWheelFieldOffset && grip % 4 == 0
                   && spin >= MinWheelFieldOffset && spin < MaxWheelFieldOffset && spin % 4 == 0
                   && grip - spin == OrigTyreGrip - OrigWheelSpin;
            if (ok)
            {
                TyreGripOffset = grip; WetGripOffset = grip + 4; WheelSpinOffset = spin;
                LogInfo(string.Format("[MEMORY] UseScannedWheelOffsets=1: CWheel spin=0x{0:X} grip=0x{1:X} (original 0x{2:X}/0x{3:X}).", spin, grip, OrigWheelSpin, OrigTyreGrip));
            }
            else
                LogError(string.Format("[MEMORY] UseScannedWheelOffsets=1 but scan gave spin=0x{0:X} grip=0x{1:X} (inconsistent or not found); keeping originals 0x{2:X}/0x{3:X}.", spin, grip, OrigWheelSpin, OrigTyreGrip));
        }

        static bool ValidVeh(int off) { return off >= MinVehicleOffset && off < MaxVehicleOffset; }

        static Type FindShvdnVehicleType()
        {
            try
            {
                foreach (Assembly a in Assemblies())
                {
                    Type t = null;
                    try { t = a.GetType("SHVDN.NativeMemory+Vehicle", false); } catch { }
                    if (t != null) return t;
                }
            }
            catch (Exception e) { LogError("[MEMORY] assembly enumeration failed: " + e.Message); }
            return null;
        }

        static int GetStaticInt(Type t, string name)
        {
            try
            {
                const BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo p = t.GetProperty(name, f);
                if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(null, null);
                FieldInfo fi = t.GetField(name, f);
                if (fi != null && fi.FieldType == typeof(int)) return (int)fi.GetValue(null);
            }
            catch (Exception e) { LogError("[MEMORY] reading SHVDN " + name + " failed: " + (e.InnerException ?? e).Message); }
            return 0;
        }

        // =====================================================================================
        // Pattern scanning (fallback only; each pattern is scanned at most once per session)
        // =====================================================================================
        static byte* Scan(string hex, string mask)
        {
            ScanCount++;
            string[] parts = hex.Split(' ');
            byte[] pat = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) pat[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (mask.Length != pat.Length) throw new ArgumentException("mask length");
            KeyValuePair<IntPtr, long> r = ScanRegion();
            return FindPattern((byte*)r.Key, r.Value, pat, mask);
        }

        public static byte* FindPattern(byte* start, long size, byte[] pat, string mask)
        {
            if (start == null || size < pat.Length) return null;
            int first = mask.IndexOf('x');
            if (first < 0) return start;
            byte fb = pat[first];
            byte* end = start + size - pat.Length;
            for (byte* p = start; p <= end; p++)
            {
                if (p[first] != fb) continue;
                int i = 0;
                for (; i < pat.Length; i++)
                    if (mask[i] == 'x' && p[i] != pat[i]) break;
                if (i == pat.Length) return p;
            }
            return null;
        }

        // =====================================================================================
        // Checked access
        // =====================================================================================
        static bool Ok(IntPtr veh, int off) { return veh != IntPtr.Zero && off > 0; }

        public static bool WriteVehFloat(IntPtr veh, int off, float value)
        {
            EnsureResolved();
            if (!Ok(veh, off)) return false;
            *(float*)((byte*)veh + off) = value;
            return true;
        }

        public static float ReadVehFloat(IntPtr veh, int off)
        {
            EnsureResolved();
            if (!Ok(veh, off)) return 0f;
            return *(float*)((byte*)veh + off);
        }

        public static int GetNumWheels(IntPtr veh)
        {
            EnsureResolved();
            if (!Ok(veh, WheelCount)) return 0;
            int n = *(int*)((byte*)veh + WheelCount);
            return (n < 0 || n > MaxWheels) ? 0 : n;
        }

        public static ulong GetWheelArray(IntPtr veh)
        {
            EnsureResolved();
            if (!Ok(veh, WheelPtrArray)) return 0;
            return *(ulong*)((byte*)veh + WheelPtrArray);
        }

        public static List<ulong> GetWheelPtrs(IntPtr veh)
        {
            List<ulong> list = new List<ulong>();
            ulong arr = GetWheelArray(veh);
            if (arr == 0) return list;
            int n = GetNumWheels(veh);
            for (int i = 0; i < n; i++)
            {
                ulong w = *(ulong*)(arr + 8UL * (ulong)i);
                if (w != 0) list.Add(w);
            }
            return list;
        }

        public static float ReadWheelFloat(ulong wheel, int off)
        {
            if (wheel == 0 || off <= 0) return 0f;
            return *(float*)(wheel + (ulong)off);
        }

        public static ulong GetHandlingPtr(IntPtr veh)
        {
            EnsureResolved();
            if (!Ok(veh, HandlingData)) return 0;
            return *(ulong*)((byte*)veh + HandlingData);
        }
    }
}
