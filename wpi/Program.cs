using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;

namespace wpi
{
    class Program
    {
        private static string GUID_APOLLO_DEVICE_INTERFACE = "{7EAFF726-34CC-4204-B09D-F95471B873CF}";
        private static string GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE = "{0FD3B15C-D457-45D8-A779-C2B2C9F9D0FD}";
        private static string GUID_LUMIA_EMERGENCY_DEVICE_INTERFACE = "{71DE994D-8B7C-43DB-A27E-2AE7CD579A0C}";
        private static string GUID_MASS_STORAGE_DEVICE_INTERFACE = "{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}";
        private static string GUID_COM_PORT_DEVICE_INTERFACE = "{86E0D1E0-8089-11D0-9CE4-08003E301F73}";
        private static string VID_PID_NOKIA_LUMIA_NORMAL_MODE = "VID_0421&PID_0661";
        private static string VID_PID_NOKIA_LUMIA_UEFI_MODE = "VID_0421&PID_066E";
        private static string VID_PID_QUALCOMM_EMERGENCY_MODE = "VID_05C6&PID_9008";
        private static string VID_PID_QUALCOMM_DIAGNOSTIC = "VID_05C6&PID_9006";
        private static string PATH_QUALCOMM_MASS_STORAGE = "disk&ven_qualcomm&prod_mmc_storage";

        public static bool verbose = false;

        static void Main(string[] args)
        {
            ////////////////////////////////////////////////////////////////////////////
            // Read parameters
            // And check their validity
            ////////////////////////////////////////////////////////////////////////////
            string ffuPath = getStringParameter("ffu", args); // FFU file (.ffu) compatible with the phone.
            string donorFfuPath = getStringParameter("donorFfu", args); // FFU file (.ffu) containing a patchable version of mobilestartup.efi Not needed in REPAIR mode.
            string engeeniringSBL3Path = getStringParameter("sbl3Bin", args); // Raw image of an engeeniring SBL3 file (.bin) compatible with the phone. Not needed in REPAIR mode.
            string unlockedUefiBsNvPath = getStringParameter("uefiBsNvBin", args); // Raw image of an UEFI_BS_NV file (.bin) patched to deactivate Secure Boot. Not needed in REPAIR mode.
            string programmerPath = getStringParameter("hex", args); // Programmer file (.hex) compatible with the phone.
            verbose = getBoolParameter("verbose", args);  // optional.
            bool repair = getBoolParameter("repair", args);  // optional.

            if (ffuPath == null || !File.Exists(ffuPath))
            {
                Console.WriteLine("FFU file not found.");
                printUsage();
                ProgramExit(-1);
            }

            if (donorFfuPath == null || !File.Exists(donorFfuPath))
            {
                Console.WriteLine("Donor FFU file not found.");
                printUsage();
                ProgramExit(-1);
            }

            if (engeeniringSBL3Path == null || !File.Exists(engeeniringSBL3Path))
            {
                Console.WriteLine("Raw image of an engeeniring SBL3 no found.");
                printUsage();
                ProgramExit(-1);
            }

            if (unlockedUefiBsNvPath == null || !File.Exists(unlockedUefiBsNvPath))
            {
                Console.WriteLine("Raw image of an unlocked UEFI_BS_NV no found.");
                printUsage();
                ProgramExit(-1);
            }

            if (programmerPath == null || !File.Exists(programmerPath))
            {
                Console.WriteLine("Emergency programmer no found.");
                printUsage();
                ProgramExit(-1);
            }

            uint bytesRead;
            byte[] Buffer;

            // Check the validity of the FFU file
            if (!FFU.checkFile(ffuPath))
            {
                ProgramExit(-1);
            }
            FFU ffu = new FFU(ffuPath);

            // Get Root Hash Key (RHK) contained in SBL1 for later check (must be the same as the one of the phone).
            byte[] sbl1Content = ffu.GetPartition("SBL1");
            Console.WriteLine("Parse FFU SBL1...");
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
                //if (programmer.Skip(i).Take(programmerType.Length).SequenceEqual(programmerType))
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
            byte[] mobilestartupefiContent = null;
            byte[] uefiBsNvContent = null;
            if (repair)
            {
                // Repair mode
                // Communicate directly with the phone in Emergency Download (EDL) mode.
                gptContent = new GPT(ffu.GetSectors(0x01, 0x22), 0x4200);
                sbl1Partition = ffu.gpt.GetPartition("SBL1");
                sbl2Partition = ffu.gpt.GetPartition("SBL2");
                uefiPartition = ffu.gpt.GetPartition("UEFI");
                uefiContent = new UEFI(ffu.GetPartition("UEFI"), false);
                goto repair_bricked_phone;
            }

            ////////////////////////////////////////////////////////////////////////////
            // Start the unlock 
            // and prepare the content of the partitions we will flash later into the phone
            ////////////////////////////////////////////////////////////////////////////

            // Must be run as Administrator to be able to patch the files of the EFIESP and MainOS partitions.
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("This program must be run as Administrator for \"ROOT\" mode.");
                ProgramExit(-1);
            }

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
                ProgramExit(-1);
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

            if (verbose) Console.WriteLine("\nRead the UEFI partition.");
            uefiContent = new UEFI(ffu.GetPartition("UEFI"), true);
            Console.Write("\nPrepare a patched version of the UEFI partition.");
            uefiContent.Patch();

