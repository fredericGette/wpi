using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    class CareConnectivity
    {
        public static void printRaw(byte[] values, int length)
        {
            string characters = "";
            int normalizedLength = ((length / 18) + 1) * 18;  
            for (int i=0; i< normalizedLength; i++)
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

                if ((i+1)%18 == 0)
                {
                    Console.WriteLine(characters);
                    characters = "";
                }
            }
        }


        // see https://github.com/WOA-Project/UnifiedFlashingPlatform
        // NOKV = Info Query
        public static void parseNOKV(byte[] values, int length)
        {
            printRaw(values, length);
            if (length < 14) // header + flash app version + 1 subblock
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
            bool isFlashApp = values[5] == 2;
            if (!isFlashApp)
            {
                Console.WriteLine("Response doesn't come from FlashApp.");
                return;
            }

            Console.WriteLine("Flash App:");
            Console.WriteLine("\tprotocol: {0}.{1}", values[6], values[7]);
            Console.WriteLine("\tversion: {0}.{1}", values[8], values[9]);

            Console.WriteLine("Sub-blocks:");
            int subblockCount = values[10];
            int subblockOffset = 11;
            for (int i=0; i < subblockCount; i++)
            {
                // ID:
                // 0x01: Transfer size (UInt32)
                // 0x02 : Write buffer size (UInt32)
                // 0x03 : Emmc size in sectors (UInt32)
                // 0x04 : SDCard size in sectors (UInt32)
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
    }
}
