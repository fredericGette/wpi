using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    internal class Fat16Layout
    {
        internal UInt32 offset;
        internal UInt32 startFatRegion;
        internal UInt32 startRootDirectoryRegion;
        internal UInt32 startDataRegion;
        internal UInt32 dataClusterCount;
    }

    class EFIESP
    {
        Fat16Layout layout;

        public EFIESP(byte[] EfiEspBinary)
        {
            UInt32 offset = 11;

            // Read BIOS Parameter Block (BPB)
            byte[] oemNameBuffer = new byte[8];
            Buffer.BlockCopy(EfiEspBinary, 3, oemNameBuffer, 0, 8);
            string oemName = System.Text.Encoding.ASCII.GetString(oemNameBuffer);
            if (Program.verbose) Console.WriteLine("FAT OEM name: {0}", oemName);
            // DOS 2.0
            UInt16 bytesPerLogicalSector = (ushort)((EfiEspBinary[offset+1] << 8) + EfiEspBinary[offset]);
            if (Program.verbose) Console.WriteLine("FAT bytes per sector: {0}", bytesPerLogicalSector);
            byte logicalSectorsPerCluster = EfiEspBinary[offset+2];
            UInt16 reversedLogicalSectors = (ushort)((EfiEspBinary[offset+4] << 8) + EfiEspBinary[offset+3]);
            byte numberOfFats = EfiEspBinary[offset+5];
            UInt16 rootDirectoryEntries = (ushort)((EfiEspBinary[offset+7] << 8) + EfiEspBinary[offset+6]);
            UInt16 totalLogicalSectors = (ushort)((EfiEspBinary[offset+9] << 8) + EfiEspBinary[offset+8]);
            byte mediaDescriptor = EfiEspBinary[offset+10];
            UInt16 logicalSectorByFat = (ushort)((EfiEspBinary[offset+12] << 8) + EfiEspBinary[offset+11]);
            // DOS 3.31
            UInt16 physicalSectorsByTrack = (ushort)((EfiEspBinary[offset+14] << 8) + EfiEspBinary[offset+13]);
            UInt16 numberOfHeads = (ushort)((EfiEspBinary[offset+16] << 8) + EfiEspBinary[offset+15]);
            UInt32 hiddenSectors = ((uint)EfiEspBinary[offset +20] << 24) + ((uint)EfiEspBinary[offset +19] << 16) + ((uint)EfiEspBinary[offset +18] << 8) + EfiEspBinary[offset +17];
            UInt32 largeTotalLogicalSectors = ((uint)EfiEspBinary[offset +24] << 24) + ((uint)EfiEspBinary[offset +23] << 16) + ((uint)EfiEspBinary[offset +22] << 8) + EfiEspBinary[offset +21];
            // DOS 4.0
            byte physicalDriveNumber = EfiEspBinary[offset +25];
            byte flags = EfiEspBinary[offset +26]; 
            byte extendedBootsignature = EfiEspBinary[offset +27]; // 0x29 aka 4.1
            UInt32 volumeId = ((uint)EfiEspBinary[offset +31] << 24) + ((uint)EfiEspBinary[offset +30] << 16) + ((uint)EfiEspBinary[offset +29] << 8) + EfiEspBinary[offset +28];
            if (Program.verbose) Console.WriteLine("FAT volume ID: 0x{0:X8}", volumeId);
            byte[] labelBuffer = new byte[11];
            Buffer.BlockCopy(EfiEspBinary, (int)offset+32, labelBuffer, 0, 11);
            string label = System.Text.Encoding.ASCII.GetString(labelBuffer);
            if (Program.verbose) Console.WriteLine("FAT label: {0}", label);
            byte[] fsTypeBuffer = new byte[8];
            Buffer.BlockCopy(EfiEspBinary, (int)offset+43, fsTypeBuffer, 0, 8);
            string fsType = System.Text.Encoding.ASCII.GetString(fsTypeBuffer);
            if (Program.verbose) Console.WriteLine("FAT filse system type: {0}", fsType);

            this.layout = new Fat16Layout();
            this.layout.offset = offset;
            UInt32 rootDirectorySectorCount = (uint)rootDirectoryEntries * 32 / bytesPerLogicalSector;
            UInt32 dataSectorCount = largeTotalLogicalSectors - reversedLogicalSectors + (uint)numberOfFats * logicalSectorByFat + rootDirectorySectorCount;
            this.layout.dataClusterCount = dataSectorCount / logicalSectorsPerCluster;

            this.layout.startFatRegion = (uint)reversedLogicalSectors * bytesPerLogicalSector;
            this.layout.startRootDirectoryRegion = (uint)numberOfFats * logicalSectorByFat * bytesPerLogicalSector + this.layout.startFatRegion;
            this.layout.startDataRegion = rootDirectorySectorCount * bytesPerLogicalSector + this.layout.startRootDirectoryRegion;

            if (Program.verbose)
            {
                Console.WriteLine("File system layout:");
                Console.WriteLine("\tStart FAT region=0x{0:X8}", this.layout.startFatRegion);
                Console.WriteLine("\tStart root directory region=0x{0:X8}", this.layout.startRootDirectoryRegion);
                Console.WriteLine("\tStart data region=0x{0:X8}", this.layout.startDataRegion);
                Console.WriteLine("\tData cluster count=0x{0:X8}", this.layout.dataClusterCount);
            }

            // Move to root directory region 0
            UInt32 posRoot = this.layout.startRootDirectoryRegion + this.layout.offset; // 0x40000+0xB
            UInt32 directoryEntrySize = 32;

            posRoot = posRoot + 85; // why +85 ?
            // Find directory "Windows" in root
            if (Program.verbose) Console.WriteLine("Root directory entries:");
            UInt16 windowsStartingCluster = findStartingCluster("Windows", EfiEspBinary, posRoot, rootDirectoryEntries, directoryEntrySize);

            // Find directory "System32" in subdir
            // Move to data region "windowsStartingCluster"
            if (Program.verbose) Console.WriteLine("Sub-directory entries:");
            UInt32 tmp = ((uint)windowsStartingCluster - 2) * logicalSectorsPerCluster * bytesPerLogicalSector;
            UInt32 posData = this.layout.startDataRegion + tmp;
            UInt16 system32StartingCluster = findStartingCluster("System32", EfiEspBinary, posData, 100, directoryEntrySize);

            // Find directory "Boot" in subdir
            // Move to data region "system32StartingCluster"
            if (Program.verbose) Console.WriteLine("Sub-directory entries:");
            tmp = ((uint)system32StartingCluster - 2) * logicalSectorsPerCluster * bytesPerLogicalSector;
            posData = this.layout.startDataRegion + tmp;
            UInt16 bootStartingCluster = findStartingCluster("Boot", EfiEspBinary, posData, 100, directoryEntrySize);

            // List content of directory "Boot"
            // Move to data region "bootStartingCluster"
            if (Program.verbose) Console.WriteLine("Sub-directory entries:");
            tmp = ((uint)bootStartingCluster - 2) * logicalSectorsPerCluster * bytesPerLogicalSector;
            posData = this.layout.startDataRegion + tmp;
            findStartingCluster("MOBILE~1EFI", EfiEspBinary, posData, 100, directoryEntrySize);
        }

        private UInt16 findStartingCluster(string name, byte[] EfiEspBinary, uint startPos, uint nbEntries, uint directoryEntrySize)
        {
            UInt16 foundStartingCluster = 0;
            byte[] dirEntryNameBuffer = new byte[11];
            uint pos = startPos - directoryEntrySize;
            for (int i = 0; i < nbEntries; i++)
            {
                pos += directoryEntrySize;
                byte typeDirEntry = EfiEspBinary[pos];
                if (typeDirEntry == 0xE5) continue; //0xE5=AVAILABLE_DIR_ENTRY (deleted entry)
                if (typeDirEntry == 0x00) break; //filename starts with a NULL character. No more entries.
                Buffer.BlockCopy(EfiEspBinary, (int)pos, dirEntryNameBuffer, 0, 11);
                string dirEntryName = System.Text.Encoding.ASCII.GetString(dirEntryNameBuffer);
                byte attribute = EfiEspBinary[pos + 11];
                if ((attribute & 0x0F) == 0x0F) continue; //0x0F=VFAT_DIR_ENTRY
                UInt16 startingCluster = (ushort)((EfiEspBinary[pos + 27] << 8) + EfiEspBinary[pos + 26]);
                UInt32 size = ((uint)EfiEspBinary[pos + 31] << 24) + ((uint)EfiEspBinary[pos + 30] << 16) + ((uint)EfiEspBinary[pos + 29] << 8) + EfiEspBinary[pos + 28];
                if (Program.verbose) Console.WriteLine("\t[{0}] {1}(0x{2:X2}) {3} bytes ", dirEntryName, (attribute & 0x10) == 0x10 ? "Directory" : "", attribute, size);
                if (name.Equals(dirEntryName.Trim(), StringComparison.OrdinalIgnoreCase) && foundStartingCluster == 0)
                {
                    foundStartingCluster = startingCluster;
                    break;
                }
            }

            return foundStartingCluster;
        }

        private void printRawConsole(byte[] values, int offset, int length)
        {
            string characters = "";
            int normalizedLength = ((length / 19) + 1) * 19; // display 19 values by line
            for (int i = 0; i < normalizedLength; i++)
            {
                if (i < length)
                {
                    Console.Write("{0:X2} ", values[i+ offset]);
                    if (values[i+ offset] > 31 && values[i+ offset] < 127)
                    {
                        characters += (char)values[i+ offset] + "";
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
