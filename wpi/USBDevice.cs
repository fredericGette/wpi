using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace wpi
{
    [StructLayout(LayoutKind.Sequential)]
    struct USB_DEVICE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    };

    [StructLayout(LayoutKind.Sequential)]
    struct USB_CONFIGURATION_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort wTotalLength;
        public byte bNumInterfaces;
        public byte bConfigurationValue;
        public byte iConfiguration;
        public byte bmAttributes;
        public byte MaxPower;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    };

    enum USBD_PIPE_TYPE : int
    {
        UsbdPipeTypeControl,
        UsbdPipeTypeIsochronous,
        UsbdPipeTypeBulk,
        UsbdPipeTypeInterrupt,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINUSB_PIPE_INFORMATION
    {
        public USBD_PIPE_TYPE PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    public enum POLICY_TYPE : int
    {
        SHORT_PACKET_TERMINATE = 1,
        AUTO_CLEAR_STALL,
        PIPE_TRANSFER_TIMEOUT,
        IGNORE_SHORT_PACKETS,
        ALLOW_PARTIAL_READS,
        AUTO_FLUSH,
        RAW_IO,
    }

    public class USBDevice
    {
        private USBDeviceDescriptor Descriptor;
        private SafeFileHandle _deviceHandle;
        private IntPtr _winUsbHandle = IntPtr.Zero;
        private IntPtr[] _addInterfaces = null;
        public USBPipe InputPipe;
        public USBPipe OutputPipe;
        private USBPipeCollection Pipes;
        private USBInterfaceCollection Interfaces;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(String lpFileName, UInt32 dwDesiredAccess, Int32 dwShareMode, IntPtr lpSecurityAttributes, Int32 dwCreationDisposition, Int32 dwFlagsAndAttributes, Int32 hTemplateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, ref IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetAssociatedInterface(IntPtr InterfaceHandle, byte AssociatedInterfaceIndex,
            out IntPtr AssociatedInterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetDescriptor(IntPtr InterfaceHandle, byte DescriptorType,
                        byte Index, UInt16 LanguageID, byte[] Buffer, UInt32 BufferLength, out UInt32 LengthTransfered);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetDescriptor(IntPtr InterfaceHandle, byte DescriptorType,
                        byte Index, UInt16 LanguageID, out USB_DEVICE_DESCRIPTOR deviceDesc, UInt32 BufferLength, out UInt32 LengthTransfered);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetDescriptor(IntPtr InterfaceHandle, byte DescriptorType,
                        byte Index, UInt16 LanguageID, out USB_CONFIGURATION_DESCRIPTOR deviceDesc, UInt32 BufferLength, out UInt32 LengthTransfered);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr InterfaceHandle, Byte AlternateInterfaceNumber, out USB_INTERFACE_DESCRIPTOR UsbAltInterfaceDescriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr InterfaceHandle, Byte AlternateInterfaceNumber, Byte PipeIndex, out WINUSB_PIPE_INFORMATION PipeInformation);

        [DllImport("winusb.dll", SetLastError = true)]
        private static unsafe extern bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, byte* pBuffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static unsafe extern bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, byte* pBuffer, uint BufferLength, out uint LengthTransferred, NativeOverlapped* pOverlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static unsafe extern bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, byte* pBuffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static unsafe extern bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, byte* pBuffer, uint BufferLength, out uint LengthTransferred, NativeOverlapped* pOverlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_AbortPipe(IntPtr InterfaceHandle, byte PipeID);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_FlushPipe(IntPtr InterfaceHandle, byte PipeID);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetPipePolicy(IntPtr InterfaceHandle, Byte PipeID, UInt32 PolicyType, ref UInt32 ValueLength, out byte Value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetPipePolicy(IntPtr InterfaceHandle, Byte PipeID, UInt32 PolicyType, ref UInt32 ValueLength, out UInt32 Value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, Byte PipeID, UInt32 PolicyType, UInt32 ValueLength, ref byte Value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, Byte PipeID, UInt32 PolicyType, UInt32 ValueLength, ref UInt32 Value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr InterfaceHandle);

        private const UInt32 FILE_IO_GENERIC_READ = 0X80000000;
        private const UInt32 FILE_IO_GENERIC_WRITE = 0X40000000;
        private const Int32 FILE_IO_FILE_SHARE_READ = 1;
        private const Int32 FILE_IO_FILE_SHARE_WRITE = 2;
        private const Int32 FILE_IO_OPEN_EXISTING = 3;
        private const Int32 FILE_IO_FILE_ATTRIBUTE_NORMAL = 0X80;
        private const Int32 FILE_IO_FILE_FLAG_OVERLAPPED = 0X40000000;
        private const Int32 FILE_IO_ERROR_IO_PENDING = 997;

        private const int ERROR_NO_MORE_ITEMS = 259;

        private const int USB_DEVICE_DESCRIPTOR_TYPE = 0x01;
        private const int USB_STRING_DESCRIPTOR_TYPE = 0x03;

        public USBDevice(string devicePathName)
        {
            try
            {
                Descriptor = GetDeviceDescriptor(devicePathName);
            }
            catch  (Exception e)
            {
                Console.WriteLine("Exception GetDeviceDescriptor : {0}", e);
            }

            try
            {
                // Device already opened by GetDeviceDescriptor(devicePathName)
                // OpenDevice(devicePathName);
                InitializeInterfaces();
            }
            catch (Exception e)
            {
                //Dispose();
                throw new System.Exception("Failed to open device.", e);
            }
        }

        private USBDeviceDescriptor GetDeviceDescriptor(string devicePath)
        {
            try
            {
                USBDeviceDescriptor descriptor;

                OpenDevice(devicePath);
                USB_DEVICE_DESCRIPTOR deviceDesc = GetDeviceDescriptor();
                string manufacturer = null, product = null, serialNumber = null;

                descriptor = new USBDeviceDescriptor(devicePath, deviceDesc, manufacturer, product, serialNumber);

                return descriptor;

            }
            catch (Exception e)
            {
                throw new System.Exception("Failed to retrieve device descriptor.", e);
            }
        }

        private USB_DEVICE_DESCRIPTOR GetDeviceDescriptor()
        {
            USB_DEVICE_DESCRIPTOR deviceDesc;
            uint transfered;
            uint size = (uint)Marshal.SizeOf(typeof(USB_DEVICE_DESCRIPTOR));
            bool success = WinUsb_GetDescriptor(_winUsbHandle, USB_DEVICE_DESCRIPTOR_TYPE,
                        0, 0, out deviceDesc, size, out transfered);
            if (!success)
                throw new System.Exception("Failed to get USB device descriptor.");

            if (transfered != size)
                throw new System.Exception("Incomplete USB device descriptor.");

            return deviceDesc;
        }

        private string GetStringDescriptor(byte index)
        {
            byte[] buffer = new byte[256];
            uint transfered;
            bool success = WinUsb_GetDescriptor(_winUsbHandle, USB_STRING_DESCRIPTOR_TYPE,
                        index, 0, buffer, (uint)buffer.Length, out transfered);
            if (!success)
                throw new System.Exception("Failed to get USB string descriptor (" + index + "): 0x" + Marshal.GetLastWin32Error().ToString("X8"));

            int length = buffer[0] - 2;
            if (length <= 0)
                return null;
            char[] chars = System.Text.Encoding.Unicode.GetChars(buffer, 2, length);
            return new string(chars);
        }

        public void OpenDevice(string devicePathName)
        {
            try
            {
                _deviceHandle = CreateFile(devicePathName,
                        (FILE_IO_GENERIC_WRITE | FILE_IO_GENERIC_READ),
                        FILE_IO_FILE_SHARE_READ | FILE_IO_FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        FILE_IO_OPEN_EXISTING,
                        FILE_IO_FILE_ATTRIBUTE_NORMAL | FILE_IO_FILE_FLAG_OVERLAPPED,
                        0);
                if (_deviceHandle.IsInvalid)
                    throw new System.Exception("Failed to open WinUSB device handle.");
                InitializeDevice();

            }
            catch (Exception)
            {
                if (_deviceHandle != null)
                {
                    _deviceHandle.Dispose();
                    _deviceHandle = null;
                }
                //FreeWinUSB();
                throw;
            }
        }

        private void InitializeDevice()
        {
            bool success;

            success = WinUsb_Initialize(_deviceHandle, ref _winUsbHandle);

            if (!success)
                throw new System.Exception("Failed to initialize WinUSB handle. Device might not be connected.");

            List<IntPtr> interfaces = new List<IntPtr>();
            byte numAddInterfaces = 0;
            byte idx = 0;

            try
            {
                while (true)
                {
                    IntPtr ifaceHandle = IntPtr.Zero;
                    success = WinUsb_GetAssociatedInterface(_winUsbHandle, idx, out ifaceHandle);
                    if (!success)
                    {
                        if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS)
                            break;

                        throw new System.Exception("Failed to enumerate interfaces for WinUSB device.");
                    }
                    interfaces.Add(ifaceHandle);
                    idx++;
                    numAddInterfaces++;
                }
            }
            finally
            {
                // Save interface handles (will be cleaned by Dispose)
                // also in case of exception (which is why it is in finally block), 
                // because some handles might have already been opened and need
                // to be disposed.
                _addInterfaces = interfaces.ToArray();
            }

            // Bind handle (needed for overlapped I/O thread pool)
            ThreadPool.BindHandle(_deviceHandle);
            // TODO: bind interface handles as well? doesn't seem to be necessary
        }

        public int InterfaceCount
        {
            get
            {
                return 1 + (_addInterfaces == null ? 0 : _addInterfaces.Length);
            }
        }

        private void InitializeInterfaces()
        {
            int numInterfaces = InterfaceCount;

            List<USBPipe> allPipes = new List<USBPipe>();
            InputPipe = null;
            OutputPipe = null;

            USBInterface[] interfaces = new USBInterface[numInterfaces];
            // UsbEndpoint
            for (int i = 0; i < numInterfaces; i++)
            {
                USB_INTERFACE_DESCRIPTOR descriptor;
                WINUSB_PIPE_INFORMATION[] pipesInfo;
                GetInterfaceInfo(i, out descriptor, out pipesInfo);
                USBPipe[] interfacePipes = new USBPipe[pipesInfo.Length];
                for (int k = 0; k < pipesInfo.Length; k++)
                {
                    USBPipe pipe = new USBPipe(this, pipesInfo[k]);
                    interfacePipes[k] = pipe;
                    allPipes.Add(pipe);
                    if (Convert.ToBoolean((pipesInfo[k].PipeId & 0x80)) && (InputPipe == null)) InputPipe = pipe;
                    if (!Convert.ToBoolean((pipesInfo[k].PipeId & 0x80)) && (OutputPipe == null)) OutputPipe = pipe;
                }
                // TODO:
                //if (descriptor.iInterface != 0)
                //    _wuDevice.GetStringDescriptor(descriptor.iInterface);
                USBPipeCollection pipeCollection = new USBPipeCollection(interfacePipes);
                interfaces[i] = new USBInterface(this, i, descriptor, pipeCollection);
            }
            Pipes = new USBPipeCollection(allPipes.ToArray());
            Interfaces = new USBInterfaceCollection(interfaces);
        }

        private void GetInterfaceInfo(int interfaceIndex, out USB_INTERFACE_DESCRIPTOR descriptor, out WINUSB_PIPE_INFORMATION[] pipes)
        {
            var pipeList = new List<WINUSB_PIPE_INFORMATION>();
            bool success = WinUsb_QueryInterfaceSettings(InterfaceHandle(interfaceIndex), 0, out descriptor);
            if (!success)
                throw new System.Exception("Failed to get WinUSB device interface descriptor.");

            IntPtr interfaceHandle = InterfaceHandle(interfaceIndex);
            for (byte pipeIdx = 0; pipeIdx < descriptor.bNumEndpoints; pipeIdx++)
            {
                WINUSB_PIPE_INFORMATION pipeInfo;
                success = WinUsb_QueryPipe(interfaceHandle, 0, pipeIdx, out pipeInfo);

                pipeList.Add(pipeInfo);
                if (!success)
                    throw new System.Exception("Failed to get WinUSB device pipe information.");
            }
            pipes = pipeList.ToArray();

        }

        private IntPtr InterfaceHandle(int index)
        {
            if (index == 0)
                return _winUsbHandle;
            return _addInterfaces[index - 1];
        }

        public void ReadPipe(int ifaceIndex, byte pipeID, byte[] buffer, int offset, int bytesToRead, out uint bytesRead)
        {
            bool success;
            unsafe
            {
                fixed (byte* pBuffer = buffer)
                {
                    success = WinUsb_ReadPipe(InterfaceHandle(ifaceIndex), pipeID, pBuffer + offset, (uint)bytesToRead,
                                    out bytesRead, IntPtr.Zero);
                }
            }
            if (!success)
                throw new System.Exception("Failed to read pipe on WinUSB device.");
        }

        public void ReadPipeOverlapped(int ifaceIndex, byte pipeID, byte[] buffer, int offset, int bytesToRead, USBAsyncResult result)
        {
            Overlapped overlapped = new Overlapped();

            overlapped.AsyncResult = result;

            unsafe
            {
                NativeOverlapped* pOverlapped = null;
                uint bytesRead;

                pOverlapped = overlapped.Pack(PipeIOCallback, buffer);
                bool success;
                // Buffer is pinned already by overlapped.Pack
                fixed (byte* pBuffer = buffer)
                {
                    success = WinUsb_ReadPipe(InterfaceHandle(ifaceIndex), pipeID, pBuffer + offset, (uint)bytesToRead,
                                out bytesRead, pOverlapped);
                }
                HandleOverlappedAPI(success, "Failed to asynchronously read pipe on WinUSB device.", pOverlapped, result, (int)bytesRead);
            }
        }

        private unsafe void PipeIOCallback(uint errorCode, uint numBytes, NativeOverlapped* pOverlapped)
        {
            try
            {
                Exception error = null;
                if (errorCode != 0)
                {
                    error = new System.Exception("Asynchronous operation on WinUSB device failed." + errorCode);
                }
                Overlapped overlapped = Overlapped.Unpack(pOverlapped);
                USBAsyncResult result = (USBAsyncResult)overlapped.AsyncResult;
                Overlapped.Free(pOverlapped);
                pOverlapped = null;

                result.OnCompletion(false, error, (int)numBytes, true);
            }
            finally
            {
                if (pOverlapped != null)
                {
                    Overlapped.Unpack(pOverlapped);
                    Overlapped.Free(pOverlapped);
                }
            }
        }

        private unsafe void HandleOverlappedAPI(bool success, string errorMessage, NativeOverlapped* pOverlapped, USBAsyncResult result, int bytesTransfered)
        {
            if (!success)
            {
                if (Marshal.GetLastWin32Error() != FILE_IO_ERROR_IO_PENDING)
                {
                    Overlapped.Unpack(pOverlapped);
                    Overlapped.Free(pOverlapped);
                    throw new System.Exception(errorMessage);
                }
            }
            else
            {
                // Immediate success!
                Overlapped.Unpack(pOverlapped);
                Overlapped.Free(pOverlapped);

                result.OnCompletion(true, null, bytesTransfered, false);
                // is the callback still called in this case?? todo
            }

        }

        public void WritePipe(int ifaceIndex, byte pipeID, byte[] buffer, int offset, int length)
        {
            uint bytesWritten;
            bool success;
            unsafe
            {
                fixed (byte* pBuffer = buffer)
                {
                    success = WinUsb_WritePipe(InterfaceHandle(ifaceIndex), pipeID, pBuffer + offset, (uint)length,
                            out bytesWritten, IntPtr.Zero);
                }
            }
            if (!success || (bytesWritten != length))
                throw new System.Exception("Failed to write pipe on WinUSB device.");

        }

        public void WriteOverlapped(int ifaceIndex, byte pipeID, byte[] buffer, int offset, int bytesToWrite, USBAsyncResult result)
        {
            Overlapped overlapped = new Overlapped();
            overlapped.AsyncResult = result;

            unsafe
            {
                NativeOverlapped* pOverlapped = null;

                uint bytesWritten;
                pOverlapped = overlapped.Pack(PipeIOCallback, buffer);

                bool success;
                // Buffer is pinned already by overlapped.Pack
                fixed (byte* pBuffer = buffer)
                {
                    success = WinUsb_WritePipe(InterfaceHandle(ifaceIndex), pipeID, pBuffer + offset, (uint)bytesToWrite,
                            out bytesWritten, pOverlapped);
                }
                HandleOverlappedAPI(success, "Failed to asynchronously write pipe on WinUSB device.", pOverlapped, result, (int)bytesWritten);

            }
        }

        public void AbortPipe(int ifaceIndex, byte pipeID)
        {
            bool success = WinUsb_AbortPipe(InterfaceHandle(ifaceIndex), pipeID);
            if (!success)
                throw new System.Exception("Failed to abort pipe on WinUSB device.");

        }

        public void FlushPipe(int ifaceIndex, byte pipeID)
        {
            bool success = WinUsb_FlushPipe(InterfaceHandle(ifaceIndex), pipeID);
            if (!success)
                throw new System.Exception("Failed to flush pipe on WinUSB device.");
        }

        public bool GetPipePolicyBool(int ifaceIndex, byte pipeID, POLICY_TYPE policyType)
        {
            byte result;
            uint length = 1;

            bool success = WinUsb_GetPipePolicy(InterfaceHandle(ifaceIndex), pipeID, (uint)policyType, ref length, out result);
            if (!success || length != 1)
                throw new System.Exception("Failed to get WinUSB pipe policy.");
            return result != 0;
        }

        public uint GetPipePolicyUInt(int ifaceIndex, byte pipeID, POLICY_TYPE policyType)
        {

            uint result;
            uint length = 4;
            bool success = WinUsb_GetPipePolicy(InterfaceHandle(ifaceIndex), pipeID, (uint)policyType, ref length, out result);

            if (!success || length != 4)
                throw new System.Exception("Failed to get WinUSB pipe policy.");
            return result;
        }

        public void SetPipePolicy(int ifaceIndex, byte pipeID, POLICY_TYPE policyType, bool value)
        {
            byte byteVal = (byte)(value ? 1 : 0);
            bool success = WinUsb_SetPipePolicy(InterfaceHandle(ifaceIndex), pipeID, (uint)policyType, 1, ref byteVal);
            if (!success)
                throw new System.Exception("Failed to set WinUSB pipe policy.");
        }

        public void SetPipePolicy(int ifaceIndex, byte pipeID, POLICY_TYPE policyType, uint value)
        {

            bool success = WinUsb_SetPipePolicy(InterfaceHandle(ifaceIndex), pipeID, (uint)policyType, 4, ref value);

            if (!success)
                throw new System.Exception("Failed to set WinUSB pipe policy.");
        }

        public void Close()
        {
            // Dispose managed resources
            if (_deviceHandle != null && !_deviceHandle.IsInvalid)
                _deviceHandle.Dispose();
            _deviceHandle = null;            

            // Dispose unmanaged resources
            FreeWinUSB();
        }
        private void FreeWinUSB()
        {
            if (_addInterfaces != null)
            {
                for (int i = 0; i < _addInterfaces.Length; i++)
                {
                    WinUsb_Free(_addInterfaces[i]);
                }
                _addInterfaces = null;
            }
            if (_winUsbHandle != IntPtr.Zero)
                WinUsb_Free(_winUsbHandle);
            _winUsbHandle = IntPtr.Zero;
        }
    }
}
