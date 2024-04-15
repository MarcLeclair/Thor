using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using TheAirBlow.Thor.Library.Communication;
using System.Numerics;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Dynamic;
using Serilog;
using static TheAirBlow.Thor.Library.Platform.Linux;
using System.Runtime.InteropServices;
namespace TheAirBlow.Thor.Library.Platform;

public class WSL : Linux, IHandler, IDisposable
{
    private bool _connected = false;
    private uint _interface;
    private uint _alternate;
    private uint? _readEndpoint;
    private uint? _writeEndpoint;
    private int? _deviceFd;
    private bool _detached;
    private bool _writeZlp;

    public unsafe byte[] BulkRead(int amount, out int read, int timeout = 5000)
    {
        if (!_connected)
            throw new InvalidOperationException("Not connected to a device!");

        var buf = new byte[amount];
        fixed (void* p = buf)
        {
            var bufPtr = (nint)p;
            var bulk = new Interop.BulkTransfer
            {
                Endpoint = _readEndpoint!.Value,
                Timeout = (uint)timeout,
                Length = (uint)amount,
                Data = bufPtr
            };

            Log.Debug($"Attempting bulk read: Endpoint: 0x{_readEndpoint!.Value:X2}, Amount: {amount}, Timeout: {timeout}");

            read = Interop.IoCtl(_deviceFd!.Value, Interop.USBDEVFS_BULK, ref bulk);
            if (read < 0)
            {
                int errorCode = Marshal.GetLastWin32Error();
                Log.Debug($"Failed to bulk read: Endpoint: 0x{_readEndpoint!.Value:X2}, Error code: {errorCode}");
                Interop.HandleError("Failed to bulk read");
            }

            var arr = new byte[read];
            Marshal.Copy(bufPtr, arr, 0, read);
            Log.Debug($"Bulk read successful: Read {read} bytes");
            return arr;
        }
    }


    public unsafe void BulkWrite(byte[] buf, int timeout = 5000, bool zlp = false)
    {
        if (!_connected)
            throw new InvalidOperationException("Not connected to a device!");
        fixed (void* p = buf)
        {
            var bufPtr = (nint)p;
            var bulk = new Interop.BulkTransfer
            {
                Endpoint = _writeEndpoint!.Value,
                Length = (uint)buf.Length,
                Timeout = (uint)timeout,
                Data = bufPtr
            };

            if (Interop.IoCtl(_deviceFd!.Value, Interop.USBDEVFS_BULK, ref bulk) < 0)
                Interop.HandleError("Failed to bulk write");
        }

        // Write ZLP, disable if failed
        if (_writeZlp && !zlp)
        {
            try
            {
                SendZLP();
            }
            catch
            {
                _writeZlp = false;
            }
        }
    }
    

    public void Disconnect()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public async Task<List<DeviceInfo>> GetDevices()
    {
        var list = new List<DeviceInfo>();
        string busId;
        bool isShared;
        //USB.GetSamsungDeviceBusId(out string samsungBusId, out bool shared);
        //USB.BindDevice(samsungBusId);
        //USB.AttachDevice(samsungBusId);
        foreach (var bus in Directory.EnumerateDirectories("/dev/bus/usb/"))
            foreach (var device in Directory.EnumerateFiles(bus))
            {
                try
                {
                    using var file = new FileStream(device, FileMode.Open, FileAccess.Read);
                    using var reader = new BinaryReader(file);
                    // USB_DT_DEVICE usb_device_descriptor struct:
                    // https://github.com/torvalds/linux/blob/master/include/uapi/linux/usb/ch9.h#L289
                    file.Seek(1, SeekOrigin.Current);
                    if (reader.ReadByte() != 0x01)
                        continue; // Not USB_DT_DEVICE
                    file.Seek(6, SeekOrigin.Current);
                    var vendor = reader.ReadInt16();
                    if (vendor == USB.Vendor)
                    {
                        var product = reader.ReadInt16();
                        list.Add(new DeviceInfo
                        {
                            DisplayName = await Lookup.GetDisplayName(vendor, product),
                            Identifier = device[13..].Replace("/", ":")
                        });
                    }
                }
                catch { /* Ignored */ }
            }

        return list;
    }


