using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.MobileImageMounter;
using iMobileDevice.Service;

namespace Location
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            string lat = null, lon = null;
            if (args.Length == 2 && decimal.TryParse(args[0], out var d) && decimal.TryParse(args[1], out var d2))
            {
                lat = d.ToString();
                lon = d2.ToString();
            }
            iMobileDevice.NativeLibraries.Load();
            var iDevice = iMobileDevice.LibiMobileDevice.Instance.iDevice;
            int count = 0;
            var error = iDevice.idevice_get_device_list(out var devices, ref count);
            if (error != iDeviceError.Success)
            {
                Console.WriteLine(error.ToString());
            }
            foreach (var shandle in devices)
            {
                Console.WriteLine(shandle + ": ");
                DoProcess(shandle, lat, lon);
                Console.WriteLine(shandle + ": end\n\n");
            }
        }

        private static void DoProcess(string shandle, string lat, string lon)
        {
            var iDevice = iMobileDevice.LibiMobileDevice.Instance.iDevice;
            var lockdown = iMobileDevice.LibiMobileDevice.Instance.Lockdown;
            var service = iMobileDevice.LibiMobileDevice.Instance.Service;

            var basePath = $"win-x{(Environment.Is64BitProcess ? "64" : "86")}/";

            Console.WriteLine("idevice_new");

            var error = iDevice.idevice_new(out var iDeviceHandle, shandle);
            if (error != iDeviceError.Success)
            {
                Console.WriteLine(error);
                return;
            }
            using (iDeviceHandle)
            {
                Console.WriteLine("lockdownd_client_new");
                var lockdownError = lockdown.lockdownd_client_new_with_handshake(iDeviceHandle, out var lockdownClientHandle, "com.alpha.jailout." + Guid.NewGuid().ToString("N"));
                if (lockdownError != LockdownError.Success)
                {
                    Console.WriteLine(lockdownError.ToString());
                    return;
                }
                using (lockdownClientHandle)
                {
                    string iOSVersion = null;
                    Console.WriteLine("mount development image");
                    lockdownError = lockdown.lockdownd_get_value(lockdownClientHandle, null, "ProductVersion", out var plistHandle);
                    if (lockdownError != LockdownError.Success)
                    {
                        Console.WriteLine("get iOS version error: " + lockdownError.ToString());
                    }
                    else
                    {
                        using (plistHandle)
                        {
                            iMobileDevice.LibiMobileDevice.Instance.Plist.plist_get_string_val(plistHandle, out iOSVersion);
                            Console.WriteLine("iOS: " + iOSVersion);
                            if (!File.Exists(PathForImage(iOSVersion)) || !File.Exists(PathForImageSign(iOSVersion)))
                            {
                                iOSVersion = Regex.Replace(iOSVersion, @"^(\d+.\d+).*$", "$1");
                            }
                            if (!File.Exists(PathForImage(iOSVersion)) || !File.Exists(PathForImageSign(iOSVersion)))
                            {
                                Console.WriteLine($"can not found {iOSVersion} driver");
                                return;
                            }
                            //
                            // var process = Process.Start(new ProcessStartInfo()
                            // {
                            //     FileName = $"{basePath}ideviceimagemounter.exe",
                            //     Arguments = $"-u {shandle} drivers/{iOSVersion}/inject.dmg drivers/{iOSVersion}/inject.dmg.signature",
                            //     RedirectStandardOutput = true,
                            //     RedirectStandardError = true,
                            //     UseShellExecute = false,
                            // });
                            // process.OutputDataReceived += (sender, args) => Console.WriteLine(args.Data);
                            // process.ErrorDataReceived += (sender, args) => Console.WriteLine(args.Data);
                            // process.EnableRaisingEvents = true;
                            // process.BeginOutputReadLine();
                            // process.BeginErrorReadLine();
                            // process.WaitForExit();
                        }
                    }
                    if (iOSVersion != null)
                    {
                        if (!MountDevelopmentImage(iDeviceHandle, lockdownClientHandle, iOSVersion))
                        {
                            Console.WriteLine("mount failed.");
                            return;
                        }
                    }
                    Console.WriteLine("start com.apple.dt.simulatelocation");
                    lockdownError = lockdown.lockdownd_start_service(lockdownClientHandle, "com.apple.dt.simulatelocation", out var lockdownHandle);
                    if (lockdownError != LockdownError.Success)
                    {
                        Console.WriteLine(lockdownError.ToString());
                        return;
                    }
                    using (lockdownHandle)
                    {
                        Console.WriteLine("service_client_new");
                        var serviceError = service.service_client_new(iDeviceHandle, lockdownHandle, out var serviceClientHandle);
                        if (serviceError != ServiceError.Success)
                        {
                            Console.WriteLine(serviceError.ToString());
                            return;
                        }
                        using (serviceClientHandle)
                        {
                            if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
                            {
                                Restore(serviceClientHandle);
                            }
                            else
                            {
                                SendLocation(serviceClientHandle, lat, lon);
                            }
                        }
                    }
                }
            }
        }

        private static string PathForImage(string iOSVersion)
        {
            return $"drivers/{iOSVersion}/inject.dmg";
        }

        private static string PathForImageSign(string iOSVersion)
        {
            return $"drivers/{iOSVersion}/inject.dmg.signature";
        }

        private static bool MountDevelopmentImage(iDeviceHandle iDeviceHandle, LockdownClientHandle lockdownClientHandle, string iOsVersion)
        {
            var mounter = iMobileDevice.LibiMobileDevice.Instance.MobileImageMounter;
            var lockdown = iMobileDevice.LibiMobileDevice.Instance.Lockdown;
            var plist = iMobileDevice.LibiMobileDevice.Instance.Plist;

            Console.WriteLine("start com.apple.mobile.mobile_image_mounter");
            var lockdownError = lockdown.lockdownd_start_service(lockdownClientHandle, "com.apple.mobile.mobile_image_mounter", out var lockdownHandle);
            if (lockdownError != LockdownError.Success)
            {
                Console.WriteLine(lockdownError.ToString());
                return false;
            }
            using (lockdownHandle)
            {
                var mounterError = mounter.mobile_image_mounter_new(iDeviceHandle, lockdownHandle, out var mounterClientHandle);
                if (mounterError != MobileImageMounterError.Success)
                {
                    Console.WriteLine("connect to com.apple.mobile.mobile_image_mounter failed.");
                    return false;
                }
                using (mounterClientHandle)
                {
                    try
                    {
                        mounterError = mounter.mobile_image_mounter_lookup_image(mounterClientHandle, "Developer", out var plistHandle);
                        if (mounterError != MobileImageMounterError.Success)
                        {
                            Console.WriteLine("lookup_image failed: " + mounterError);
                            return false;
                        }
                        using (plistHandle)
                        {
                            var arr = plist.plist_dict_get_item(plistHandle, "ImageSignature");
                            using (arr)
                            {
                                var size = plist.plist_array_get_size(arr);
                                if (size > 0)
                                {
                                    Console.WriteLine("mounted, skip.");
                                    return true;
                                }
                            }
                        }
                        var image = File.ReadAllBytes(PathForImage(iOsVersion));
                        var imageSign = File.ReadAllBytes(PathForImageSign(iOsVersion));
                        var i = 0;
                        mounterError = mounter.mobile_image_mounter_upload_image(mounterClientHandle, "Developer", (uint)image.Length, imageSign, (ushort)imageSign.Length,
                            ((buffer, length, data) =>
                            {
                                Console.WriteLine($"{buffer.ToString("X")} {i} {length} {data.ToString("X")}");
                                Marshal.Copy(image, i, buffer, (int)length);
                                i += (int)length;
                                return (int)length;
                            }), new IntPtr(0));
                        if (mounterError != MobileImageMounterError.Success)
                        {
                            Console.WriteLine("upload_image failed: " + mounterError);
                            return false;
                        }
                        Console.WriteLine("upload_image done");

                        mounterError = mounter.mobile_image_mounter_mount_image(mounterClientHandle, "/private/var/mobile/Media/PublicStaging/staging.dimage", imageSign,
                            (ushort)imageSign.Length, "Developer", out plistHandle);
                        if (mounterError != MobileImageMounterError.Success)
                        {
                            Console.WriteLine("mount_image failed: " + mounterError);
                            return false;
                        }
                        using (plistHandle)
                        {
                            uint slen = 0;
                            plist.plist_to_xml(plistHandle, out var result, ref slen);
                            var xmlDocument = new XmlDocument();
                            xmlDocument.Load(result);
                            xmlDocument.GetElementsByTagName("");
                        }
                        Console.WriteLine("mount_image done");
                    }
                    finally
                    {
                        mounterError = mounter.mobile_image_mounter_hangup(mounterClientHandle);
                        if (mounterError != MobileImageMounterError.Success)
                        {
                            Console.WriteLine("hangup failed: " + mounterError);
                        }
                    }
                }
            }
            return true;
        }

        private static void SendLocation(ServiceClientHandle serviceClientHandle, string lat, string lon)
        {
            Console.WriteLine($"SendLocation {lat},{lon}");
            var serviceError = SendCmd(serviceClientHandle, 0);
            if (serviceError != ServiceError.Success)
            {
                return;
            }
            serviceError = SendString(serviceClientHandle, lon);
            if (serviceError != ServiceError.Success)
            {
                return;
            }
            serviceError = SendString(serviceClientHandle, lat);
            if (serviceError != ServiceError.Success)
            {
                Console.WriteLine("SendLocation failed");
                return;
            }
            Console.WriteLine("SendLocation success");
        }


        private static ServiceError SendString(ServiceClientHandle serviceClientHandle, string str)
        {
            var service = iMobileDevice.LibiMobileDevice.Instance.Service;
            uint num2 = 0;
            var sLat = Encoding.UTF8.GetBytes(str);
            byte[] dataLen = BitConverter.GetBytes(sLat.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(dataLen);
            }
            byte[] data = sLat;
            var serviceError = service.service_send(serviceClientHandle, dataLen, (uint)dataLen.Length, ref num2);
            if (serviceError != ServiceError.Success)
            {
                Console.WriteLine("send len:" + serviceError);
                return serviceError;
            }
            serviceError = service.service_send(serviceClientHandle, data, (uint)data.Length, ref num2);
            if (serviceError != ServiceError.Success)
            {
                Console.WriteLine("send data:" + serviceError);
                return serviceError;
            }
            return serviceError;
        }

        private static void Restore(ServiceClientHandle serviceClientHandle)
        {
            //还原
            Console.WriteLine("Restore");
            if (SendCmd(serviceClientHandle, 1) != ServiceError.Success)
            {
                Console.WriteLine("Restore failed");
                return;
            }
            Console.WriteLine("Restore success");
        }

        private static ServiceError SendCmd(ServiceClientHandle serviceClientHandle, uint cmd)
        {
            var service = iMobileDevice.LibiMobileDevice.Instance.Service;
            byte[] bytes = BitConverter.GetBytes(cmd);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            uint num = 0;
            Console.WriteLine("SendCmd: " + cmd);
            var serviceError = service.service_send(serviceClientHandle, bytes, (uint)bytes.Length, ref num);
            if (serviceError != ServiceError.Success)
            {
                Console.WriteLine("SendCmd: " + serviceError.ToString());
                return serviceError;
            }
            return serviceError;
        }
    }
}