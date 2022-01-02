using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaDevices;

namespace PhotoSync
{
    internal class Program
    {
        static MediaDevice _Device = null;
        static Queue<string> _Directories = new Queue<string>();
        static string _TargetDirectory = "";
        static int _StatsCheckpoint = 100;
        static int _ExceptionRetries = 5;
        static List<ExceptionFile> _ExceptionFiles = new List<ExceptionFile>();
        static int _SleepBetweenRetriesMs = 1;
        static int _SleepBetweenFilesMs = 1;
        static bool _DeleteOnSuccess = false;

        static void Main(string[] args)
        {
            Console.WriteLine("PhotoSync");
            Console.WriteLine("");

            IEnumerable<MediaDevice> devicesEnumerable = MediaDevice.GetDevices();
            if (devicesEnumerable == null)
            {
                Console.WriteLine("No media devices found");
                return;
            }

            List<MediaDevice> devices = new List<MediaDevice>(devicesEnumerable);
            Console.WriteLine(devices.Count + " media device(s) found:");
            for (int i = 0; i < devices.Count; i++)
            {
                Console.WriteLine("  " + i + ": " + devices[i].FriendlyName);
            }
            Console.WriteLine("");

            int deviceNum = InputHelper.InputInteger("Device number:", 0, true, true);
            _Device = devices[deviceNum];

            Console.WriteLine("Connecting to " + _Device.FriendlyName);
            _Device.Connect();
            Console.WriteLine("Connected");

            _TargetDirectory = InputHelper.InputString("Destination:", "./backup/", false);
            _TargetDirectory = _TargetDirectory.Replace("\\", "/");
            if (!_TargetDirectory.EndsWith("/")) _TargetDirectory += "/";
            if (!Directory.Exists(_TargetDirectory)) Directory.CreateDirectory(_TargetDirectory);

            List<string> sourceDirectories = EnumerateDirectoriesAndQueue("/");

            int totalDirectories = _Directories.Count;
            Console.WriteLine("Queued: " + totalDirectories + " directories");

            bool reverse = InputHelper.InputBoolean("Reverse the queue:", false);
            if (reverse) _Directories = new Queue<string>(_Directories.Reverse());

            _DeleteOnSuccess = InputHelper.InputBoolean("Delete on success:", false);

            int directoriesProcessed = 0;
            int filesProcessed = 0;
            int copySuccess = 0;
            int copyFailure = 0;
            long totalBytesCopied = 0;

            while (true)
            {
                if (_Directories.Count <= 0) break;
                string dir = _Directories.Dequeue();

                Console.WriteLine("Processing directory " + directoriesProcessed + ": " + dir);
                List<string> files = EnumerateFiles(dir);

                foreach (string file in files)
                {
                    int bytesCopied = BackupFile(dir, file);
                    if (bytesCopied > 0)
                    {
                        copySuccess++;
                    }
                    else
                    {
                        copyFailure++;
                        _ExceptionFiles.Add(new ExceptionFile(dir, file));
                    }

                    totalBytesCopied += bytesCopied;

                    if ((copyFailure + copySuccess) % _StatsCheckpoint == 0)
                    {
                        EnumerateStatistics(
                            "Current Statistics",
                            totalDirectories,
                            directoriesProcessed, 
                            filesProcessed, 
                            copySuccess, 
                            copyFailure, 
                            totalBytesCopied);
                    }
                }

                directoriesProcessed++;
                filesProcessed += files.Count;
            }

            EnumerateStatistics(
                "Final Statistics", 
                totalDirectories,
                directoriesProcessed, 
                filesProcessed, 
                copySuccess, 
                copyFailure, 
                totalBytesCopied);

            if (_ExceptionFiles.Count > 0)
            {
                while (true)
                {
                    Console.WriteLine("Exceptions:");
                    Console.WriteLine(SerializationHelper.SerializeJson(_ExceptionFiles, true));
                    Console.WriteLine("");

                    bool retry = InputHelper.InputBoolean("Retry exceptions:", true);
                    if (!retry) break;

                    List<ExceptionFile> remainingExceptionFiles = new List<ExceptionFile>();
                    foreach (ExceptionFile exFile in _ExceptionFiles)
                    {
                        int backedUp = BackupFile(exFile.Directory, exFile.Filename);
                        if (backedUp <= 0)
                        {
                            remainingExceptionFiles.Add(exFile);
                        }
                    }

                    _ExceptionFiles = new List<ExceptionFile>(remainingExceptionFiles);
                    if (remainingExceptionFiles.Count == 0) break;
                }
            }
        }

