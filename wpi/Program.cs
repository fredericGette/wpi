using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;

namespace wpi
{
    class Program
    {
        private static string GUID_APOLLO_DEVICE_INTERFACE = "{7EAFF726-34CC-4204-B09D-F95471B873CF}";
        private static string GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE = "{0FD3B15C-D457-45D8-A779-C2B2C9F9D0FD}";
        private static string GUID_LUMIA_EMERGENCY_DEVICE_INTERFACE = "{71DE994D-8B7C-43DB-A27E-2AE7CD579A0C}";
        private static string GUID_MASS_STORAGE_DEVICE_INTERFACE = "{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}";
        private static string VID_PID_NOKIA_LUMIA_NORMAL_MODE = "VID_0421&PID_0661";
        private static string VID_PID_NOKIA_LUMIA_UEFI_MODE = "VID_0421&PID_066E";
        private static string VID_PID_NOKIA_LUMIA_EMERGENCY_MODE = "VID_05C6&PID_9008";
        private static string PATH_QUALCOMM_MASS_STORAGE = "disk&ven_qualcomm&prod_mmc_storage";

        public static bool verbose = false;

        static void Main(string[] args)
        {
            ////////////////////////////////////////////////////////////////////////////
            // Read parameters
            // And check their validity
            ////////////////////////////////////////////////////////////////////////////
            string ffuPath = getStringParameter("ffu", args); // FFU file (.ffu) compatible with the phone.
            string engeeniringSBL3Path = getStringParameter("bin", args); // Raw image of an engeeniring SBL3 file (.bin) compatible with the phone. Not needed in REPAIR mode.
            string programmerPath = getStringParameter("hex", args); // Programmer file (.hex) compatible with the phone.
            string mode = getStringParameter("mode", args); // REPAIR = partially repair phone in EDL mode. After repair the phone should be able to boot in "flash" mode. And you can use WPInternals to flash a ffu file.
            verbose = getBoolParameter("verbose", args);  // optional.

            if (!"REPAIR".Equals(mode) && !"UNLOCK".Equals(mode) && !"ROOT".Equals(mode))
            {
                Console.WriteLine("Unkown \"mode={0}\". Only UNLOCK, REPAIR and ROOT are available.", mode);
                printUsage();
                ProgramExit(-1);
            }

            if (("UNLOCK".Equals(mode) || "REPAIR".Equals(mode)) && (ffuPath == null || !File.Exists(ffuPath)))
            {
                Console.WriteLine("FFU file not found.");
                printUsage();
                ProgramExit(-1);
            }

            if ("UNLOCK".Equals(mode) && (engeeniringSBL3Path == null || !File.Exists(engeeniringSBL3Path)))
            {
                Console.WriteLine("Raw image of an engeeniring SBL3 no found.");
                printUsage();
                ProgramExit(-1);
            }

            if (("UNLOCK".Equals(mode) || "REPAIR".Equals(mode)) && (programmerPath == null || !File.Exists(programmerPath)))
            {
                Console.WriteLine("Emergency programmer no found.");
                printUsage();
                ProgramExit(-1);
            }

            uint bytesRead;
            byte[] Buffer;
            if ("ROOT".Equals(mode))
            {
                goto root_phone;
            }

            // Check the validity of the FFU file
            if (!FFU.checkFile(ffuPath))
            {
                ProgramExit(-1);
            }
            FFU ffu = new FFU(ffuPath);

            // Get Root Hash Key (RHK) contained in SBL1 for later check (must be the same as the one of the phone).
            byte[] sbl1Content = ffu.GetPartition("SBL1");
            Console.WriteLine("Parse SBL1...");
            byte[] ffuRKH = Qualcomm.parseSBL1orProgrammer(sbl1Content, 0x2800); // Offset in case of SBL1 partition.
            if (ffuRKH == null)
            {
                Console.WriteLine("Unable to extract Root Key Hash (RKH) from the SBL1 partition of the FFU file.");
                ProgramExit(-1);
            }

            // We need a "loader" (a programmer) to be able to write partitions in the eMMC in Emergency DownLoad mode (EDL mode)
            // Parse .hex file from "Intel HEX" format
            byte[] programmer = Qualcomm.parseHexFile(programmerPath);
            if (programmer == null)
            {
                Console.WriteLine("Unable to read the emergency programmer file.");
                ProgramExit(-1);
            }
            Console.Write("\nParse programmer...");
            byte[] programmerType = new byte[] { 0x51, 0x0, 0x48, 0x0, 0x53, 0x0, 0x55, 0x0, 0x53, 0x0, 0x42, 0x0, 0x5F, 0x0, 0x41, 0x0, 0x52, 0x0, 0x4D, 0x0, 0x50, 0x0, 0x52, 0x0, 0x47, 0x0 }; //QHSUSB_ARMPRG
            int patternPosition = -1;
            for (int i = 0; i < programmer.Length; i++)
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
                ProgramExit(-1);
            }
            byte[] programmerRKH = Qualcomm.parseSBL1orProgrammer(programmer, 0);
            if (!ffuRKH.SequenceEqual(programmerRKH))
            {
                Console.WriteLine("\nThe Root Key Hash (RKH) of the programmer doesn't match the one of the FFU file.");
                ProgramExit(-1);
            }
            //File.WriteAllBytes("C:\\Users\\frede\\Documents\\programmer.bin", programmer);
            //Console.WriteLine("Programmer size: {0} bytes.", programmer.Length);

