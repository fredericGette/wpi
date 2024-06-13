using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    class GPT
    {
        public static List<Partition> parse(byte[] values, int length)
        {
            List<Partition> partitions = new List<Partition>();

            if (length < 0x4400) // 34 sectors of 512 bytes
            {
                Console.WriteLine("Byte array too short.");
                return partitions;
            }

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
                Console.WriteLine("Unsupported sector size.");
                return partitions;
            }

            // first sector is Master Boot Record (MBR)

            byte[] headerPattern = new byte[] { 0x45, 0x46, 0x49, 0x20, 0x50, 0x41, 0x52, 0x54 }; // "EFI PART"
            int headerOffset = -1;
            for (int i = 0; i < length; i++)
            {
                if (values.Skip(i).Take(headerPattern.Length).SequenceEqual(headerPattern))
                {
                    headerOffset = i;
                }
            }
            if (headerOffset == -1)
            {
                Console.WriteLine("Header not found.");
                return partitions;
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
                return partitions;
            }

            uint partitionOffset = tableOffset;
            Console.WriteLine("\nPartition name                       firstSect. lastSect.  Attributes");
            Console.WriteLine("\tPartition type GUID");
            Console.WriteLine("\tPartition GUID");
            while (partitionOffset < tableOffset + tableSize)
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

                partitionOffset += partitionEntrySize;
            }

            return partitions;
        }
    }
}