        static List<string> EnumerateDirectoriesAndQueue(string dir)
        {
            List<string> ret = new List<string>();

            try
            {
                IEnumerable<string> dirs = _Device.EnumerateDirectories(dir);
                if (dirs != null) ret = new List<string>(dirs);
                for (int i = 0; i < ret.Count; i++)
                {
                    Console.WriteLine(_Directories.Count + ":  " + ret[i]);
                    _Directories.Enqueue(ret[i]);
                    EnumerateDirectoriesAndQueue(ret[i]);
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Exception enumerating: " + dir);
            }

            return ret;
        }

        static List<string> EnumerateFiles(string dir)
        {
            List<string> ret = new List<string>();

            Console.Write("Directory " + dir + ": ");

            try
            {
                IEnumerable<string> files = _Device.EnumerateFiles(dir);
                if (files != null && files.Count() > 0) ret = new List<string>(files);
                Console.WriteLine(ret.Count + " files");
                if (ret.Count > 0)
                {
                    for (int i = 0; i < ret.Count; i++)
                    {
                        if (i == 0) Console.WriteLine("");
                        Console.WriteLine("  " + i + ": " + ret[i]);
                    }
                }
            }
            catch (Exception)
            {
                Console.WriteLine("exception");
            }

            return ret;
        }

        static int BackupFile(string dir, string file)
        {
            string filename = file.Replace(dir, "");
            while (filename.StartsWith("\\")) filename = filename.Substring(1);

            int attempts = 0;
            int len = 0;

            if (File.Exists(_TargetDirectory + filename))
            {
                len = (int)(new FileInfo(_TargetDirectory + filename).Length);

                if (len > 0)
                {
                    Console.WriteLine("Backup file " + filename + ": exists (" + len + " bytes)");

                    if (_DeleteOnSuccess)
                    {
                        if (_Device.FileExists(file))
                        {
                            Console.WriteLine("Cleaning up file " + file);
                            _Device.DeleteFile(file);
                        }
                    }

                    return len;
                }
            }

            while (attempts < _ExceptionRetries)
            {
                try
                {
                    Console.Write("Backup file " + file + " to " + _TargetDirectory + filename + ": ");

                    MediaFileInfo mfi = _Device.GetFileInfo(file);
                    len = (int)mfi.Length;
                    using (FileStream fs = new FileStream(_TargetDirectory + filename, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                    {
                        _Device.DownloadFile(file, fs);
                    }

                    Console.WriteLine("success (" + len + " bytes)");

                    if (_DeleteOnSuccess)
                    {
                        Console.WriteLine("Cleaning up file " + file);
                        _Device.DeleteFile(file);
                    }

                    Task.Delay(_SleepBetweenFilesMs).Wait();
                    return len;
                }
                catch (Exception e)
                {
                    Console.WriteLine("exception (attempt " + (attempts + 1) + "/" + _ExceptionRetries + "): " + e.Message);
                    attempts++;

                    Task.Delay(_SleepBetweenRetriesMs).Wait();

                    if (!_Device.IsConnected)
                    {
                        Console.WriteLine("*** Reconnecting");
                        _Device.Connect();
                    }
                }
            }

            Console.WriteLine("*** File " + file + " exceeded exception retry count");
            Task.Delay(_SleepBetweenFilesMs).Wait();
            return 0;
        }

        static void EnumerateStatistics(
            string msg,
            int totalDirectories,
            int directoriesProcessed,
            int filesProcessed,
            int copySuccess,
            int copyFailure,
            long totalBytesCopied)
        {
            Console.WriteLine("");
            Console.WriteLine("");
            Console.WriteLine(msg);
            Console.WriteLine("| Directory count       : " + totalDirectories);
            Console.WriteLine("| Directories processed : " + directoriesProcessed);
            Console.WriteLine("| Files processed       : " + filesProcessed);
            Console.WriteLine("| Copy success          : " + copySuccess);
            Console.WriteLine("| Copy failure          : " + copyFailure);
            Console.WriteLine("| Bytes copied          : " + totalBytesCopied);

            if (totalBytesCopied > (1024 * 1024))
            {
                Console.WriteLine("| Megabytes copied      : " + (totalBytesCopied / (1024 * 1024)) + " MiB");

                if (totalBytesCopied > (1024 * 1024 * 1024))
                {
                    Console.WriteLine("| Gigabytes copied      : " + (totalBytesCopied / (1024 * 1024 * 1024)) + " GiB");
                }
            }

            Console.WriteLine("| Exception files       : " + _ExceptionFiles.Count);
            Console.WriteLine("");
            Console.WriteLine("");
        }
    }
}
