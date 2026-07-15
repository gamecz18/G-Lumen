using System;
using System.Runtime.InteropServices;

namespace G_Lumen.Services
{
    /// <summary>
    /// Minimal DXGI interop to read the luminance a monitor reports in its EDID
    /// (IDXGIOutput6::GetDesc1 → MaxLuminance / MaxFullFrameLuminance).
    /// Used by the "Auto" button next to the HDR range in Settings.
    ///
    /// The interfaces are declared flat (no COM inheritance) with placeholder
    /// methods to keep the vtable slots aligned — only the methods we actually
    /// call have real signatures.
    /// </summary>
    internal static class DxgiNative
    {
        /// <summary>
        /// Reads the EDID luminance for the monitor with the given GDI name
        /// (\\.\DISPLAYx). fullFrame = sustained full-screen white,
        /// peak = small-area peak. Returns false when DXGI/monitor doesn't expose it.
        /// </summary>
        public static bool TryGetMonitorLuminance(string gdiDeviceName, out double fullFrame, out double peak)
        {
            fullFrame = 0;
            peak = 0;

            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out object factoryObj) != 0)
                    return false;

                var factory = (IDXGIFactory1)factoryObj;
                try
                {
                    for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter) == 0; a++)
                    {
                        try
                        {
                            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output) == 0; o++)
                            {
                                try
                                {
                                    if (output is IDXGIOutput6 output6
                                        && output6.GetDesc1(out var desc) == 0
                                        && string.Equals(desc.DeviceName, gdiDeviceName,
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        fullFrame = desc.MaxFullFrameLuminance;
                                        peak = desc.MaxLuminance;
                                        return fullFrame > 0 || peak > 0;
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(output);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch
            {
                // DXGI unavailable (remote session, ancient driver) — treat as "not detected".
            }
            return false;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppFactory);

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void _Slot0(); void _Slot1(); void _Slot2(); void _Slot3();   // IDXGIObject
            void _Slot4();                                                // EnumAdapters
            void _Slot5(); void _Slot6(); void _Slot7(); void _Slot8();   // MakeWindowAssociation … CreateSoftwareAdapter
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void _Slot0(); void _Slot1(); void _Slot2(); void _Slot3();   // IDXGIObject
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
        }

        /// <summary>Only used as a handle to QI to <see cref="IDXGIOutput6"/>.</summary>
        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
        }

        [ComImport, Guid("068346e8-aaec-4b84-add7-137f513f77a1"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput6
        {
            void _Slot0(); void _Slot1(); void _Slot2(); void _Slot3();       // IDXGIObject
            void _Slot4(); void _Slot5(); void _Slot6(); void _Slot7();       // IDXGIOutput: GetDesc … WaitForVBlank
            void _Slot8(); void _Slot9(); void _Slot10(); void _Slot11();     //   TakeOwnership … SetGammaControl
            void _Slot12(); void _Slot13(); void _Slot14(); void _Slot15();   //   GetGammaControl … GetFrameStatistics
            void _Slot16(); void _Slot17(); void _Slot18(); void _Slot19();   // IDXGIOutput1
            void _Slot20();                                                   // IDXGIOutput2: SupportsOverlays
            void _Slot21();                                                   // IDXGIOutput3: CheckOverlaySupport
            void _Slot22();                                                   // IDXGIOutput4: CheckOverlayColorSpaceSupport
            void _Slot23();                                                   // IDXGIOutput5: DuplicateOutput1
            [PreserveSig] int GetDesc1(out DXGI_OUTPUT_DESC1 desc);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;                       // \\.\DISPLAYx
            public int Left, Top, Right, Bottom;            // DesktopCoordinates (RECT)
            public int AttachedToDesktop;                   // BOOL
            public uint Rotation;
            public IntPtr Monitor;                          // HMONITOR
            public uint BitsPerColor;
            public uint ColorSpace;
            public float RedPrimaryX, RedPrimaryY;
            public float GreenPrimaryX, GreenPrimaryY;
            public float BluePrimaryX, BluePrimaryY;
            public float WhitePointX, WhitePointY;
            public float MinLuminance;
            public float MaxLuminance;                      // small-area peak (nits)
            public float MaxFullFrameLuminance;             // sustained full-frame (nits)
        }
    }
}
