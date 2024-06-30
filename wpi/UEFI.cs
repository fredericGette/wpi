using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace wpi
{
    internal class EFI
    {
        internal Guid Guid;
        internal string Name;
        internal int Type;
        internal UInt32 Size;
        internal UInt32 FileOffset;
        internal UInt32 SectionOffset;
        internal UInt32 BinaryOffset;
    }

    class UEFI
    {
        internal byte[] Binary;
        private byte[] DecompressedImage;
        internal List<EFI> EFIs = new List<EFI>();
        private byte PaddingByteValue = 0xFF;
        private UInt32 DecompressedVolumeSectionHeaderOffset;
        private UInt32 DecompressedVolumeHeaderOffset;
        private UInt32 VolumeSize;
        private UInt16 VolumeHeaderSize;
        private UInt32 FileHeaderOffset;
        private UInt32 SectionHeaderOffset;
        private UInt32 CompressedSubImageOffset;
        private UInt32 CompressedSubImageSize;

        // First 40 bytes are Qualcomm partition header
        // Inside the attributes of the VolumeHeader, the Volume-alignment is set to 8 (on Windows Phone UEFI images)
        // The Volume always starts right after the Qualcomm header at position 40.
        // So the VolumeHeader-alignment is always complied.
        private UInt32 VolumeHeaderOffset = 40;


        public UEFI(byte[] UefiBinary, bool parse)
        {
            Binary = UefiBinary;

            if (!parse) return; // We dont need to parse the content of the UEFI partition (certainly because we are in REPAIR mode).

            // Find VolumeHeaderOffset
            byte[] VolumeHeaderMagic = new byte[] { 0x5F, 0x46, 0x56, 0x48 }; // _FVH
            int patternPosition = -1;
            for (int i = 0; i < Binary.Length; i++)
            {
                if (Binary.Skip(i).Take(VolumeHeaderMagic.Length).SequenceEqual(VolumeHeaderMagic))
                {
                    patternPosition = i;
                    break;
                }
            }
            if (patternPosition == -1)
                throw new BadImageFormatException();
            else
                VolumeHeaderOffset = (UInt32)patternPosition - 40;

            if (!VerifyVolumeChecksum(Binary, VolumeHeaderOffset))
                throw new BadImageFormatException();

            VolumeSize = ((uint)Binary[VolumeHeaderOffset + 35] << 24) + ((uint)Binary[VolumeHeaderOffset + 34] << 16) + ((uint)Binary[VolumeHeaderOffset + 33] << 8) + Binary[VolumeHeaderOffset + 32]; // TODO: This is actually a QWORD
            VolumeHeaderSize = (ushort)((Binary[VolumeHeaderOffset + 49] << 8) + Binary[VolumeHeaderOffset + 48]); 
            uint test = ((uint)Binary[VolumeHeaderOffset + 47] << 24) + ((uint)Binary[VolumeHeaderOffset + 46] << 16) + ((uint)Binary[VolumeHeaderOffset + 45] << 8) + Binary[VolumeHeaderOffset + 44];
            PaddingByteValue = (test & 0x00000800) > 0 ? (byte)0xFF : (byte)0x00; // EFI_FVB_ERASE_POLARITY = 0x00000800

            // In the volume look for a file of type EFI_FV_FILETYPE_FIRMWARE_VOLUME_IMAGE (0x0B)

            FileHeaderOffset = VolumeHeaderOffset + VolumeHeaderSize;
            bool VolumeFound = false;
            int FileType;
            UInt32 FileSize;
            do
            {
                if (!VerifyFileChecksum(Binary, FileHeaderOffset))
                    throw new BadImageFormatException();

                FileType = Binary[FileHeaderOffset + 18];
                FileSize = ((uint)Binary[FileHeaderOffset + 22] << 16) + ((uint)Binary[FileHeaderOffset + 21] << 8) + Binary[FileHeaderOffset + 20];

                if (FileType == 0x0B) // EFI_FV_FILETYPE_FIRMWARE_VOLUME_IMAGE
                {
                    VolumeFound = true;
                }
                else
                {
                    FileHeaderOffset += FileSize;

                    // FileHeaderOffset in Volume-body must be Align 8
                    // In the file-header-attributes the file-alignment relative to the start of the volume is always set to 1,
                    // so that alignment can be ignored.
                    FileHeaderOffset = Align(VolumeHeaderOffset + VolumeHeaderSize, FileHeaderOffset, 8);
                }
            }
            while (!VolumeFound && (FileHeaderOffset < (VolumeHeaderOffset + VolumeSize)));

            if (!VolumeFound)
                throw new BadImageFormatException();

            // Look in file for section of type EFI_SECTION_GUID_DEFINED (0x02)

            SectionHeaderOffset = FileHeaderOffset + 24;
            int SectionType;
            UInt32 SectionSize;
            UInt16 SectionHeaderSize = 0;

            bool DecompressedVolumeFound = false;
            do
            {
                SectionType = Binary[SectionHeaderOffset + 3];
                SectionSize = ((uint)Binary[SectionHeaderOffset + 2] << 16) + ((uint)Binary[SectionHeaderOffset + 1] << 8) + Binary[SectionHeaderOffset];

                if (SectionType == 0x02) // EFI_SECTION_GUID_DEFINED
                {
                    SectionHeaderSize = (ushort)((Binary[SectionHeaderOffset + 21] << 8) + Binary[SectionHeaderOffset+20]);
                    DecompressedVolumeFound = true;
                }
                else
                {
                    SectionHeaderOffset += SectionSize;

                    // SectionHeaderOffset in File-body must be Align 4
                    SectionHeaderOffset = Align(FileHeaderOffset + 24, SectionHeaderOffset, 4);
                }
            }
            while (!DecompressedVolumeFound && (SectionHeaderOffset < (FileHeaderOffset + FileSize)));

            if (!DecompressedVolumeFound)
                throw new BadImageFormatException();

            // Decompress subvolume
            CompressedSubImageOffset = SectionHeaderOffset + SectionHeaderSize;
            CompressedSubImageSize = SectionSize - SectionHeaderSize;

            // DECOMPRESS HERE
            DecompressedImage = LZMA.Decompress(Binary, CompressedSubImageOffset, CompressedSubImageSize);

            // Extracted volume contains Sections at its root level

            DecompressedVolumeSectionHeaderOffset = 0;
            DecompressedVolumeFound = false;
            do
            {
                SectionType = DecompressedImage[DecompressedVolumeSectionHeaderOffset + 3];
                SectionSize = ((uint)DecompressedImage[DecompressedVolumeSectionHeaderOffset + 2] << 16) + ((uint)DecompressedImage[DecompressedVolumeSectionHeaderOffset + 1] << 8) + DecompressedImage[DecompressedVolumeSectionHeaderOffset];
                SectionHeaderSize = (ushort)((DecompressedImage[DecompressedVolumeSectionHeaderOffset + 21] << 8) + DecompressedImage[DecompressedVolumeSectionHeaderOffset + 20]);

                if (SectionType == 0x17) // EFI_SECTION_FIRMWARE_VOLUME_IMAGE
                {
                    DecompressedVolumeFound = true;
                }
                else
                {
                    DecompressedVolumeSectionHeaderOffset += SectionSize;

                    // SectionHeaderOffset in File-body must be Align 4
                    DecompressedVolumeSectionHeaderOffset = Align(FileHeaderOffset + 24, DecompressedVolumeSectionHeaderOffset, 4);
                }
            }
            while (!DecompressedVolumeFound && (DecompressedVolumeSectionHeaderOffset < DecompressedImage.Length));

            if (!DecompressedVolumeFound)
                throw new BadImageFormatException();

            DecompressedVolumeHeaderOffset = DecompressedVolumeSectionHeaderOffset + 4;

            // PARSE COMPRESSED VOLUME
            if (!DecompressedImage.Skip((int)DecompressedVolumeHeaderOffset + 40).Take(VolumeHeaderMagic.Length).SequenceEqual(VolumeHeaderMagic))
                throw new BadImageFormatException();

            if (!VerifyVolumeChecksum(DecompressedImage, DecompressedVolumeHeaderOffset))
                throw new BadImageFormatException();

            uint DecompressedVolumeSize = ((uint)DecompressedImage[DecompressedVolumeHeaderOffset + 35] << 24) + ((uint)DecompressedImage[DecompressedVolumeHeaderOffset + 34] << 16) + ((uint)DecompressedImage[DecompressedVolumeHeaderOffset + 33] << 8) + DecompressedImage[DecompressedVolumeHeaderOffset + 32]; // TODO: This is actually a QWORD
            uint DecompressedVolumeHeaderSize = ((uint)DecompressedImage[DecompressedVolumeHeaderOffset + 49] << 8) + DecompressedImage[DecompressedVolumeHeaderOffset + 48];

            // The files in this decompressed volume are the real EFI's.
            if (Program.verbose) Console.WriteLine("\nUEFI files:");
            UInt32 DecompressedFileHeaderOffset = DecompressedVolumeHeaderOffset + DecompressedVolumeHeaderSize;
            EFI CurrentEFI;
            do
            {
                if ((DecompressedFileHeaderOffset + 24) >= (DecompressedVolumeHeaderOffset + DecompressedVolumeSize))
                    break;

                bool ContentFound = false;
                for (int i = 0; i < 24; i++)
                {
                    if (DecompressedImage[DecompressedFileHeaderOffset + i] != PaddingByteValue)
                    {
                        ContentFound = true;
                        break;
                    }
                }
                if (!ContentFound)
                    break;

                FileSize = ((uint)DecompressedImage[DecompressedFileHeaderOffset + 22] << 16) + ((uint)DecompressedImage[DecompressedFileHeaderOffset + 21] << 8) + DecompressedImage[DecompressedFileHeaderOffset + 20];

                if ((DecompressedFileHeaderOffset + FileSize) >= (DecompressedVolumeHeaderOffset + DecompressedVolumeSize))
                    break;

                if (!VerifyFileChecksum(DecompressedImage, DecompressedFileHeaderOffset))
                    throw new BadImageFormatException();

                CurrentEFI = new EFI();

                CurrentEFI.Type = DecompressedImage[DecompressedFileHeaderOffset + 18];
                byte[] FileGuidBytes = new byte[16];
                System.Buffer.BlockCopy(DecompressedImage, (int)DecompressedFileHeaderOffset, FileGuidBytes, 0, 16);
                CurrentEFI.Guid = new Guid(FileGuidBytes);

                // Parse sections of the EFI
                CurrentEFI.FileOffset = DecompressedFileHeaderOffset;
                UInt32 DecompressedSectionHeaderOffset = DecompressedFileHeaderOffset + 0x18;
                do
                {
                    SectionType = DecompressedImage[DecompressedSectionHeaderOffset + 3];
                    SectionSize = ((uint)DecompressedImage[DecompressedSectionHeaderOffset + 2] << 16) + ((uint)DecompressedImage[DecompressedSectionHeaderOffset + 1] << 8) + DecompressedImage[DecompressedSectionHeaderOffset];

                    // SectionTypes that are relevant here:
                    // 0x10 = PE File
                    // 0x19 = RAW
                    // 0x15 = Description
                    // Not all section headers in the UEFI specs are 4 bytes long,
                    // but the sections that are used in Windows Phone EFI's all have a header of 4 bytes.
                    if (SectionType == 0x15)
                    {
                        byte[] nameBuffer = new byte[SectionSize - 4];  // UTF-16 characters
                        Buffer.BlockCopy(DecompressedImage, (int)DecompressedSectionHeaderOffset + 4, nameBuffer, 0, (int)SectionSize - 4);
                        CurrentEFI.Name = System.Text.Encoding.Unicode.GetString(nameBuffer);
                        CurrentEFI.Name = CurrentEFI.Name.TrimEnd(new char[] { (char)0, ' ' }); // Remove multiple trailing \0 
                    }
                    else if ((SectionType == 0x10) || (SectionType == 0x19))
                    {
                        CurrentEFI.SectionOffset = DecompressedSectionHeaderOffset;
                        CurrentEFI.BinaryOffset = DecompressedSectionHeaderOffset + 0x04;
                        CurrentEFI.Size = SectionSize - 0x04;
                    }

                    DecompressedSectionHeaderOffset += SectionSize;

                    // SectionHeaderOffset in File-body must be Align 4
                    DecompressedSectionHeaderOffset = Align(DecompressedFileHeaderOffset + 0x18, DecompressedSectionHeaderOffset, 4);
                }
                while (DecompressedSectionHeaderOffset < (DecompressedFileHeaderOffset + FileSize));

                DecompressedFileHeaderOffset += FileSize;

                // FileHeaderOffset in Volume-body must be Align 8
                // In the file-header-attributes the file-alignment relative to the start of the volume is always set to 1,
                // so that alignment can be ignored.
                DecompressedFileHeaderOffset = Align(DecompressedVolumeHeaderOffset + DecompressedVolumeHeaderSize, DecompressedFileHeaderOffset, 8);

                if (Program.verbose) Console.WriteLine("\t{0,-30} {1,6} bytes", CurrentEFI.Name, CurrentEFI.Size);

                EFIs.Add(CurrentEFI);
            }
            while (DecompressedFileHeaderOffset < (DecompressedVolumeHeaderOffset + DecompressedVolumeSize));
        }

        public byte[] Patch()
        {
            byte[] SecurityDxe = GetFile("SecurityDxe");
            // Clear EFI checksum
            uint PEHeaderOffset = ((uint)SecurityDxe[63] << 24) + ((uint)SecurityDxe[62] << 16) + ((uint)SecurityDxe[61] << 8) + SecurityDxe[60];
            SecurityDxe[PEHeaderOffset + 88] = 0x00;
            SecurityDxe[PEHeaderOffset + 89] = 0x00;
            SecurityDxe[PEHeaderOffset + 90] = 0x00;
            SecurityDxe[PEHeaderOffset + 91] = 0x00;

            byte[] pattern1ToPatch = new byte[] { 0xF0, 0x41, 0x2D, 0xE9 };
            byte[] pattern2ToPatch = new byte[] { 0xB0, 0xE1, 0x28, 0xD0, 0x4D, 0xE2 };
            byte[] pattern3ToPatch = new byte[] { 0xA0, 0xE1, 0x00, 0x00 };
            byte[] pattern4ToPatch = new byte[] { 0x13, 0x20 };
            byte[] pattern5ToPatch = new byte[] { 0xA0, 0xE3 };
            int PatchOffset = -1;
            for (int i = 0; i < SecurityDxe.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar

                if (SecurityDxe.Skip(i).Take(pattern1ToPatch.Length).SequenceEqual(pattern1ToPatch))
                {
                   if (SecurityDxe.Skip(i + 6).Take(pattern2ToPatch.Length).SequenceEqual(pattern2ToPatch)
                        &&
                        SecurityDxe.Skip(i + 14).Take(pattern3ToPatch.Length).SequenceEqual(pattern3ToPatch)
                        &&
                        SecurityDxe.Skip(i + 19).Take(pattern4ToPatch.Length).SequenceEqual(pattern4ToPatch)
                        &&
                        SecurityDxe.Skip(i + 22).Take(pattern5ToPatch.Length).SequenceEqual(pattern5ToPatch)
                        )
                    {
                        PatchOffset = i;
                        break;
                    }
                }
            }
            if (PatchOffset == -1)
                throw new BadImageFormatException();

            Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1 }, 0, SecurityDxe, (int)PatchOffset, 8);

            ReplaceFile("SecurityDxe", SecurityDxe);

            byte[] SecurityServicesDxe = GetFile("SecurityServicesDxe");
            // Clear EFI checksum
            PEHeaderOffset = ((uint)SecurityServicesDxe[63] << 24) + ((uint)SecurityServicesDxe[62] << 16) + ((uint)SecurityServicesDxe[61] << 8) + SecurityServicesDxe[60];
            SecurityServicesDxe[PEHeaderOffset + 88] = 0x00;
            SecurityServicesDxe[PEHeaderOffset + 89] = 0x00;
            SecurityServicesDxe[PEHeaderOffset + 90] = 0x00;
            SecurityServicesDxe[PEHeaderOffset + 91] = 0x00;

            pattern1ToPatch = new byte[] { 0x10 };
            pattern2ToPatch = new byte[] { 0xE5, 0x80 };
            pattern3ToPatch = new byte[] { 0x10, 0xE3 };
            pattern4ToPatch = new byte[] { 0x0A };
            PatchOffset = -1;
            for (int i = 0; i < SecurityServicesDxe.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar

                if (SecurityServicesDxe.Skip(i).Take(pattern1ToPatch.Length).SequenceEqual(pattern1ToPatch))
                {
                    if (SecurityServicesDxe.Skip(i + 3).Take(pattern2ToPatch.Length).SequenceEqual(pattern2ToPatch)
                         &&
                         SecurityServicesDxe.Skip(i + 6).Take(pattern3ToPatch.Length).SequenceEqual(pattern3ToPatch)
                         &&
                         SecurityServicesDxe.Skip(i + 11).Take(pattern4ToPatch.Length).SequenceEqual(pattern4ToPatch)
                         )
                    {
                        PatchOffset = i;
                        break;
                    }
                }
            }
            if (PatchOffset == -1)
                throw new BadImageFormatException();

            SecurityServicesDxe[(int)PatchOffset + 11] = 0xEA;

            pattern1ToPatch = new byte[] { 0x11 };
            pattern2ToPatch = new byte[] { 0xE5, 0x40 };
            pattern3ToPatch = new byte[] { 0x10, 0xE3 };
            pattern4ToPatch = new byte[] { 0x0A };
            PatchOffset = -1;
            for (int i = 0; i < SecurityServicesDxe.Length; i++)
            {
                if (i % 1000 == 0) Console.Write("."); // Progress bar

                if (SecurityServicesDxe.Skip(i).Take(pattern1ToPatch.Length).SequenceEqual(pattern1ToPatch))
                {
                    if (SecurityServicesDxe.Skip(i + 3).Take(pattern2ToPatch.Length).SequenceEqual(pattern2ToPatch)
                         &&
                         SecurityServicesDxe.Skip(i + 6).Take(pattern3ToPatch.Length).SequenceEqual(pattern3ToPatch)
                         &&
                         SecurityServicesDxe.Skip(i + 11).Take(pattern4ToPatch.Length).SequenceEqual(pattern4ToPatch)
                         )
                    {
                        PatchOffset = i;
                        break;
                    }
                }
            }
            Console.WriteLine();
            if (PatchOffset == -1)
                throw new BadImageFormatException();

            SecurityServicesDxe[PatchOffset + 11] = 0xEA;

            ReplaceFile("SecurityServicesDxe", SecurityServicesDxe);

            return Rebuild();
        }

        internal byte[] Rebuild()
        {
            // The new binary will include the Qualcomm header, but not the signature and certificates, because they won't match anyway.
            byte[] NewBinary = new byte[Binary.Length];
            Buffer.BlockCopy(Binary, 0, NewBinary, 0, (int)CompressedSubImageOffset);

            NewBinary[16] = NewBinary[20]; // Complete image size - does not include signature and certs anymore
            NewBinary[17] = NewBinary[21];
            NewBinary[18] = NewBinary[22];
            NewBinary[19] = NewBinary[23];

            NewBinary[24] = 0x00; // Address of signature
            NewBinary[25] = 0x00;
            NewBinary[26] = 0x00;
            NewBinary[27] = 0x00;

            NewBinary[28] = 0x00; // Signature length
            NewBinary[29] = 0x00;
            NewBinary[30] = 0x00;
            NewBinary[31] = 0x00;

            NewBinary[32] = 0x00; // Address of certificate
            NewBinary[33] = 0x00;
            NewBinary[34] = 0x00;
            NewBinary[35] = 0x00;

            NewBinary[36] = 0x00; // Certificate length
            NewBinary[37] = 0x00;
            NewBinary[38] = 0x00;
            NewBinary[39] = 0x00; 

            // Compress volume
            byte[] NewCompressedImage = LZMA.Compress(DecompressedImage, 0, (UInt32)DecompressedImage.Length);

            // Replace compressed volume and add correct padding
            // First copy new image
            Buffer.BlockCopy(NewCompressedImage, 0, NewBinary, (int)CompressedSubImageOffset, NewCompressedImage.Length);

            // Determine padding
            UInt32 OldSectionPadding = Align(0, CompressedSubImageSize, 4) - CompressedSubImageSize;
            UInt32 NewSectionPadding = Align(0, (UInt32)NewCompressedImage.Length, 4) - (UInt32)NewCompressedImage.Length;
            UInt32 OldFileSize = ((uint)Binary[FileHeaderOffset + 22] << 16) + ((uint)Binary[FileHeaderOffset + 21] << 8) + Binary[FileHeaderOffset + 20];

            // Filesize includes fileheader. But it does not include the padding-bytes. Not even the padding bytes of the last section.
            UInt32 NewFileSize;
            if ((CompressedSubImageOffset + CompressedSubImageSize + OldSectionPadding) >= (FileHeaderOffset + OldFileSize))
                // Compressed image is the last section of this file
                NewFileSize = CompressedSubImageOffset - FileHeaderOffset + (UInt32)NewCompressedImage.Length;
            else
                // Compressed image is NOT the last section of this file
                NewFileSize = OldFileSize - CompressedSubImageSize - OldSectionPadding + (UInt32)NewCompressedImage.Length + NewSectionPadding;

            // Add section padding
            for (int i = 0; i < NewSectionPadding; i++)
                NewBinary[CompressedSubImageOffset + NewCompressedImage.Length + i] = PaddingByteValue;

            // If there are more bytes after the section padding of the compressed image, then copy the trailing sections
            if (((Int32)FileHeaderOffset + OldFileSize - CompressedSubImageOffset - CompressedSubImageSize - OldSectionPadding) > 0)
                Buffer.BlockCopy(Binary, (int)(CompressedSubImageOffset + CompressedSubImageSize + OldSectionPadding), NewBinary,
                    (int)(CompressedSubImageOffset + NewCompressedImage.Length + NewSectionPadding),
                    (int)(FileHeaderOffset + OldFileSize - CompressedSubImageOffset - CompressedSubImageSize - OldSectionPadding));

            // Add file padding
            // Filesize does not include last section padding or file padding
            UInt32 OldFilePadding = Align(0, OldFileSize, 8) - OldFileSize;
            UInt32 NewFilePadding = Align(0, NewFileSize, 8) - NewFileSize;
            for (int i = 0; i < NewFilePadding; i++)
                NewBinary[FileHeaderOffset + NewFileSize + i] = PaddingByteValue;

            if (NewCompressedImage.Length > CompressedSubImageSize)
            {
                Buffer.BlockCopy(Binary, (int)(FileHeaderOffset + OldFileSize + OldFilePadding), NewBinary, (int)(FileHeaderOffset + NewFileSize + NewFilePadding),
                    (int)(VolumeHeaderOffset + VolumeSize - FileHeaderOffset - NewFileSize - NewFilePadding));
            }
            else
            {
                Buffer.BlockCopy(Binary, (int)(FileHeaderOffset + OldFileSize + OldFilePadding), NewBinary, (int)(FileHeaderOffset + NewFileSize + NewFilePadding),
                    (int)(VolumeHeaderOffset + VolumeSize - FileHeaderOffset - OldFileSize - OldFilePadding));
                for (int i = (int)(VolumeHeaderOffset + VolumeSize - OldFileSize - OldFilePadding + NewFileSize + NewFilePadding); i < VolumeHeaderOffset + VolumeSize; i++)
                    NewBinary[i] = PaddingByteValue;
            }
            CompressedSubImageSize = (UInt32)NewCompressedImage.Length;

            // Fix section
            uint sectionValue = CompressedSubImageSize + ((uint)Binary[SectionHeaderOffset + 21] << 8) + Binary[SectionHeaderOffset + 20];
            NewBinary[SectionHeaderOffset] = (byte)(sectionValue & 0xFF);
            NewBinary[SectionHeaderOffset + 1] = (byte)((sectionValue >> 8) & 0xFF);
            NewBinary[SectionHeaderOffset + 2] = (byte)((sectionValue >> 16) & 0xFF);

            // Fix file
            NewBinary[FileHeaderOffset + 20] = (byte)(NewFileSize & 0xFF);
            NewBinary[FileHeaderOffset + 21] = (byte)((NewFileSize >> 8) & 0xFF);
            NewBinary[FileHeaderOffset + 22] = (byte)((NewFileSize >> 16) & 0xFF);
            CalculateFileChecksum(NewBinary, FileHeaderOffset);

            // Fix volume (volume size is fixed)
            CalculateVolumeChecksum(NewBinary, VolumeHeaderOffset);

            Binary = NewBinary;
            return Binary;
        }

        private bool VerifyVolumeChecksum(byte[] Image, UInt32 Offset)
        {
            uint VolumeHeaderSize = ((uint)Image[Offset + 49] << 8) + Image[Offset + 48];
            byte[] Header = new byte[VolumeHeaderSize];
            System.Buffer.BlockCopy(Image, (int)Offset, Header, 0, (int)VolumeHeaderSize);

            Header[50] = 0x00; // Clear checksum
            Header[51] = 0x00;

            uint CurrentChecksum = ((uint)Image[Offset + 51] << 8) + Image[Offset + 50];
            UInt16 NewChecksum = CalculateChecksum16(Header, 0, VolumeHeaderSize);
            return (CurrentChecksum == NewChecksum);
        }

        internal UInt16 CalculateChecksum16(byte[] Buffer, UInt32 Offset, UInt32 Size)
        {
            UInt16 Checksum = 0;

            for (UInt32 i = Offset; i < (Offset + Size - 1); i += 2)
                Checksum += BitConverter.ToUInt16(Buffer, (int)i);

            return (UInt16)(0x10000 - Checksum);
        }

        internal byte CalculateChecksum8(byte[] Buffer, UInt32 Offset, UInt32 Size)
        {
            byte Checksum = 0;

            for (UInt32 i = Offset; i < (Offset + Size); i++)
                Checksum += Buffer[i];

            return (byte)(0x100 - Checksum);
        }

        private bool VerifyFileChecksum(byte[] Image, UInt32 Offset)
        {
            // This function only checks fixed checksum-values 0x55 and 0xAA.

            UInt16 FileHeaderSize = 0x18;
            UInt32 FileSize = ((uint)Image[Offset + 22] << 16) + ((uint)Image[Offset + 21] << 8) + Image[Offset + 20];

            byte[] Header = new byte[FileHeaderSize - 1];
            System.Buffer.BlockCopy(Image, (int)Offset, Header, 0, FileHeaderSize - 1);

            Header[16] = 0x00; // Clear checksum
            Header[17] = 0x00;

            byte CurrentHeaderChecksum = Image[Offset + 16];
            byte CalculatedHeaderChecksum = CalculateChecksum8(Header, 0, (UInt32)FileHeaderSize - 1);

            if (CurrentHeaderChecksum != CalculatedHeaderChecksum)
                return false;

            byte FileAttribs = Image[Offset+19];
            byte CurrentFileChecksum = Image[Offset + 17];
            if ((FileAttribs & 0x40) > 0)
            {
                // Calculate file checksum
                byte CalculatedFileChecksum = CalculateChecksum8(Image, Offset + FileHeaderSize, FileSize - FileHeaderSize);
                if (CurrentFileChecksum != CalculatedFileChecksum)
                    return false;
            }
            else
            {
                // Fixed file checksum
                if ((CurrentFileChecksum != 0xAA) && (CurrentFileChecksum != 0x55))
                    return false;
            }

            return true;
        }

        internal UInt32 Align(UInt32 Base, UInt32 Offset, UInt32 Alignment)
        {
            if (((Offset - Base) % Alignment) == 0)
                return Offset;
            else
                return ((((Offset - Base) / Alignment) + 1) * Alignment) + Base;
        }

        internal byte[] GetFile(string Name)
        {
            EFI File = EFIs.Where(f => (string.Compare(Name, f.Name, true) == 0) || (string.Compare(Name, f.Guid.ToString(), true) == 0)).FirstOrDefault();
            if (File == null)
                return null;

            byte[] Bytes = new byte[File.Size];
            Buffer.BlockCopy(DecompressedImage, (int)File.BinaryOffset, Bytes, 0, (int)File.Size);

            return Bytes;
        }

        internal void ReplaceFile(string Name, byte[] Binary)
        {
            EFI File = EFIs.Where(f => (string.Compare(Name, f.Name, true) == 0) || (string.Compare(Name, f.Guid.ToString(), true) == 0)).FirstOrDefault();
            if (File == null)
                throw new ArgumentOutOfRangeException();

            UInt32 OldBinarySize = File.Size;
            UInt32 NewBinarySize = (UInt32)Binary.Length;

            UInt32 OldSectionPadding = Align(0, OldBinarySize, 4) - OldBinarySize;
            UInt32 NewSectionPadding = Align(0, NewBinarySize, 4) - NewBinarySize;

            UInt32 OldFileSize = ((uint)DecompressedImage[File.FileOffset + 22] << 16) + ((uint)DecompressedImage[File.FileOffset + 21] << 8) + DecompressedImage[File.FileOffset + 20];
            UInt32 NewFileSize = OldFileSize - OldBinarySize - OldSectionPadding + NewBinarySize + NewSectionPadding;

            UInt32 OldFilePadding = Align(0, OldFileSize, 8) - OldFileSize;
            UInt32 NewFilePadding = Align(0, NewFileSize, 8) - NewFileSize;

            if ((OldBinarySize + OldSectionPadding) != (NewBinarySize + NewSectionPadding))
            {
                byte[] NewImage = new byte[DecompressedImage.Length - OldFileSize - OldFilePadding + NewFileSize + NewFilePadding]; // Also preserve space for File-alignement here

                // Copy Volume-head and File-head
                Buffer.BlockCopy(DecompressedImage, 0, NewImage, 0, (int)File.BinaryOffset);

                // Copy new binary
                Buffer.BlockCopy(Binary, 0, NewImage, (int)File.BinaryOffset, Binary.Length);

                // Insert section-padding
                for (int i = 0; i < NewSectionPadding; i++)
                    NewImage[File.BinaryOffset + NewBinarySize + i] = PaddingByteValue;

                // Copy file-tail
                Buffer.BlockCopy(
                    DecompressedImage,
                    (int)(File.BinaryOffset + OldBinarySize + OldSectionPadding),
                    NewImage,
                    (int)(File.BinaryOffset + NewBinarySize + NewSectionPadding),
                    (int)(File.FileOffset + OldFileSize - File.BinaryOffset - OldBinarySize - OldSectionPadding));

                // Insert file-padding
                for (int i = 0; i < NewFilePadding; i++)
                    NewImage[File.BinaryOffset + NewFileSize + i] = PaddingByteValue;

                // Copy volume-tail
                Buffer.BlockCopy(
                    DecompressedImage,
                    (int)(File.FileOffset + OldFileSize + OldFilePadding),
                    NewImage,
                    (int)(File.FileOffset + NewFileSize + NewFilePadding),
                    (int)(DecompressedImage.Length - File.FileOffset - OldFileSize - OldFilePadding));

                Int32 NewOffset = (int)(NewFileSize + NewFilePadding) - (int)(OldFileSize - OldFilePadding);

                // Fix section-size
                uint sectionSize = ((uint)NewImage[File.SectionOffset + 2] << 16) + ((uint)NewImage[File.SectionOffset + 1] << 8) + NewImage[File.SectionOffset] + (uint)NewOffset;
                NewImage[File.SectionOffset] = (byte)(sectionSize & 0xFF);
                NewImage[File.SectionOffset + 1] = (byte)((sectionSize >> 8) & 0xFF);
                NewImage[File.SectionOffset + 2] = (byte)((sectionSize >> 16) & 0xFF);

                // Fix file-size
                uint fileSize = ((uint)NewImage[File.FileOffset + 22] << 16) + ((uint)NewImage[File.FileOffset + 21] << 8) + NewImage[File.FileOffset + 20] + (uint)NewOffset;
                NewImage[File.FileOffset + 20] = (byte)(fileSize & 0xFF);
                NewImage[File.FileOffset + 21] = (byte)((fileSize >> 8) & 0xFF);
                NewImage[File.FileOffset + 22] = (byte)((fileSize >> 16) & 0xFF);

                // Fix volume-size - TODO: This is actually a QWORD
                uint volumeSize = ((uint)NewImage[DecompressedVolumeHeaderOffset + 35] << 24) + ((uint)NewImage[DecompressedVolumeHeaderOffset + 34] << 16) + ((uint)NewImage[DecompressedVolumeHeaderOffset + 33] << 8) + NewImage[DecompressedVolumeHeaderOffset + 32] + (uint)NewOffset;
                NewImage[DecompressedVolumeHeaderOffset + 32] = (byte)(volumeSize & 0xFF);
                NewImage[DecompressedVolumeHeaderOffset + 33] = (byte)((volumeSize >> 8) & 0xFF);
                NewImage[DecompressedVolumeHeaderOffset + 34] = (byte)((volumeSize >> 16) & 0xFF);
                NewImage[DecompressedVolumeHeaderOffset + 35] = (byte)((volumeSize >> 24) & 0xFF);

                // Fix section-size
                sectionSize = ((uint)NewImage[DecompressedVolumeHeaderOffset + 2] << 16) + ((uint)NewImage[DecompressedVolumeHeaderOffset + 1] << 8) + NewImage[DecompressedVolumeHeaderOffset] + (uint)NewOffset;
                NewImage[DecompressedVolumeHeaderOffset] = (byte)(sectionSize & 0xFF);
                NewImage[DecompressedVolumeHeaderOffset + 1] = (byte)((sectionSize >> 8) & 0xFF);
                NewImage[DecompressedVolumeHeaderOffset + 2] = (byte)((sectionSize >> 16) & 0xFF);

                DecompressedImage = NewImage;

                // Modify all sizes in EFI's
                foreach (EFI CurrentFile in EFIs)
                {
                    if (CurrentFile.FileOffset > File.FileOffset)
                    {
                        CurrentFile.FileOffset = (UInt32)(CurrentFile.FileOffset + NewOffset);
                        CurrentFile.SectionOffset = (UInt32)(CurrentFile.SectionOffset + NewOffset);
                        CurrentFile.BinaryOffset = (UInt32)(CurrentFile.BinaryOffset + NewOffset);
                    }
                }
            }
            else
            {
                Buffer.BlockCopy(Binary, 0, DecompressedImage, (int)File.BinaryOffset, Binary.Length);
                for (int i = 0; i < NewSectionPadding; i++)
                    DecompressedImage[File.BinaryOffset + Binary.Length + i] = PaddingByteValue;
            }

            // Calculate File-checksum
            CalculateFileChecksum(DecompressedImage, File.FileOffset);

            // Calculate Volume-checksum
            CalculateVolumeChecksum(DecompressedImage, DecompressedVolumeHeaderOffset);
        }

        private void CalculateFileChecksum(byte[] Image, UInt32 Offset)
        {
            UInt16 FileHeaderSize = 24;
            UInt32 FileSize = ((uint)Image[Offset + 22] << 16) + ((uint)Image[Offset + 21] << 8) + Image[Offset + 20];

            Image[Offset + 16] = 0x00; // Clear checksum
            Image[Offset + 17] = 0x00;
            byte NewChecksum = CalculateChecksum8(Image, Offset, (UInt32)FileHeaderSize - 1);
            Image[Offset + 16] = NewChecksum; // File-Header checksum

            byte FileAttribs = Image[Offset + 19];
            if ((FileAttribs & 0x40) > 0)
            {
                // Calculate file checksum
                byte CalculatedFileChecksum = CalculateChecksum8(Image, Offset + FileHeaderSize, FileSize - FileHeaderSize);
                Image[Offset + 17] = CalculatedFileChecksum;
            }
            else
            {
                // Fixed file checksum
                Image[Offset + 17] = 0xAA;
            }
        }

        private void CalculateVolumeChecksum(byte[] Image, UInt32 Offset)
        {
            UInt16 VolumeHeaderSize = (ushort)((Image[Offset + 49] << 8) + Image[Offset + 48]);
            Image[Offset + 50] = 0x00; // Clear checksum
            Image[Offset + 51] = 0x00;
            UInt16 NewChecksum = CalculateChecksum16(Image, Offset, VolumeHeaderSize);
            Image[Offset + 50] = (byte)(NewChecksum & 0xFF);
            Image[Offset + 51] = (byte)((NewChecksum >> 8) & 0xFF);
        }

    }
}
