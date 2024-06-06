using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace wpi
{
    /// <summary>
    /// USB device details
    /// </summary>
    public class USBDeviceDescriptor
    {
        /// <summary>
        /// Windows path name for the USB device
        /// </summary>
        public string PathName { get; private set; }

        /// <summary>
        /// USB vendor ID (VID) of the device
        /// </summary>
        public int VID { get; private set; }

        /// <summary>
        /// USB product ID (PID) of the device
        /// </summary>
        public int PID { get; private set; }

        /// <summary>
        /// Manufacturer name, or null if not available
        /// </summary>
        public string Manufacturer { get; private set; }

        /// <summary>
        /// Product name, or null if not available
        /// </summary>
        public string Product { get; private set; }

        /// <summary>
        /// Device serial number, or null if not available
        /// </summary>
        public string SerialNumber { get; private set; }


        /// <summary>
        /// Friendly device name, or path name when no 
        /// further device information is available
        /// </summary>
        public string FullName
        {
            get
            {
                if (Manufacturer != null && Product != null)
                    return Product + " - " + Manufacturer;
                else if (Product != null)
                    return Product;
                else if (SerialNumber != null)
                    return SerialNumber;
                else
                    return PathName;
            }
        }

        /// <summary>
        /// Device class code as defined in the interface descriptor
        /// This property can be used if the class type is not defined
        /// int the USBBaseClass enumeraiton
        /// </summary>
        public byte ClassValue
        {
            get;
            private set;
        }

        /// <summary>
        /// Device subclass code
        /// </summary>
        public byte SubClass
        {
            get;
            private set;
        }

        /// <summary>
        /// Device protocol code
        /// </summary>
        public byte Protocol
        {
            get;
            private set;
        }


        internal USBDeviceDescriptor(string path, USB_DEVICE_DESCRIPTOR deviceDesc, string manufacturer, string product, string serialNumber)
        {
            PathName = path;
            VID = deviceDesc.idVendor;
            PID = deviceDesc.idProduct;
            Manufacturer = manufacturer;
            Product = product;
            SerialNumber = serialNumber;


            ClassValue = deviceDesc.bDeviceClass;
            SubClass = deviceDesc.bDeviceSubClass;
            Protocol = deviceDesc.bDeviceProtocol;

        }
    }
}
