using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.IO;

namespace wpi
{
    class Program
    {
        private static string GUID_APOLLO_DEVICE_INTERFACE = "{7EAFF726-34CC-4204-B09D-F95471B873CF}";
        private static string GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE = "{0FD3B15C-D457-45D8-A779-C2B2C9F9D0FD}";
        private static string GUID_LUMIA_EMERGENCY_DEVICE_INTERFACE = "{71DE994D-8B7C-43DB-A27E-2AE7CD579A0C}";
        private static string VID_PID_NOKIA_LUMIA_NORMAL_MODE = "VID_0421&PID_0661";
        private static string VID_PID_NOKIA_LUMIA_UEFI_MODE = "VID_0421&PID_066E";
        private static string VID_PID_NOKIA_LUMIA_EMERGENCY_MODE = "VID_05C6&PID_9008";

        static void Main(string[] args)
        {
            // We need a signed FFU (Full Flash Update).
            Console.Write("\nPath of FFU file (.ffu) :");
            string ffuPath = Console.ReadLine();
            // Check the validity of the FFU file
            if (!FFU.checkFile(ffuPath))
            {
                ProgramExit(-5);
            }
            FFU ffu = new FFU(ffuPath);

            // Get Root Hash Key (RHK) contained in SBL1 for later check.
            byte[] ffuSBL1 = ffu.GetPartition("SBL1");
            Console.WriteLine("Parse SBL1...");
            byte[] ffuRKH = Qualcomm.parseSBL1orProgrammer(ffuSBL1, 0x2800); // Offset in case of SBL1 partition.
            if (ffuRKH == null)
            {
                Console.WriteLine("Unable to extract Root Key Hash (RKH) from the SBL1 partition of the FFU file.");
                ProgramExit(-5);
            }

            // Prepare a patched SBL2 that will be flashed in the phone
            // Replace 0x28, 0x00, 0xD0, 0xE5 : ldrb r0, [r0, #0x28]
            // By      0x00, 0x00, 0xA0, 0xE3 : mov r0, #0
            Console.Write("\nPrepare a patched version of the SBL2 partition");
            byte[] ffuSBL2 = ffu.GetPartition("SBL2");
            byte[] patternToPatch = new byte[] { 0xE3, 0x01, 0x0E, 0x42, 0xE3, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            int patternPosition = -1;
            for (int i=0; i<ffuSBL2.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar
                if (ffuSBL2.Skip(i).Take(patternToPatch.Length).SequenceEqual(patternToPatch))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("\nUnable to find the pattern to patch in the SBL2 partition of the FFU file.");
                ProgramExit(-5);
            }
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3 }, 0, ffuSBL2, patternPosition + 5, 4);

            // Prepare the content of the "HACK" partition that is going to replace the last sector of the SBL1 partition.
            Console.Write("\nGenerate the content of the \"HACK\" partition");
            byte[] hackPartitionContent = Qualcomm.createHACK(ffuSBL1, ffuSBL2);

            // We need a "engeeniring" SBL3 to enable "Mass Storage" mode (it will be required to patch windows files)
            Console.Write("\nPath of the raw image of an engeeniring SBL3 (.bin) :");
            string engeeniringSBL3Path = Console.ReadLine();
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

            Console.Write("\nPrepare a patched version of the SBL3 partition");
            // Prepare a patched "engeeniring" SBL3 that will be flashed in the phone
            // Replace 0x28, 0x00, 0xD0, 0xE5 : ldrb r0, [r0, #0x28]
            // By      0x00, 0x00, 0xA0, 0xE3 : mov r0, #0
            patternToPatch = new byte[] { 0x04, 0x00, 0x9F, 0xE5, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            patternPosition = -1;
            for (int i = 0; i < engeeniringSBL3.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar

                if (engeeniringSBL3.Skip(i).Take(patternToPatch.Length).SequenceEqual(patternToPatch))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("\nUnable to find the pattern to patch in the engeeniring SBL3 partition.");
                ProgramExit(-5);
            }
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3 }, 0, engeeniringSBL3, patternPosition + 4, 4);

            //TODO prepare a patched UEFI 

            // We need a "loader" (a programmer) to be able to write partitions in the eMMC in Emergency DownLoad mode (EDL mode)
            Console.Write("\n\nPath of the emergency programmer (.hex) :");
            string programmerFile = Console.ReadLine();
            // Parse .hex file from "Intel HEX" format
            byte[] programmer = Qualcomm.parseHexFile(programmerFile);
            if (programmer == null)
            {
                Console.WriteLine("Unable to read the emergency programmer file.");
                ProgramExit(-5);
            }
            Console.Write("Parse programmer...");
            byte[] programmerType = new byte[] { 0x51, 0x0, 0x48, 0x0, 0x53, 0x0, 0x55, 0x0, 0x53, 0x0, 0x42, 0x0, 0x5F, 0x0, 0x41, 0x0, 0x52, 0x0, 0x4D, 0x0, 0x50, 0x0, 0x52, 0x0, 0x47, 0x0 }; //QHSUSB_ARMPRG
            patternPosition = -1;
            for (int i=0; i<programmer.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar
                if (programmer.Skip(i).Take(programmerType.Length).SequenceEqual(programmerType))
                {
                    patternPosition = i;
                    break;
                }
            }
            Console.WriteLine("");
            if (patternPosition == -1)
            {
                Console.WriteLine("The programmer is not of type QHSUSB_ARMPRG.");
                ProgramExit(-5);
            }
            byte[] programmerRKH = Qualcomm.parseSBL1orProgrammer(programmer, 0);
            if (!ffuRKH.SequenceEqual(programmerRKH))
            {
                Console.WriteLine("\nThe Root Key Hash (RKH) of the programmer doesn't match the one of the FFU file.");
                ProgramExit(-6);
            }
            File.WriteAllBytes("C:\\Users\\frede\\Documents\\programmer.bin", programmer);
            Console.WriteLine("Programmer size: {0} bytes.", programmer.Length);

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
            goto test;
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

