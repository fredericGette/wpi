using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    class GPT
    {
        public byte[] GPTBuffer;
        private int HeaderOffset;
        private UInt32 HeaderSize;
        private UInt32 TableOffset;
        private UInt32 TableSize;
        private UInt32 PartitionEntrySize;
        public List<Partition> partitions;

        internal GPT(byte[] values, int length)
        {
            partitions = new List<Partition>();

            if (length < 0x4400) // 34 sectors of 512 bytes
            {
                throw new System.Exception("Byte array too short.");
            }
            this.GPTBuffer = values;

            uint sectorSize;
            if (length == 0x4400) // 34 sectors of 0x200 bytes.
            {
                sectorSize = 512; // 0x200
            }
            else if (length == 0x6000) // 6 sectors of 0x1000 bytes.
            {
                sectorSize = 4096; // 0x1000
            }
            else
            {
                throw new System.Exception("Unsupported sector size.");
            }

            // first sector is Master Boot Record (MBR)

            byte[] headerPattern = new byte[] { 0x45, 0x46, 0x49, 0x20, 0x50, 0x41, 0x52, 0x54 }; // "EFI PART"
            HeaderOffset = -1;
            for (int i = 0; i < length; i++)
            {
                if (values.Skip(i).Take(headerPattern.Length).SequenceEqual(headerPattern))
                {
                    HeaderOffset = i;
                }
            }
            if (HeaderOffset == -1)
            {
                throw new System.Exception("Header not found.");
            }

            HeaderSize = (uint)(values[HeaderOffset + 15] << 24) + (uint)(values[HeaderOffset + 14] << 16) + (uint)(values[HeaderOffset + 13] << 8) + values[HeaderOffset + 12];
            TableOffset = (uint)HeaderOffset + sectorSize;
            ulong firstUsableSector = (ulong)(values[HeaderOffset + 47] << 56) + (ulong)(values[HeaderOffset + 46] << 48) + (ulong)(values[HeaderOffset + 45] << 40) + (ulong)(values[HeaderOffset + 44] << 32) + (ulong)(values[HeaderOffset + 43] << 24) + (ulong)(values[HeaderOffset + 42] << 16) + (ulong)(values[HeaderOffset + 41] << 8) + values[HeaderOffset + 40];
            ulong lastUsableSector = (ulong)(values[HeaderOffset + 55] << 56) + (ulong)(values[HeaderOffset + 54] << 48) + (ulong)(values[HeaderOffset + 53] << 40) + (ulong)(values[HeaderOffset + 52] << 32) + (ulong)(values[HeaderOffset + 51] << 24) + (ulong)(values[HeaderOffset + 50] << 16) + (ulong)(values[HeaderOffset + 49] << 8) + values[HeaderOffset + 48];
            uint maxPartitions = (uint)(values[HeaderOffset + 83] << 24) + (uint)(values[HeaderOffset + 82] << 16) + (uint)(values[HeaderOffset + 81] << 8) + values[HeaderOffset + 80];
            PartitionEntrySize = (uint)(values[HeaderOffset + 87] << 24) + (uint)(values[HeaderOffset + 86] << 16) + (uint)(values[HeaderOffset + 85] << 8) + values[HeaderOffset + 84];
            TableSize = maxPartitions * PartitionEntrySize;
            if (TableOffset + TableSize > length)
            {
                throw new System.Exception("Response too short compared to the GPT table size.");
            }

            uint partitionOffset = TableOffset;
            Console.WriteLine("\nPartition name                       firstSect. lastSect. Attributes");
            Console.WriteLine("\tPartition type GUID");
            Console.WriteLine("\tPartition GUID");
            while (partitionOffset < TableOffset + TableSize)
            {
                byte[] guidBuffer = new byte[16];
                Buffer.BlockCopy(values, (int)partitionOffset, guidBuffer, 0, 16);
                Guid partitionTypeGuid = new Guid(guidBuffer);

                Buffer.BlockCopy(values, (int)partitionOffset + 16, guidBuffer, 0, 16);
                Guid partitionGuid = new Guid(guidBuffer);

                ulong firstSector = (ulong)(values[partitionOffset + 39] << 56) + (ulong)(values[partitionOffset + 38] << 48) + (ulong)(values[partitionOffset + 37] << 40) + (ulong)(values[partitionOffset + 36] << 32) + (ulong)(values[partitionOffset + 35] << 24) + (ulong)(values[partitionOffset + 34] << 16) + (ulong)(values[partitionOffset + 33] << 8) + values[partitionOffset + 32];
                ulong lastSector = (ulong)(values[partitionOffset + 47] << 56) + (ulong)(values[partitionOffset + 46] << 48) + (ulong)(values[partitionOffset + 45] << 40) + (ulong)(values[partitionOffset + 44] << 32) + (ulong)(values[partitionOffset + 43] << 24) + (ulong)(values[partitionOffset + 42] << 16) + (ulong)(values[partitionOffset + 41] << 8) + values[partitionOffset + 40];
                ulong attributes = (ulong)(values[partitionOffset + 55] << 56) + (ulong)(values[partitionOffset + 54] << 48) + (ulong)(values[partitionOffset + 45] << 53) + (ulong)(values[partitionOffset + 52] << 32) + (ulong)(values[partitionOffset + 51] << 24) + (ulong)(values[partitionOffset + 50] << 16) + (ulong)(values[partitionOffset + 49] << 8) + values[partitionOffset + 48];

                byte[] nameBuffer = new byte[72];  // 36 UTF-16 characters
                Buffer.BlockCopy(values, (int)partitionOffset + 56, nameBuffer, 0, 72);
                string name = System.Text.Encoding.Unicode.GetString(nameBuffer);
                name = name.TrimEnd(new char[] { (char)0, ' ' }); // Remove multiple trailing \0 


                if (firstSector != 0 && lastSector != 0)
                {
                    Console.WriteLine("{0} 0x{1:X6} - 0x{2:X6}  0x{3:X16}", name.PadRight(36, ' '), firstSector, lastSector, attributes);
                    Console.WriteLine("\t{0}", partitionTypeGuid.ToString().ToUpper());
                    Console.WriteLine("\t{0}\n", partitionGuid.ToString().ToUpper());

                    Partition partition = new Partition();
                    partition.firstSector = firstSector;
                    partition.lastSector = lastSector;
                    partition.attributes = attributes;
                    partition.name = name;
                    partition.partitionGuid = partitionGuid;
                    partition.partitionTypeGuid = partitionTypeGuid;

                    partitions.Add(partition);
                }

                partitionOffset += PartitionEntrySize;
            }
        }

        public void Rebuild()
        {
            Array.Clear(GPTBuffer, (int)TableOffset, (int)TableSize);

            UInt32 PartitionOffset = TableOffset;
            foreach (Partition CurrentPartition in partitions)
            {
                Console.WriteLine("\tProcessing partition {0}", CurrentPartition.name);

                Buffer.BlockCopy(CurrentPartition.partitionTypeGuid.ToByteArray(), 0, GPTBuffer, (int)PartitionOffset, 16);
                Buffer.BlockCopy(CurrentPartition.partitionGuid.ToByteArray(), 0, GPTBuffer, (int)PartitionOffset+16, 16);

                GPTBuffer[PartitionOffset + 32] = (byte)(CurrentPartition.firstSector & 0xFF);
                GPTBuffer[PartitionOffset + 33] = (byte)((CurrentPartition.firstSector >> 8) & 0xFF);
                GPTBuffer[PartitionOffset + 34] = (byte)((CurrentPartition.firstSector >> 16) & 0xFF);
                GPTBuffer[PartitionOffset + 35] = (byte)((CurrentPartition.firstSector >> 24) & 0xFF);
                GPTBuffer[PartitionOffset + 36] = (byte)((CurrentPartition.firstSector >> 32) & 0xFF);
                GPTBuffer[PartitionOffset + 37] = (byte)((CurrentPartition.firstSector >> 40) & 0xFF);
                GPTBuffer[PartitionOffset + 38] = (byte)((CurrentPartition.firstSector >> 48) & 0xFF);
                GPTBuffer[PartitionOffset + 39] = (byte)((CurrentPartition.firstSector >> 56) & 0xFF);

                GPTBuffer[PartitionOffset + 40] = (byte)(CurrentPartition.lastSector & 0xFF);
                GPTBuffer[PartitionOffset + 41] = (byte)((CurrentPartition.lastSector >> 8) & 0xFF);
                GPTBuffer[PartitionOffset + 42] = (byte)((CurrentPartition.lastSector >> 16) & 0xFF);
                GPTBuffer[PartitionOffset + 43] = (byte)((CurrentPartition.lastSector >> 24) & 0xFF);
                GPTBuffer[PartitionOffset + 44] = (byte)((CurrentPartition.lastSector >> 32) & 0xFF);
                GPTBuffer[PartitionOffset + 45] = (byte)((CurrentPartition.lastSector >> 40) & 0xFF);
                GPTBuffer[PartitionOffset + 46] = (byte)((CurrentPartition.lastSector >> 48) & 0xFF);
                GPTBuffer[PartitionOffset + 47] = (byte)((CurrentPartition.lastSector >> 56) & 0xFF);

                GPTBuffer[PartitionOffset + 48] = (byte)(CurrentPartition.attributes & 0xFF);
                GPTBuffer[PartitionOffset + 49] = (byte)((CurrentPartition.attributes >> 8) & 0xFF);
                GPTBuffer[PartitionOffset + 50] = (byte)((CurrentPartition.attributes >> 16) & 0xFF);
                GPTBuffer[PartitionOffset + 51] = (byte)((CurrentPartition.attributes >> 24) & 0xFF);
                GPTBuffer[PartitionOffset + 52] = (byte)((CurrentPartition.attributes >> 32) & 0xFF);
                GPTBuffer[PartitionOffset + 53] = (byte)((CurrentPartition.attributes >> 40) & 0xFF);
                GPTBuffer[PartitionOffset + 54] = (byte)((CurrentPartition.attributes >> 48) & 0xFF);
                GPTBuffer[PartitionOffset + 55] = (byte)((CurrentPartition.attributes >> 56) & 0xFF);

                Array.Clear(GPTBuffer, (int)PartitionOffset + 56, 72);
                byte[] TextBytes = System.Text.UnicodeEncoding.Unicode.GetBytes(CurrentPartition.name);
                Buffer.BlockCopy(TextBytes, 0, GPTBuffer, (int)PartitionOffset + 56, TextBytes.Length);

                PartitionOffset += PartitionEntrySize;
            }

            
            uint crcHeader = CRC32.CalculateChecksum(GPTBuffer, (uint)HeaderOffset, HeaderSize);
            GPTBuffer[HeaderOffset + 16] = (byte)(crcHeader & 0xFF);
            GPTBuffer[HeaderOffset + 17] = (byte)((crcHeader >> 8) & 0xFF);
            GPTBuffer[HeaderOffset + 18] = (byte)((crcHeader >> 16) & 0xFF);
            GPTBuffer[HeaderOffset + 19] = (byte)((crcHeader >> 24) & 0xFF);

            uint crcTable = CRC32.CalculateChecksum(GPTBuffer, TableOffset, TableSize);
            GPTBuffer[HeaderOffset + 88] = (byte)(crcHeader & 0xFF);
            GPTBuffer[HeaderOffset + 89] = (byte)((crcHeader >> 8) & 0xFF);
            GPTBuffer[HeaderOffset + 90] = (byte)((crcHeader >> 16) & 0xFF);
            GPTBuffer[HeaderOffset + 91] = (byte)((crcHeader >> 24) & 0xFF);
        }

        public Partition GetPartition(string Name)
        {
            return partitions.Where(p => (string.Compare(p.name, Name, true) == 0)).FirstOrDefault();
        }
    }
}
