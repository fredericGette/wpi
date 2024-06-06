using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace wpi
{
    class Program
    {
        static void Main(string[] args)
        {
            // Look for a phone connected on a USB port and exposing interface
            // - known as "Apollo" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "New Combi" interface in WPInternals
            // This interface allows to send jsonRPC (Remote Procedure Call) (to reboot the phone in flash mode for example).
            // Only a phone in "normal" mode exposes this interface. 
            Guid guidApolloDeviceInterface = new Guid("{7EAFF726-34CC-4204-B09D-F95471B873CF}");
            Console.WriteLine("Look for a phone connected on a USB port and exposing \"Apollo\" device interface = phone in \"normal\" mode...");
            List<DeviceDetails> deviceDetails = FindDevicesFromGuid(guidApolloDeviceInterface);
            DeviceDetails device;
            if (deviceDetails.Count == 0)
            {
                Console.WriteLine("No device found.");
                ProgramExit(-1);
            }
            else if (deviceDetails.Count > 1)
            {
                Console.WriteLine("{0} devices found. Only the first will be used.", deviceDetails.Count);
                
            }
            device = deviceDetails[0];
            Console.WriteLine("Path of the device found: {0}", device.DevicePath);

            if (device.DevicePath.IndexOf("VID_0421&PID_0661", StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x0661 : Lumia 520 / 620 / 820 / 920 Normal mode
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x0661.)");
                ProgramExit(-2);
            }

            Console.WriteLine("Press [Enter] to switch to \"flash\" mode.");
            Console.ReadLine();

            // Open the interface
            // It contains 2 pipes :
            // - output: to send commands to the phone.
            // - input: to get the result of the command (if needed).
            USBDevice usbDevice = new USBDevice(device.DevicePath);

            string Request = "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            Console.WriteLine("Send the following command to the interface (could take some time before the phone reboots in \"flash\" mode):\n{0}", Request);
            byte[] OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            usbDevice.OutputPipe.Write(OutBuffer, 0, OutBuffer.Length);

            Console.WriteLine("Press [Enter] when the phone displays a big \"NOKIA\" ( = \"flash\" mode ).");
            Console.ReadLine();
            usbDevice.Close();

            // Look for a phone connected on a USB port and exposing interface
            // - known as "Care Connectivity" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "Old Combi" interface in WPInternals
            // This interface allows flash commands starting with the signature "NOK" (to reboot the phone for example).
            // Notes: 
            // this interface is also exposed when the phone is in "normal" mode.
            // But in "normal" mode the PID of the device is 0x0661
            // Whereas in "flash" mode the PID of the device is 0x066E
            Guid guidCareConnectivityDeviceInterface = new Guid("{0FD3B15C-D457-45D8-A779-C2B2C9F9D0FD}");
            Console.WriteLine("Look for a phone connected on a USB port and exposing \"Care Connectivity\" device interface...");
            deviceDetails = FindDevicesFromGuid(guidCareConnectivityDeviceInterface);
            if (deviceDetails.Count == 0)
            {
                Console.WriteLine("No device found.");
                ProgramExit(-3);
            }
            else if (deviceDetails.Count > 1)
            {
                Console.WriteLine("{0} devices found. Only the first will be used.", deviceDetails.Count);

            }
            device = deviceDetails[0];
            Console.WriteLine("Path of the device found: {0}", device.DevicePath);

            if (device.DevicePath.IndexOf("VID_0421&PID_066E", StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-4);
            }

            Console.WriteLine("Press [Enter] to switch to \"normal\" mode.");
            Console.ReadLine();

            // Send the "normal command" NOKR (reboot) to the phone
            // It will reboot in "normal" mode.
            usbDevice = new USBDevice(device.DevicePath);
            byte[] RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // normal command NOKR = Reboot
            Console.WriteLine("Send the following command to the interface:\nNOKR");
            usbDevice.OutputPipe.Write(RebootCommand, 0, RebootCommand.Length);

            ProgramExit(0);
        }

        private struct DeviceDetails
        {
            public string DevicePath;
            public string Manufacturer;
            public string DeviceDescription;
            public ushort VID;
            public ushort PID;

            // Heathcliff74
            public string BusName;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref System.Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, Int32 Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern Int32 SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, Int32 DeviceInterfaceDetailDataSize, ref Int32 RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, Int32 DeviceInterfaceDetailDataSize, ref Int32 RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref System.Guid InterfaceClassGuid, Int32 MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        // from setupapi.h
        private const Int32 DIGCF_PRESENT = 2;
        private const Int32 DIGCF_DEVICEINTERFACE = 0X10;
        private struct SP_DEVINFO_DATA
        {
            internal Int32 cbSize;
            internal System.Guid ClassGuid;
            internal Int32 DevInst;
            internal IntPtr Reserved;
        }
        private struct SP_DEVICE_INTERFACE_DATA
        {
            internal Int32 cbSize;
            internal System.Guid InterfaceClassGuid;
            internal Int32 Flags;
            internal IntPtr Reserved;
        }
        private const int ERROR_NO_MORE_ITEMS = 259;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        private static readonly IntPtr FILE_IO_INVALID_HANDLE_VALUE = new IntPtr(-1);

        private static List<DeviceDetails> FindDevicesFromGuid(Guid guid)
        {
            IntPtr deviceInfoSet = IntPtr.Zero;
            List<DeviceDetails> deviceList = new List<DeviceDetails>();
            try
            {
                deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                    DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (deviceInfoSet == FILE_IO_INVALID_HANDLE_VALUE)
                    throw new System.Exception("Failed to enumerate devices.");
                int memberIndex = 0;
                while (true)
                {
                    // Begin with 0 and increment through the device information set until
                    // no more devices are available.					
                    SP_DEVICE_INTERFACE_DATA deviceInterfaceData = new SP_DEVICE_INTERFACE_DATA();

                    // The cbSize element of the deviceInterfaceData structure must be set to
                    // the structure's size in bytes. 
                    // The size is 28 bytes for 32-bit code and 32 bytes for 64-bit code.
                    deviceInterfaceData.cbSize = Marshal.SizeOf(deviceInterfaceData);

                    bool success;

                    success = SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, memberIndex, ref deviceInterfaceData);

                    // Find out if a device information set was retrieved.
                    if (!success)
                    {
                        int lastError = Marshal.GetLastWin32Error();
                        if (lastError == ERROR_NO_MORE_ITEMS)
                            break;

                        throw new System.Exception("Failed to get device interface.");
                    }
                    // A device is present.

                    int bufferSize = 0;

                    success = SetupDiGetDeviceInterfaceDetail
                        (deviceInfoSet,
                        ref deviceInterfaceData,
                        IntPtr.Zero,
                        0,
                        ref bufferSize,
                        IntPtr.Zero);

                    if (!success)
                    {
                        if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                            throw new System.Exception("Failed to get interface details buffer size.");
                    }

                    IntPtr detailDataBuffer = IntPtr.Zero;
                    try
                    {

                        // Allocate memory for the SP_DEVICE_INTERFACE_DETAIL_DATA structure using the returned buffer size.
                        detailDataBuffer = Marshal.AllocHGlobal(bufferSize);

                        // Store cbSize in the first bytes of the array. The number of bytes varies with 32- and 64-bit systems.

                        Marshal.WriteInt32(detailDataBuffer, (IntPtr.Size == 4) ? (4 + Marshal.SystemDefaultCharSize) : 8);

                        // Call SetupDiGetDeviceInterfaceDetail again.
                        // This time, pass a pointer to DetailDataBuffer
                        // and the returned required buffer size.

                        // build a DevInfo Data structure
                        SP_DEVINFO_DATA da = new SP_DEVINFO_DATA();
                        da.cbSize = Marshal.SizeOf(da);


                        success = SetupDiGetDeviceInterfaceDetail
                            (deviceInfoSet,
                            ref deviceInterfaceData,
                            detailDataBuffer,
                            bufferSize,
                            ref bufferSize,
                            ref da);

                        if (!success)
                            throw new System.Exception("Failed to get device interface details.");


                        // Skip over cbsize (4 bytes) to get the address of the devicePathName.

                        IntPtr pDevicePathName = new IntPtr(detailDataBuffer.ToInt64() + 4);
                        string pathName = Marshal.PtrToStringUni(pDevicePathName);

                        DeviceDetails details = new DeviceDetails();
                        details.DevicePath = pathName;
                        
                        deviceList.Add(details);
                    }
                    finally
                    {
                        if (detailDataBuffer != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(detailDataBuffer);
                            detailDataBuffer = IntPtr.Zero;
                        }
                    }
                    memberIndex++;
                }
            }
            finally
            {
                if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != FILE_IO_INVALID_HANDLE_VALUE)
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }
            }
            return deviceList;
        }


        private static void ProgramExit(int exitCode)
        {
            Console.ReadLine();
            Environment.Exit(exitCode);
        }

    }
}
