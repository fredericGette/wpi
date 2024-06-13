using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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

        public static void parseSBL1(byte[] values)
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
                return;
            }
            byte[] headerEnd = values.Skip((int)Offset + LongHeaderStart.Length + 4).Take(LongHeaderEnd.Length).ToArray();
            if (!headerEnd.SequenceEqual(LongHeaderEnd))
            {
                Console.WriteLine("End of header doesn't match \"Long Header\" values.");
                Console.WriteLine("Values:");
                printRaw(headerEnd, headerEnd.Length);
                return;
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

                        Console.Write("SBL1 Root Hash Key ({0} bytes): ", RootKeyHash.Length);
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

                        Console.Write("SBL1 Root Hash Key ({0} bytes): ", RootKeyHash.Length);
                        for (int i = 0; i < RootKeyHash.Length; i++)
                        {
                            Console.Write("{0:X2}", RootKeyHash[i]);
                        }
                        Console.WriteLine("");
                    }
                    break;
                }
            }
        }
    }
}
