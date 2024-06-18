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

            // note: values[16] contains the size of the response

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

        public static List<Partition> parseNOKT(byte[] values, int length)
        {
            List<Partition> partitions = new List<Partition>();

            if (length < 0x4408) // header (NOKT) + error code (2 bytes) + 34 sectors of 512 bytes
            {
                Console.WriteLine("Response too short.");
                return partitions;
            }

            // First 4 values must be NOKT
            bool isNOKT = values[0] == 'N' && values[1] == 'O' && values[2] == 'K' && values[3] == 'T';
            if (!isNOKT)
            {
                Console.WriteLine("Not NOKT response.");
                return partitions;
            }

            ushort error = (ushort)((values[6] << 8) + values[7]);
            if (error > 0)
            {
                Console.WriteLine("Error 0x" + error.ToString("X4"));
                return partitions;
            }

            partitions = GPT.parse(values.Skip(8).Take(length - 8).ToArray(), length-8); // header + error code = 8 bytes

            return partitions;
        }

        public static byte[] parseNOKXFRRRKH(byte[] values, int length)
        {
            if (length < 48) // header + RKH (32 bytes)
            {
                Console.WriteLine("Response too short.");
                return null;
            }

            // First 4 values must be NOKX
            bool isNOKX = values[0] == 'N' && values[1] == 'O' && values[2] == 'K' && values[3] == 'X';
            if (!isNOKX)
            {
                Console.WriteLine("Not NOKX response.");
                return null;
            }

            // values[16] indicates the size of the response
            // It should be 32 in our case
            byte[] rkh = values.Skip(17).Take(values[16]).ToArray();

            Console.Write("Phone Root Hash Key ({0} bits): ", rkh.Length*8);
            for (int i=0; i<rkh.Length; i++)
            {
                Console.Write("{0:X2}", rkh[i]);
            }
            Console.WriteLine("");

            return rkh;
        }
    }
}
