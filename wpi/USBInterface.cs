using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    /// <summary>
    /// Represents a single USB interface from a USB device
    /// </summary>
    public class USBInterface
    {
        /// <summary>
        /// Collection of pipes associated with this interface
        /// </summary>
        public USBPipeCollection Pipes;

        /// <summary>
        /// Interface number from the interface descriptor
        /// </summary>
        public int Number;

        /// <summary>
        /// USB device associated with this interface
        /// </summary>
        public USBDevice Device;

        /// <summary>
        /// First IN direction pipe on this interface
        /// </summary>
        public USBPipe InPipe;

        /// <summary>
        /// First OUT direction pipe on this interface
        /// </summary>
        public USBPipe OutPipe;

        /// <summary>
        /// Interface class code. If the interface class does
        /// not match any of the USBBaseClass enumeration values
        /// the value will be USBBaseClass.Unknown
        /// </summary>
        public USBBaseClass BaseClass;

        /// <summary>
        /// Interface class code as defined in the interface descriptor
        /// This property can be used if the class type is not defined
        /// int the USBBaseClass enumeraiton
        /// </summary>
        public byte ClassValue;

        /// <summary>
        /// Interface subclass code
        /// </summary>
        public byte SubClass;

        /// <summary>
        /// Interface protocol code
        /// </summary>
        public byte Protocol;

        /// Zero based interface index in WinUSB.
        /// Note that this is not necessarily the same as the interface *number*
        /// from the interface descriptor. There might be interfaces within the
        /// USB device that do not use WinUSB, these are not counted for index.
        internal int InterfaceIndex;

        internal USBInterface(USBDevice device, int interfaceIndex, USB_INTERFACE_DESCRIPTOR rawDesc, USBPipeCollection pipes)
        {
            // Set raw class identifiers
            ClassValue = rawDesc.bInterfaceClass;
            SubClass = rawDesc.bInterfaceSubClass;
            Protocol = rawDesc.bInterfaceProtocol;

            Number = rawDesc.bInterfaceNumber;
            InterfaceIndex = interfaceIndex;

            // If interface class is of a known type (USBBaseClass enum), use this
            // for the InterfaceClass property.
            BaseClass = USBBaseClass.Unknown;
            if (Enum.IsDefined(typeof(USBBaseClass), (int)rawDesc.bInterfaceClass))
            {
                BaseClass = (USBBaseClass)(int)rawDesc.bInterfaceClass;
            }


            Device = device;
            Pipes = pipes;

            // Handle pipes
            foreach (USBPipe pipe in pipes)
            {
                // Attach pipe to this interface
                pipe.AttachInterface(this);

                // If first in or out pipe, set InPipe and OutPipe
                if (pipe.IsIn && InPipe == null)
                    InPipe = pipe;
                if (pipe.IsOut && OutPipe == null)
                    OutPipe = pipe;

            }

        }

    }
}
