using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace VRCFaceTracking.Bundle;

/// <summary>
/// Single-file distributable for VRCFaceTracking - 518 Edition. The full app is
/// embedded as a zip. On first run (or after an update) it unpacks itself to LocalAppData,
/// then starts the real app. Friends get exactly one file to double-click.
/// </summary>
public static class Program
{
    private const string PayloadName = "AppPayload.zip";
    private const string AppFolderInZip = "VRCFaceTracking-518-Edition";

    public static int Main(string[] args)
    {
        Console.WriteLine("VRCFaceTracking - 518 Edition");

        try
        {
            var asm = Assembly.GetExecutingAssembly();

            // Hash the payload so a newer bundle automatically re-extracts over an old install
            string hash;
            using (var hashStream = asm.GetManifestResourceStream(PayloadName))
            {
                if (hashStream == null)
                {
                    Console.WriteLine("ERROR: this launcher was built without its app payload.");
                    Pause();
                    return 1;
                }
                hash = Convert.ToHexString(SHA256.HashData(hashStream))[..16];
            }

            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRCFaceTracking-518");
            var appDir = Path.Combine(baseDir, "app");
            var markerFile = Path.Combine(baseDir, "installed.txt");
            var exePath = Path.Combine(appDir, AppFolderInZip, "VRCFaceTracking.exe");

            var installedHash = File.Exists(markerFile) ? File.ReadAllText(markerFile).Trim() : null;
            if (installedHash != hash || !File.Exists(exePath))
            {
                Console.WriteLine("Unpacking app (first launch or update - takes a few seconds)...");
                try
                {
                    if (Directory.Exists(appDir))
                    {
                        Directory.Delete(appDir, true);
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("ERROR: could not replace the previous version.");
                    Console.WriteLine("Close VRCFaceTracking if it is running, then start this file again.");
                    Pause();
                    return 1;
                }

                Directory.CreateDirectory(baseDir);
                using (var payload = asm.GetManifestResourceStream(PayloadName)!)
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    archive.ExtractToDirectory(appDir);
                }
                File.WriteAllText(markerFile, hash);
                Console.WriteLine("Unpacked.");
            }

            if (args.Contains("--extract-only"))
            {
                Console.WriteLine($"Extract-only mode: app is at {exePath}");
                return 0;
            }

            Console.WriteLine("Starting VRCFaceTracking...");
            Process.Start(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true,
            });
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"ERROR: {e.Message}");
            Pause();
            return 1;
        }
    }

    private static void Pause()
    {
        Console.WriteLine("Press Enter to close...");
        try
        {
            Console.ReadLine();
        }
        catch (Exception)
        {
            // No interactive console - nothing to wait for
        }
    }
}
