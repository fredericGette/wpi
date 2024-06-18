using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    class FFU
    {
        internal uint ChunkSize; // Size in bytes of a hashed chunk within the image.
        internal string Path;
        internal uint SecurityHeaderLength; // in bytes
        internal uint ImageHeaderLength; // in bytes
        internal byte[] StoreHeader;
        private int?[] ChunkIndexes;
        private FileStream FFUFile = null;
        private int FileOpenCount = 0;

        internal string PlatformID; // Indicates what device this FFU is intended to be written to.

        internal UInt64 TotalSize;
        internal UInt64 HeaderSize;
        internal UInt64 PayloadSize;
        internal UInt64 TotalChunkCount;

        internal List<Partition> partitions;

        internal FFU(string Path)
        {
            this.Path = Path;

            try
            {
                OpenFile();

                // Read Security Header
                uint SecurityHeaderSize = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                byte[] signatureSecurityHeader = new byte[12];
                FFUFile.Read(signatureSecurityHeader, 0, 12); // Must be "SignedImage "
                ChunkSize = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                ChunkSize = ChunkSize * 1024; // Convert from Kb to byte
                uint hashAgloId = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                uint CatalogSize = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                uint HashTableSize = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                SecurityHeaderLength = RoundUpToChunks(SecurityHeaderSize + CatalogSize + HashTableSize);

                // Read Image Header
                FFUFile.Seek(SecurityHeaderLength, SeekOrigin.Begin);
                uint ImageHeaderSize = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                byte[] signatureImageHeader = new byte[12];
                FFUFile.Read(signatureImageHeader, 0, 12); // Must be "ImageFlash  "
                uint ManifestLength = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                ImageHeaderLength = RoundUpToChunks(ImageHeaderSize + ManifestLength);

                // Read Store Header
                FFUFile.Seek(SecurityHeaderLength + ImageHeaderLength + 12, SeekOrigin.Begin); // 12 = dwUpdateType(4bytes) + MajorVersion(2bytes) + MinorVersion(2bytes) + FullFlashMajorVersion(2bytes) + FullFlashMinorVersion(2bytes)
                byte[] bufferPlatformID = new byte[192];
                FFUFile.Read(bufferPlatformID, 0, 192);
                PlatformID = System.Text.Encoding.ASCII.GetString(bufferPlatformID).TrimEnd(new char[] { (char)0, ' ' });
                uint dwBlockSizeInBytes = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                uint WriteDescriptorCount = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                uint WriteDescriptorLength = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                uint ValidateDescriptorCount = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                uint ValidateDescriptorLength = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
                StoreHeader = new byte[RoundUpToChunks(248 + WriteDescriptorLength + ValidateDescriptorLength)]; // 248 = size of the fields of the Store Header structure
                FFUFile.Seek(SecurityHeaderLength + ImageHeaderLength, SeekOrigin.Begin);
                FFUFile.Read(StoreHeader, 0, StoreHeader.Length);

                // Parse Chunk Indexes
                uint HighestChunkIndex = 0;
                uint LocationCount;
                uint ChunkIndex;
                uint ChunkCount;
                uint DiskAccessMethod;
                uint WriteDescriptorEntryOffset = 248 + ValidateDescriptorLength;
                int FFUChunkIndex = 0;
                for (uint i = 0; i < WriteDescriptorCount; i++)
                {
                    LocationCount = (uint)StoreHeader[WriteDescriptorEntryOffset] + (uint)(StoreHeader[WriteDescriptorEntryOffset+1] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 2] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 3] << 24);
                    ChunkCount = (uint)StoreHeader[WriteDescriptorEntryOffset+4] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 5] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 6] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 7] << 24);

                    for (uint j = 0; j < LocationCount; j++)
                    {
                        DiskAccessMethod = (uint)StoreHeader[WriteDescriptorEntryOffset + 8 + 8*j] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 9 + 8 * j] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 10 + 8 * j] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 11 + 8 * j] << 24);
                        ChunkIndex = (uint)StoreHeader[WriteDescriptorEntryOffset + 12 + 8 * j] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 13 + 8 * j] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 14 + 8 * j] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 15 + 8 * j] << 24);

                        if (DiskAccessMethod == 0) // 0 = From begin, 2 = From end. We ignore chunks at end of disk. These contain secondairy GPT.
                        {
                            if ((ChunkIndex + ChunkCount - 1) > HighestChunkIndex)
                                HighestChunkIndex = ChunkIndex + ChunkCount - 1;
                        }
                    }
                    WriteDescriptorEntryOffset += 8 + (LocationCount * 0x08);
                    FFUChunkIndex += (int)ChunkCount;
                }
                ChunkIndexes = new int?[HighestChunkIndex + 1];
                WriteDescriptorEntryOffset = 248 + ValidateDescriptorLength;
                FFUChunkIndex = 0;
                for (int i = 0; i < WriteDescriptorCount; i++)
                {
                    LocationCount = (uint)StoreHeader[WriteDescriptorEntryOffset] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 1] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 2] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 3] << 24);
                    ChunkCount = (uint)StoreHeader[WriteDescriptorEntryOffset + 4] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 5] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 6] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 7] << 24);

                    for (int j = 0; j < LocationCount; j++)
                    {
                        DiskAccessMethod = (uint)StoreHeader[WriteDescriptorEntryOffset + 8 + 8 * j] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 9 + 8 * j] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 10 + 8 * j] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 11 + 8 * j] << 24);
                        ChunkIndex = (uint)StoreHeader[WriteDescriptorEntryOffset + 12 + 8 * j] + (uint)(StoreHeader[WriteDescriptorEntryOffset + 13 + 8 * j] << 8) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 14 + 8 * j] << 16) + (uint)(StoreHeader[WriteDescriptorEntryOffset + 15 + 8 * j] << 24);

                        if (DiskAccessMethod == 0) // 0 = From begin, 2 = From end. We ignore chunks at end of disk. These contain secondairy GPT.
                        {
                            for (int k = 0; k < ChunkCount; k++)
                            {
                                ChunkIndexes[ChunkIndex + k] = FFUChunkIndex + k;
                            }
                        }
                    }
                    WriteDescriptorEntryOffset += 8 + (LocationCount * 0x08);
                    FFUChunkIndex += (int)ChunkCount;
                }

                byte[] GPTBuffer = GetSectors(0x01, 0x22);
                partitions = GPT.parse(GPTBuffer, GPTBuffer.Length);

                HeaderSize = (UInt64)(SecurityHeaderLength + ImageHeaderLength + StoreHeader.Length);

                TotalChunkCount = (UInt64)FFUChunkIndex;
                PayloadSize = TotalChunkCount * (UInt64)ChunkSize;
                TotalSize = HeaderSize + PayloadSize;

                if (TotalSize != (UInt64)FFUFile.Length)
                    throw new System.Exception("Bad FFU file: " + Path + "." + Environment.NewLine + "Expected size: " + TotalSize.ToString() + ". Actual size: " + FFUFile.Length + ".");
            }
            finally
            {
                CloseFile();
            }
        }

        private void OpenFile()
        {
            if (FFUFile == null)
            {
                FFUFile = new FileStream(Path, FileMode.Open, FileAccess.Read);
                FileOpenCount = 0;
            }
            FileOpenCount++;
        }

        private void CloseFile()
        {
            FileOpenCount--;
            if (FileOpenCount == 0)
            {
                FFUFile.Close();
                FFUFile = null;
            }
        }

        private void FileSeek(long Position)
        {
            // https://social.msdn.microsoft.com/Forums/vstudio/en-US/2e67ca57-3556-4275-accd-58b7df30d424/unnecessary-filestreamseek-and-setting-filestreamposition-has-huge-effect-on-performance?forum=csharpgeneral

            if (FFUFile != null)
            {
                if (FFUFile.Position != Position)
                    FFUFile.Seek(Position, SeekOrigin.Begin);
            }
        }

        internal UInt32 RoundUpToChunks(UInt32 Size)
        {
            if ((Size % ChunkSize) > 0)
                return (UInt32)(((Size / ChunkSize) + 1) * ChunkSize);
            else
                return Size;
        }



        internal byte[] GetSectors(int StartSector, int SectorCount)
        {
            int FirstChunk = GetChunkIndexFromSectorIndex(StartSector);
            int LastChunk = GetChunkIndexFromSectorIndex(StartSector + SectorCount - 1);

            byte[] Buffer = new byte[ChunkSize];

            OpenFile();

            byte[] Result = new byte[SectorCount * 0x200];

            int ResultOffset = 0;

            for (int j = FirstChunk; j <= LastChunk; j++)
            {
                GetChunk(Buffer, j);

                int FirstSector = 0;
                int LastSector = ((int)ChunkSize / 0x200) - 1;

                if (j == FirstChunk)
                    FirstSector = GetSectorNumberInChunkFromSectorIndex(StartSector);

                if (j == LastChunk)
                    LastSector = GetSectorNumberInChunkFromSectorIndex(StartSector + SectorCount - 1);

                int Offset = FirstSector * 0x200;
                int Size = ((int)LastSector - FirstSector + 1) * 0x200;

                System.Buffer.BlockCopy(Buffer, Offset, Result, ResultOffset, Size);

                ResultOffset += Size;
            }

            CloseFile();

            return Result;
        }

        internal byte[] GetPartition(string Name)
        {
            Partition Target = partitions.Where(p => (string.Compare(p.name, Name, true) == 0)).FirstOrDefault();
            if (Target == null)
                throw new ArgumentOutOfRangeException();
            return GetSectors((int)Target.firstSector, (int)(Target.lastSector - Target.firstSector + 1));
        }


        private void GetChunk(byte[] Chunk, int ChunkIndex)
        {
            long BaseOffset = SecurityHeaderLength + ImageHeaderLength + StoreHeader.Length;
            if (ChunkIndexes[ChunkIndex] == null)
                Array.Clear(Chunk, 0, (int)ChunkSize);
            else
            {
                OpenFile();
                FileSeek(BaseOffset + ((long)ChunkIndexes[ChunkIndex] * ChunkSize));
                FFUFile.Read(Chunk, 0, (int)ChunkSize);
                CloseFile();
            }
        }

        private int GetChunkIndexFromSectorIndex(int SectorIndex)
        {
            int SectorsPerChunk = (int)ChunkSize / 0x200;
            return SectorIndex / SectorsPerChunk;
        }

        private int GetSectorNumberInChunkFromSectorIndex(int SectorIndex)
        {
            int SectorsPerChunk = (int)ChunkSize / 0x200;
            return SectorIndex % SectorsPerChunk;
        }


        private int GetChunkIndexFromSectorIndex(ulong p)
        {
            throw new NotImplementedException();
        }


        public static bool checkFile(string ffuPath)
        {
            bool correct = true;

            FileStream FFUFile = new FileStream(ffuPath, FileMode.Open, FileAccess.Read);

            // Read Security Header
            uint securityHeaderSize = (uint)FFUFile.ReadByte() + (uint)(FFUFile.ReadByte() << 8) + (uint)(FFUFile.ReadByte() << 16) + (uint)(FFUFile.ReadByte() << 24);
            byte[] signature = new byte[12];
            FFUFile.Read(signature, 0, 12);
            if (!"SignedImage ".Equals(System.Text.Encoding.ASCII.GetString(signature)))
            {
                Console.WriteLine("Incorrect signature.");
                correct = false;
            }

            FFUFile.Close();

            return correct;
        }

        public byte[] getCombinedHeader()
        {
            ulong combinedFFUHeaderSize = HeaderSize; // SecurityHeader + ImageHeader + StoreHeader
            byte[] FfuHeader = new byte[combinedFFUHeaderSize];
            OpenFile();
            FFUFile.Read(FfuHeader, 0, (int)combinedFFUHeaderSize);
            CloseFile();

            return FfuHeader;
        }
    }
}
