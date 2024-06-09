using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    class CareConnectivity
    {

        // see https://github.com/WOA-Project/UnifiedFlashingPlatform
        // NOKV = Info Query
        public static void parseNOKV(byte[] values, int length)
        {
            if (length < 14) // header + app version + 1 subblock
            {
                Console.WriteLine("Response too short.");
                return;
            }

            // First 4 values must be NOKV
            bool isNOKV = values[0] == 'N' && values[1] == 'O' && values[2] == 'K' && values[3] == 'V';
            if (!isNOKV)
            {
                Console.WriteLine("Not NOKV response.");
                return;
            }

            // App type: BootManager = 1, FlashApp = 2, PhoneInfoApp = 3
            string app = "Unknown";
            switch (values[5])
            {
                case 1:
                    app = "BootManager";
                    break;
                case 2:
                    app = "FlashApp";
                    break;
                case 3:
                    app = "PhoneInfoApp";
                    break;
            }

            Console.WriteLine("{0}:", app);
            Console.WriteLine("\tprotocol: {0}.{1}", values[6], values[7]);
            Console.WriteLine("\tversion: {0}.{1}", values[8], values[9]);

            Console.WriteLine("Sub-blocks:");
            int subblockCount = values[10];
            int subblockOffset = 11;
            for (int i = 0; i < subblockCount; i++)
            {
                // ID:
                // 0x01: Transfer size (UInt32)
                // 0x02 : Write buffer size (UInt32)
                // 0x03 : Emmc size in sectors (UInt32)
                // 0x04 : 
                //  BootManager : 
                //      FlashApp protocol version major (byte)
                //      FlashApp protocol version minor (byte)
                //      FlashApp version major (byte)
                //      FlashApp version minor (byte)
                //  FlashApp : SDCard size in sectors (UInt32)
                // 0x05 : Platform ID (String)
                // 0x0D : Async support (Boolean)
                // 0x0F : 
                //  Sub-Block version (0x03 expected)
                //  Platform secure boot enabled (Boolean)
                //  Secure FFU enabled (Boolean)
                //  Jtag disabled (Boolean)
                //  Rdc present (Boolean)
                //  Authenticated (0x01 or 0x02) 
                //  UEFI secure boot (Boolean)
                //  Secondary hardware key present (Boolean)
                // 0x10 : 
                //  Sub-Block version (0x01 expected)
                //  Secure FFU supported protocol mask (UInt16)
                // 0x1F : MMOS over USB supported (Boolean)
                // 0x20 : CRC header info
                // 0x22 :
                //   Sector count (UInt32)
                //   Sector size (UInt32)
                //   Flash type (UInt16)
                //   Flash type index (UInt16)
                //   Unknown (UInt32)
                //   Device path (UnicodeString)
                // 0x23 :
                //   Manufacturer length (UInt16)
                //   Family length (UInt16)
                //   Product name length (UInt16)
                //   Product version length (UInt16)
                //   SKU number length (UInt16)
                //   Baseboard manufacturer length (UInt16)
                //   Baseboard product length (UInt16)
                //   Manufacturer (String)
                //   Family (String)
                //   Product name (String)
                //   Product version (String)
                //   SKU number (String)
                //   Baseboard manufacturer (String)
                //   Baseboard product (String)
                // 0x25 : App type (Byte)

                int subblockID = values[subblockOffset];
                int subblockLength = (values[subblockOffset + 1] << 8) + values[subblockOffset + 2];
                Console.WriteLine("\tID: 0x{0:X2} size: {1} bytes", subblockID, subblockLength);
                subblockOffset += subblockLength + 3; // ID (1 byte) + Length (2 bytes)
            }
        }

        public static void parseNOKXFRCID(byte[] values, int length)
        {
            if (length < 19) // header + manufacturerID
            {
                Console.WriteLine("Response too short.");
                return;
            }

            // First 4 values must be NOKX
            bool isNOKX = values[0] == 'N' && values[1] == 'O' && values[2] == 'K' && values[3] == 'X';
            if (!isNOKX)
            {
                Console.WriteLine("Not NOKX response.");
                return;
            }

            int manufacturerID = (values[17] << 8) + values[18];
            string Manufacturer = null;
            switch (manufacturerID)
            {
                case 0x0002:
                case 0x0045:
                    Manufacturer = "SanDisk";
                    break;
                case 0x0011:
                    Manufacturer = "Toshiba";
                    break;
                case 0x0013:
                    Manufacturer = "Micron";
                    break;
                case 0x0015:
                    Manufacturer = "Samsung";
                    break;
                case 0x0090:
                    Manufacturer = "Hynix";
                    break;
                case 0x0070:
                    Manufacturer = "Kingston";
                    break;
                case 0x00EC:
                    Manufacturer = "GigaDevice";
                    break;
                default:
                    Manufacturer = "Unknown";
                    break;
            }
            Console.WriteLine("eMMC manufacturer: {0}", Manufacturer);
        }

        public static void parseNOKT(byte[] values, int length)
        {
            if (length < 0x4408) // header (NOKT) + error code (2 bytes) + 34 sectors of 512 bytes
            {
                Console.WriteLine("Response too short.");
                return;
            }

            // First 4 values must be NOKT
            bool isNOKT = values[0] == 'N' && values[1] == 'O' && values[2] == 'K' && values[3] == 'T';
            if (!isNOKT)
            {
                Console.WriteLine("Not NOKT response.");
                return;
            }

            ushort error = (ushort)((values[6] << 8) + values[7]);
            if (error > 0)
            {
                Console.WriteLine("Error 0x" + error.ToString("X4"));
                return;
            }

            uint sectorSize;
            if (length == 0x4408) // header + error code + 34 sectors of 0x200 bytes.
            {
                sectorSize = 512; // 0x200
            }
            else if (length == 0x6008) // header + error code + 6 sectors of 0x1000 bytes.
            {
                sectorSize = 4096; // 0x1000
            }
            else
            {
                Console.WriteLine("Unsupported sector size.");
                return;
            }

            // first sector is Master Boot Record (MBR)

            byte[] headerPattern = new byte[] { 0x45,0x46,0x49,0x20,0x50,0x41,0x52,0x54}; // "EFI PART"
            int headerOffset = -1;
            for (int i=0; i < length; i++)
            {
                if (values.Skip(i).Take(headerPattern.Length).SequenceEqual(headerPattern))
                {
                    headerOffset = i;
                }
            }
            if (headerOffset == -1)
            {
                Console.WriteLine("Header not found.");
                return;
            }

            uint headerSize = (uint)(values[headerOffset + 15] << 24) + (uint)(values[headerOffset + 14] << 16) + (uint)(values[headerOffset + 13] << 8) + values[headerOffset + 12];
            uint tableOffset = (uint)headerOffset + sectorSize;
            ulong firstUsableSector = (ulong)(values[headerOffset + 47] << 56) + (ulong)(values[headerOffset + 46] << 48) + (ulong)(values[headerOffset + 45] << 40) + (ulong)(values[headerOffset + 44] << 32) + (ulong)(values[headerOffset + 43] << 24) + (ulong)(values[headerOffset + 42] << 16) + (ulong)(values[headerOffset + 41] << 8) + values[headerOffset + 40];
            ulong lastUsableSector = (ulong)(values[headerOffset + 55] << 56) + (ulong)(values[headerOffset + 54] << 48) + (ulong)(values[headerOffset + 53] << 40) + (ulong)(values[headerOffset + 52] << 32) + (ulong)(values[headerOffset + 51] << 24) + (ulong)(values[headerOffset + 50] << 16) + (ulong)(values[headerOffset + 49] << 8) + values[headerOffset + 48];
            uint maxPartitions = (uint)(values[headerOffset + 83] << 24) + (uint)(values[headerOffset + 82] << 16) + (uint)(values[headerOffset + 81] << 8) + values[headerOffset + 80];
            uint partitionEntrySize = (uint)(values[headerOffset + 87] << 24) + (uint)(values[headerOffset + 86] << 16) + (uint)(values[headerOffset + 85] << 8) + values[headerOffset + 84];
            uint tableSize = maxPartitions * partitionEntrySize;
            if (tableOffset + tableSize > length)
            {
                Console.WriteLine("Response too short compared to the GPT table size.");
                return;
            }

            uint partitionOffset = tableOffset;
            while (partitionOffset < tableOffset + tableSize)
            {
                byte[] nameBuffer = new byte[72];
                Buffer.BlockCopy(values, (int)partitionOffset+56, nameBuffer, 0, 72);
                string name = System.Text.Encoding.Unicode.GetString(nameBuffer);
                Console.WriteLine("Partition {0} {1}", name, name.Length);

                partitionOffset += partitionEntrySize;
            }

        }
    }
}