            Console.WriteLine("\nReturning to \"flash\" mode to start flashing the phone...");

            // Go from "bootloader" mode to "flash" mode.
            byte[] RebootToFlashCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x53 }; // NOKS
            CareConnectivityDeviceInterface.WritePipe(RebootToFlashCommand, RebootToFlashCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.WriteLine("Press [Enter] when the phone displays a big \"NOKIA\"");
            Console.WriteLine("in the top part of the screen ( = \"flash\" mode ).");
            Console.ReadLine();

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

            byte[] ReadSSCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x46, 0x52, 0x00, 0x53, 0x53, 0x00, 0x00 }; // NOKXFR\0SS\0\0
            CareConnectivityDeviceInterface.WritePipe(ReadSSCommand, ReadSSCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);

            byte[] nokdCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x44 }; // NOKD
            CareConnectivityDeviceInterface.WritePipe(nokdCommand, nokdCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);

            Console.WriteLine("\n\"Soft brick\" the phone in order to boot in EDL mode after the next reboot.");
            // To enter Emergency DownLoad mode (EDL) we are going to erase a part of the eMMC to "brick" the phone.
            // First, we send the header of a signed FFU file in order to start the flashing:
            byte[] ffuHeader = ffu.getCombinedHeader();
            byte[] secureFlashCommand = new byte[ffuHeader.Length + 32]; // command header size = 32 bytes
            secureFlashCommand[0] = 0x4E; // N
            secureFlashCommand[1] = 0x4F; // O
            secureFlashCommand[2] = 0x4B; // K
            secureFlashCommand[3] = 0x58; // X
            secureFlashCommand[4] = 0x46; // F
            secureFlashCommand[5] = 0x53; // S
            secureFlashCommand[6] = 0x00; // Protocol version = 0x0001
            secureFlashCommand[7] = 0x01;  
            secureFlashCommand[8] = 0; // Progress = 0%
            secureFlashCommand[11] = 1; // Subblock count = 1
            secureFlashCommand[12] = 0x00; // Subblock type for "Header" = 0x0000000B
            secureFlashCommand[13] = 0x00;
            secureFlashCommand[14] = 0x00;
            secureFlashCommand[15] = 0x0B;
            uint subBlockLength = (uint)ffuHeader.Length + 12;
            secureFlashCommand[16] = (byte)((subBlockLength>>24) & 0xFF);
            secureFlashCommand[17] = (byte)((subBlockLength >> 16) & 0xFF);
            secureFlashCommand[18] = (byte)((subBlockLength >> 8) & 0xFF);
            secureFlashCommand[19] = (byte)(subBlockLength  & 0xFF);
            secureFlashCommand[20] = 0x00; // Header type = 0x00000000
            secureFlashCommand[21] = 0x00;
            secureFlashCommand[22] = 0x00;
            secureFlashCommand[23] = 0x00;
            uint payloadLength = (uint)ffuHeader.Length;
            secureFlashCommand[24] = (byte)((payloadLength >> 24) & 0xFF);
            secureFlashCommand[25] = (byte)((payloadLength >> 16) & 0xFF);
            secureFlashCommand[26] = (byte)((payloadLength >> 8) & 0xFF);
            secureFlashCommand[27] = (byte)(payloadLength & 0xFF);
            secureFlashCommand[28] = 0; // Header options = 0
            System.Buffer.BlockCopy(ffuHeader, 0, secureFlashCommand, 32, ffuHeader.Length);
            CareConnectivityDeviceInterface.WritePipe(secureFlashCommand, secureFlashCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            int flashReturnCode = (int)((Buffer[6] << 8) + Buffer[7]);
            if (flashReturnCode != 0)
            {
                Console.WriteLine("Flash of FFU header failed (return code 0x{0:X16})", flashReturnCode);
                ProgramExit(-3);
            }

            // Send 1 empty chunk (according to layout in FFU headers, it will be written to first and last chunk) ?
            // Erase the 256 first sectors (GPT ?) ?
            byte[] EmptyChunk = new byte[0x20000];
            Array.Clear(EmptyChunk, 0, 0x20000);
            secureFlashCommand = new byte[ffuHeader.Length + 28]; // command header size = 28 bytes
            secureFlashCommand[0] = 0x4E; // N
            secureFlashCommand[1] = 0x4F; // O
            secureFlashCommand[2] = 0x4B; // K
            secureFlashCommand[3] = 0x58; // X
            secureFlashCommand[4] = 0x46; // F
            secureFlashCommand[5] = 0x53; // S
            secureFlashCommand[6] = 0x00; // Protocol version = 0x0001
            secureFlashCommand[7] = 0x01;
            secureFlashCommand[8] = 0; // Progress = 0%
            secureFlashCommand[11] = 1; // Subblock count = 1
            secureFlashCommand[12] = 0x00; // Subblock type for "ChunkData" = 0x0000000C
            secureFlashCommand[13] = 0x00;
            secureFlashCommand[14] = 0x00;
            secureFlashCommand[15] = 0x0C;
            subBlockLength = (uint)EmptyChunk.Length + 8;
            secureFlashCommand[16] = (byte)((subBlockLength >> 24) & 0xFF);
            secureFlashCommand[17] = (byte)((subBlockLength >> 16) & 0xFF);
            secureFlashCommand[18] = (byte)((subBlockLength >> 8) & 0xFF);
            secureFlashCommand[19] = (byte)(subBlockLength & 0xFF);
            payloadLength = (uint)EmptyChunk.Length;
            secureFlashCommand[20] = (byte)((payloadLength >> 24) & 0xFF);
            secureFlashCommand[21] = (byte)((payloadLength >> 16) & 0xFF);
            secureFlashCommand[22] = (byte)((payloadLength >> 8) & 0xFF);
            secureFlashCommand[23] = (byte)(payloadLength & 0xFF);
            secureFlashCommand[24] = 0; // Data options = 0 (1 = verify)
            System.Buffer.BlockCopy(EmptyChunk, 0, secureFlashCommand, 28, EmptyChunk.Length);
            CareConnectivityDeviceInterface.WritePipe(secureFlashCommand, secureFlashCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            flashReturnCode = (int)((Buffer[6] << 8) + Buffer[7]);
            if (flashReturnCode != 0)
            {
                Console.WriteLine("Flash of FFU header failed (return code 0x{0:X16})", flashReturnCode);
                ProgramExit(-3);
            }

            Console.WriteLine("\nPress [Enter] to reboot the phone.");
            Console.ReadLine();

            // Reboot the phone. As we "bricked" it, it will reboot in Emergency DownLoad mode (EDL)
            RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            test:

            Console.WriteLine("Look for a phone connected on a USB port");
            Console.WriteLine("and exposing \"Lumia Emergency\" device interface...\n");

            Guid guidEmergencyDeviceInterface = new Guid(GUID_LUMIA_EMERGENCY_DEVICE_INTERFACE);
            devicePaths = USB.FindDevicePathsFromGuid(guidEmergencyDeviceInterface);
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            Console.WriteLine("Path of the device found:\n{0}", devicePath);
            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_EMERGENCY_MODE, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Vendor ID 0x05C6 : Qualcomm Inc.
                // Product ID 0x066E : Qualcomm Download
                Console.WriteLine("Incorrect VID (expecting 0x05C6) and/or incorrect PID (expecting 0x9008)");
                ProgramExit(-3);
            }
            USB EmergencyDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nCheck communication with the phone in EDL mode...");
            // Send DLOAD NOP command (0x06) to check we are able to communicate with the phone
            byte[] nopCommand = Qualcomm.encodeHDLC(new byte[] { 0x06 }, 1);
            EmergencyDeviceInterface.WritePipe(nopCommand, nopCommand.Length);
            byte[] ResponseBuffer = new byte[0x2000];
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            byte[] commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length !=1 || commandResult[0] != 0x02)
            {
                Console.WriteLine("Expected DLOAD ACK (0x02) but received:");
                printRaw(commandResult, commandResult.Length);
            }

            ProgramExit(0);
        }

        private static void ProgramExit(int exitCode)
        {
            Console.ReadLine();
            Environment.Exit(exitCode);
        }

        private static void printRaw(byte[] values, int length)
        {
            string characters = "";
            bool truncated = false;
            int truncatedLength = length;
            if (truncatedLength > 190) // display at max 10 lines of values
            {
                truncatedLength = 190;
                truncated = true;
            }
            int normalizedLength = ((truncatedLength / 19) + 1) * 19; // display 19 values by line
            for (int i = 0; i < normalizedLength; i++)
            {
                if (i < length)
                {
                    Console.Write("{0:X2} ", values[i]);
                    if (values[i] > 31 && values[i] < 127)
                    {
                        characters += (char)values[i] + "";
                    }
                    else
                    {
                        characters += ".";
                    }
                }
                else
                {
                    Console.Write("   ");
                }

                if ((i + 1) % 19 == 0)
                {
                    Console.WriteLine(" {0}", characters);
                    characters = "";
                }
            }
            if (truncated)
            {
                Console.WriteLine("Displayed only the first {0} bytes of {1} bytes.", truncatedLength, length);
            }
        }

    }
}