            GPT gptContent = null;
            Partition hackPartition = null;
            byte[] hackContent = null;
            Partition sbl1Partition = null;
            Partition sbl2Partition = null;
            byte[] sbl2Content = ffu.GetPartition("SBL2");
            byte[] engeeniringSbl3Content = null;
            Partition uefiPartition = null;
            UEFI uefiContent = null;
            if ("REPAIR".Equals(mode))
            {
                ////////////////////////////////////////////////////////////////////////////
                // Start the REPAIR mode 
                // we got directly to the part where communicate with the phone in Emergency DownLoad (EDL) mode
                ////////////////////////////////////////////////////////////////////////////
                gptContent = new GPT(ffu.GetSectors(0x01, 0x22), 0x4200);
                sbl1Partition = ffu.gpt.GetPartition("SBL1");
                sbl2Partition = ffu.gpt.GetPartition("SBL2");
                uefiPartition = ffu.gpt.GetPartition("UEFI");
                uefiContent = new UEFI(ffu.GetPartition("UEFI"), false);
                goto repair_bricked_phone;
            }

            ////////////////////////////////////////////////////////////////////////////
            // Start the UNLOCK mode 
            // and prepare the content of the partitions we will flash later into the phone
            ////////////////////////////////////////////////////////////////////////////

