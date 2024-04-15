using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using TheAirBlow.Thor.Library.Platform;
using Serilog;
namespace TheAirBlow.Thor.Library.Communication; 

public static class USB {
    public const int Vendor = 0x04E8;
    
    private static Dictionary<PlatformID, IHandler> _handlers = new() {
        { PlatformID.Unix, new Linux() }
    };

    public static bool TryGetHandler(out IHandler handler) {
        if (IsCalledFromWsl())
        {
            handler = new WSL();
            Log.Debug("WSL");
            return true;
        }
        Log.Debug("linux");
        var platform = Environment.OSVersion.Platform;
        return _handlers.TryGetValue(platform, out handler!);
    }

    public static string GetSupported() {
        var list = _handlers.Keys.Select(i => i.ToString()).ToList();
        return string.Join(", ", list);
    }

    public static void GetSamsungDeviceBusId(out string busId, out bool isShared)
    {
        busId = null;
        isShared = false;

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c usbipd list",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = processStartInfo };
        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Console.WriteLine(result);
        var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.IndexOf("samsung", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Extracting the busid using a regex assuming busid format is "number-number" at the start of the line
                var match = Regex.Match(line, @"^\d+-\d+");
                if (match.Success)
                {
                    busId = match.Value;
                    isShared = line.ToLowerInvariant().Contains("not shared");
                    return;
                }
            }
        }
    }

    //TODO: fix command to use other arguments from the porocessstartinfo class
    public static void BindDevice(string busid)
    {
        Console.WriteLine($"Attempting to bind Samsung device with busid: {busid}");

        // Adjust this command to bind the device
        var psiBind = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"Start-Process cmd.exe -ArgumentList \"/k\", \"usbipd\", \"bind\", \"-b\", \"{busid}\" -Verb RunAs",
        };
        try
        {
            var p = Process.Start(psiBind);
            p.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to bind device: {ex.Message}");
        }
    }

    public static void AttachDevice(string  busid)
    {
        Console.WriteLine($"Attempting to attach to Samsung device with busid: {busid}");

        // Adjust this command to bind the device
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c usbipd attach --wsl --busid {busid}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = new Process { StartInfo = processStartInfo };
        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }
    private static bool IsCalledFromWsl()
    {
        Log.Debug("here");
        Log.Debug(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));
        var isWsl = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null;
        return isWsl;
    }


}