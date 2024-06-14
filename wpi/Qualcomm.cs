using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace wpi
{
    class Qualcomm
    {

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

        public static byte[] parseSBL1(byte[] values)
        {
            uint Offset = 0x2800; // Offset in case of SBL1 partition.

            // Check we have a "Long Header"
            byte[] LongHeaderStart = new byte[] { 0xD1, 0xDC, 0x4B, 0x84, 0x34, 0x10, 0xD7, 0x73};
            byte[] LongHeaderEnd = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            byte[] headerStart = values.Skip((int)Offset).Take(LongHeaderStart.Length).ToArray();
            if (!headerStart.SequenceEqual(LongHeaderStart))
            {
                Console.WriteLine("Start of header doesn't match \"Long Header\" values.");
                Console.WriteLine("Values:");
                printRaw(headerStart, headerStart.Length);
                return null;
            }
            byte[] headerEnd = values.Skip((int)Offset + LongHeaderStart.Length + 4).Take(LongHeaderEnd.Length).ToArray();
            if (!headerEnd.SequenceEqual(LongHeaderEnd))
            {
                Console.WriteLine("End of header doesn't match \"Long Header\" values.");
                Console.WriteLine("Values:");
                printRaw(headerEnd, headerEnd.Length);
                return null;
            }

            uint HeaderOffset = Offset + 20; // total size of "Long Header"
            uint ImageOffset = values[HeaderOffset] + (uint)(values[HeaderOffset + 1] << 8) + (uint)(values[HeaderOffset + 2] << 16) + (uint)(values[HeaderOffset + 2] << 24);

            uint ImageAddress = values[HeaderOffset + 4] + (uint)(values[HeaderOffset + 5] << 8) + (uint)(values[HeaderOffset + 6] << 16) + (uint)(values[HeaderOffset + 7] << 24);
            uint CertificatesAddress = values[HeaderOffset + 24] + (uint)(values[HeaderOffset + 25] << 8) + (uint)(values[HeaderOffset + 26] << 16) + (uint)(values[HeaderOffset + 27] << 24);
            uint CertificatesSize = values[HeaderOffset + 28] + (uint)(values[HeaderOffset + 29] << 8) + (uint)(values[HeaderOffset + 30] << 16) + (uint)(values[HeaderOffset + 31] << 24);
            uint CertificatesOffset = CertificatesAddress - ImageAddress + ImageOffset;

            uint CurrentCertificateOffset = CertificatesOffset;
            uint CertificateSize = 0;
            byte[] RootKeyHash = null;
            while (CurrentCertificateOffset < (CertificatesOffset + CertificatesSize))
            {
                if ((values[CurrentCertificateOffset] == 0x30) && (values[CurrentCertificateOffset + 1] == 0x82))
                {
                    CertificateSize = (uint)(values[CurrentCertificateOffset + 2] << 8) + values[CurrentCertificateOffset + 3] + 4; // Big endian!

                    if ((CurrentCertificateOffset + CertificateSize) == (CertificatesOffset + CertificatesSize))
                    {
                        // This is the last certificate. So this is the root key.
                        RootKeyHash = new SHA256Managed().ComputeHash(values, (int)CurrentCertificateOffset, (int)CertificateSize);

                        Console.Write("SBL1 Root Hash Key ({0} bits): ", RootKeyHash.Length*8);
                        for (int i = 0; i < RootKeyHash.Length; i++)
                        {
                            Console.Write("{0:X2}", RootKeyHash[i]);
                        }
                        Console.WriteLine("");
                    }
                    CurrentCertificateOffset += CertificateSize;
                }
                else
                {
                    if ((RootKeyHash == null) && (CurrentCertificateOffset > CertificatesOffset))
                    {
                        CurrentCertificateOffset -= CertificateSize;

                        // This is the last certificate. So this is the root key.
                        RootKeyHash = new SHA256Managed().ComputeHash(values, (int)CurrentCertificateOffset, (int)CertificateSize);

                        Console.Write("SBL1 Root Hash Key ({0} bits): ", RootKeyHash.Length*8);
                        for (int i = 0; i < RootKeyHash.Length; i++)
                        {
                            Console.Write("{0:X2}", RootKeyHash[i]);
                        }
                        Console.WriteLine("");
                    }
                    break;
                }
            }
            return RootKeyHash;
        }

        public static byte[] createHACK(byte[] sbl1, byte[] sbl2)
        {
            // Copy the header of the SBL2 partition
            //byte[] sbl2Header = new byte[12];
            //Buffer.BlockCopy(sbl2, 0, sbl2Header, 0, sbl2Header.Length);

            // Find the position of the PartitionLoaderTable inside SBL1
            byte[] patternToFind = new byte[] {
                0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x05, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            int patternPosition = -1;
            for (int i = 0; i < sbl1.Length; i++)
            {
                if (sbl1.Skip(i).Take(patternToFind.Length).SequenceEqual(patternToFind))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern of the PartitionLoaderTable in the SBL1 partition of the FFU file.");
                return null;
            }
            uint PartitionLoaderTableOffset = (uint)patternPosition;
            Console.WriteLine("SBL1 PartitionLoaderTableOffset = 0x{0:X8}", PartitionLoaderTableOffset);

            // Find the position of the SharedMemoryAddress inside SBL1
            patternToFind = new byte[] { 0x04, 0x00, 0x9F, 0xE5, 0x28, 0x00, 0xD0, 0xE5, 0x1E, 0xFF, 0x2F, 0xE1 };
            patternPosition = -1;
            for (int i = 0; i < sbl1.Length; i++)
            {
                if (sbl1.Skip(i).Take(patternToFind.Length).SequenceEqual(patternToFind))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern of the SharedMemoryAddress in the SBL1 partition of the FFU file.");
                return null;
            }
            uint SharedMemoryAddress = sbl1[patternPosition + 12] + (uint)(sbl1[patternPosition + 13] << 8) + (uint)(sbl1[patternPosition + 14] << 16) + (uint)(sbl1[patternPosition + 15] << 24);
            uint GlobalIsSecurityEnabledAddress = SharedMemoryAddress + 40;
            Console.WriteLine("SBL1 SharedMemoryAddress = 0x{0:X8}", SharedMemoryAddress);
            Console.WriteLine("SBL1 GlobalIsSecurityEnabledAddress = 0x{0:X8}", GlobalIsSecurityEnabledAddress);

            // Compute the ImageOffset and the ImageAddress of SBL1
            uint HeaderOffset = 0x2800 + 20; // SBL1 offset + total size of "Long Header"
            uint ImageOffset = sbl1[HeaderOffset] + (uint)(sbl1[HeaderOffset + 1] << 8) + (uint)(sbl1[HeaderOffset + 2] << 16) + (uint)(sbl1[HeaderOffset + 2] << 24);
            uint ImageAddress = sbl1[HeaderOffset + 4] + (uint)(sbl1[HeaderOffset + 5] << 8) + (uint)(sbl1[HeaderOffset + 6] << 16) + (uint)(sbl1[HeaderOffset + 7] << 24);
            Console.WriteLine("SBL1 HeaderOffset = 0x{0:X8}", HeaderOffset);
            Console.WriteLine("SBL1 ImageOffset = 0x{0:X8}", ImageOffset);
            Console.WriteLine("SBL1 ImageAddress = 0x{0:X8}", ImageAddress);

            // Find the position of the ReturnAddress inside SBL1
            patternToFind = new byte[] { 0xA0, 0xE1, 0x1C, 0xD0, 0x8D, 0xE2, 0xF0, 0x4F, 0xBD, 0xE8, 0x1E, 0xFF, 0x2F, 0xE1 };
            patternPosition = -1;
            for (int i = 0; i < sbl1.Length; i++)
            {
                if (sbl1.Skip(i).Take(patternToFind.Length).SequenceEqual(patternToFind))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern of the ReturnAddress in the SBL1 partition of the FFU file.");
                return null;
            }
            uint ReturnAddress = (UInt32)patternPosition - 6 - ImageOffset + ImageAddress;
            Console.WriteLine("SBL1 ReturnAddress = 0x{0:X8}", ReturnAddress);

            // Initialize the content of the HACK partition (partition size = 1 sector)
            byte[] hackPartitionContent = new byte[0x200];
            Array.Clear(hackPartitionContent, 0, 0x200);
            byte[] Content = new byte[] {
                0x16, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x28, 0xBD, 0x02, 0x00,
                0xD8, 0x01, 0x00, 0x00, 0xD8, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA0, 0xE3, 0x3C, 0x10, 0x9F, 0xE5,
                0x00, 0x00, 0xC1, 0xE5, 0x38, 0x00, 0x9F, 0xE5, 0x38, 0x10, 0x9F, 0xE5, 0x00, 0x00, 0x81, 0xE5,
                0x34, 0x10, 0x9F, 0xE5, 0x00, 0x00, 0x81, 0xE5, 0x30, 0x00, 0x9F, 0xE5, 0x20, 0x10, 0x9F, 0xE5,
                0x2C, 0x30, 0x9F, 0xE5, 0x00, 0x20, 0x90, 0xE5, 0x00, 0x20, 0x81, 0xE5, 0x04, 0x00, 0x80, 0xE2,
                0x04, 0x10, 0x81, 0xE2, 0x03, 0x00, 0x50, 0xE1, 0xF9, 0xFF, 0xFF, 0xBA, 0x14, 0xF0, 0x9F, 0xE5,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x02, 0x00, 0x90, 0xBF, 0x02, 0x00, 0xD0, 0xBF, 0x02, 0x00,
                0xA0, 0xBD, 0x02, 0x00, 0xA0, 0xBE, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            Buffer.BlockCopy(Content, 0, hackPartitionContent, 0, Content.Length);

            // Copy the header of the SBL2 partition (this header is specific to the model of the phone)
            Buffer.BlockCopy(sbl2, 0, hackPartitionContent, 0, 12);

            // Copy the GlobalIsSecurityEnabledAddress and ReturnAddress of the SBL1 partition
            hackPartitionContent[112] = (byte)(GlobalIsSecurityEnabledAddress & 0xFF);
            hackPartitionContent[113] = (byte)((GlobalIsSecurityEnabledAddress >> 8) & 0xFF);
            hackPartitionContent[114] = (byte)((GlobalIsSecurityEnabledAddress >> 16) & 0xFF);
            hackPartitionContent[115] = (byte)((GlobalIsSecurityEnabledAddress >> 24) & 0xFF);
            hackPartitionContent[136] = (byte)(ReturnAddress & 0xFF);
            hackPartitionContent[137] = (byte)((ReturnAddress >> 8) & 0xFF);
            hackPartitionContent[138] = (byte)((ReturnAddress >> 16) & 0xFF);
            hackPartitionContent[139] = (byte)((ReturnAddress >> 24) & 0xFF);

            // Copy the PartitionLoaderTable of the SBL1 partition and zeroing a part of it.
            Buffer.BlockCopy(sbl1, (int)PartitionLoaderTableOffset, hackPartitionContent, 160, 80);
            hackPartitionContent[208] = 0;
            hackPartitionContent[209] = 0;
            hackPartitionContent[210] = 0;
            hackPartitionContent[211] = 0;

            // Copy again the PartitionLoaderTable of the SBL1 partition, zeroing a part of it and set an unknown value.
            Buffer.BlockCopy(sbl1, (int)PartitionLoaderTableOffset, hackPartitionContent, 240, 80);
            hackPartitionContent[284] = 0;
            hackPartitionContent[285] = 0;
            hackPartitionContent[286] = 0;
            hackPartitionContent[287] = 0;
            hackPartitionContent[296] = 0xF0; // 0x000210F0 what is the meaning of this value ?
            hackPartitionContent[297] = 0x10;
            hackPartitionContent[297] = 0x02;
            hackPartitionContent[298] = 0x00;

            // Copy the new Type GUID thta is going to be applied to partition SBL2
            byte[] PartitionTypeGuid = new byte[] {
                0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74, 0x74
            };
            Buffer.BlockCopy(PartitionTypeGuid, 0, hackPartitionContent, 400, 16);

            // Set an unknown value 
            hackPartitionContent[508] = 0x28; // 0x0002BD28 what is the meaning of this value ?
            hackPartitionContent[509] = 0xBD;
            hackPartitionContent[510] = 0x02;
            hackPartitionContent[511] = 0x00;

            return hackPartitionContent;
        }

        public static byte[] loadSBL3img(string FileName)
        {
            byte[] rawSbl3 = File.ReadAllBytes(FileName);

            // Find the start position of the header of SBL3 inside the raw image
            byte[] startPatternToFind = new byte[] { 0x18, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00 };
            byte[] endPatternToFind = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x8F };
            int patternPosition = -1;
            for (int i = 0; i < rawSbl3.Length; i++)
            {
                if (rawSbl3.Skip(i).Take(startPatternToFind.Length).SequenceEqual(startPatternToFind))
                {
                    if (rawSbl3.Skip(i + startPatternToFind.Length + 1).Take(endPatternToFind.Length).SequenceEqual(endPatternToFind))
                    {
                        patternPosition = i;
                        break;
                    }
                }
            }
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the start position of the SBL3 header inside the raw image.");
                return null;
            }

            // Read the length of the SBL3 image stored in the header
            uint Length = rawSbl3[patternPosition + 16] + (uint)(rawSbl3[patternPosition + 17] << 8) + (uint)(rawSbl3[patternPosition + 18] << 16) + (uint)(rawSbl3[patternPosition + 19] << 24);
            // Add the length of the header
            Length += 40;

            byte[] sbl3 = new byte[Length];
            Buffer.BlockCopy(rawSbl3, patternPosition, sbl3, 0, (int)Length);

            return sbl3;

        }
    }
}
