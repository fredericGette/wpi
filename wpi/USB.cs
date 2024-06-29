using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace wpi
{

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

    public class USB
    {
        private SafeFileHandle _deviceHandle;
        private IntPtr _winUsbHandle = IntPtr.Zero;
        private WINUSB_PIPE_INFORMATION Input;
        private WINUSB_PIPE_INFORMATION Output;

        // from setupapi.h
        private const Int32 DIGCF_PRESENT = 2;
        private const Int32 DIGCF_DEVICEINTERFACE = 0X10;
        private struct SP_DEVINFO_DATA
        {
            internal Int32 cbSize;
            internal System.Guid ClassGuid;
            internal Int32 DevInst;
            internal IntPtr Reserved;
        }
        private struct SP_DEVICE_INTERFACE_DATA
        {
            internal Int32 cbSize;
            internal System.Guid InterfaceClassGuid;
            internal Int32 Flags;
            internal IntPtr Reserved;
        }
        private const int ERROR_NO_MORE_ITEMS = 259;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        private static readonly IntPtr FILE_IO_INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref System.Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, Int32 Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern Int32 SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, Int32 DeviceInterfaceDetailDataSize, ref Int32 RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, Int32 DeviceInterfaceDetailDataSize, ref Int32 RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref System.Guid InterfaceClassGuid, Int32 MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(String lpFileName, UInt32 dwDesiredAccess, Int32 dwShareMode, IntPtr lpSecurityAttributes, Int32 dwCreationDisposition, Int32 dwFlagsAndAttributes, Int32 hTemplateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, ref IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr InterfaceHandle, Byte AlternateInterfaceNumber, out USB_INTERFACE_DESCRIPTOR UsbAltInterfaceDescriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr InterfaceHandle, Byte AlternateInterfaceNumber, Byte PipeIndex, out WINUSB_PIPE_INFORMATION PipeInformation);

        [DllImport("winusb.dll", SetLastError = true)]
        private static unsafe extern bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, byte* pBuffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static unsafe extern bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, byte* pBuffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

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

        public USB(string devicePath)
        {
            try
            {
                OpenDevice(devicePath);
                InitializeInterface();
            }
            catch (Exception e)
            {
                throw new System.Exception("Failed to open device.", e);
            }
        }

        private void OpenDevice(string devicePath)
        {
            try
            {
                _deviceHandle = CreateFile(devicePath,
                        (FILE_IO_GENERIC_WRITE | FILE_IO_GENERIC_READ),
                        FILE_IO_FILE_SHARE_READ | FILE_IO_FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        FILE_IO_OPEN_EXISTING,
                        FILE_IO_FILE_ATTRIBUTE_NORMAL | FILE_IO_FILE_FLAG_OVERLAPPED,
                        0);
                if (_deviceHandle.IsInvalid)
                    throw new System.Exception("Failed to open WinUSB device handle.");

                bool success = WinUsb_Initialize(_deviceHandle, ref _winUsbHandle);
                if (!success)
                    throw new System.Exception("Failed to initialize WinUSB handle. Device might not be connected.");

            }
            catch (Exception)
            {
                if (_deviceHandle != null)
                {
                    _deviceHandle.Dispose();
                    _deviceHandle = null;
                }
                FreeWinUSB();
                throw;
            }
        }

        private void InitializeInterface()
        {
            USB_INTERFACE_DESCRIPTOR descriptor;
            bool success = WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, out descriptor);
            if (!success)
                throw new System.Exception("Failed to get WinUSB device interface descriptor.");

            for (byte pipeIdx = 0; pipeIdx < descriptor.bNumEndpoints; pipeIdx++)
            {
                WINUSB_PIPE_INFORMATION pipeInfo;
                success = WinUsb_QueryPipe(_winUsbHandle, 0, pipeIdx, out pipeInfo);
                if (!success)
                    throw new System.Exception("Failed to get WinUSB device pipe information.");

                if (Convert.ToBoolean((pipeInfo.PipeId & 0x80)))
                {
                    Input = pipeInfo;
                }
                if (!Convert.ToBoolean((pipeInfo.PipeId & 0x80)))
                {
                    Output = pipeInfo;
                }
            }
        }

        private void printRawConsole(byte[] values, int length, bool write)
        {
            if (write)
            {
                Console.WriteLine("< {0} bytes", length);
            }
            else
            {
                Console.WriteLine("> {0} bytes", length);
            }

            string characters = "";
            int normalizedLength = ((length / 19) + 1) * 19; // display 19 values by line
            for (int i = 0; i < normalizedLength; i++)
            {
                if (i < length)
                {
                    Console.Write("{0:X2} ", values[i]);
                    if (values[i] > 31 && values[i] < 127)
                    {
                        characters += (char)values[i] + "";
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

        public void ReadPipe(byte[] buffer, int bytesToRead, out uint bytesRead)
        {
            bool success;
            unsafe
            {
                fixed (byte* pBuffer = buffer)
                {
                    success = WinUsb_ReadPipe(_winUsbHandle, Input.PipeId, pBuffer, (uint)bytesToRead,
                                    out bytesRead, IntPtr.Zero);
                }
            }
            if (!success)
                throw new System.Exception("Failed to read pipe on WinUSB device.");

            if (Program.verbose)
            {
                printRawConsole(buffer, (int)bytesRead, false);
            }
            else
            {
                Console.Write(".");
            }
        }

        public void WritePipe(byte[] buffer, int length)
        {
            uint bytesWritten;
            bool success;

            if (Program.verbose)
            {
                printRawConsole(buffer, length, true);
            }
            else
            {
                Console.Write(".");
            }

            unsafe
            {
                fixed (byte* pBuffer = buffer)
                {
                    success = WinUsb_WritePipe(_winUsbHandle, Output.PipeId, pBuffer, (uint)length,
                            out bytesWritten, IntPtr.Zero);
                }
            }
            if (!success || (bytesWritten != length))
                throw new System.Exception("Failed to write pipe on WinUSB device.");

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
            if (_winUsbHandle != IntPtr.Zero)
                WinUsb_Free(_winUsbHandle);
            _winUsbHandle = IntPtr.Zero;
        }

        public static List<string> FindDevicePathsFromGuid(Guid guid)
        {
            IntPtr deviceInfoSet = IntPtr.Zero;
            List<string> devicePaths = new List<string>();
            try
            {
                deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                    DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (deviceInfoSet == FILE_IO_INVALID_HANDLE_VALUE)
                    throw new System.Exception("Failed to enumerate devices.");
                int memberIndex = 0;
                while (true)
                {
                    // Begin with 0 and increment through the device information set until
                    // no more devices are available.					
                    SP_DEVICE_INTERFACE_DATA deviceInterfaceData = new SP_DEVICE_INTERFACE_DATA();

                    // The cbSize element of the deviceInterfaceData structure must be set to
                    // the structure's size in bytes. 
                    // The size is 28 bytes for 32-bit code and 32 bytes for 64-bit code.
                    deviceInterfaceData.cbSize = Marshal.SizeOf(deviceInterfaceData);

                    bool success = SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, memberIndex, ref deviceInterfaceData);
                    // Find out if a device information set was retrieved.
                    if (!success)
                    {
                        int lastError = Marshal.GetLastWin32Error();
                        if (lastError == ERROR_NO_MORE_ITEMS)
                            break;

                        throw new System.Exception("Failed to get device interface.");
                    }
                    // A device is present.

                    int bufferSize = 0;

                    success = SetupDiGetDeviceInterfaceDetail
                        (deviceInfoSet,
                        ref deviceInterfaceData,
                        IntPtr.Zero,
                        0,
                        ref bufferSize,
                        IntPtr.Zero);
                    if (!success)
                    {
                        if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                            throw new System.Exception("Failed to get interface details buffer size.");
                    }

                    IntPtr detailDataBuffer = IntPtr.Zero;
                    try
                    {

                        // Allocate memory for the SP_DEVICE_INTERFACE_DETAIL_DATA structure using the returned buffer size.
                        detailDataBuffer = Marshal.AllocHGlobal(bufferSize);

                        // Store cbSize in the first bytes of the array. The number of bytes varies with 32- and 64-bit systems.

                        Marshal.WriteInt32(detailDataBuffer, (IntPtr.Size == 4) ? (4 + Marshal.SystemDefaultCharSize) : 8);

                        // Call SetupDiGetDeviceInterfaceDetail again.
                        // This time, pass a pointer to DetailDataBuffer
                        // and the returned required buffer size.

                        // build a DevInfo Data structure
                        SP_DEVINFO_DATA da = new SP_DEVINFO_DATA();
                        da.cbSize = Marshal.SizeOf(da);


                        success = SetupDiGetDeviceInterfaceDetail
                            (deviceInfoSet,
                            ref deviceInterfaceData,
                            detailDataBuffer,
                            bufferSize,
                            ref bufferSize,
                            ref da);
                        if (!success)
                            throw new System.Exception("Failed to get device interface details.");


                        // Skip over cbsize (4 bytes) to get the address of the devicePathName.
                        IntPtr pDevicePathName = new IntPtr(detailDataBuffer.ToInt64() + 4);
                        string pathName = Marshal.PtrToStringUni(pDevicePathName);
                        devicePaths.Add(pathName);
                    }
                    finally
                    {
                        if (detailDataBuffer != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(detailDataBuffer);
                            detailDataBuffer = IntPtr.Zero;
                        }
                    }
                    memberIndex++;
                }
            }
            finally
            {
                if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != FILE_IO_INVALID_HANDLE_VALUE)
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }
            }
            return devicePaths;
        }
    }
}