    public string GetNotes()
    {
        throw new NotImplementedException();
    }


    public void Initialize(string? id, byte[]? direct = null)
    {
        Stream? file; var path = "";

        if (id != null)
        {
            id = id.Replace(":", "/");
            path = $"/dev/bus/usb/{id}";
            if (!File.Exists(path))
                throw new InvalidOperationException("Device disconnected?");
            file = new FileStream(path, FileMode.Open, FileAccess.Read);
        }
        else if (direct != null) file = new MemoryStream(direct);
        else throw new InvalidDataException("ID or Direct should not be null");
        int _alternateSetting = -1;
        var found = false;
        using var reader = new BinaryReader(file);
        // USB_DT_DEVICE usb_device_descriptor struct:
        // https://github.com/torvalds/linux/blob/master/include/uapi/linux/usb/ch9.h#L289
        file.Seek(1, SeekOrigin.Current);
        if (reader.ReadByte() != 0x01)
            throw new InvalidDataException("USB_DT_DEVICE assertion fail!");
        file.Seek(6, SeekOrigin.Current);
        if (reader.ReadInt16() != USB.Vendor)
            throw new InvalidDataException("This is not a Samsung device!");
        file.Seek(7, SeekOrigin.Current);
        var configs = reader.ReadByte();
        Log.Debug("Number of configurations: {0}", configs);
        // USB_DT_CONFIG usb_config_descriptor struct:
        // https://github.com/torvalds/linux/blob/master/include/uapi/linux/usb/ch9.h#L349
        file.Seek(1, SeekOrigin.Current);
        if (reader.ReadByte() != 0x02)
            throw new InvalidDataException("USB_DT_CONFIG assertion fail!");
        file.Seek(2, SeekOrigin.Current);
        var numInterfaces = (int)reader.ReadByte();
        file.Seek(4, SeekOrigin.Current);
        Log.Debug("Number of interfaces: {0}", numInterfaces);
        // USB_DT_INTERFACE usb_interface_descriptor struct:
        // https://github.com/torvalds/linux/blob/master/include/uapi/linux/usb/ch9.h#L388
        int totalInterfacesProcessed = 0;
        while (totalInterfacesProcessed < numInterfaces)
        {
            // Assuming you're now at the start of an interface descriptor
            byte bLength = reader.ReadByte(); // Length of the descriptor
            byte bDescriptorType = reader.ReadByte(); // Descriptor Type

            if (bDescriptorType != 0x04) // If not an interface descriptor
            {
                Log.Debug($"Unexpected Descriptor Type: 0x{bDescriptorType:X2}, Expected: 0x04. Skipping...");
                reader.BaseStream.Seek(bLength - 2, SeekOrigin.Current); // Skip this descriptor
                continue; // Skip to the next loop iteration
            }

            // Read the rest of the interface descriptor
            _interface = reader.ReadByte();
            _alternateSetting = reader.ReadByte();
            int numEndpoints = reader.ReadByte();
            byte interfaceClass = reader.ReadByte();
            byte interfaceSubClass = reader.ReadByte();
            byte interfaceProtocol = reader.ReadByte();
            /* byte iInterface = */
            reader.ReadByte(); // Skip interface string index

            Log.Debug($"Interface {_interface}, Alternate Setting {_alternateSetting}, Number of Endpoints: {numEndpoints}, Class: 0x{interfaceClass:X2}, SubClass: 0x{interfaceSubClass:X2}, Protocol: 0x{interfaceProtocol:X2}");

            bool isValidInterface = interfaceClass == 0x0A; // CDC Data Class Code

            for (int i = 0; i < numEndpoints && isValidInterface; i++)
            {
                // Read the endpoint descriptor
                bLength = reader.ReadByte();
                bDescriptorType = reader.ReadByte();

                if (bDescriptorType != 0x05) // If not an endpoint descriptor
                {
                    Log.Debug($"Unexpected Endpoint Descriptor Type: 0x{bDescriptorType:X2}, Expected: 0x05. Skipping...");
                    reader.BaseStream.Seek(bLength - 2, SeekOrigin.Current); // Skip this descriptor
                    isValidInterface = false; // Invalidate this interface as it does not meet expected structure
                    break; // Break out of endpoint processing loop
                }

                byte endpointAddress = reader.ReadByte();
                byte bmAttributes = reader.ReadByte();
                /* ushort wMaxPacketSize = */
                reader.ReadUInt16(); // Maximum packet size
                /* byte bInterval = */
                reader.ReadByte(); // Interval for polling endpoint

                bool isBulkEndpoint = (bmAttributes & 0x03) == 0x02;
                bool isEndpointIn = (endpointAddress & 0x80) == 0x80;

                Log.Debug($"Endpoint Address: 0x{endpointAddress:X2}, Attributes: 0x{bmAttributes:X2}, Bulk: {isBulkEndpoint}, Direction In: {isEndpointIn}");

                if (isBulkEndpoint)
                {
                    if (isEndpointIn)
                    {
                        _readEndpoint = endpointAddress;
                    }
                    else
                    {
                        _writeEndpoint = endpointAddress;
                    }
                }
            }

            // After processing all endpoints for the current interface
            if (isValidInterface && _readEndpoint.HasValue && _writeEndpoint.HasValue)
            {
                found = true; // Found a valid interface with the required endpoints
                Log.Debug("Found a valid CDC Data interface with required bulk endpoints.");
                break; // Exit the loop as we found the desired interface and endpoints
            }

            totalInterfacesProcessed++;
        }

        file.Dispose();
        if (!found) throw new InvalidOperationException("Failed to find valid endpoints!");
        Log.Debug("Interface: 0x{0:X2}, Alternate: 0x{1:X2}, Read Endpoint: 0x{2:X2}, Write Endpoint: 0x{3:X2}",
            _interface, _alternate, _readEndpoint, _writeEndpoint);


        if (direct != null) return;

        if ((_deviceFd = Interop.Open(path, Interop.O_RDWR)) < 0)
            Interop.HandleError("Failed to open the device for RW");

        var driver = new Interop.GetDriver
        {
            Interface = (int)_interface
        };
        Interop.LogIoctlCode("USBDEVFS_GETDRIVER", Interop.USBDEVFS_GETDRIVER);
        Interop.LogIoctlCode("USBDEVFS_CLAIMINTERFACE", Interop.USBDEVFS_CLAIMINTERFACE);
        int result = Interop.IoCtl(_deviceFd.Value, Interop.USBDEVFS_GETDRIVER, ref driver);
        if (result == 0)
        {
            Log.Debug("Kernel driver detected, detaching it!");
            var ioctl = new Interop.UsbIoCtl
            {
                CommandCode = (int)Interop.USBDEVFS_DISCONNECT,
                Interface = (int)_interface,
                Data = nint.Zero
            };

            if (Interop.IoCtl(_deviceFd.Value, Interop.USBDEVFS_IOCTL, ref ioctl) < 0)
                Interop.HandleError("Failed to detach kernel driver");

            _detached = true;
        }
        uint interfaceIndex = (uint)_interface;

        // Claim interface
        if (Interop.IoCtl(_deviceFd.Value, Interop.USBDEVFS_CLAIMINTERFACE, ref interfaceIndex) < 0)
            Interop.HandleError("Failed to claim interface");

        _connected = true;
    }

