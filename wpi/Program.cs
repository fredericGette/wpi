using System;
using System.Collections.Generic;
using System.Threading;

namespace wpi
{
    class Program
    {
        private static string GUID_APOLLO_DEVICE_INTERFACE = "{7EAFF726-34CC-4204-B09D-F95471B873CF}";
        private static string GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE = "{0FD3B15C-D457-45D8-A779-C2B2C9F9D0FD}";
        private static string VID_PID_NOKIA_LUMIA_NORMAL_MODE = "VID_0421&PID_0661";
        private static string VID_PID_NOKIA_LUMIA_UEFI_MODE = "VID_0421&PID_066E";

        static void Main(string[] args)
        {
            // Look for a phone connected on a USB port and exposing interface
            // - known as "Apollo" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "New Combi" interface in WPInternals
            // This interface allows to send jsonRPC (Remote Procedure Call) (to reboot the phone in flash mode for example).
            // Only a phone in "normal" mode exposes this interface. 
            Guid guidApolloDeviceInterface = new Guid(GUID_APOLLO_DEVICE_INTERFACE);
            Console.WriteLine("Look for a phone connected on a USB port");
            Console.WriteLine("and exposing \"Apollo\" device interface ( = \"normal\" mode )...\n");
            List<string> devicePaths = USB.FindDevicePathsFromGuid(guidApolloDeviceInterface);
            string devicePath;
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_NORMAL_MODE, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x0661 : Lumia 520 / 620 / 820 / 920 Normal mode
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x0661.)");
                ProgramExit(-2);
            }

            Console.WriteLine("\nPress [Enter] to switch to \"flash\" mode.");
            Console.ReadLine();

            // Open the interface
            // It contains 2 pipes :
            // - output: to send commands to the phone.
            // - input: to get the result of the command (if needed).
            USBDevice ApolloDeviceInterface = new USBDevice(devicePath);

            // Send command to reboot in flash mode
            string Request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            byte[] OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            ApolloDeviceInterface.OutputPipe.Write(OutBuffer, 0, OutBuffer.Length);

            byte[] Buffer = new byte[0x8000];
            int readLength = ApolloDeviceInterface.InputPipe.Read(Buffer);
            string resultString = System.Text.ASCIIEncoding.ASCII.GetString(Buffer, 0, readLength);
            ApolloDeviceInterface.Close();

            Console.WriteLine("\nYou may have to wait 15s before the phone reboots in \"flash\" mode.");
            Console.WriteLine("Press [Enter] when the phone displays a big \"NOKIA\"");
            Console.WriteLine("in the top part of the screen ( = \"flash\" mode ).");
            Console.ReadLine();

            // Look for a phone connected on a USB port and exposing interface
            // - known as "Care Connectivity" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "Old Combi" interface in WPInternals
            // This interface allows flash commands starting with the signature "NOK" (to reboot the phone for example).
            // Notes: 
            // this interface is also exposed when the phone is in "normal" mode.
            // But in "normal" mode the PID of the device is 0x0661
            // Whereas in "flash" or "bootloader" mode the PID of the device is 0x066E
            Guid guidCareConnectivityDeviceInterface = new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE);
            Console.WriteLine("Look for a phone connected on a USB port");
            Console.WriteLine("and exposing \"Care Connectivity\" device interface...\n");
            devicePaths = USB.FindDevicePathsFromGuid(guidCareConnectivityDeviceInterface);
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-3);
            }
            // Open the interface
            USBDevice CareConnectivityDeviceInterface = new USBDevice(devicePath);

            Console.WriteLine("\nRead Flash application version (require 1.28 <= version < 2.0)...");
            byte[] ReadVersionCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x56 }; // NOKV = Info Query
            CareConnectivityDeviceInterface.OutputPipe.Write(ReadVersionCommand, 0, ReadVersionCommand.Length);
            int ReadLength = CareConnectivityDeviceInterface.InputPipe.Read(Buffer);
            CareConnectivity.parseNOKV(Buffer, ReadLength);

            Console.WriteLine("\nRead eMMC manufacturer (Samsung = risk of locking eMMC in read-only mode)...");
            byte[] ReadEmmcCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x46, 0x52, 0x00, 0x43, 0x49, 0x44, 0x00 }; // NOKXFR\0CID\0 
            CareConnectivityDeviceInterface.OutputPipe.Write(ReadEmmcCommand, 0, ReadEmmcCommand.Length);
            ReadLength = CareConnectivityDeviceInterface.InputPipe.Read(Buffer);
            CareConnectivity.parseNOKXFRCID(Buffer, ReadLength);


            Console.WriteLine("\nPress [Enter] to switch to \"bootloader\" mode.");
            Console.ReadLine();

            // Send the "normal command" NOKR (reboot) to the phone
            // It will reboot in "bootloader" mode.
            byte[] RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.OutputPipe.Write(RebootCommand, 0, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.WriteLine("\nLook for a phone connected on a USB port");
            Console.Write("and exposing \"Care Connectivity\" device interface.");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(guidCareConnectivityDeviceInterface);
            } while (devicePaths.Count == 0);
            Console.WriteLine("");
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-3);
            }
            // Open the interface
            CareConnectivityDeviceInterface = new USBDevice(devicePath);

            Console.WriteLine("\nRead BootManager version...");
            ReadVersionCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x56 }; // NOKV = Info Query
            CareConnectivityDeviceInterface.OutputPipe.Write(ReadVersionCommand, 0, ReadVersionCommand.Length);
            ReadLength = CareConnectivityDeviceInterface.InputPipe.Read(Buffer);
            CareConnectivity.parseNOKV(Buffer, ReadLength);

            Console.WriteLine("\nRead GUID Partitions Table (GPT)...");
            byte[] ReadGPTCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x54 }; // NOKT = Read GPT
            CareConnectivityDeviceInterface.OutputPipe.Write(ReadGPTCommand, 0, ReadGPTCommand.Length);
            ReadLength = CareConnectivityDeviceInterface.InputPipe.Read(Buffer);
            CareConnectivity.parseNOKT(Buffer, ReadLength);

            // After a timeout, the phone automatically reboots to "normal" mode.

            ProgramExit(0);
        }

        private static void ProgramExit(int exitCode)
        {
            Console.ReadLine();
            Environment.Exit(exitCode);
        }

    }
}
