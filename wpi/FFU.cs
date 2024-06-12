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


    }
}
