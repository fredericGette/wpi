using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

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
            // We need a signed FFU (Full Flash Update).
            Console.Write("\nInput path of FFU file:");
            string ffuPath = Console.ReadLine();
            // Check the validity of the FFU file
            if (!FFU.checkFile(ffuPath))
            {
                ProgramExit(-5);
            }
            FFU ffu = new FFU(ffuPath);

            // Get Root Hash Key (RHK) contained in SBL1 for later check.
            byte[] ffuSBL1 = ffu.GetPartition("SBL1");
            byte[] ffuRKH = Qualcomm.parseSBL1(ffuSBL1);
            if (ffuRKH == null)
            {
                Console.WriteLine("Unable to extract Root Key Hash (RKH) from the SBL1 partition of the FFU file.");
                ProgramExit(-5);
            }

            // Prepare a patched SBL2 that will be flashed in the phone
            // Replace 0x28, 0x00, 0xD0, 0xE5 : ldrb r0, [r0, #0x28]
            // By      0x00, 0x00, 0xA0, 0xE3 : mov r0, #0
            byte[] ffuSBL2 = ffu.GetPartition("SBL2");
            byte[] patternToPatch = new byte[] { 0xE3, 0x01, 0x0E, 0x42, 0xE3, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            int patternPosition = -1;
            for (int i=0; i<ffuSBL2.Length; i++)
            {
                if (ffuSBL2.Skip(i).Take(patternToPatch.Length).SequenceEqual(patternToPatch))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern to patch in the SBL2 partition of the FFU file.");
                ProgramExit(-5);
            }
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3 }, 0, ffuSBL2, patternPosition + 5, 4);

            // Prepare the content of the "HACK" partition that is going to replace the last sector of the SBL1 partition.
            Console.WriteLine("\nGenerate the content of the \"HACK\" partition...");
            byte[] hackPartitionContent = Qualcomm.createHACK(ffuSBL1, ffuSBL2);

            // We need a "engeeniring" SBL3 to enable "Mass Storage" mode (it will be required to patch windows files)
            Console.Write("\nInput path of the raw image of an engeeniring SBL3:");
            string engeeniringSBL3Path = Console.ReadLine();
            Console.WriteLine("Processing...");
            byte[] engeeniringSBL3 = Qualcomm.loadSBL3img(engeeniringSBL3Path);
            if (engeeniringSBL3 == null)
            {
                Console.WriteLine("Unable to read the raw image of an engeeniring SBL3.");
                ProgramExit(-5);
            }

            // Check the size of the "engeeniring" SBL3
            byte[] ffuSBL3 = ffu.GetPartition("SBL3");
            if (engeeniringSBL3.Length > ffuSBL3.Length)
            {
                Console.WriteLine("The engeeniring SBL3 is too large ({0} bytes instead of {1} bytes).", engeeniringSBL3.Length, ffuSBL3.Length);
                ProgramExit(-5);
            }

            // Prepare a patched "engeeniring" SBL3 that will be flashed in the phone
            // Replace 0x28, 0x00, 0xD0, 0xE5 : ldrb r0, [r0, #0x28]
            // By      0x00, 0x00, 0xA0, 0xE3 : mov r0, #0
            patternToPatch = new byte[] { 0x04, 0x00, 0x9F, 0xE5, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            patternPosition = -1;
            for (int i = 0; i < engeeniringSBL3.Length; i++)
            {
                if (engeeniringSBL3.Skip(i).Take(patternToPatch.Length).SequenceEqual(patternToPatch))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern to patch in the engeeniring SBL3 partition.");
                ProgramExit(-5);
            }
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3 }, 0, engeeniringSBL3, patternPosition + 4, 4); 

            // Look for a phone connected on a USB port and exposing interface
            // - known as "Apollo" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "New Combi" interface in WPInternals
            // This interface allows to send jsonRPC (Remote Procedure Call) (to reboot the phone in flash mode for example).
            // Only a phone in "normal" mode exposes this interface. 
            Guid guidApolloDeviceInterface = new Guid(GUID_APOLLO_DEVICE_INTERFACE);
            Console.WriteLine("\nLook for a phone connected on a USB port");
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
            USB ApolloDeviceInterface = new USB(devicePath);

            // Send command to reboot in flash mode
            string Request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            byte[] OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            ApolloDeviceInterface.WritePipe(OutBuffer, OutBuffer.Length);

            uint bytesRead;
            byte[] Buffer = new byte[0x8000]; // Must be large enough to contain the GPT (see later)
            ApolloDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            string resultString = System.Text.ASCIIEncoding.ASCII.GetString(Buffer, 0, (int)bytesRead);
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
            Console.Write("and exposing \"Care Connectivity\" device interface.");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(guidCareConnectivityDeviceInterface);
            } while (devicePaths.Count == 0);
            Console.WriteLine("\n");
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
            USB CareConnectivityDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nRead Flash application version (require 1.28 <= version < 2.0)...");
            byte[] ReadVersionCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x56 }; // NOKV = Info Query
            CareConnectivityDeviceInterface.WritePipe(ReadVersionCommand, ReadVersionCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            CareConnectivity.parseNOKV(Buffer, (int)bytesRead);

            Console.WriteLine("\nRead eMMC manufacturer (Samsung = risk of locking eMMC in read-only mode)...");
            byte[] ReadEmmcCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x46, 0x52, 0x00, 0x43, 0x49, 0x44, 0x00 }; // NOKXFR\0CID\0 
            CareConnectivityDeviceInterface.WritePipe(ReadEmmcCommand, ReadEmmcCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            CareConnectivity.parseNOKXFRCID(Buffer, (int)bytesRead);

            // Check if the Root Key Hash (RKH) of the phone matches the one of the FFU (contained in partition SBL1)
            Console.WriteLine("\nRead Root Key Hash of the phone...");
            byte[] ReadRKHCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x46, 0x52, 0x00, 0x52, 0x52, 0x4B, 0x48 }; // NOKXFR\0RRKH 
            CareConnectivityDeviceInterface.WritePipe(ReadRKHCommand, ReadRKHCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            byte[] phoneRKH = CareConnectivity.parseNOKXFRRRKH(Buffer, (int)bytesRead);
            if (!ffuRKH.SequenceEqual(phoneRKH))
            {
                Console.WriteLine("The Root Key Hash (RKH) of the phone doesn't match the one of the FFU file.");
                ProgramExit(-6);
            }

            Console.WriteLine("\nPress [Enter] to switch to \"bootloader\" mode.");
            Console.ReadLine();

            // Send the "normal command" NOKR (reboot) to the phone
            // It will reboot in "bootloader" mode.
            // (then, after a timeout, the phone automatically continues to "normal" mode)
            byte[] RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.WriteLine("\nLook for a phone connected on a USB port");
            Console.Write("and exposing \"Care Connectivity\" device interface.");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(guidCareConnectivityDeviceInterface);
            } while (devicePaths.Count == 0);
            Console.WriteLine("\n");
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
            CareConnectivityDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nRead BootManager version...");
            ReadVersionCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x56 }; // NOKV = Info Query
            CareConnectivityDeviceInterface.WritePipe(ReadVersionCommand, ReadVersionCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            CareConnectivity.parseNOKV(Buffer, (int)bytesRead);

            Console.WriteLine("\nRead GUID Partition Table (GPT)...");
            byte[] ReadGPTCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x54 }; // NOKT = Read GPT
            CareConnectivityDeviceInterface.WritePipe(ReadGPTCommand, ReadGPTCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            List<Partition> phonePartitions = CareConnectivity.parseNOKT(Buffer, (int)bytesRead);

            // Check if the bootloader of the phone is already unlocked
            // We test the presence of a partition named "HACK"
            foreach (Partition partition in phonePartitions)
            {
                if ("HACK".Equals(partition.name))
                {
                    Console.WriteLine("**** Bootloader is already unlocked ****");
                    Console.WriteLine("Continue booting in \"normal\" mode.");
                    byte[] ContinueBootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x43, 0x42, 0x57 }; // NOKXCBW : Continue Boot command (Common Extended Message)
                    CareConnectivityDeviceInterface.WritePipe(ContinueBootCommand, ContinueBootCommand.Length);
                    CareConnectivityDeviceInterface.Close();
                    ProgramExit(-4);
                }
            }


            // Check first and last sectors of the partitions we are going to flash
            // because the "Lumia V1 programmer" can only flashes sectors below 0xF400
            // Theoritically, the last sector of the partition WINSECAPP is 0xF3FF
            // But it can be above because it can be exchanged with BACKUP_WINSECAPP
            // (same for SBL1, SBL2, SBL3, UEFI, TZ, RPM)
            List<string> RevisePartitions = new List<string>(new string[] { "SBL1", "SBL2", "SBL3", "UEFI", "TZ", "RPM", "WINSECAPP" });
            foreach (string RevisePartitionName in RevisePartitions)
            {
                Partition RevisePartition = null;
                foreach (Partition partition in phonePartitions)
                {
                    if (partition.name.Equals(RevisePartitionName))
                    {
                        RevisePartition = partition;
                    }
                }
                Partition ReviseBackupPartition = null;
                foreach (Partition partition in phonePartitions)
                {
                    if (partition.name.Equals("BACKUP_" + RevisePartitionName))
                    {
                        ReviseBackupPartition = partition;
                    }
                }
                if ((RevisePartition != null) && (ReviseBackupPartition != null) && (RevisePartition.firstSector > ReviseBackupPartition.firstSector))
                {
                    Console.WriteLine("Exchange {0} and {1}", RevisePartition.name, ReviseBackupPartition.name);
                    Console.WriteLine("\t{0}: first sector 0x{1:X6} - last sector 0x{2:X6}", RevisePartition.name, RevisePartition.firstSector, RevisePartition.lastSector);
                    Console.WriteLine("\t{0}: first sector 0x{1:X6} - last sector 0x{2:X6}", ReviseBackupPartition.name, ReviseBackupPartition.firstSector, ReviseBackupPartition.lastSector);

                    ulong OriginalFirstSector = RevisePartition.firstSector;
                    ulong OriginalLastSector = RevisePartition.lastSector;
                    RevisePartition.firstSector = ReviseBackupPartition.firstSector;
                    RevisePartition.lastSector = ReviseBackupPartition.lastSector;
                    ReviseBackupPartition.firstSector = OriginalFirstSector;
                    ReviseBackupPartition.lastSector = OriginalLastSector;
                }

                if (RevisePartition.lastSector >= 0xF400)
                {
                    Console.WriteLine("Last sector of partition {0} is still above 0xF400 (0x{1:X6})", RevisePartition.name, RevisePartition.lastSector);
                    ProgramExit(-4);
                }
            }

            // Prepare the modification of the GPT 
            // We replace the last sector of the SBL1 partition by a new partition named "HACK"
            // This partition has the same property (GUID, type GUID, attributes) as the SBL2 partition
            // And we mask the GUID and type GUID of the real SBL2 partition.
            Partition SBL1 = null;
            Partition SBL2 = null;
            foreach (Partition partition in phonePartitions)
            {
                if ("SBL1".Equals(partition.name))
                {
                    SBL1 = partition;
                }
                else if ("SBL2".Equals(partition.name))
                {
                    SBL2 = partition;
                }
            }
            Partition HackPartition = new Partition();
            HackPartition.name = "HACK";
            HackPartition.attributes = SBL2.attributes;
            HackPartition.firstSector = SBL1.lastSector;
            HackPartition.lastSector = SBL1.lastSector;
            HackPartition.partitionTypeGuid = SBL2.partitionTypeGuid;
            HackPartition.partitionGuid = SBL2.partitionGuid;
            phonePartitions.Add(HackPartition);
            SBL1.lastSector = SBL1.lastSector-1;
            SBL2.partitionTypeGuid = new Guid(new byte[] { 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74 });
            SBL2.partitionGuid = new Guid(new byte[] { 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74 });

            Console.WriteLine("\nPress [Enter] to return to \"flash\" mode and start flashing the phone.");
            Console.WriteLine("Be quick because the phone will not stay long in \"bootloader\" mode.");
            Console.ReadLine();

            // Go from "bootloader" mode to "flash" mode.
            byte[] RebootToFlashCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x53 }; // NOKS
            CareConnectivityDeviceInterface.WritePipe(RebootToFlashCommand, RebootToFlashCommand.Length);
            CareConnectivityDeviceInterface.Close();

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
            CareConnectivityDeviceInterface = new USB(devicePath);

            ProgramExit(0);
        }

        private static void ProgramExit(int exitCode)
        {
            Console.ReadLine();
            Environment.Exit(exitCode);
        }

    }
}
