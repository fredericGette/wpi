using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    public class Partition
    {
        public ulong firstSector;
        public ulong lastSector;

        public string name;            
        public Guid partitionTypeGuid; 
        public Guid partitionGuid;     
   
        public ulong attributes;
    }
}