    private int ParseVendorId(string id)
    {
        // Implement parsing logic based on your 'id' format
        // Example: "0x04E8:0x6860" where the first part is the Vendor ID
        return Convert.ToInt32(id.Split(':')[0], 16);
    }

    private int ParseProductId(string id)
    {
        // Implement parsing logic based on your 'id' format
        // Example: "0x04E8:0x6860" where the second part is the Product ID
        return Convert.ToInt32(id.Split(':')[1], 16);
    }

    public bool IsConnected()
     => _connected;

    public void ReadZLP()
    {
        throw new NotImplementedException();
    }

    public void SendZLP()
    {
        throw new NotImplementedException();
    }

    private void StartAdbTcpip()
    {
       
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe", // Assuming 'adb' is in your system's PATH
            Arguments = "/c adb tcpip 5555; adb devices", // Command to execute
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // It's a good idea to redirect standard error as well to capture any errors
            CreateNoWindow = true
        };

        // Create and start the process
        using (var process = Process.Start(startInfo))
        {
            // Read the output (if any) to ensure the command was executed successfully
            string output = process.StandardOutput.ReadToEnd();
            Console.WriteLine(output);

            // Wait for the process to exit
            process.WaitForExit();
        }
    }

    private void ConnectAdbOverWsl(string deviceIp)
    {

        // Create a new process start info
        var startInfo = new ProcessStartInfo
        {
            FileName = "adb", // Use 'wsl' to execute the command inside WSL
            Arguments = $"connect {deviceIp}:5555", // Pass the command to execute in WSL
            UseShellExecute = false, // Do not use the system shell to start the process
            RedirectStandardOutput = true, // Redirect output so we can read it
            RedirectStandardError = true, // Optionally redirect standard error to capture any errors
            CreateNoWindow = true // Don't create a window for this process
        };

        // Create and start the process
        using (var process = Process.Start(startInfo))
        {
            // Read the output (and error output) to ensure the command was executed successfully
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            Console.WriteLine(output);
            if (!string.IsNullOrEmpty(error))
            {
                Console.WriteLine($"Error: {error}");
            }

            // Wait for the process to exit
            process.WaitForExit();
        }
    }

    private  string GetDeviceIpAddress()
    {
        // Execute the adb shell ifconfig command
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c adb shell ifconfig",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using (var process = Process.Start(processStartInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Use a regular expression to parse the output for wlan0 or eth0 IP address
            var regex = new Regex(@"wlan0.*?inet addr:(\d+\.\d+\.\d+\.\d+)|eth0.*?inet addr:(\d+\.\d+\.\d+\.\d+)", RegexOptions.Singleline);
            var match = regex.Match(output);

            if (match.Success)
            {
                // Try to get the IP address from the regex groups
                for (int i = 1; i <= match.Groups.Count; i++)
                {
                    if (match.Groups[i].Success)
                    {
                        return match.Groups[i].Value;
                    }
                }
            }
        }

        // Return null or an appropriate default/fallback value if the IP address could not be found
        return null;
    }

    private string ConnectThroughUsbipd()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c usbipd list",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using (var process = Process.Start(startInfo))
        {
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string busId = null;

            foreach (var line in lines)
            {
                if (line.Contains("SAMSUNG"))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    busId = parts[0]; // Assuming the BUSID is the first part 
                    break;
                }
            }

            return busId;
        }
    }   


    private void ConnectToSamsungDevice(string busId)
    {
        if (string.IsNullOrEmpty(busId))
        {
            Console.WriteLine("Samsung device not found or not shared.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe ",
            Arguments = $"/c usbipd  bind --busid {busId}",
            Verb = "runas",
            RedirectStandardOutput = true,
        };

        using (var process = Process.Start(startInfo))
        {
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Console.WriteLine(output);
        }


        var bindInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c usbipd attach --wsl --busid  {busId}",
            Verb = "runas",
            RedirectStandardOutput = true,
        };

        using (var bindProcess = Process.Start(bindInfo))
        {
            var output = bindProcess.StandardOutput.ReadToEnd();
            bindProcess.WaitForExit();
            Console.WriteLine(output);
        }
    }
}

