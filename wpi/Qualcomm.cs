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

        public static byte[] parseSBL1orProgrammer(byte[] values, uint Offset)
        {
            // Check we have a "Long Header"
            // - CodeWord (4 bytes)
            // - Magic (4 bytes)
            byte[] LongHeaderStart = new byte[] { 0xD1, 0xDC, 0x4B, 0x84, 0x34, 0x10, 0xD7, 0x73 };
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

            byte imageType = values[Offset + 8];
            if (imageType == 0x0D)
            {
                if (Program.verbose) Console.WriteLine("EhostDL image detected."); // Emergency Host Download 
            }


            uint HeaderOffset = Offset + 20; // total size of "Long Header"
            uint ImageOffset = values[HeaderOffset] + ((uint)values[HeaderOffset + 1] << 8) + ((uint)values[HeaderOffset + 2] << 16) + ((uint)values[HeaderOffset + 2] << 24);
            if (Program.verbose) Console.WriteLine("Image offset = 0x{0:X8} ", ImageOffset);

            uint ImageAddress = values[HeaderOffset + 4] + ((uint)values[HeaderOffset + 5] << 8) + ((uint)values[HeaderOffset + 6] << 16) + ((uint)values[HeaderOffset + 7] << 24);
            if (Program.verbose) Console.WriteLine("Image address = 0x{0:X8} ", ImageAddress);
            // To disassemble the binary, use the ImageAddress-ImageOffset to rebase the code
            // then ImageAddress is the entry point.

            uint CertificatesAddress = values[HeaderOffset + 24] + ((uint)values[HeaderOffset + 25] << 8) + ((uint)values[HeaderOffset + 26] << 16) + ((uint)values[HeaderOffset + 27] << 24);
            uint CertificatesSize = values[HeaderOffset + 28] + ((uint)values[HeaderOffset + 29] << 8) + ((uint)values[HeaderOffset + 30] << 16) + ((uint)values[HeaderOffset + 31] << 24);
            uint CertificatesOffset = CertificatesAddress - ImageAddress + ImageOffset;

            uint CurrentCertificateOffset = CertificatesOffset;
            uint CertificateSize = 0;
            byte[] RootKeyHash = null;
            while (CurrentCertificateOffset < (CertificatesOffset + CertificatesSize))
            {
                if ((values[CurrentCertificateOffset] == 0x30) && (values[CurrentCertificateOffset + 1] == 0x82))
                {
                    CertificateSize = ((uint)values[CurrentCertificateOffset + 2] << 8) + values[CurrentCertificateOffset + 3] + 4; // Big endian!

                    if ((CurrentCertificateOffset + CertificateSize) == (CertificatesOffset + CertificatesSize))
                    {
                        // This is the last certificate. So this is the root key.
                        RootKeyHash = new SHA256Managed().ComputeHash(values, (int)CurrentCertificateOffset, (int)CertificateSize);

                        if (Program.verbose)
                        {
                            Console.WriteLine("Root Hash Key ({0} bits): ", RootKeyHash.Length * 8);
                            for (int i = 0; i < RootKeyHash.Length; i++)
                            {
                                Console.Write("{0:X2}", RootKeyHash[i]);
                            }
                            Console.WriteLine("");
                        }
                            
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

                        if (Program.verbose)
                        {
                            Console.WriteLine("Root Hash Key ({0} bits): ", RootKeyHash.Length * 8);
                            for (int i = 0; i < RootKeyHash.Length; i++)
                            {
                                Console.Write("{0:X2}", RootKeyHash[i]);
                            }
                            Console.WriteLine("");
                        }
                    }
                    break;
                }
            }
            return RootKeyHash;
        }

        public static byte[] createHACK(byte[] sbl1, byte[] sbl2)
        {
            // Find the position of the PartitionLoaderTable inside SBL1
            byte[] patternToFind = new byte[] {
                0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x05, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            int patternPosition = -1;
            for (int i = 0; i < sbl1.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar

                if (sbl1.Skip(i).Take(patternToFind.Length).SequenceEqual(patternToFind))
                {
                    patternPosition = i;
                    break;
                }
            }
            Console.WriteLine();
            if (patternPosition == -1)
            {
                Console.WriteLine("Unable to find the pattern of the PartitionLoaderTable in the SBL1 partition of the FFU file.");
                return null;
            }
            uint PartitionLoaderTableOffset = (uint)patternPosition;

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
            uint SharedMemoryAddress = sbl1[patternPosition + 12] + ((uint)sbl1[patternPosition + 13] << 8) + ((uint)sbl1[patternPosition + 14] << 16) + ((uint)sbl1[patternPosition + 15] << 24);
            uint GlobalIsSecurityEnabledAddress = SharedMemoryAddress + 40;

            // Compute the ImageOffset and the ImageAddress of SBL1
            uint HeaderOffset = 0x2800 + 20; // SBL1 offset + total size of "Long Header"
            uint ImageOffset = sbl1[HeaderOffset] + ((uint)sbl1[HeaderOffset + 1] << 8) + ((uint)sbl1[HeaderOffset + 2] << 16) + ((uint)sbl1[HeaderOffset + 2] << 24);
            uint ImageAddress = sbl1[HeaderOffset + 4] + ((uint)sbl1[HeaderOffset + 5] << 8) + ((uint)sbl1[HeaderOffset + 6] << 16) + ((uint)sbl1[HeaderOffset + 7] << 24);

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
            hackPartitionContent[298] = 0x02;
            hackPartitionContent[299] = 0x00;

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
            uint Length = rawSbl3[patternPosition + 16] + ((uint)rawSbl3[patternPosition + 17] << 8) + ((uint)rawSbl3[patternPosition + 18] << 16) + ((uint)rawSbl3[patternPosition + 19] << 24);
            // Add the length of the header
            Length += 40;

            byte[] sbl3 = new byte[Length];
            Buffer.BlockCopy(rawSbl3, patternPosition, sbl3, 0, (int)Length);

            return sbl3;

        }

        public static byte[] parseHexFile(string FilePath)
        {
            byte[] Result = null;

            string[] Lines = File.ReadAllLines(FilePath);
            byte[] Buffer = null;
            int BufferSize = 0;

            foreach (string Line in Lines)
            {
                if (Line[0] != ':')
                {
                    Console.WriteLine("Line doesn't start with a \":\" character:\n{0}", Line);
                    return null;
                }

                string hexString = Line.Substring(1); // remove the : at the start of the line

                if (hexString.Length % 2 == 1)
                {
                    Console.WriteLine("The line contains an odd number of digits:\n{0}", Line);
                    return null;
                }

                byte[] LineBytes = new byte[hexString.Length / 2]; // Each byte is coded by 2 digits
                for (int i = 0; i < LineBytes.Length; ++i)
                {
                    string hexValue = hexString.Substring(i * 2, 2);
                    LineBytes[i] = Convert.ToByte(hexValue, 16);
                }

                if ((LineBytes[0] + 5) != LineBytes.Length)
                {
                    Console.WriteLine("The number of bytes in the line is not correct (size={0}, expected={1}):\n{2}", LineBytes.Length, (LineBytes[0] + 5), Line);
                    return null;
                }

                if (Buffer == null)
                    Buffer = new byte[0x40000]; // Should be enough ?

                if (LineBytes[3] == 0) // record type = data
                {
                    System.Buffer.BlockCopy(LineBytes, 4, Buffer, BufferSize, LineBytes[0]); // Line header size = 4 bytes, line trailer (checksum) = 1 byte
                    BufferSize += LineBytes[0];
                }
            }

            Result = new byte[BufferSize];
            System.Buffer.BlockCopy(Buffer, 0, Result, 0, BufferSize);

            return Result;
        }

        /**
         * Encode a DMSS (Dual-Mode Subscriber Station) Download Protocol command
         * in HDLC (High-level Data Link Control)
         * to communicate whith the PBL (Primary Boot Loader)
         * see https://xdaforums.com/t/r-d-qualcomm-using-qdl-ehostdl-and-diag-interfaces-features.2086142/post-36371804
         * see https://github.com/openpst/libopenpst/blob/master/include%2Fqualcomm%2Fdload.h
         */
        public static byte[] encodeHDLC(byte[] value, int length)
        {
            CRC16 crc16 = new CRC16(0x1189, 0xFFFF, 0xFFFF);

            byte[] encoded = new byte[(length * 2) + 4]; // Header (1byte) + value (each byte can be escaped by one other byte) + CRC16 (2 bytes) + Trailer (1byte)
            int index = 0;

            encoded[index++] = 0x7E; // Header

            // Escape 0x7D and 0x7E values
            for (int i = 0; i < length; i++)
            {
                if ((value[i] == 0x7D) || (value[i] == 0x7E))
                {
                    encoded[index++] = 0x7D;
                    encoded[index++] = (byte)(value[i] ^ 0x20);
                }
                else
                    encoded[index++] = value[i];
            }

            // Compute the 16bits CRC (Cyclic Redundancy Checks) 
            // and escape 0x7D and 0x7E CRC values (these values can appear in the high or low byte of the CRC value)
            UInt16 crcResult = crc16.CalculateChecksum(value);
            if (((byte)(crcResult & 0xFF) == 0x7D) || ((byte)(crcResult & 0xFF) == 0x7E))
            {
                encoded[index++] = 0x7D;
                encoded[index++] = (byte)((crcResult & 0xFF) ^ 0x20);
            }
            else
                encoded[index++] = (byte)(crcResult & 0xFF);
            if (((byte)(crcResult >> 8) == 0x7D) || ((byte)(crcResult >> 8) == 0x7E))
            {
                encoded[index++] = 0x7D;
                encoded[index++] = (byte)((crcResult >> 8) ^ 0x20);
            }
            else
                encoded[index++] = (byte)(crcResult >> 8);

            encoded[index++] = 0x7E; // Trailer

            // Resize result by removing useless trailing bytes
            byte[] Result = new byte[index];
            Buffer.BlockCopy(encoded, 0, Result, 0, index);
            return Result;
        }

        public static byte[] decodeHDLC(byte[] value, int length)
        {
            CRC16 crc16 = new CRC16(0x1189, 0xFFFF, 0xFFFF);

            int SourceLength = length;
            int SourcePos = 1; // exclude the header byte (0x7E) at position 0
            int index = 0;

            byte[] decoded = new byte[SourceLength]; // the decoded array will be smaller than the input array because we remove the header and trailer.

            while (SourcePos < SourceLength)
            {
                if (value[SourcePos] == 0x7E) // Trailer
                    break;
                if (value[SourcePos] == 0x7D) // Escape byte
                    decoded[index++] = (byte)(value[++SourcePos] ^ 0x20);
                else
                    decoded[index++] = value[SourcePos];
                SourcePos++;
            }

            // Remove the crc value (the 2 last bytes)
            byte[] Result = new byte[index - 2];
            Buffer.BlockCopy(decoded, 0, Result, 0, index - 2);

            // Compute the 16bits CRC (Cyclic Redundancy Checks) 
            // and compare it with the crc contained in the decoded value (last 2 bytes)
            UInt16 crcResult = crc16.CalculateChecksum(Result);
            if (((byte)(crcResult & 0xFF) != decoded[index - 2]) || ((byte)(crcResult >> 8) != decoded[index - 1]))
            {
                Console.WriteLine("The CRC16 doesn't match: expected 0x{0:X4}, received 0x{1:X4}", crcResult, (decoded[index - 2] + decoded[index - 1] << 8));
                return null;
            }

            return Result;
        }

        public static bool SendToPhoneMemory(UInt32 Address, byte[] Data, UInt32 Length, USB device)
        {
            long Remaining = Length;
            UInt32 CurrentLength;
            UInt32 CurrentOffset = 0;
            byte[] Buffer = new byte[263]; // Command (1byte) + Address (4bytes) + Length (2bytes) + Data (256bytes)
            UInt32 CurrentAddress = Address;
            byte[] CurrentBytes;

            int progress = 0;
            while (Remaining > 0)
            {
                if (!Program.verbose && progress++ % 10 == 0) Console.Write("."); // Progress bar

                if (Remaining >= 256)
                {
                    CurrentLength = 256;
                    CurrentBytes = Buffer;
                }
                else
                {
                    CurrentLength = (UInt32)Remaining;
                    CurrentBytes = new byte[CurrentLength + 7];
                }
                CurrentBytes[0] = 0x0F; // DLOAD command to write data at a 32bits address
                System.Buffer.BlockCopy(BitConverter.GetBytes(CurrentAddress).Reverse().ToArray(), 0, CurrentBytes, 1, 4); // Address is in Big Endian (4 bytes) at position 1
                System.Buffer.BlockCopy(BitConverter.GetBytes((UInt16)CurrentLength).Reverse().ToArray(), 0, CurrentBytes, 5, 2); // Length is in Big Endian (2 bytes) at position 5
                System.Buffer.BlockCopy(Data, (int)CurrentOffset, CurrentBytes, 7, (int)CurrentLength); // Read max 100bytes from Data at put them at position 7 in the CurrentBytes

                // Send write command
                byte[] writeCommand = Qualcomm.encodeHDLC(CurrentBytes, CurrentBytes.Length);
                device.WritePipe(writeCommand, writeCommand.Length);

                // Read response
                uint bytesRead;
                byte[] ResponseBuffer = new byte[0x2000]; // I don't know why we need this size.
                device.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
                byte[] commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
                if (commandResult.Length != 1 || commandResult[0] != 0x02)
                {
                    Console.WriteLine("Expected DLOAD ACK (0x02) but received:");
                    printRaw(commandResult, commandResult.Length);
                    return false;
                }

                CurrentAddress += CurrentLength;
                CurrentOffset += CurrentLength;
                Remaining -= CurrentLength;
            }
            Console.WriteLine();

            return true;
        }

        public static bool Flash(UInt32 StartInBytes, byte[] Data, UInt32 LengthInBytes, USB device)
        {
            long RemainingBytes = LengthInBytes;
            UInt32 CurrentLength;
            UInt32 CurrentOffset = 0;
            byte[] Buffer = new byte[1029]; // command (1byte) + start position (4bytes) + Data (1024bytes)
            byte[] FinalCommand;
            Buffer[0] = 0x07; // Ehost STREAM_WRITE_REQ
            UInt32 CurrentPosition = StartInBytes; // Position in the eMMC storage

            int progress = 0;
            while (RemainingBytes > 0)
            {
                if (!Program.verbose && progress++ % 10 == 0) Console.Write("."); // Progress bar

                System.Buffer.BlockCopy(BitConverter.GetBytes(CurrentPosition), 0, Buffer, 1, 4); // Start position is in bytes and in Little Endian (on Samsung phones the start position is in Sectors!!)

                if (RemainingBytes >= 1024)
                    CurrentLength = 1024;
                else
                    CurrentLength = (UInt32)RemainingBytes;
                System.Buffer.BlockCopy(Data, (int)CurrentOffset, Buffer, 5, (int)CurrentLength);

                if (CurrentLength < 1024)
                {
                    FinalCommand = new byte[CurrentLength + 5];
                    System.Buffer.BlockCopy(Buffer, 0, FinalCommand, 0, (int)CurrentLength + 5);
                }
                else
                    FinalCommand = Buffer;

                // Send write command
                device.WritePipe(FinalCommand, FinalCommand.Length);

                // Read response
                uint bytesRead;
                byte[] ResponseBuffer = new byte[0x2000]; // I don't know why we need this size.
                device.ReadPipe(ResponseBuffer, ResponseBuffer.Length, out bytesRead);
                byte[] commandResult = Qualcomm.decodeHDLC(ResponseBuffer, (int)bytesRead);
                if (commandResult.Length < 5 || commandResult[0] != 0x08 
                    || commandResult[1] != (byte)(CurrentPosition & 0xFF)
                    || commandResult[2] != (byte)((CurrentPosition >> 8) & 0xFF)
                    || commandResult[3] != (byte)((CurrentPosition >> 16) & 0xFF)
                    || commandResult[4] != (byte)((CurrentPosition >> 24) & 0xFF)
                    )
                {
                    Console.WriteLine("Expected Ehost STREAM_WRITE_RSP (0x08) and start position 0x{0:X8} but received:", CurrentPosition);
                    printRaw(commandResult, commandResult.Length);
                    return false;
                }

                CurrentPosition += CurrentLength;
                CurrentOffset += CurrentLength;
                RemainingBytes -= CurrentLength;
            }
            Console.WriteLine();

            return true;
        }
    }
}
