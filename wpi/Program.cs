using System;
using System.Collections.Generic;


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
            List<string> devicePaths = USB.FindDevicePathsFromGuid(guidApolloDeviceInterface);
            string devicePath;
            if (devicePaths.Count == 0)
            {
                Console.WriteLine("No device found.");
                ProgramExit(-1);
            }
            else if (devicePaths.Count > 1)
            {
                Console.WriteLine("{0} devices found. Only the first will be used.", devicePaths.Count);
                
            }
            devicePath = devicePaths[0];
            Console.WriteLine("Path of the device found: {0}", devicePath);

            if (devicePath.IndexOf("VID_0421&PID_0661", StringComparison.OrdinalIgnoreCase) == 0)
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
            USBDevice ApolloDeviceInterface = new USBDevice(devicePath);

            // Send command to reboot in flash mode
            string Request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            Console.WriteLine("Send the following command to the interface (could take some time before the phone reboots in \"flash\" mode):\n{0}", Request);
            byte[] OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            ApolloDeviceInterface.OutputPipe.Write(OutBuffer, 0, OutBuffer.Length);

            byte[] Buffer = new byte[0x8000];
            int readLength = ApolloDeviceInterface.InputPipe.Read(Buffer);
            string resultString = System.Text.ASCIIEncoding.ASCII.GetString(Buffer, 0, readLength);
            Console.WriteLine("Result: {0}", resultString);

            Console.WriteLine("Press [Enter] when the phone displays a big \"NOKIA\" in the top half part of the screen ( = \"flash\" mode ).");
            Console.ReadLine();
            ApolloDeviceInterface.Close();

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
            devicePaths = USB.FindDevicePathsFromGuid(guidCareConnectivityDeviceInterface);
            if (devicePaths.Count == 0)
            {
                Console.WriteLine("No device found.");
                ProgramExit(-3);
            }
            else if (devicePaths.Count > 1)
            {
                Console.WriteLine("{0} devices found. Only the first will be used.", devicePaths.Count);

            }
            devicePath = devicePaths[0];
            Console.WriteLine("Path of the device found: {0}", devicePath);

            if (devicePath.IndexOf("VID_0421&PID_066E", StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-4);
            }
            // Open the interface
            USBDevice CareConnectivityDeviceInterface = new USBDevice(devicePath);

            Console.WriteLine("Read Flash application version (require 1.28 <= version < 2.0)");
            byte[] ReadVersionCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x56 }; // NOKV = Info Query
            Console.WriteLine("Send the following command to the interface:\nNOKV");
            CareConnectivityDeviceInterface.OutputPipe.Write(ReadVersionCommand, 0, ReadVersionCommand.Length);
            int ReadLength = CareConnectivityDeviceInterface.InputPipe.Read(Buffer);
            Console.WriteLine("Result:");
            CareConnectivity.parseNOKV(Buffer, ReadLength);
      
            Console.WriteLine("Press [Enter] to switch to \"normal\" mode.");
            Console.ReadLine();

            // Send the "normal command" NOKR (reboot) to the phone
            // It will reboot in "normal" mode.
            byte[] RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            Console.WriteLine("Send the following command to the interface:\nNOKR");
            CareConnectivityDeviceInterface.OutputPipe.Write(RebootCommand, 0, RebootCommand.Length);

            ProgramExit(0);
        }

        private static void ProgramExit(int exitCode)
        {
            Console.ReadLine();
            Environment.Exit(exitCode);
        }

    }
}
