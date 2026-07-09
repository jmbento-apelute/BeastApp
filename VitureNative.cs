internal static partial class VitureUsbDeviceFinder
{
    private const int VitureVendorId = 0x35CA;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpHardwareId = 0x00000001;
    private static readonly Regex UsbIdPattern = UsbHardwareIdRegex();

    public static int? FindFirstVitureGlassesPid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Viture USB detection is only implemented for Windows.");
        }

        var deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DigcfAllClasses | DigcfPresent);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var deviceInfoData = new SpDevInfoData { CbSize = Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData); index++)
            {
                foreach (var hardwareId in GetHardwareIds(deviceInfoSet, ref deviceInfoData))
                {
                    var match = UsbIdPattern.Match(hardwareId);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var vendorId = Convert.ToInt32(match.Groups["vid"].Value, 16);
                    var productId = Convert.ToInt32(match.Groups["pid"].Value, 16);
                    if (vendorId == VitureVendorId && VitureNative.xr_device_provider_is_product_id_valid(productId) == 1)
                    {
                        return productId;
                    }
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return null;
    }

    private static IReadOnlyList<string> GetHardwareIds(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceInfoSet,
                ref deviceInfoData,
                SpdrpHardwareId,
                out _,
                buffer,
                (uint)buffer.Length,
                out _))
        {
            return Array.Empty<string>();
        }

        var multiString = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return multiString.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [GeneratedRegex("VID_(?<vid>[0-9A-Fa-f]{4}).*PID_(?<pid>[0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbHardwareIdRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

internal static class NativeLibraryLoader
{
    [DllImport("kernel32.dll", EntryPoint = "AddDllDirectory", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectoryNative(string newDirectory);

    private static int loaded;

    public static void AddDllDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Viture SDK x86_64 directory was not found: {directory}");
        }

        if (Interlocked.Exchange(ref loaded, 1) == 0 && AddDllDirectoryNative(directory) == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Unable to add native DLL directory: {directory}");
        }
    }
}

internal static class VitureNative
{
    public const int VITURE_GLASSES_SUCCESS = 0;
    public const int XR_CAMERA_FORMAT_MJPEG = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void XRCameraFrameCallback(IntPtr frame, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    public struct XRCameraFrame
    {
        public IntPtr Data;
        public uint Size;
        public uint Width;
        public uint Height;
        public int Format;
        public ulong Timestamp;
        public uint Sequence;
    }

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern int xr_device_provider_is_product_id_valid(int productId);

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern int xr_camera_provider_get_camera_vid(int glassesProductId);

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern int xr_camera_provider_get_camera_pid(int glassesProductId);

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr xr_camera_provider_create(int cameraVid, int cameraPid);

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern int xr_camera_provider_start(
        IntPtr handle,
        XRCameraFrameCallback callback,
        IntPtr userData);

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern int xr_camera_provider_stop(IntPtr handle);

    [DllImport("glasses", CallingConvention = CallingConvention.Cdecl)]
    public static extern void xr_camera_provider_destroy(IntPtr handle);
}