            // Prepare a patched SBL2 that will be flashed into the phone
            // Replace 0x28, 0x00, 0xD0, 0xE5 : ldrb r0, [r0, #0x28]
            // By      0x00, 0x00, 0xA0, 0xE3 : mov r0, #0
            Console.Write("\nPrepare a patched version of the SBL2 partition");
            byte[] patternToPatch = new byte[] { 0xE3, 0x01, 0x0E, 0x42, 0xE3, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            patternPosition = -1;
            for (int i = 0; i < sbl2Content.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar
                if (sbl2Content.Skip(i).Take(patternToPatch.Length).SequenceEqual(patternToPatch))
                {
                    patternPosition = i;
                    break;
                }
            }
            Console.WriteLine();
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern to patch in the SBL2 partition of the FFU file.");
                ProgramExit(-1);
            }
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3 }, 0, sbl2Content, patternPosition + 5, 4);

            // Prepare the content of the "HACK" partition that is going to replace the last sector of the SBL1 partition.
            Console.Write("\nGenerate the content of the \"HACK\" partition");
            hackContent = Qualcomm.createHACK(sbl1Content, sbl2Content);

            // We need a "engeeniring" SBL3 to enable "Mass Storage" mode (it will be required to patch windows files)
            engeeniringSbl3Content = Qualcomm.loadSBL3img(engeeniringSBL3Path);
            if (engeeniringSbl3Content == null)
            {
                Console.WriteLine("Unable to read the raw image of the engeeniring SBL3.");
                ProgramExit(-1);
            }

            // Check the size of the "engeeniring" SBL3
            if (engeeniringSbl3Content.Length > ffu.GetPartition("SBL3").Length)
            {
                Console.WriteLine("The engeeniring SBL3 is too large ({0} bytes instead of {1} bytes).", engeeniringSbl3Content.Length, ffu.GetPartition("SBL3").Length);
                ProgramExit(-5);
            }

            Console.Write("\nPrepare a patched version of the engeeniring SBL3 partition");
            // Prepare a patched "engeeniring" SBL3 that will be flashed in the phone
            // Replace 0x28, 0x00, 0xD0, 0xE5 : ldrb r0, [r0, #0x28]
            // By      0x00, 0x00, 0xA0, 0xE3 : mov r0, #0
            patternToPatch = new byte[] { 0x04, 0x00, 0x9F, 0xE5, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            patternPosition = -1;
            for (int i = 0; i < engeeniringSbl3Content.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar

                if (engeeniringSbl3Content.Skip(i).Take(patternToPatch.Length).SequenceEqual(patternToPatch))
                {
                    patternPosition = i;
                    break;
                }
            }
            Console.WriteLine();
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern to patch in the engeeniring SBL3 partition.");
                ProgramExit(-1);
            }
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3 }, 0, engeeniringSbl3Content, patternPosition + 4, 4);

            Console.WriteLine("\nRead the UEFI partition.");
            uefiContent = new UEFI(ffu.GetPartition("UEFI"), true);
            Console.Write("Prepare a patched version of the UEFI partition.");
            uefiContent.Patch();

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK mode 
            // Reboot in flash mode to read some important information from the phone:
            // - mainly the Root Hash Key (RKH) to check its compatibility with the FFU and the programmer files
            ////////////////////////////////////////////////////////////////////////////

            // Look for a phone connected on a USB port and exposing interface
            // - known as "Apollo" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "New Combi" interface in WPInternals
            // This interface allows to send jsonRPC (Remote Procedure Call) (to reboot the phone in flash mode for example).
            // Only a phone in "normal" mode exposes this interface. 
            Console.Write("\nLook for a phone connected on a USB port and exposing \"Apollo\" device interface ( = \"normal\" mode )");
            List<string> devicePaths;
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_APOLLO_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            string devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_NORMAL_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x0661 : Lumia 520 / 620 / 820 / 920 Normal mode
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x0661.)");
                ProgramExit(-1);
            }

            Console.WriteLine("\nSwitch to \"flash\" mode...");

            // Open the interface
            // It contains 2 pipes :
            // - output: to send commands to the phone.
            // - input: to get the result of the command (if needed).
            USB ApolloDeviceInterface = new USB(devicePath);

            // Send command to reboot in flash mode
            string Request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            byte[] OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            ApolloDeviceInterface.WritePipe(OutBuffer, OutBuffer.Length);

            Buffer = new byte[0x8000]; // Must be large enough to contain the GPT (see later)
            ApolloDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            ApolloDeviceInterface.Close();

            Console.WriteLine("\nWait 15s until the phone reboots in \"flash\" mode...");
            Console.WriteLine("Notes: In \"flash\" mode, the phone displays a big \"NOKIA\" in the top part of the screen.");
            for (int i=0; i<15; i++)
            {
                Thread.Sleep(1000);
                Console.Write(".");
            }            

            Buffer = new byte[0x8000]; // Must be large enough, because later it will contain the GPT of the phone.
            // Look for a phone connected on a USB port and exposing interface
            // - known as "Care Connectivity" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "Old Combi" interface in WPInternals
            // This interface allows flash commands starting with the signature "NOK" (to reboot the phone for example).
            // Notes: 
            // this interface is also exposed when the phone is in "normal" mode.
            // But in "normal" mode the PID of the device is 0x0661
            // Whereas in "flash" or "bootloader" mode the PID of the device is 0x066E
            Console.Write("\nLook for a phone connected on a USB port and exposing \"Care Connectivity\" device interface.");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-1);
            }
            // Open the interface
            USB CareConnectivityDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nRead Flash application version...");
            Console.WriteLine("Unlock of the bootloader requires 1.28 <= version < 2.0");
            Console.WriteLine("Root of the MainOS requires protocol < 2.0");
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
                ProgramExit(-1);
            }

            // Read the Security Status of the phone
            Console.WriteLine("\nRead Security Status of the phone...");
            byte[] ReadSSCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x46, 0x52, 0x00, 0x53, 0x53, 0x00, 0x00 }; // NOKXFR\0SS\0\0 
            CareConnectivityDeviceInterface.WritePipe(ReadSSCommand, ReadSSCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            bool secureBoot = CareConnectivity.parseNOKXFRSS(Buffer, (int)bytesRead);
            if (!secureBoot)
            {
                Console.WriteLine("**** Bootloader is already unlocked ****");
                Console.WriteLine("Continue booting in \"normal\" mode.");
                byte[] RebootNormalCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
                CareConnectivityDeviceInterface.WritePipe(RebootNormalCommand, RebootNormalCommand.Length);
                CareConnectivityDeviceInterface.Close();
                ProgramExit(-1);
            }

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK mode 
            // Reboot in bootloader mode to read the content of GUIG Partition Table (GPT) of the phone
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nSwitch to \"bootloader\" mode...");

            // Send the "normal command" NOKR (reboot) to the phone
            // It will reboot in "bootloader" mode.
            // (then, after a timeout, the phone automatically continues to "normal" mode)
            byte[] RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Care Connectivity\" device interface.");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-1);
            }
            // Open the interface
            CareConnectivityDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nRead BootManager version...");
            ReadVersionCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x56 }; // NOKV = Info Query
            CareConnectivityDeviceInterface.WritePipe(ReadVersionCommand, ReadVersionCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            CareConnectivity.parseNOKV(Buffer, (int)bytesRead);

            Console.WriteLine("\nRead GUID Partition Table (GPT)...");
            byte[] ReadGPTCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x54 }; // NOKT = Read 34 first sectors (MBR + GPT)
            CareConnectivityDeviceInterface.WritePipe(ReadGPTCommand, ReadGPTCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            gptContent = CareConnectivity.parseNOKT(Buffer, (int)bytesRead);

            // Check if the bootloader of the phone is already unlocked
            // We test the presence of a partition named "HACK"
            if (gptContent.GetPartition("HACK") != null)
            {
                Console.WriteLine("**** Bootloader is already unlocked ****");
                Console.WriteLine("Continue booting in \"normal\" mode.");
                byte[] ContinueBootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x43, 0x42, 0x57 }; // NOKXCBW : Continue Boot command (Common Extended Message)
                CareConnectivityDeviceInterface.WritePipe(ContinueBootCommand, ContinueBootCommand.Length);
                CareConnectivityDeviceInterface.Close();
                ProgramExit(-1);
            }

            // Check first and last sectors of the partitions we are going to flash
            // because the "Lumia V1 programmer" can only flashes sectors below 0xF400
            // Theoritically, the last sector of the partition WINSECAPP is 0xF3FF
            // But it can be above because it can be exchanged with BACKUP_WINSECAPP
            // (same for SBL1, SBL2, SBL3, UEFI, TZ, RPM)
            List<string> RevisePartitions = new List<string>(new string[] { "SBL1", "SBL2", "SBL3", "UEFI", "TZ", "RPM", "WINSECAPP" });
            foreach (string RevisePartitionName in RevisePartitions)
            {
                Partition RevisePartition = gptContent.GetPartition(RevisePartitionName);
                Partition ReviseBackupPartition = gptContent.GetPartition("BACKUP_" + RevisePartitionName);
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
                    ProgramExit(-1);
                }
            }
            sbl1Partition = gptContent.GetPartition("SBL1");
            sbl2Partition = gptContent.GetPartition("SBL2");
            uefiPartition = gptContent.GetPartition("UEFI");

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK mode 
            // Prepare the modified GPT
            ////////////////////////////////////////////////////////////////////////////

            // Prepare the modification of the GPT 
            // We replace the last sector of the SBL1 partition by a new partition named "HACK"
            // This partition has the same property (GUID, type GUID, attributes) as the SBL2 partition
            // And we mask the GUID and type GUID of the real SBL2 partition.
            hackPartition = new Partition();
            hackPartition.name = "HACK";
            hackPartition.attributes = sbl2Partition.attributes;
            hackPartition.firstSector = sbl1Partition.lastSector;
            hackPartition.lastSector = sbl1Partition.lastSector;
            hackPartition.partitionTypeGuid = sbl2Partition.partitionTypeGuid;
            hackPartition.partitionGuid = sbl2Partition.partitionGuid;
            gptContent.partitions.Add(hackPartition);
            sbl1Partition.lastSector = sbl1Partition.lastSector - 1;
            sbl2Partition.partitionTypeGuid = new Guid(new byte[] { 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74 });
            sbl2Partition.partitionGuid = new Guid(new byte[] { 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74 });

            Console.WriteLine("\nRebuild the GPT...");
            gptContent.Rebuild();

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK mode 
            // Reboot in flash mode to "brick" the phone
            // This is the easiest way to reach our goal: have a phone in Emergency DownLoad (EDL) mode.
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nReturning to \"flash\" mode to start flashing the phone...");

            // Go from "bootloader" mode to "flash" mode.
            byte[] RebootToFlashCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x53 }; // NOKS
            CareConnectivityDeviceInterface.WritePipe(RebootToFlashCommand, RebootToFlashCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Care Connectivity\" device interface");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-1);
            }
            // Open the interface
            CareConnectivityDeviceInterface = new USB(devicePath);

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
            secureFlashCommand[16] = (byte)((subBlockLength >> 24) & 0xFF);
            secureFlashCommand[17] = (byte)((subBlockLength >> 16) & 0xFF);
            secureFlashCommand[18] = (byte)((subBlockLength >> 8) & 0xFF);
            secureFlashCommand[19] = (byte)(subBlockLength & 0xFF);
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
                ProgramExit(-1);
            }

            // Send 1 empty chunk (according to layout in FFU headers, it will be written to first and last chunk) ?
            // Erase the 256 first sectors (MBR + GPT ?) ?
            byte[] EmptyChunk = new byte[0x20000];
            Array.Clear(EmptyChunk, 0, 0x20000);
            secureFlashCommand = new byte[EmptyChunk.Length + 28]; // command header size = 28 bytes
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
                ProgramExit(-1);
            }

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK mode 
            // Reboot the phone.
            // As we destroyed the GPT, the phone will go in EDL mode.
            ////////////////////////////////////////////////////////////////////////////
            Console.WriteLine("\nReboot the phone in \"EDL\" mode...");

            // Reboot the phone. As we "bricked" it, it will reboot in Emergency DownLoad mode (EDL)
            RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK/REPAIR mode 
            // Upload and start the programmer
            ////////////////////////////////////////////////////////////////////////////

        repair_bricked_phone:
            Buffer = new byte[0x8000];

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Lumia Emergency\" device interface");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_LUMIA_EMERGENCY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);
            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_EMERGENCY_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x05C6 : Qualcomm Inc.
                // Product ID 0x066E : Qualcomm Download
                Console.WriteLine("Incorrect VID (expecting 0x05C6) and/or incorrect PID (expecting 0x9008)");
                ProgramExit(-1);
            }
            USB EmergencyDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nCheck communication with the phone in EDL mode...");
            // Send No-op command (0x06) to check we are able to communicate with the phone
            // Notes: the PBL of the Lumia520 doesn't conform totally to the DDMS download protocol v8
            // because the implementation is non-secure and almost all the commands are "unknown/invalid"
            byte[] nopCommand = Qualcomm.encodeHDLC(new byte[] { 0x06 }, 1);
            EmergencyDeviceInterface.WritePipe(nopCommand, nopCommand.Length);
            byte[] ResponseBuffer = new byte[0x2000]; // I don't know why we need this size.
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            byte[] commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length != 1 || commandResult[0] != 0x02)
            {
                Console.WriteLine("Did not received the expected DLOAD ACK (0x02).");
                ProgramExit(-1);
            }

            Console.WriteLine("\nUpload the emergency programmer...");
            if (!Qualcomm.SendToPhoneMemory(0x2A000000, programmer, (uint)programmer.Length, EmergencyDeviceInterface))
            {
                Console.WriteLine("Failed to upload the programmer.");
                ProgramExit(-1);
            }

            Console.WriteLine("\nStart the emergency programmer...");
            // Send Go command (0x05) to execute code at a given 32bits address
            byte[] goCommand = Qualcomm.encodeHDLC(new byte[] { 0x05, 0x2A, 0x00, 0x00, 0x00 }, 5); // command (1byte) + address (4 bytes)
            EmergencyDeviceInterface.WritePipe(goCommand, goCommand.Length);
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length != 1 || commandResult[0] != 0x02)
            {
                Console.WriteLine("Did not received the expected DLOAD ACK (0x02).");
                ProgramExit(-1);
            }
            EmergencyDeviceInterface.Close(); // The successful loading of the programmer causes a disconnection of the phone

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Lumia Emergency\" device interface");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_LUMIA_EMERGENCY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);
            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_EMERGENCY_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x05C6 : Qualcomm Inc.
                // Product ID 0x066E : Qualcomm Download
                Console.WriteLine("Incorrect VID (expecting 0x05C6) and/or incorrect PID (expecting 0x9008)");
                ProgramExit(-1);
            }
            EmergencyDeviceInterface = new USB(devicePath);

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK/REPAIR mode 
            // Use the programmer to flash the following partitions:
            // MBR, GPT, SBL1, (HACK in UNLOCK mode only), SBL2, SBL3, TZ, RPM, UEFI, WINSECAPP
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nSend the hello text \"QCOM fast download protocol host\" to the programmer...");
            byte[] helloCommand = new byte[]
            {
                0x01, // "Hello" Ehost command
                0x51, 0x43, 0x4F, 0x4D, 0x20, 0x66, 0x61, 0x73, 0x74, 0x20, 0x64, 0x6F, 0x77, 0x6E, 0x6C, 0x6F, // "QCOM fast download protocol host"
                0x61, 0x64, 0x20, 0x70, 0x72, 0x6F, 0x74, 0x6F, 0x63, 0x6F, 0x6C, 0x20, 0x68, 0x6F, 0x73, 0x74,
                0x02,
                0x02, // Protocol version - Must be at least 0x02
                0x01
            };
            // Strange: we don't encode in HDLC the message sent to the phone
            // but the reponse from the phone is encoded in HDLC...
            EmergencyDeviceInterface.WritePipe(helloCommand, helloCommand.Length);
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length < 1 || commandResult[0] != 0x02)
            {
                Console.WriteLine("Did not received the expected Ehost \"Hello\" response (0x02)");
                ProgramExit(-1);
            }

            Console.WriteLine("\nChange security mode...");
            byte[] setSecurityModeCommand = new byte[] { 0x17, 0x00 }; // SECURITY_REQ 0
            // I don't known what is value 0
            EmergencyDeviceInterface.WritePipe(setSecurityModeCommand, setSecurityModeCommand.Length);
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length < 1 || commandResult[0] != 0x18)
            {
                Console.WriteLine("Did not received the expected Ehost \"SECURITY_RSP\" response (0x18).");
                ProgramExit(-1);
            }

            Console.WriteLine("\nOpen partition...");
            byte[] openPartitionCommand = new byte[] { 0x1B, 0x21 }; // OPEN_MULTI_REQ 0x21
            // 0x21=33=Partition type EMMCUSER - For programming eMMC chip (singleimage.mbn) ?
            EmergencyDeviceInterface.WritePipe(openPartitionCommand, openPartitionCommand.Length);
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length < 1 || commandResult[0] != 0x1C)
            {
                Console.WriteLine("Did not received the expected Ehost \"OPEN_MULTI_RSP\" response (0x1C).");
                ProgramExit(-1);
            }

            if ("UNLOCK".Equals(mode))
            {
                Console.WriteLine("\nFlash the HACK partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", hackPartition.firstSector, hackContent.Length);
                Qualcomm.Flash((uint)hackPartition.firstSector * 512, hackContent, (uint)hackContent.Length, EmergencyDeviceInterface);
            }

            // To minimize risk of brick we also flash unmodified partitions (MBR, SBL1, TZ, RPM, WINSECAPP)
            // Note: SBL1 is not really modified, just truncated by the HACK partition.
            byte[] ffuMBR = ffu.GetSectors(0, 1);
            Console.WriteLine("\nFlash the MBR partition (sector 0x0 ,size 0x{0:X} bytes)...", ffuMBR.Length);
            Qualcomm.Flash(0, ffuMBR, (uint)ffuMBR.Length, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the GPT partition (sector 0x1 ,size 0x{0:X} bytes)...", gptContent.GPTBuffer.Length);
            Qualcomm.Flash(0x200, gptContent.GPTBuffer, 0x41FF, EmergencyDeviceInterface); // Bad bounds-check in the flash-loader prohibits to write the last byte.

            Console.WriteLine("\nFlash the SBL2 partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", sbl2Partition.firstSector * 512, sbl2Content.Length);
            Qualcomm.Flash((uint)sbl2Partition.firstSector * 512, sbl2Content, (uint)sbl2Content.Length, EmergencyDeviceInterface);

            if ("UNLOCK".Equals(mode))
            {
                Console.WriteLine("\nFlash the engeeniring SBL3 partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", gptContent.GetPartition("SBL3").firstSector * 512, engeeniringSbl3Content.Length);
                Qualcomm.Flash((uint)gptContent.GetPartition("SBL3").firstSector * 512, engeeniringSbl3Content, (uint)engeeniringSbl3Content.Length, EmergencyDeviceInterface);
            }
            else
            {
                Console.WriteLine("\nFlash the SBL3 partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", ffu.gpt.GetPartition("SBL3").firstSector * 512, ffu.GetPartition("SBL3").Length);
                Qualcomm.Flash((uint)ffu.gpt.GetPartition("SBL3").firstSector * 512, ffu.GetPartition("SBL3"), (uint)ffu.GetPartition("SBL3").Length, EmergencyDeviceInterface);
            }

            Console.WriteLine("\nFlash the UEFI partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", uefiPartition.firstSector * 512, uefiContent.Binary.Length);
            Qualcomm.Flash((uint)uefiPartition.firstSector * 512, uefiContent.Binary, (uint)uefiContent.Binary.Length, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the SBL1 partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", sbl1Partition.firstSector * 512, (sbl1Partition.lastSector - sbl1Partition.firstSector + 1) * 512); // Don't use the size of the array of bytes because in UNLOCK mode the last sector of the partition SBL1 is removed
            Qualcomm.Flash((uint)sbl1Partition.firstSector * 512, sbl1Content, (uint)(sbl1Partition.lastSector - sbl1Partition.firstSector + 1) * 512, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the TZ partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", ffu.gpt.GetPartition("TZ").firstSector * 512, ffu.GetPartition("TZ").Length);
            Qualcomm.Flash((uint)ffu.gpt.GetPartition("TZ").firstSector * 512, ffu.GetPartition("TZ"), (uint)ffu.GetPartition("TZ").Length, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the RPM partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", ffu.gpt.GetPartition("RPM").firstSector * 512, ffu.GetPartition("RPM").Length);
            Qualcomm.Flash((uint)ffu.gpt.GetPartition("RPM").firstSector * 512, ffu.GetPartition("RPM"), (uint)ffu.GetPartition("RPM").Length, EmergencyDeviceInterface);

            // Workaround for bad bounds-check in flash-loader
            UInt32 WINSECAPPLength = (UInt32)ffu.GetPartition("WINSECAPP").Length;
            UInt32 WINSECAPPStart = (UInt32)ffu.gpt.GetPartition("WINSECAPP").firstSector * 512;

            if ((WINSECAPPStart + WINSECAPPLength) > 0x1E7FE00)
                WINSECAPPLength = 0x1E7FE00 - WINSECAPPStart;

            // repair_bricked_phone: invert the comment of the 4 following lines
            Console.WriteLine("\nFlash the WINSECAPP partition (sector 0x{0:X} ,size 0x{1:X} bytes)...", ffu.gpt.GetPartition("WINSECAPP").firstSector * 512, ffu.GetPartition("WINSECAPP").Length);
            Qualcomm.Flash((uint)ffu.gpt.GetPartition("WINSECAPP").firstSector * 512, ffu.GetPartition("WINSECAPP"), WINSECAPPLength, EmergencyDeviceInterface);

            Console.WriteLine("\nClose partition ..."); //Close and flush last partial write to flash
            byte[] closePartitionCommand = new byte[] { 0x15 }; // CLOSE_REQ
            EmergencyDeviceInterface.WritePipe(closePartitionCommand, closePartitionCommand.Length);
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length < 1 || commandResult[0] != 0x16)
            {
                Console.WriteLine("Did not recevied the expected Ehost \"CLOSE_RSP\" response (0x16.");
                ProgramExit(-1);
            }

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK/REPAIR mode 
            // Reboot the phone
            // In UNLOCK mode, it will reboot in flash mode because we previously interrupt a flash session to brick the phone
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nReboot the phone...");
            byte[] rebootCommand = new byte[] { 0x0B }; // RESET_REQ
            EmergencyDeviceInterface.WritePipe(rebootCommand, rebootCommand.Length);
            EmergencyDeviceInterface.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
            commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
            if (commandResult.Length < 1 || commandResult[0] != 0x0C)
            {
                Console.WriteLine("Did not received the expected Ehost \"RESET_ACK\" response (0x0C).");
                ProgramExit(-1);
            }

            EmergencyDeviceInterface.Close();

            if ("REPAIR".Equals(mode))
            {
                // No need to go further in repair mode
                // We should be able to use another tool (like WPInternals) to finish the repair
                ProgramExit(0);
            }

            Console.WriteLine("\nAfter reboot, the phone should be in \"flash\" mode \"in-progress\" : A big \"NOKIA\" in the top part of the screen on a dark red background.\n");
            // This is because we previously interrupt a flash session to brick the phone...

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Care Connectivity\" device interface");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-1);
            }
            // Open the interface
            CareConnectivityDeviceInterface = new USB(devicePath);

            ////////////////////////////////////////////////////////////////////////////
            // UNLOCK mode 
            // Finish the flash session to reboot in normal mode
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nRead secure flash status...");
            byte[] ReadSecureFlashCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x46, 0x52, 0x00, 0x46, 0x53, 0x00, 0x00 }; // NOKXFR\0FS\0\0 
            CareConnectivityDeviceInterface.WritePipe(ReadSecureFlashCommand, ReadSecureFlashCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            if (!CareConnectivity.parseNOKXFRFS(Buffer, (int)bytesRead))
            {
                Console.WriteLine("Flash mode is not \"in-progress\".");
                ProgramExit(-1);
            }

            // Flash dummy sector (only allowed when phone is authenticated)
            Console.WriteLine("\nFlash an empty sector to exit the \"flash\" mode...");
            byte[] flashCommand = new byte[576]; // command header (64 bytes) + empty sector (512 bytes)
            // We use the normal command NOKF instead of the UFP extended command NOKXFS
            flashCommand[0] = 0x4E; // N
            flashCommand[1] = 0x4F; // O
            flashCommand[2] = 0x4B; // K
            flashCommand[3] = 0x46; // F
            flashCommand[5] = 0x00; // Device type = 0
            flashCommand[11] = 0x00; // Start sector is just after the last sector of the GPT (0x22)
            flashCommand[12] = 0x00;
            flashCommand[13] = 0x00;
            flashCommand[14] = 0x22;
            flashCommand[15] = 0x00; // Sector count = 1
            flashCommand[16] = 0x00;
            flashCommand[17] = 0x00;
            flashCommand[18] = 0x01;
            flashCommand[19] = 0x00; // Progress (0 - 100)
            flashCommand[24] = 0x00; // Do Verify
            flashCommand[25] = 0x00; //  Is Test
            Array.Clear(flashCommand, 64, 512); // Add the content of the empty sector
            CareConnectivityDeviceInterface.WritePipe(flashCommand, flashCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            // todo check return

            Console.WriteLine("\nReboot the phone...");
            RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            ////////////////////////////////////////////////////////////////////////////
            // ROOT mode 
            // Switch to "mass storage" mode to patch the MainOS and EFIESP partitions
            ////////////////////////////////////////////////////////////////////////////

            root_phone:

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Apollo\" device interface ( = \"normal\" mode )");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_APOLLO_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_NORMAL_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x0661 : Lumia 520 / 620 / 820 / 920 Normal mode
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x0661.)");
                ProgramExit(-1);
            }

            Console.WriteLine("\nSwitch to \"flash\" mode...");
            // Open the interface
            ApolloDeviceInterface = new USB(devicePath);

            // Send command to reboot in flash mode
            Request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            ApolloDeviceInterface.WritePipe(OutBuffer, OutBuffer.Length);
            Buffer = new byte[64]; // Should be enough to read the reboot response
            ApolloDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            ApolloDeviceInterface.Close();

            Console.WriteLine("\nWait 15s until the phone reboots in \"flash\" mode...");
            Console.WriteLine("Notes: In \"flash\" mode, the phone displays a big \"NOKIA\" in the top part of the screen.");
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(1000);
                Console.Write(".");
            }

            // Look for a phone connected on a USB port and exposing interface
            // - known as "Care Connectivity" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "Old Combi" interface in WPInternals
            // This interface allows flash commands starting with the signature "NOK" (to reboot the phone for example).
            // Notes: 
            // this interface is also exposed when the phone is in "normal" mode.
            // But in "normal" mode the PID of the device is 0x0661
            // Whereas in "flash" or "bootloader" mode the PID of the device is 0x066E
            Console.Write("\nLook for a phone connected on a USB port and exposing \"Care Connectivity\" device interface.");
            do
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
            } while (devicePaths.Count == 0);
            Console.WriteLine();
            if (devicePaths.Count != 1)
            {
                Console.WriteLine("Number of devices found: {0}. Must be one.", devicePaths.Count);
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            if (devicePath.IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x0421 : Nokia Corporation
                // Product ID 0x066E : UEFI mode (including flash and bootloader mode)
                Console.WriteLine("Incorrect VID (expecting 0x0421) and/or incorrect PID (expecting 0x066E)");
                ProgramExit(-1);
            }
            // Open the interface
            CareConnectivityDeviceInterface = new USB(devicePath);

            Console.WriteLine("\nSwitch to \"mass storage\" mode...");
            byte[] MassStorageCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x4D }; // NOKM = Mass Storage
            CareConnectivityDeviceInterface.WritePipe(MassStorageCommand, MassStorageCommand.Length);
            // The phone immediatly switches to "mass storage" and doesn't send a response.

            Console.Write("\nLook for a phone connected on a USB port and exposing \"Mass Storage\" device interface.");
            devicePath = null;
            for (int i=0; i<15; i++) // Wait 15s max
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_MASS_STORAGE_DEVICE_INTERFACE));
                if (devicePaths.Count > 0)
                {
                    for (int j=0; j< devicePaths.Count; j++)
                    {
                        if (devicePaths[j].IndexOf(PATH_QUALCOMM_MASS_STORAGE, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            devicePath = devicePaths[j];
                            goto mass_storage_found;
                        }
                    }
                }
            }
            Console.WriteLine();
            if (devicePath == null)
            {
                Console.WriteLine("Unable to find a phone exposing a \"Mass Storage\" device interface.");
                ProgramExit(-1);
            }
            mass_storage_found:
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            ProgramExit(0);
        }

        private static void ProgramExit(int exitCode)
        {
            //Console.ReadLine();
            Environment.Exit(exitCode);
        }

        public static string getStringParameter(string argName, string[] args)
        {
            Match argument;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                argument = Regex.Match(arg, @"^--" + argName + "=(.*)$");
                if (argument.Success)
                {
                    return argument.Groups[1].Value;
                }
            }

            return null;
        }

        public static bool getBoolParameter(string argName, string[] args)
        {
            Match argument;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                argument = Regex.Match(arg, @"^--" + argName);
                if (argument.Success)
                {
                    return true;
                }
            }

            return false; ;
        }

        public static void printUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("--mode=REPAIR");
            Console.WriteLine("\t--ffu=<.ffu file>");
            Console.WriteLine("\t--hex=<.hex programmer file>");
            Console.WriteLine("--mode=UNLOCK");
            Console.WriteLine("\t--ffu=<.ffu file>");
            Console.WriteLine("\t--hex=<.hex programmer file>");
            Console.WriteLine("\t--bin=<.bin engeeniring SBL3 file>");
            Console.WriteLine("[--verbose]");
        }
    }
}