            Console.WriteLine("\nPrepare a patched version of mobilestartup.efi");
            // We need a patched mobilestartup.efi from Windows 10 (I don't know why).
            // We get it from a "donor FFU".
            // In our case the donor FFU is:
            //   RM1085_1078.0053.10586.13169.13829.034EA6_retail_prod_signed.ffu
            //   OS version 10.0.10586.318
            FFU donorFFU = new FFU(donorFfuPath);
            byte[] EFIESPcontent = donorFFU.GetPartition("EFIESP");
            EFIESP efiEsp = new EFIESP(EFIESPcontent);
            mobilestartupefiContent = efiEsp.getFileContent();
            // Check hash of the file
            SHA1Managed sha = new SHA1Managed();
            byte[] hash = sha.ComputeHash(mobilestartupefiContent);
            if (verbose)
            {
                Console.Write("\nHash of mobilestartup.efi before patch: ");
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x8E, 0x71, 0xD1, 0xF0, 0x25, 0x5E, 0x8C, 0x71, 0xBD, 0xE6, 0xF8, 0xD3, 0x78, 0x0C, 0x48, 0x27, 0xC2, 0x44, 0x37, 0x5B }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 10.0.10586.318 is supported.");
                ProgramExit(-1);
            }
            // Patch the content of the file
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x20, 0x70, 0x47, 0x78, 0x46, 0x25, 0x49, 0xA0, 0xEB, 0x01, 0x00, 0x70, 0xB4, 0x81, 0xB0, 0x04, 0x46, 0x23, 0x4B, 0x04, 0xEB, 0x03, 0x00, 0x22, 0x4B, 0x04, 0xEB, 0x03, 0x01, 0x03, 0x22, 0x00, 0x23, 0x00, 0x93, 0x43, 0xF2, 0x2D, 0x56, 0x04, 0xEB, 0x06, 0x05, 0xA8, 0x47, 0x1E, 0x49, 0x04, 0xEB, 0x01, 0x05, 0xA8, 0x47, 0x06, 0x46, 0x01, 0x2E, 0x04, 0xD0, 0x01, 0x20, 0x1B, 0x49, 0x04, 0xEB, 0x01, 0x05, 0xA8, 0x47, 0x1A, 0x48, 0x04, 0xEB, 0x00, 0x01, 0x09, 0x68, 0xD1, 0xF8, 0xAC, 0x50, 0x0E, 0xA0, 0x00, 0x21, 0x6A, 0x46, 0xA8, 0x47, 0x00, 0x9D, 0x6D, 0x68, 0x00, 0x2D, 0x01, 0xD1, 0x00, 0x9D, 0xAD, 0x68, 0xA8, 0x47, 0x01, 0x2E, 0x04, 0xD0, 0x30, 0x46, 0x0F, 0x49, 0x04, 0xEB, 0x01, 0x05, 0xA8, 0x47, 0x0F, 0x4E, 0x04, 0xEB, 0x06, 0x05, 0xA8, 0x47, 0x42, 0xF6, 0xC1, 0x26, 0x04, 0xEB, 0x06, 0x00, 0x01, 0xB0, 0x70, 0xBC, 0x00, 0x47, 0x9D, 0x5B, 0x08, 0xF9, 0x04, 0x93, 0xFB, 0x40, 0x8F, 0xE0, 0x4A, 0xEE, 0x3B, 0x1A, 0x78, 0x4B, 0xB0, 0x8D, 0x06, 0x00, 0x28, 0x12, 0x0B, 0x00, 0x48, 0x12, 0x0B, 0x00, 0xC5, 0x87, 0x07, 0x00, 0x6D, 0x87, 0x07, 0x00, 0xC4, 0xCF, 0x0E, 0x00, 0x41, 0xFF, 0x06, 0x00 }, 0, mobilestartupefiContent, 0x000681A8, 184);
            System.Buffer.BlockCopy(new byte[] { 0xF8, 0xB8, 0x0C, 0x00 }, 0, mobilestartupefiContent, 0x00000138, 4);
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x20, 0x70, 0x47 }, 0, mobilestartupefiContent, 0x000710F8, 4);
            System.Buffer.BlockCopy(new byte[] { 0x00, 0x20, 0x70, 0x47 }, 0, mobilestartupefiContent, 0x00028A2C, 4);
            System.Buffer.BlockCopy(new byte[] { 0x48, 0x00, 0x65, 0x00, 0x61, 0x00, 0x74, 0x00, 0x68, 0x00, 0x63, 0x00, 0x6C, 0x00, 0x69, 0x00, 0x66, 0x00, 0x66, 0x00, 0x37, 0x00, 0x34, 0x00, 0x4D, 0x00, 0x53, 0x00, 0x4D, 0x00, 0x00, 0x00 }, 0, mobilestartupefiContent, 0x000AF828, 32);
            System.Buffer.BlockCopy(new byte[] { 0x00, 0xBF }, 0, mobilestartupefiContent, 0x00001EB6, 2);
            System.Buffer.BlockCopy(new byte[] { 0x66, 0xF0, 0x76, 0xB9 }, 0, mobilestartupefiContent, 0x00001EBC, 4);

            // Read the content of the unlocked UEFI_BS_NV partition file
            // As indicated in WPinternals:
            // In this partition the SecureBoot variable is disabled.
            // It overwrites the variable in a different NV-partition than where this variable is stored usually.
            // This normally leads to endless-loops when the NV-variables are enumerated.
            // But the partition contains an extra hack to break out the endless loops.
            uefiBsNvContent = File.ReadAllBytes(unlockedUefiBsNvPath);
            List<string> devicePaths = null;
            string devicePath = null;

            ////////////////////////////////////////////////////////////////////////////
            // Reboot in flash mode to read some important information from the phone:
            // - mainly the Root Hash Key (RKH) to check its compatibility with the FFU and the programmer files
            ////////////////////////////////////////////////////////////////////////////

            // Look for a phone connected on a USB port and exposing interface
            // - known as "Apollo" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "New Combi" interface in WPInternals
            // This interface allows to send jsonRPC (Remote Procedure Call) (to reboot the phone in flash mode for example).
            // Only a phone in "normal" mode exposes this interface. 
            Console.Write("\nLook for a phone exposing \"Apollo\" device interface ( = \"normal\" mode )");
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

            Buffer = new byte[0x8000]; // Must be large enough, because later it will contain the GPT of the phone.
            // Look for a phone connected on a USB port and exposing interface
            // - known as "Care Connectivity" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "Old Combi" interface in WPInternals
            // This interface allows flash commands starting with the signature "NOK" (to reboot the phone for example).
            // Notes: 
            // this interface is also exposed when the phone is in "normal" mode.
            // But in "normal" mode the PID of the device is 0x0661
            // Whereas in "flash" or "bootloader" mode the PID of the device is 0x066E
            Console.Write("\nLook for a phone exposing \"Care Connectivity\" device interface.");
            for (int i = 0; i < 120; i++) // Wait 120s max
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
                if (devicePaths.Count > 0)
                {
                    for (int j = 0; j < devicePaths.Count; j++)
                    {
                        if (devicePaths[j].IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            devicePath = devicePaths[j];
                            goto flash_found1;
                        }
                    }
                }
            } while (devicePaths.Count == 0) ;
          flash_found1:
            Console.WriteLine();
            if (devicePath == null)
            {
                Console.WriteLine("Unable to find a phone exposing a \"Care Connectivity\" device interface.");
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

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
                // But, as we are going to flash the original FFU, it is going to be locked again.

                //Console.WriteLine("Continue booting in \"normal\" mode.");
                //byte[] RebootNormalCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
                //CareConnectivityDeviceInterface.WritePipe(RebootNormalCommand, RebootNormalCommand.Length);
                //CareConnectivityDeviceInterface.Close();
                //ProgramExit(-1);
            }

            ////////////////////////////////////////////////////////////////////////////
            // Reboot in bootloader mode to read the content of GUIG Partition Table (GPT) of the phone
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nSwitch to \"bootloader\" mode...");

            // Send the "normal command" NOKR (reboot) to the phone
            // It will reboot in "bootloader" mode.
            // (then, after a timeout, the phone automatically continues to "normal" mode)
            byte[] RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.Write("\nLook for a phone exposing \"Care Connectivity\" device interface.");
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
            // Prepare the modified GPT
            ////////////////////////////////////////////////////////////////////////////

            // Prepare the modification of the GPT 
            // We replace the last sector of the SBL1 partition by a new partition named "HACK"
            // This partition has the same property (GUID, type GUID, attributes) as the SBL2 partition
            // And we mask the GUID and type GUID of the real SBL2 partition.

            // Check if the bootloader of the phone is already unlocked
            // We test the presence of a partition named "HACK"
            hackPartition = gptContent.GetPartition("HACK");
            if (hackPartition != null)
            {
                Console.WriteLine("**** Bootloader is already unlocked ****");
                // But, as we are going to flash the original FFU, it is going to be locked again.

                //Console.WriteLine("Continue booting in \"normal\" mode.");
                //byte[] ContinueBootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x58, 0x43, 0x42, 0x57 }; // NOKXCBW : Continue Boot command (Common Extended Message)
                //CareConnectivityDeviceInterface.WritePipe(ContinueBootCommand, ContinueBootCommand.Length);
                //CareConnectivityDeviceInterface.Close();
                //ProgramExit(-1);
            }
            else
            {
                hackPartition = new Partition();
                gptContent.partitions.Add(hackPartition);
            }
           
            hackPartition.name = "HACK";
            hackPartition.attributes = sbl2Partition.attributes;
            hackPartition.firstSector = sbl1Partition.lastSector;
            hackPartition.lastSector = sbl1Partition.lastSector;
            hackPartition.partitionTypeGuid = sbl2Partition.partitionTypeGuid;
            hackPartition.partitionGuid = sbl2Partition.partitionGuid;
         
            sbl1Partition.lastSector = sbl1Partition.lastSector - 1;
            sbl2Partition.partitionTypeGuid = new Guid(new byte[] { 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74 });
            sbl2Partition.partitionGuid = new Guid(new byte[] { 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74 });

            if (verbose) Console.WriteLine("\nRebuild the GPT...");
            gptContent.Rebuild();

            ////////////////////////////////////////////////////////////////////////////
            // Reboot in flash mode to flash the original FFU the phone
            // This step is not required to unlock/root the phone.
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nReturning to \"flash\" mode to start flashing the phone...");

            // Go from "bootloader" mode to "flash" mode.
            byte[] RebootToFlashCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x53 }; // NOKS
            CareConnectivityDeviceInterface.WritePipe(RebootToFlashCommand, RebootToFlashCommand.Length);
            CareConnectivityDeviceInterface.Close();

            Console.Write("\nLook for a phone exposing \"Care Connectivity\" device interface");
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

            Console.Write("\nFlash the original FFU on the phone.");
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
                Console.WriteLine("\nFlash of FFU header failed (return code 0x{0:X16})", flashReturnCode);
                ProgramExit(-1);
            }

            // We flash the content of the FFU in order to get a phone with a consistent content.
            System.IO.FileStream FfuFile = new System.IO.FileStream(ffu.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            FfuFile.Seek(ffuHeader.Length, 0);
            int Position = ffuHeader.Length;
            byte[] Payload;
            int ChunkCount = 0;
            Payload = new byte[ffu.ChunkSize];
            while (Position < FfuFile.Length)
            {
                if (!verbose && ((Position-ffuHeader.Length)/Payload.Length) % 50 == 0) Console.Write("."); // Progress bar

                FfuFile.Read(Payload, 0, Payload.Length);
                ChunkCount++;

                secureFlashCommand = new byte[Payload.Length + 28]; // command header size = 28 bytes
                secureFlashCommand[0] = 0x4E; // N
                secureFlashCommand[1] = 0x4F; // O
                secureFlashCommand[2] = 0x4B; // K
                secureFlashCommand[3] = 0x58; // X
                secureFlashCommand[4] = 0x46; // F
                secureFlashCommand[5] = 0x53; // S
                secureFlashCommand[6] = 0x00; // Protocol version = 0x0001
                secureFlashCommand[7] = 0x01;
                secureFlashCommand[8] = (byte)((ChunkCount * 100 / (int)ffu.TotalChunkCount) & 0xFF); // Progress
                secureFlashCommand[11] = 1; // Subblock count = 1
                secureFlashCommand[12] = 0x00; // Subblock type for "ChunkData" = 0x0000000C
                secureFlashCommand[13] = 0x00;
                secureFlashCommand[14] = 0x00;
                secureFlashCommand[15] = 0x0C;
                subBlockLength = (uint)Payload.Length + 8;
                secureFlashCommand[16] = (byte)((subBlockLength >> 24) & 0xFF);
                secureFlashCommand[17] = (byte)((subBlockLength >> 16) & 0xFF);
                secureFlashCommand[18] = (byte)((subBlockLength >> 8) & 0xFF);
                secureFlashCommand[19] = (byte)(subBlockLength & 0xFF);
                payloadLength = (uint)Payload.Length;
                secureFlashCommand[20] = (byte)((payloadLength >> 24) & 0xFF);
                secureFlashCommand[21] = (byte)((payloadLength >> 16) & 0xFF);
                secureFlashCommand[22] = (byte)((payloadLength >> 8) & 0xFF);
                secureFlashCommand[23] = (byte)(payloadLength & 0xFF);
                secureFlashCommand[24] = 0; // Data options = 0 (1 = verify)
                System.Buffer.BlockCopy(Payload, 0, secureFlashCommand, 28, Payload.Length);
                CareConnectivityDeviceInterface.WritePipe(secureFlashCommand, secureFlashCommand.Length);
                CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
                flashReturnCode = (int)((Buffer[6] << 8) + Buffer[7]);
                if (flashReturnCode != 0)
                {
                    Console.WriteLine("Flash of FFU header failed (return code 0x{0:X16})", flashReturnCode);
                    ProgramExit(-1);
                }

                Position += Payload.Length;
            }
            FfuFile.Close();

            // Reboot the phone.
            RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            ////////////////////////////////////////////////////////////////////////////
            // Reboot in flash mode to "brick" the phone
            // This is the easiest way to reach our goal: have a phone in Emergency DownLoad (EDL) mode.
            ////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\n\nWe are rebooting to normal mode, then to flash mode again.");
            Console.WriteLine("But as we flashed an original FFU on the phone, these 2 reboots are going to be longer than usual.");
            Console.Write("\nLook for a phone exposing \"Apollo\" device interface ( = \"normal\" mode )");
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

            ApolloDeviceInterface = new USB(devicePath);

            // Send command to reboot in flash mode
            Request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SetDeviceMode\",\"params\":{\"DeviceMode\":\"Flash\",\"ResetMethod\":\"HwReset\",\"MessageVersion\":0}}";
            OutBuffer = System.Text.Encoding.ASCII.GetBytes(Request);
            ApolloDeviceInterface.WritePipe(OutBuffer, OutBuffer.Length);

            Buffer = new byte[0x8000]; 
            ApolloDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            ApolloDeviceInterface.Close();

            Buffer = new byte[0x8000]; 
            // Look for a phone connected on a USB port and exposing interface
            // - known as "Care Connectivity" device interface in WindowsDeviceRecoveryTool / NokiaCareSuite
            // - known as "Old Combi" interface in WPInternals
            // This interface allows flash commands starting with the signature "NOK" (to reboot the phone for example).
            // Notes: 
            // this interface is also exposed when the phone is in "normal" mode.
            // But in "normal" mode the PID of the device is 0x0661
            // Whereas in "flash" or "bootloader" mode the PID of the device is 0x066E
            Console.Write("\nLook for a phone exposing \"Care Connectivity\" device interface.");
            for (int i = 0; i < 120; i++) // Wait 120s max
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_NOKIA_CARE_CONNECTIVITY_DEVICE_INTERFACE));
                if (devicePaths.Count > 0)
                {
                    for (int j = 0; j < devicePaths.Count; j++)
                    {
                        if (devicePaths[j].IndexOf(VID_PID_NOKIA_LUMIA_UEFI_MODE, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            devicePath = devicePaths[j];
                            goto flash_found2;
                        }
                    }
                }
            } while (devicePaths.Count == 0) ;
        flash_found2:
            Console.WriteLine();
            if (devicePath == null)
            {
                Console.WriteLine("Unable to find a phone exposing a \"Care Connectivity\" device interface.");
                ProgramExit(-1);
            }
            devicePath = devicePaths[0];
            if (verbose) Console.WriteLine("Path of the device found:\n{0}", devicePath);

            CareConnectivityDeviceInterface = new USB(devicePath);

            // To enter Emergency DownLoad mode (EDL) we are going to erase a part of the eMMC to "brick" the phone.
            // First, we send the header of a signed FFU file in order to start the flashing:
            ffuHeader = ffu.getCombinedHeader();
            secureFlashCommand = new byte[ffuHeader.Length + 32]; // command header size = 32 bytes
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
            subBlockLength = (uint)ffuHeader.Length + 12;
            secureFlashCommand[16] = (byte)((subBlockLength >> 24) & 0xFF);
            secureFlashCommand[17] = (byte)((subBlockLength >> 16) & 0xFF);
            secureFlashCommand[18] = (byte)((subBlockLength >> 8) & 0xFF);
            secureFlashCommand[19] = (byte)(subBlockLength & 0xFF);
            secureFlashCommand[20] = 0x00; // Header type = 0x00000000
            secureFlashCommand[21] = 0x00;
            secureFlashCommand[22] = 0x00;
            secureFlashCommand[23] = 0x00;
            payloadLength = (uint)ffuHeader.Length;
            secureFlashCommand[24] = (byte)((payloadLength >> 24) & 0xFF);
            secureFlashCommand[25] = (byte)((payloadLength >> 16) & 0xFF);
            secureFlashCommand[26] = (byte)((payloadLength >> 8) & 0xFF);
            secureFlashCommand[27] = (byte)(payloadLength & 0xFF);
            secureFlashCommand[28] = 0; // Header options = 0
            System.Buffer.BlockCopy(ffuHeader, 0, secureFlashCommand, 32, ffuHeader.Length);
            CareConnectivityDeviceInterface.WritePipe(secureFlashCommand, secureFlashCommand.Length);
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);
            flashReturnCode = (int)((Buffer[6] << 8) + Buffer[7]);
            if (flashReturnCode != 0)
            {
                Console.WriteLine("\nFlash of FFU header failed (return code 0x{0:X16})", flashReturnCode);
                ProgramExit(-1);
            }

            Console.WriteLine("\n\"Soft brick\" the phone in order to boot in EDL mode after the next reboot.");
            // Third: Send 1 empty chunk (according to layout in FFU headers, it will be written to first and last chunk) ?
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
                Console.WriteLine("Flash of FFU empty chunk failed (return code 0x{0:X16})", flashReturnCode);
                ProgramExit(-1);
            }

            ////////////////////////////////////////////////////////////////////////////
            // Reboot the phone.
            // As we destroyed the GPT, the phone will go in EDL mode.
            ////////////////////////////////////////////////////////////////////////////
            Console.WriteLine("\nReboot the phone in \"EDL\" mode...");

            // Reboot the phone. As we "bricked" it, it will reboot in Emergency DownLoad mode (EDL)
            RebootCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x52 }; // NOKR = Reboot
            CareConnectivityDeviceInterface.WritePipe(RebootCommand, RebootCommand.Length);
            CareConnectivityDeviceInterface.Close();

            ////////////////////////////////////////////////////////////////////////////
            // Upload and start the programmer
            ////////////////////////////////////////////////////////////////////////////

         repair_bricked_phone:
            Buffer = new byte[0x8000];

            Console.Write("\nLook for a phone exposing \"Lumia Emergency\" device interface");
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
            if (devicePath.IndexOf(VID_PID_QUALCOMM_EMERGENCY_MODE, StringComparison.OrdinalIgnoreCase) == -1)
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

            Console.Write("\nLook for a phone exposing \"Lumia Emergency\" device interface");
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
            if (devicePath.IndexOf(VID_PID_QUALCOMM_EMERGENCY_MODE, StringComparison.OrdinalIgnoreCase) == -1)
            {
                // Vendor ID 0x05C6 : Qualcomm Inc.
                // Product ID 0x9008 : Qualcomm Download
                Console.WriteLine("Incorrect VID (expecting 0x05C6) and/or incorrect PID (expecting 0x9008)");
                ProgramExit(-1);
            }
            EmergencyDeviceInterface = new USB(devicePath);

            ////////////////////////////////////////////////////////////////////////////
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

            if (!repair)
            {
                Console.WriteLine("\nFlash the HACK partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", hackPartition.firstSector, hackContent.Length);
                Qualcomm.Flash((uint)hackPartition.firstSector * 512, hackContent, (uint)hackContent.Length, EmergencyDeviceInterface);
            }

            // To minimize risk of brick we also flash unmodified partitions (MBR, SBL1, TZ, RPM, WINSECAPP)
            // Note: SBL1 is not really modified, just truncated by the HACK partition.
            byte[] ffuMBR = ffu.GetSectors(0, 1);
            Console.WriteLine("\nFlash the MBR partition (start sector 0x0, size 0x{0:X} bytes)...", ffuMBR.Length);
            Qualcomm.Flash(0, ffuMBR, (uint)ffuMBR.Length, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the GPT partition (start sector 0x1, size 0x{0:X} bytes)...", gptContent.GPTBuffer.Length);
            Qualcomm.Flash(0x200, gptContent.GPTBuffer, 0x41FF, EmergencyDeviceInterface); // Bad bounds-check in the flash-loader prohibits to write the last byte.

            Console.WriteLine("\nFlash the SBL2 partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", sbl2Partition.firstSector, sbl2Content.Length);
            Qualcomm.Flash((uint)sbl2Partition.firstSector * 512, sbl2Content, (uint)sbl2Content.Length, EmergencyDeviceInterface);

            if (repair)
            {
                Console.WriteLine("\nFlash the SBL3 partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", ffu.gpt.GetPartition("SBL3").firstSector, ffu.GetPartition("SBL3").Length);
                Qualcomm.Flash((uint)ffu.gpt.GetPartition("SBL3").firstSector * 512, ffu.GetPartition("SBL3"), (uint)ffu.GetPartition("SBL3").Length, EmergencyDeviceInterface);
            }
            else
            {
                Console.WriteLine("\nFlash the engeeniring SBL3 partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", gptContent.GetPartition("SBL3").firstSector, engeeniringSbl3Content.Length);
                Qualcomm.Flash((uint)gptContent.GetPartition("SBL3").firstSector * 512, engeeniringSbl3Content, (uint)engeeniringSbl3Content.Length, EmergencyDeviceInterface);
            }

            Console.WriteLine("\nFlash the UEFI partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", uefiPartition.firstSector, uefiContent.Binary.Length);
            Qualcomm.Flash((uint)uefiPartition.firstSector * 512, uefiContent.Binary, (uint)uefiContent.Binary.Length, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the SBL1 partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", sbl1Partition.firstSector, (sbl1Partition.lastSector - sbl1Partition.firstSector + 1) * 512); // Don't use the size of the array of bytes because in UNLOCK mode the last sector of the partition SBL1 is removed
            Qualcomm.Flash((uint)sbl1Partition.firstSector * 512, sbl1Content, (uint)(sbl1Partition.lastSector - sbl1Partition.firstSector + 1) * 512, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the TZ partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", ffu.gpt.GetPartition("TZ").firstSector, ffu.GetPartition("TZ").Length);
            Qualcomm.Flash((uint)ffu.gpt.GetPartition("TZ").firstSector * 512, ffu.GetPartition("TZ"), (uint)ffu.GetPartition("TZ").Length, EmergencyDeviceInterface);

            Console.WriteLine("\nFlash the RPM partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", ffu.gpt.GetPartition("RPM").firstSector, ffu.GetPartition("RPM").Length);
            Qualcomm.Flash((uint)ffu.gpt.GetPartition("RPM").firstSector * 512, ffu.GetPartition("RPM"), (uint)ffu.GetPartition("RPM").Length, EmergencyDeviceInterface);

            // Workaround for bad bounds-check in flash-loader
            UInt32 WINSECAPPLength = (UInt32)ffu.GetPartition("WINSECAPP").Length;
            UInt32 WINSECAPPStart = (UInt32)ffu.gpt.GetPartition("WINSECAPP").firstSector * 512;

            if ((WINSECAPPStart + WINSECAPPLength) > 0x1E7FE00)
                WINSECAPPLength = 0x1E7FE00 - WINSECAPPStart;

            Console.WriteLine("\nFlash the WINSECAPP partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", ffu.gpt.GetPartition("WINSECAPP").firstSector, ffu.GetPartition("WINSECAPP").Length);
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
            // Reboot the phone
            // It will reboot in flash mode because we previously interrupt a flash session to brick the phone
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

            if (repair)
            {
                // No need to go further in repair mode.
                // We should be able to use another tool (like WPinternals) to finish the repair.
                ProgramExit(0); 
            }

            Console.WriteLine("\nAfter reboot, the phone should be in \"flash\" mode \"in-progress\" : A big \"NOKIA\" in the top part of the screen on a dark red background.\n");
            // This is because we previously interrupt a flash session to brick the phone...

            Console.Write("\nLook for a phone exposing \"Care Connectivity\" device interface");
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
            // Flash the UEFI_BS_NV to unlock the UEFI
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


            // Get info of current partition UEFI_BS_NV
            Partition partitionCurrentUefiBsNv = gptContent.GetPartition("UEFI_BS_NV");

            // Create new patched partition UEFI_BS_NV
            // We mustn't touch the current partition to avoid a boot loop (with gears).
            Partition partitionNewUefiBsNv = new Partition();
            partitionNewUefiBsNv.name = "UEFI_BS_NV";
            partitionNewUefiBsNv.attributes = partitionCurrentUefiBsNv.attributes;
            partitionNewUefiBsNv.firstSector = partitionCurrentUefiBsNv.lastSector + 1;
            partitionNewUefiBsNv.lastSector = partitionNewUefiBsNv.firstSector + (partitionCurrentUefiBsNv.lastSector - partitionCurrentUefiBsNv.firstSector);
            partitionNewUefiBsNv.partitionTypeGuid = partitionCurrentUefiBsNv.partitionTypeGuid;
            partitionNewUefiBsNv.partitionGuid = partitionCurrentUefiBsNv.partitionGuid;
            gptContent.partitions.Add(partitionNewUefiBsNv);

            // Transform current partition UEFI_BS_NV into BACKUP_BS_NV
            partitionCurrentUefiBsNv.name = "BACKUP_BS_NV";
            partitionCurrentUefiBsNv.partitionTypeGuid = Guid.NewGuid();
            partitionCurrentUefiBsNv.partitionGuid = Guid.NewGuid();

            if (verbose) Console.WriteLine("\nRebuild the GPT...");
            gptContent.Rebuild();

            Console.WriteLine("\nFlash the UEFI_BS_NV partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", partitionNewUefiBsNv.firstSector, uefiBsNvContent.Length);
            byte[] flashCommand = new byte[262208]; // command header (64 bytes) + partition  (262144 bytes)
            // We use the normal command NOKF instead of the UFP extended command NOKXFS
            flashCommand[0] = 0x4E; // N
            flashCommand[1] = 0x4F; // O
            flashCommand[2] = 0x4B; // K
            flashCommand[3] = 0x46; // F
            flashCommand[5] = 0x00; // Device type = 0
            flashCommand[11] = (byte)((partitionNewUefiBsNv.firstSector >> 24) & 0xFF); // Start sector
            flashCommand[12] = (byte)((partitionNewUefiBsNv.firstSector >> 16) & 0xFF);
            flashCommand[13] = (byte)((partitionNewUefiBsNv.firstSector >> 8) & 0xFF);
            flashCommand[14] = (byte)(partitionNewUefiBsNv.firstSector & 0xFF);
            flashCommand[15] = 0x00; // Sector count = 512 ( = 262144 / 512)
            flashCommand[16] = 0x00;
            flashCommand[17] = 0x02;
            flashCommand[18] = 0x00;
            flashCommand[19] = 0x00; // Progress (0 - 100)
            flashCommand[24] = 0x00; // Do Verify
            flashCommand[25] = 0x00; //  Is Test
            System.Buffer.BlockCopy(uefiBsNvContent, 0, flashCommand, 64, 262144);
            CareConnectivityDeviceInterface.WritePipe(flashCommand, flashCommand.Length);
            Buffer = new byte[0x8000];
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);

            Console.WriteLine("\nFlash the GPT partition (start sector 0x{0:X}, size 0x{1:X} bytes)...", 1, gptContent.GPTBuffer.Length);
            UInt32 gptSectorCount = (uint)gptContent.GPTBuffer.Length / 512;
            flashCommand = new byte[64 + gptContent.GPTBuffer.Length]; // command header (64 bytes) + partition
            // We use the normal command NOKF instead of the UFP extended command NOKXFS
            flashCommand[0] = 0x4E; // N
            flashCommand[1] = 0x4F; // O
            flashCommand[2] = 0x4B; // K
            flashCommand[3] = 0x46; // F
            flashCommand[5] = 0x00; // Device type = 0
            flashCommand[11] = 0x00; // Start sector = 1
            flashCommand[12] = 0x00;
            flashCommand[13] = 0x00;
            flashCommand[14] = 0x01;
            flashCommand[15] = (byte)((gptSectorCount >> 24) & 0xFF); // Sector count
            flashCommand[16] = (byte)((gptSectorCount >> 16) & 0xFF);
            flashCommand[17] = (byte)((gptSectorCount >> 8) & 0xFF);
            flashCommand[18] = (byte)(gptSectorCount & 0xFF);
            flashCommand[19] = 0x00; // Progress (0 - 100)
            flashCommand[24] = 0x00; // Do Verify
            flashCommand[25] = 0x00; //  Is Test
            System.Buffer.BlockCopy(gptContent.GPTBuffer, 0, flashCommand, 64, gptContent.GPTBuffer.Length);
            CareConnectivityDeviceInterface.WritePipe(flashCommand, flashCommand.Length);
            Buffer = new byte[0x8000];
            CareConnectivityDeviceInterface.ReadPipe(Buffer, Buffer.Length, out bytesRead);

            ////////////////////////////////////////////////////////////////////////////
            // Switch to "mass storage" mode to patch the MainOS and EFIESP partitions
            ////////////////////////////////////////////////////////////////////////////
            bool rootSuccess = false;

            Console.WriteLine("\nSwitch to \"mass storage\" mode...");
            byte[] MassStorageCommand = new byte[] { 0x4E, 0x4F, 0x4B, 0x4D }; // NOKM = Mass Storage
            CareConnectivityDeviceInterface.WritePipe(MassStorageCommand, MassStorageCommand.Length);
            // The phone immediatly switches to "mass storage" and doesn't send a response.

            Console.Write("\nLook for a phone exposing \"Mass Storage\" device interface.");
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
        mass_storage_found:
            Console.WriteLine();
            if (devicePath == null)
            {
                Console.WriteLine("Unable to find a phone exposing a \"Mass Storage\" device interface.");
                ProgramExit(-1);
            }
            if (verbose) Console.WriteLine("\nPath of the device found:\n{0}", devicePath);

            // Find the "drive letter" of the mass storage
            string Drive = null;
            ManagementObjectCollection coll = new ManagementObjectSearcher("select * from Win32_LogicalDisk").Get();
            foreach (ManagementObject logical in coll)
            {
                string Label = "";
                foreach (ManagementObject partition in logical.GetRelated("Win32_DiskPartition"))
                {
                    foreach (ManagementObject drive in partition.GetRelated("Win32_DiskDrive"))
                    {
                        if (drive["PNPDeviceID"].ToString().IndexOf("VEN_QUALCOMM&PROD_MMC_STORAGE") >= 0)
                        {
                            Label = (logical["VolumeName"] == null ? "" : logical["VolumeName"].ToString());
                            if (string.Compare(Label, "MainOS", true) == 0) // Always prefer the MainOS drive-mapping
                            {
                                Drive = logical["Name"].ToString();
                            }
                            goto drive_found;
                        }
                    }
                }
            }
        drive_found:
            Console.WriteLine("");
            if (Drive == null)
            {
                Console.WriteLine("Unable to find the drive letter of the mass storage.");
                ProgramExit(-1);
            }
            if (verbose) Console.WriteLine("\nDriver letter of the mass storage: {0}", Drive);

            // Path of the Extensible Firmware Interface System Partition (EFIESP)
            string EFIESPPath = Drive + @"\EFIESP\";

            //Path of the Main Operating System (MainOS)
            string MainOSPath = Drive + @"\";

            ////////////////////////////////////////////////////////////////////////////
            // Check hash of files
            ////////////////////////////////////////////////////////////////////////////

            // Check hash of the file
            string filePath2 = EFIESPPath + @"Windows\System32\boot\mobilestartup.efi";
            FileStream stream2 = File.OpenRead(filePath2);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream2);
            stream2.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath2);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x20, 0xED, 0xDF, 0x16, 0xCF, 0x5C, 0x28, 0xF1, 0x70, 0x36, 0x53, 0x32, 0x29, 0x0A, 0xCD, 0xBA, 0x8C, 0x2C, 0xD4, 0xC8 }))
            //if (!hash.SequenceEqual(new byte[] { 0x40, 0x3C, 0x49, 0x4E, 0xCB, 0x15, 0x40, 0xCF, 0x7E, 0x86, 0xB4, 0x21, 0x30, 0x15, 0xC7, 0x51, 0x4D, 0x90, 0x77, 0x65 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            string filePath = MainOSPath + @"Windows\System32\SecRuntime.dll";
            FileStream stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0xA3, 0x22, 0x22, 0x8B, 0xBD, 0x07, 0x00, 0x49, 0x12, 0x76, 0x21, 0xD3, 0x5C, 0x7C, 0x94, 0x20, 0x5E, 0x30, 0x2D, 0x5D }))
            //if (!hash.SequenceEqual(new byte[] { 0x7A, 0xD8, 0x91, 0x13, 0xF0, 0x39, 0x66, 0x23, 0xD7, 0xDC, 0x81, 0x55, 0x91, 0xE4, 0xCA, 0x05, 0xB4, 0xB5, 0x22, 0x11 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\pacmanserver.dll";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0xD5, 0x99, 0xBD, 0x0D, 0x8D, 0x61, 0xCE, 0xE4, 0x77, 0x20, 0x94, 0x1E, 0xC7, 0xAD, 0x13, 0x8D, 0xEB, 0x53, 0x7F, 0x6E }))
            //if (!hash.SequenceEqual(new byte[] { 0x8C, 0x1C, 0xA8, 0xE2, 0x50, 0x0E, 0x53, 0x91, 0xD7, 0x9E, 0xA8, 0x00, 0xE6, 0x8C, 0x11, 0x74, 0x21, 0x99, 0x14, 0x81 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\SSPISRV.DLL";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x49, 0x37, 0x09, 0x84, 0xAD, 0x49, 0xFF, 0xCB, 0x5E, 0x33, 0xD4, 0xD9, 0x48, 0x1F, 0xB2, 0xA6, 0x45, 0x9A, 0x58, 0x29 }))
            //if (!hash.SequenceEqual(new byte[] { 0xEE, 0x96, 0xB7, 0x59, 0x04, 0xF2, 0xD6, 0x38, 0xE3, 0xB3, 0x73, 0x45, 0x84, 0xCC, 0xD9, 0xE9, 0x44, 0x8E, 0x1B, 0x66 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\MSV1_0.DLL";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x9A, 0x4A, 0x52, 0xC9, 0xB6, 0x6B, 0x50, 0x8F, 0x8B, 0x5D, 0x29, 0xDC, 0x8F, 0x76, 0xDD, 0xA7, 0x42, 0x6E, 0xA8, 0x52 }))
            //if (!hash.SequenceEqual(new byte[] { 0x2B, 0x73, 0xB4, 0x98, 0x69, 0xEB, 0x7C, 0x1E, 0x11, 0xC3, 0x4F, 0xC0, 0x0E, 0x4D, 0x5A, 0x73, 0x87, 0xBF, 0x15, 0x80 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\MSCOREE.DLL";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x5B, 0xB1, 0x97, 0x35, 0x6E, 0x69, 0xC8, 0x7A, 0x48, 0x90, 0x95, 0xC8, 0x19, 0x2C, 0x81, 0x0F, 0x53, 0x53, 0xD5, 0xF1 }))
            //if (!hash.SequenceEqual(new byte[] { 0x6B, 0x3B, 0xF6, 0xEF, 0x63, 0xE8, 0xA6, 0xCE, 0x2B, 0x49, 0x29, 0x23, 0x86, 0x5A, 0xC4, 0xC4, 0xB0, 0x26, 0x19, 0xE3 }))        
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\AUDIODG.EXE";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0xA2, 0x99, 0xFF, 0xCB, 0x35, 0x32, 0x2E, 0xFE, 0x09, 0x44, 0xA1, 0xD9, 0x38, 0xEC, 0x8F, 0x3F, 0x8F, 0x8B, 0x49, 0x7B }))
            //if (!hash.SequenceEqual(new byte[] { 0xA0, 0xBB, 0x16, 0xA9, 0xE7, 0xE3, 0xD3, 0x91, 0x53, 0x6F, 0x0C, 0x32, 0xAF, 0x2D, 0x5D, 0xEB, 0x09, 0x49, 0xAC, 0x2B }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\NTOSKRNL.EXE";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0xA3, 0xD4, 0x9F, 0x5D, 0x2F, 0x46, 0x99, 0x8B, 0x8C, 0xB5, 0x00, 0xEC, 0x07, 0x05, 0xA9, 0x1D, 0xA2, 0xE1, 0x0B, 0x05 }))
            //if (!hash.SequenceEqual(new byte[] { 0x14, 0x39, 0xC3, 0xAB, 0x0C, 0x84, 0xAB, 0x93, 0xEC, 0xB7, 0x71, 0x21, 0x74, 0x42, 0x08, 0x49, 0x7F, 0x99, 0x33, 0xA6 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\BOOT\winload.efi";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x7D, 0xCF, 0xEF, 0x11, 0x13, 0xE9, 0xD2, 0xC6, 0x48, 0xB8, 0x57, 0xFD, 0x12, 0x58, 0x57, 0xEC, 0x35, 0xC8, 0xEF, 0xFE }))
            //if (!hash.SequenceEqual(new byte[] { 0x02, 0x43, 0x9F, 0x74, 0x01, 0x15, 0x47, 0xEC, 0x21, 0x92, 0x89, 0x56, 0x57, 0x79, 0xDF, 0x36, 0x2D, 0x91, 0x21, 0x43 }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            // Check hash of the file
            filePath = MainOSPath + @"Windows\System32\ci.dll";
            stream = File.OpenRead(filePath);
            sha = new SHA1Managed();
            hash = sha.ComputeHash(stream);
            stream.Close();
            if (verbose)
            {
                Console.Write("\nHash of {0} before patch: ", filePath);
                for (int i = 0; i < hash.Length; i++)
                {
                    Console.Write("{0:X2}", hash[i]);
                }
                Console.WriteLine("");
            }
            if (!hash.SequenceEqual(new byte[] { 0x7C, 0xA9, 0x51, 0xF4, 0xA5, 0x80, 0xC9, 0x23, 0xD1, 0x2F, 0xB0, 0x9B, 0x2E, 0xEB, 0xD3, 0x5E, 0x2F, 0x6B, 0xE8, 0xA5 }))
            //if (!hash.SequenceEqual(new byte[] { 0xF0, 0x5F, 0xE9, 0x6F, 0xB9, 0x95, 0x2B, 0x57, 0xBC, 0x1C, 0x30, 0x79, 0x25, 0x82, 0x02, 0x85, 0x85, 0x78, 0x73, 0x2D }))
            {
                Console.WriteLine("The hash of the file doesn't match the expected hash. Only version 8.10.14234.375 is supported.\nReboot in normal mode...");
                goto exit_mass_storage;
            }

            //////////////////////////////////////////////////////////////////////////////
            //// Replace EFIESP mobilestartup.efi with the patched version
            //////////////////////////////////////////////////////////////////////////////

            filePath = EFIESPPath + @"Windows\System32\boot\mobilestartup.efi";
            Console.WriteLine("Replace {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            Privilege restorePrivilege2 = new Privilege(Privilege.Restore);
            restorePrivilege2.Enable();
            FileSecurity originalACL2 = Privilege.prepareFileModification(filePath);

            // Replace file content (new content is bigger than previous content)
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x00000000;
            stream.Write(mobilestartupefiContent, 0, mobilestartupefiContent.Length);
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL2, restorePrivilege2);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS SecRuntime.dll
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\SecRuntime.dll";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            Privilege restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            FileSecurity originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x0000A088;
            stream.Write(new byte[] { 0x01, 0x21, 0x19, 0x60, 0x00, 0x20, 0x70, 0x47 }, 0, 8); // was 0x0F,0xB4,0x2D,0xE9,0xF0,0x4F,0x0D,0xF1
            stream.Position = 0x00000148;
            stream.Write(new byte[] { 0x04, 0x47, 0x02, 0x00 }, 0, 4); // was 0x7F,0x82,0x01,0x00
            stream.Position = 0x0000D9AC;
            stream.Write(new byte[] { 0x01, 0x21, 0x01, 0x60, 0x00, 0x20, 0x70, 0x47 }, 0, 8); // was 0x2D,0xE9,0xF0,0x48,0x0D,0xF1,0x10,0x0B
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS pacmanserver.dll
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\pacmanserver.dll";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x0004F364;
            stream.Write(new byte[] { 0x01, 0x49, 0x01, 0x60, 0x00, 0x20, 0x70, 0x47, 0xFF, 0xFF, 0xFF, 0x7F }, 0, 12); // was 0x2D,0xE9,0x70,0x48,0x0D,0xF1,0x0C,0x0B,0xEA,0xF7,0x2A,0xFC
            stream.Position = 0x00000148;
            stream.Write(new byte[] { 0x71, 0x10, 0x0E, 0x00 }, 0, 4); // was 0xCD,0xA1,0x0D,0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS SSPISRV.DLL
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\SSPISRV.DLL";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x00002454;
            stream.Write(new byte[] { 0x01, 0x21, 0x01, 0x60, 0x00, 0x20, 0x70, 0x47 }, 0, 8); // was 0x2D,0xE9,0x70,0x48,0x0D,0xF1,0x0C,0x0B
            stream.Position = 0x00000140;
            stream.Write(new byte[] { 0xA0, 0x82, 0x01, 0x00 }, 0, 4); // was 0xE6,0xC7,0x00,0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS MSV1_0.DLL
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\MSV1_0.DLL";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x000035A8;
            stream.Write(new byte[] { 0x01, 0x20, 0x70, 0x47 }, 0, 4); // was 0x2D, 0xE9, 0xF0, 0x4F
            stream.Position = 0x00000148;
            stream.Write(new byte[] { 0x86, 0xDE, 0x05, 0x00 }, 0, 4); // was 0x33, 0xB0, 0x05, 0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS MSCOREE.DLL
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\MSCOREE.DLL";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x0000579C;
            stream.Write(new byte[] { 0x00, 0x20, 0x70, 0x47 }, 0, 4); // was 0x2D, 0xE9, 0xF0, 0x48
            stream.Position = 0x00000140;
            stream.Write(new byte[] { 0x3F, 0xB4, 0x01, 0x00 }, 0, 4); // was 0xED, 0x7E, 0x01, 0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            //////////////////////////////////////////////////////////////////////////// 
            // Patch MainOS AUDIODG.EXE
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\AUDIODG.EXE";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x00000140;
            stream.Write(new byte[] { 0x6C, 0x48, 0x03, 0x00 }, 0, 4); // was 0x7F, 0x83, 0x03, 0x00
            stream.Position = 0x00000180;
            stream.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0, 8); // was 0x00, 0x10, 0x03, 0x00, 0x10, 0x2B, 0x00, 0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            //////////////////////////////////////////////////////////////////////////// 
            // Patch MainOS NTOSKRNL.EXE
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\NTOSKRNL.EXE";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x00027F1A;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0x01, 0xD1
            stream.Position = 0x00000158;
            stream.Write(new byte[] { 0x41, 0x31, 0x50, 0x00 }, 0, 4); // was 0x48, 0x51, 0x50, 0x00
            stream.Position = 0x00027F6C;
            stream.Write(new byte[] { 0xF7, 0xE7 }, 0, 2); // was 0xF7, 0xD0
            stream.Position = 0x000284B8;
            stream.Write(new byte[] { 0x1D, 0xE0 }, 0, 2); // was 0x1D, 0xD2
            stream.Position = 0x000284E0;
            stream.Write(new byte[] { 0x05, 0xE0 }, 0, 2); // was 0x05, 0xD1
            stream.Position = 0x00028918;
            stream.Write(new byte[] { 0x02, 0xE0 }, 0, 2); // was 0x14, 0xB9
            stream.Position = 0x00028A4A;
            stream.Write(new byte[] { 0x4F, 0xE0 }, 0, 2); // was 0x4F, 0xD0
            stream.Position = 0x00028B2E;
            stream.Write(new byte[] { 0x02, 0xE0 }, 0, 2); // was 0x02, 0xD1
            stream.Position = 0x00028B5E;
            stream.Write(new byte[] { 0x01, 0xE0 }, 0, 2); // was 0x08, 0xB9
            stream.Position = 0x002140FC;
            stream.Write(new byte[] { 0x04, 0xE0 }, 0, 2); // was 0x22, 0xB9
            stream.Position = 0x002145C4;
            stream.Write(new byte[] { 0x1A, 0xE0 }, 0, 2); // was 0x1A, 0xD1
            stream.Position = 0x0021463C;
            stream.Write(new byte[] { 0x1E, 0xE0 }, 0, 2); // was 0xF3, 0xB1
            stream.Position = 0x001793EC;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0xEB, 0xB9
            stream.Position = 0x00058448;
            stream.Write(new byte[] { 0x00, 0xF0, 0xD9, 0xB9 }, 0, 4); // was 0x00, 0xF0, 0xD9, 0x81
            stream.Position = 0x002306B8;
            stream.Write(new byte[] { 0x01, 0x20, 0x70, 0x47 }, 0, 4); // was 0x0F, 0xB4, 0x2D, 0xE9
            stream.Position = 0x00226444;
            stream.Write(new byte[] { 0xFA, 0xF0, 0x61, 0xBB }, 0, 4); // was 0x3A, 0xF0, 0x61, 0xAB
            stream.Position = 0x0002C27C;
            stream.Write(new byte[] { 0x01, 0x20, 0x70, 0x47 }, 0, 4); // was 0x2D, 0xE9, 0xF0, 0x4F
            stream.Position = 0x00027744;
            stream.Write(new byte[] { 0x00, 0x20, 0x70, 0x47 }, 0, 4); // was 0x2D, 0xE9, 0x00, 0x48
            stream.Position = 0x00134C24;
            stream.Write(new byte[] { 0xFF, 0xF7, 0x85, 0xBF }, 0, 4); // was 0xBA, 0xF1, 0x00, 0x7F
            stream.Position = 0x00134CD4;
            stream.Write(new byte[] { 0x02, 0xE0 }, 0, 2); // was 0x13, 0xB1
            stream.Position = 0x00134D58;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0xBD, 0xD1
            stream.Position = 0x00134D96;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0x9E, 0xD1
            stream.Position = 0x00134E66;
            stream.Write(new byte[] { 0xAF, 0xF3, 0x00, 0x80 }, 0, 4); // was 0x7F, 0xF4, 0x36, 0xAF
            stream.Position = 0x00134EA2;
            stream.Write(new byte[] { 0xFF, 0xF7, 0x46, 0xBE }, 0, 4); // was 0x3F, 0xF4, 0x46, 0xAE
            stream.Position = 0x00134EE0;
            stream.Write(new byte[] { 0xFF, 0xF7, 0x27, 0xBE }, 0, 4); // was 0x3F, 0xF4, 0x27, 0xAE
            stream.Position = 0x00134B36;
            stream.Write(new byte[] { 0xAF, 0xF3, 0x00, 0x80 }, 0, 4); // was 0x00, 0xF0, 0xE5, 0x81
            stream.Position = 0x00058C80;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0x5C, 0xD1
            stream.Position = 0x00058CA0;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0x4C, 0xD1
            stream.Position = 0x00058DA6;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0xC9, 0xD1
            stream.Position = 0x0010084E;
            stream.Write(new byte[] { 0x58, 0xF7, 0x30, 0xBA }, 0, 4); // was 0xBE, 0xF1, 0x00, 0x7F
            stream.Position = 0x00058CBA;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0x40, 0xD0
            stream.Position = 0x00058E42;
            stream.Write(new byte[] { 0xFF, 0xF7, 0x34, 0xBF }, 0, 4); // was 0x3F, 0xF4, 0x34, 0xAF
            stream.Position = 0x00058FCA;
            stream.Write(new byte[] { 0xFF, 0xF7, 0x72, 0xBE }, 0, 4); // was 0x3F, 0xF4, 0x72, 0xAE
            stream.Position = 0x00110A84;
            stream.Write(new byte[] { 0x0B, 0xE0 }, 0, 2); // was 0x0F, 0xD0
            stream.Position = 0x000B125A;
            stream.Write(new byte[] { 0xAF, 0xF3, 0x00, 0x80 }, 0, 4); // was 0x1F, 0xF0, 0xF2, 0xA3
            stream.Position = 0x000B1260;
            stream.Write(new byte[] { 0xAF, 0xF3, 0x00, 0x80 }, 0, 4); // was 0x1F, 0xF0, 0xE6, 0xA3
            stream.Position = 0x00027778;
            stream.Write(new byte[] { 0x00, 0xBF }, 0, 2); // was 0x02, 0xD1
            stream.Position = 0x001008D2;
            stream.Write(new byte[] { 0x03, 0xE0 }, 0, 2); // was 0x03, 0xD1
            stream.Position = 0x001008E0;
            stream.Write(new byte[] { 0x03, 0xE0 }, 0, 2); // was 0x03, 0xD0
            stream.Position = 0x0010097E;
            stream.Write(new byte[] { 0x58, 0xF7, 0x98, 0xB9 }, 0, 4); // was 0x18, 0xF4, 0x98, 0xA1
            stream.Position = 0x00110ADC;
            stream.Write(new byte[] { 0x09, 0xE0 }, 0, 2); // was 0x11, 0xD0
            stream.Position = 0x00110B3A;
            stream.Write(new byte[] { 0x11, 0xE0 }, 0, 2); // was 0x0B, 0xD0
            stream.Position = 0x00226546;
            stream.Write(new byte[] { 0xD3, 0xE7 }, 0, 2); // was 0xD3, 0xD0
            stream.Position = 0x00226550;
            stream.Write(new byte[] { 0xAF, 0xF3, 0x00, 0x80 }, 0, 4); // was 0x7A, 0xF0, 0x8A, 0xAA
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS winload.efi
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\BOOT\winload.efi";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x000331C4;
            stream.Write(new byte[] { 0x00, 0x20, 0x70, 0x47 }, 0, 4); // was 0x2D, 0xE9, 0xF0, 0x4F
            stream.Position = 0x00000140;
            stream.Write(new byte[] { 0xBC, 0x83, 0x0C, 0x00 }, 0, 4); // was 0x6A, 0x55, 0x0C, 0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);

            ////////////////////////////////////////////////////////////////////////////
            // Patch MainOS ci.dll
            ////////////////////////////////////////////////////////////////////////////

            filePath = MainOSPath + @"Windows\System32\ci.dll";
            Console.WriteLine("Patch {0}", filePath);

            // Enable Take Ownership AND Restore ownership to original owner
            // Take Ownership Privilge is not enough.
            // We need Restore Privilege.
            restorePrivilege = new Privilege(Privilege.Restore);
            restorePrivilege.Enable();
            originalACL = Privilege.prepareFileModification(filePath);

            // Patch file
            stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 0x000179FC;
            stream.Write(new byte[] { 0x1B, 0xE0 }, 0, 2); // was 0x1B, 0xD1
            stream.Position = 0x00000158;
            stream.Write(new byte[] { 0x3E, 0x79, 0x07, 0x00 }, 0, 4); // was 0x3E, 0x6A, 0x07, 0x00
            stream.Close();

            Privilege.finishFileModification(filePath, originalACL, restorePrivilege);
            rootSuccess = true;

            //////////////////////////////////////////////////////////////////////////////
            //// Update BCD content
            //// Deactivate "integrity check"
            //////////////////////////////////////////////////////////////////////////////

            Console.WriteLine("\nSet \"nointegritychecks yes\" in BCD.");
            string BCDpath = EFIESPPath + @"efi\Microsoft\Boot\BCD";
            Process process2 = new Process();
            process2.StartInfo.FileName = "cmd.exe";
            process2.StartInfo.Arguments = "/c bcdedit /store \"" + BCDpath + "\"  /set {01de5a27-8705-40db-bad6-96fa5187d4a6} nointegritychecks yes "; // Windows Boot Application "Mobile Startup App" (mobilestartup.efi)
            process2.StartInfo.UseShellExecute = false;
            process2.StartInfo.RedirectStandardOutput = true;
            if (verbose) Console.WriteLine("\nExecute:\n{0} {1}", process2.StartInfo.FileName, process2.StartInfo.Arguments);
            process2.Start();
            process2.WaitForExit();
            string processOutput2 = process2.StandardOutput.ReadToEnd();
            if (verbose) Console.WriteLine("\nResult:\n{0}", processOutput2);

            process2 = new Process();
            process2.StartInfo.FileName = "cmd.exe";
            process2.StartInfo.Arguments = "/c bcdedit /store \"" + BCDpath + "\"  /set {7619dcc9-fafe-11d9-b411-000476eba25f} nointegritychecks yes "; // Windows Boot Loader "Windows Loader" (winload.efi)
            process2.StartInfo.UseShellExecute = false;
            process2.StartInfo.RedirectStandardOutput = true;
            if (verbose) Console.WriteLine("\nExecute:\n{0} {1}", process2.StartInfo.FileName, process2.StartInfo.Arguments);
            process2.Start();
            process2.WaitForExit();
            processOutput2 = process2.StandardOutput.ReadToEnd();
            if (verbose) Console.WriteLine("\nResult:\n{0}", processOutput2);


            ////////////////////////////////////////////////////////////////////////////
            // Reboot from Mass Storage to Normal mode
            ////////////////////////////////////////////////////////////////////////////
            exit_mass_storage:
            // When in Mass Storage mode it's only possible to communicate using the com port (Qualcomm HS-USB Diagnostics 9006)
            Console.Write("\nLook for a phone exposing a \"Com Port\" device interface.");
            devicePath = null;
            for (int i = 0; i < 15; i++) // Wait 15s max
            {
                Thread.Sleep(1000);
                Console.Write(".");
                devicePaths = USB.FindDevicePathsFromGuid(new Guid(GUID_COM_PORT_DEVICE_INTERFACE));
                if (devicePaths.Count > 0)
                {
                    for (int j = 0; j < devicePaths.Count; j++)
                    {
                        if (devicePaths[j].IndexOf(VID_PID_QUALCOMM_DIAGNOSTIC, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            devicePath = devicePaths[j];
                            goto diagnostic_found;
                        }
                    }
                }
            }
        diagnostic_found:
            Console.WriteLine();
            if (devicePath == null)
            {
                Console.WriteLine("Unable to find a phone exposing a \"com port\" device interface.");
                ProgramExit(-1);
            }
            if (verbose) Console.WriteLine("\nPath of the device found:\n{0}", devicePath);

            // Get the port name
            string[] devicePathElements = devicePath.Split(new char[] { '#' });
            string registryKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\USB\" + devicePathElements[1] + @"\" + devicePathElements[2] + @"\Device Parameters";
            if (verbose) Console.WriteLine("\nRegistry key: {0}", registryKey);
            Process process = new Process();
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments= "/c reg query \""+ registryKey+"\" /v PortName ";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            if (verbose) Console.WriteLine("\nExecute:\n{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);
            process.Start();
            process.WaitForExit();
            string processOutput = process.StandardOutput.ReadToEnd();
            if (verbose) Console.WriteLine("\nResult:\n{0}",processOutput);
            string portName = null;
            Match portNameRegEx = Regex.Match(processOutput, @".*(COM[0-9]+)");
            if (portNameRegEx.Success)
            {
                portName= portNameRegEx.Groups[1].Value;
            }
            else
            {
                Console.WriteLine("Unable to find the port name.");
                ProgramExit(-1);
            }

            if (verbose) Console.WriteLine("Port: {0}", portName);
            SerialPort port = new SerialPort(portName, 115200);
            port.ReadTimeout = 1000;
            port.WriteTimeout = 1000;
            port.Open();

            Console.WriteLine("\nSend reset command...");
            // Send Reset command (0x0A) to perform a hardware reset 
            byte[] resetCommand = Qualcomm.encodeHDLC(new byte[] { 0x0A }, 1); // command (1byte)
            if (verbose) printRawConsole(resetCommand, resetCommand.Length, true);
            port.Write(resetCommand, 0, resetCommand.Length);

            port.Close();

            if (rootSuccess)
                ProgramExit(0);
            else
                ProgramExit(-1);
        }

        private static void ProgramExit(int exitCode)
        {
            Console.WriteLine("Press ENTER.");
            Console.ReadLine();
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
            Console.WriteLine("[--verbose]");
            Console.WriteLine("\t--ffu=<.ffu file>");
            Console.WriteLine("\t--donorFfu=<.ffu file containing of valid mobilestartup.efi>");
            Console.WriteLine("\t--hex=<.hex programmer file>");
            Console.WriteLine("\t--sbl3Bin=<.bin engeeniring SBL3 file>");
            Console.WriteLine("\t--uefiBsNvBin=<.bin unlocked UEFI_BS_NV file>");
        }

        private static void printRawConsole(byte[] values, int length, bool write)
        {
            if (write)
            {
                Console.WriteLine("< {0} bytes", length);
            }
            else
            {
                Console.WriteLine("> {0} bytes", length);
            }

            string characters = "";
            int normalizedLength = ((length / 19) + 1) * 19; // display 19 values by line
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
        }
    }
}