﻿using IPA.Patcher;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace IPA {
    public class Program {
        public enum Architecture {
            x86,
            x64,
            Unknown
        }

        private static Version Version => new Version(Application.ProductVersion);

        static void Main(string[] args)
        {
            var ArgList = args.ToList();

            try
            {
                string arg = args.FirstOrDefault(s => s.StartsWith("--waitfor="));
                if (arg != null)
                {
                    ArgList.Remove(arg);
                    int pid = int.Parse(arg.Split('=').Last());

                    try
                    { // wait for beat saber to exit (ensures we can modify the file)
                        var parent = Process.GetProcessById(pid);

                        Console.WriteLine($"Waiting for parent ({pid}) process to die...");

                        parent.WaitForExit();
                    }
                    catch (Exception) { }
                }

                PatchContext context;

                var argExeName = ArgList.FirstOrDefault(s => s.EndsWith(".exe"));

                if (argExeName == null)
                {
                    //Fail("Drag an (executable) file on the exe!");
                    context = PatchContext.Create(ArgList.ToArray(), 
                        new DirectoryInfo(Directory.GetCurrentDirectory()).GetFiles()
                            .First(o => o.FullName.EndsWith(".exe"))
                            .FullName);
                }
                else
                {
                    context = PatchContext.Create(ArgList.ToArray(), argExeName);
                }

                bool isRevert = ArgList.Contains("--revert") || Keyboard.IsKeyDown(Keys.LMenu);
                // Sanitizing
                Validate(context);

                if (isRevert)
                {
                    Revert(context);
                }
                else
                {
                    Install(context);
                    StartIfNeedBe(context);
                }
            }
            catch (Exception e) {
                Fail(e.Message);
            }
        }

        private static void Validate(PatchContext c) {
            if (!Directory.Exists(c.DataPathDst) || !File.Exists(c.EngineFile)) {
                Fail("Game does not seem to be a Unity project. Could not find the libraries to patch.");
                Console.WriteLine($"DataPath: {c.DataPathDst}");
                Console.WriteLine($"EngineFile: {c.EngineFile}");
            }
        }

        private static void Install(PatchContext context) {
            try {
                var backup = new BackupUnit(context);

                #region Patch Version Check

                var patchedModule = PatchedModule.Load(context.EngineFile);
                var isCurrentNewer = Version.CompareTo(patchedModule.Data.Version) > 0;
                Console.WriteLine($"Current: {Version} Patched: {patchedModule.Data.Version}");
                if (isCurrentNewer) {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(
                        $"Preparing for update, {(patchedModule.Data.Version == null ? "UnPatched" : patchedModule.Data.Version.ToString())} => {Version}");
                    Console.WriteLine("--- Starting ---");
                    Revert(context, new[] {"newVersion"});
                    Console.ResetColor();


                    #region File Copying

                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("Updating files... ");
                    var nativePluginFolder = Path.Combine(context.DataPathDst, "Plugins");
                    bool isFlat = Directory.Exists(nativePluginFolder) &&
                                  Directory.GetFiles(nativePluginFolder).Any(f => f.EndsWith(".dll"));
                    bool force = !BackupManager.HasBackup(context) || context.Args.Contains("-f") ||
                                 context.Args.Contains("--force");
                    var architecture = DetectArchitecture(context.Executable);

                    Console.WriteLine("Architecture: {0}", architecture);

                    CopyAll(new DirectoryInfo(context.DataPathSrc), new DirectoryInfo(context.DataPathDst), force,
                        backup,
                        (from, to) => NativePluginInterceptor(from, to, new DirectoryInfo(nativePluginFolder), isFlat,
                            architecture));

                    Console.WriteLine("Successfully updated files!");

                    #endregion
                }
                else {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Files up to date @ Version {Version}!");
                    Console.ResetColor();
                }

                #endregion

                #region Create Plugin Folder

                if (!Directory.Exists(context.PluginsFolder)) {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("Creating plugins folder... ");
                    Directory.CreateDirectory(context.PluginsFolder);
                    Console.ResetColor();
                }

                #endregion

                #region Patching

                if (!patchedModule.Data.IsPatched || isCurrentNewer) {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Patching UnityEngine.dll with Version {Application.ProductVersion}... ");
                    backup.Add(context.EngineFile);
                    patchedModule.Patch(Version);
                    Console.WriteLine("Done!");
                    Console.ResetColor();
                }

                #endregion

                #region Virtualizing

                if (File.Exists(context.AssemblyFile)) {
                    var virtualizedModule = VirtualizedModule.Load(context.AssemblyFile);
                    if (!virtualizedModule.IsVirtualized) {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.WriteLine("Virtualizing Assembly-Csharp.dll... ");
                        backup.Add(context.AssemblyFile);
                        virtualizedModule.Virtualize();
                        Console.WriteLine("Done!");
                        Console.ResetColor();
                    }
                }

                #endregion

                #region Creating shortcut
                /*if(!File.Exists(context.ShortcutPath))
                {
                    Console.Write("Creating shortcut to IPA ({0})... ",  context.IPA);
                    try
                    {
                        Shortcut.Create(
                            fileName: context.ShortcutPath,
                            targetPath: context.IPA,
                            arguments: Args(context.Executable, "--launch"),
                            workingDirectory: context.ProjectRoot,
                            description: "Launches the game and makes sure it's in a patched state",
                            hotkey: "",
                            iconPath: context.Executable
                        );
                        Console.WriteLine("Created");
                    } catch (Exception e)
                    {
                        Console.Error.WriteLine("Failed to create shortcut, but game was patched!");
                    }
                }*/
                #endregion
            }
            catch (Exception e) {
                Fail("Oops! This should not have happened.\n\n" + e);
            }


            if (!Environment.CommandLine.Contains("--nowait"))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Finished!");
                Console.ResetColor();
                Console.ReadLine();
            }
        }

        private static void Revert(PatchContext context, string[] args = null) {
            Console.ForegroundColor = ConsoleColor.Cyan;
            bool isNewVersion = (args != null && args.Contains("newVersion"));

            Console.Write("Restoring backup... ");
            if (BackupManager.Restore(context)) {
                Console.WriteLine("Done!");
            }
            else {
                Console.WriteLine("Already vanilla or you removed your backups!");
            }


            if (File.Exists(context.ShortcutPath)) {
                Console.WriteLine("Deleting shortcut...");
                File.Delete(context.ShortcutPath);
            }

            Console.WriteLine("");
            Console.WriteLine("--- Done reverting ---");

            if (!Environment.CommandLine.Contains("--nowait") && !isNewVersion) {
                Console.WriteLine("\n\n[Press any key to quit]");
                Console.ReadKey();
            }

            Console.ResetColor();
        }

        private static void StartIfNeedBe(PatchContext context) {
            string startArg = context.Args.FirstOrDefault(s => s.StartsWith("--start="));
            if (startArg != null)
            {
                var cmdlineSplit = startArg.Split('=').ToList();
                cmdlineSplit.RemoveAt(0); // remove first
                var cmdline = string.Join("=", cmdlineSplit);
                Process.Start(context.Executable, cmdline);
            }
            else
            {
                var argList = context.Args.ToList();
                bool launch = argList.Remove("--launch");

                argList.Remove(context.Executable);

                if (launch)
                {
                    Process.Start(context.Executable, Args(argList.ToArray()));
                }
            }
        }

        public static IEnumerable<FileInfo> NativePluginInterceptor(FileInfo from, FileInfo to,
            DirectoryInfo nativePluginFolder, bool isFlat, Architecture preferredArchitecture) {
            if (to.FullName.StartsWith(nativePluginFolder.FullName)) {
                var relevantBit = to.FullName.Substring(nativePluginFolder.FullName.Length + 1);
                // Goes into the plugin folder!
                bool isFileFlat = !relevantBit.StartsWith("x86");
                if (isFlat && !isFileFlat) {
                    // Flatten structure
                    bool is64Bit = relevantBit.StartsWith("x86_64");
                    if (!is64Bit && preferredArchitecture == Architecture.x86) {
                        // 32 bit
                        yield return new FileInfo(Path.Combine(nativePluginFolder.FullName,
                            relevantBit.Substring("x86".Length + 1)));
                    }
                    else if (is64Bit && (preferredArchitecture == Architecture.x64 ||
                                         preferredArchitecture == Architecture.Unknown)) {
                        // 64 bit
                        yield return new FileInfo(Path.Combine(nativePluginFolder.FullName,
                            relevantBit.Substring("x86_64".Length + 1)));
                    }
                    else {
                        // Throw away
                        yield break;
                    }
                }
                else if (!isFlat && isFileFlat) {
                    // Deepen structure
                    yield return new FileInfo(Path.Combine(Path.Combine(nativePluginFolder.FullName, "x86"),
                        relevantBit));
                    yield return new FileInfo(Path.Combine(Path.Combine(nativePluginFolder.FullName, "x86_64"),
                        relevantBit));
                }
                else {
                    yield return to;
                }
            }
            else {
                yield return to;
            }
        }

        private static IEnumerable<FileInfo> PassThroughInterceptor(FileInfo from, FileInfo to) {
            yield return to;
        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target, bool aggressive, BackupUnit backup,
            Func<FileInfo, FileInfo, IEnumerable<FileInfo>> interceptor = null) {
            if (interceptor == null) {
                interceptor = PassThroughInterceptor;
            }

            // Copy each file into the new directory.
            foreach (FileInfo fi in source.GetFiles()) {
                foreach (var targetFile in interceptor(fi, new FileInfo(Path.Combine(target.FullName, fi.Name)))) {
                    if (!targetFile.Exists || targetFile.LastWriteTimeUtc < fi.LastWriteTimeUtc || aggressive) {
                        targetFile.Directory.Create();

                        Console.WriteLine(@"Copying {0}", targetFile.FullName);
                        backup.Add(targetFile);
                        fi.CopyTo(targetFile.FullName, true);
                    }
                }
            }

            // Copy each subdirectory using recursion.
            foreach (DirectoryInfo diSourceSubDir in source.GetDirectories()) {
                DirectoryInfo nextTargetSubDir = new DirectoryInfo(Path.Combine(target.FullName, diSourceSubDir.Name));
                CopyAll(diSourceSubDir, nextTargetSubDir, aggressive, backup, interceptor);
            }
        }


        static void Fail(string message) {
            Console.Error.Write("ERROR: " + message);
            if (!Environment.CommandLine.Contains("--nowait")) {
                Console.WriteLine("\n\n[Press any key to quit]");
                Console.ReadKey();
            }

            Environment.Exit(1);
        }

        public static string Args(params string[] args) {
            return string.Join(" ", args.Select(EncodeParameterArgument).ToArray());
        }

        /// <summary>
        /// Encodes an argument for passing into a program
        /// </summary>
        /// <param name="original">The value that should be received by the program</param>
        /// <returns>The value which needs to be passed to the program for the original value 
        /// to come through</returns>
        public static string EncodeParameterArgument(string original) {
            if (string.IsNullOrEmpty(original))
                return original;
            string value = Regex.Replace(original, @"(\\*)" + "\"", @"$1\$0");
            value = Regex.Replace(value, @"^(.*\s.*?)(\\*)$", "\"$1$2$2\"");
            return value;
        }

        public static Architecture DetectArchitecture(string assembly) {
            using (var reader = new BinaryReader(File.OpenRead(assembly))) {
                var header = reader.ReadUInt16();
                if (header == 0x5a4d) {
                    reader.BaseStream.Seek(60, SeekOrigin.Begin); // this location contains the offset for the PE header
                    var peOffset = reader.ReadUInt32();

                    reader.BaseStream.Seek(peOffset + 4, SeekOrigin.Begin);
                    var machine = reader.ReadUInt16();

                    if (machine == 0x8664) // IMAGE_FILE_MACHINE_AMD64
                        return Architecture.x64;
                    else if (machine == 0x014c) // IMAGE_FILE_MACHINE_I386
                        return Architecture.x86;
                    else if (machine == 0x0200) // IMAGE_FILE_MACHINE_IA64
                        return Architecture.x64;
                    else
                        return Architecture.Unknown;
                }
                else {
                    // Not a supported binary
                    return Architecture.Unknown;
                }
            }
        }

        public abstract class Keyboard {
            [Flags]
            private enum KeyStates {
                None = 0,
                Down = 1,
                Toggled = 2
            }

            [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
            private static extern short GetKeyState(int keyCode);

            private static KeyStates GetKeyState(Keys key) {
                KeyStates state = KeyStates.None;

                short retVal = GetKeyState((int) key);

                //If the high-order bit is 1, the key is down
                //otherwise, it is up.
                if ((retVal & 0x8000) == 0x8000)
                    state |= KeyStates.Down;

                //If the low-order bit is 1, the key is toggled.
                if ((retVal & 1) == 1)
                    state |= KeyStates.Toggled;

                return state;
            }

            public static bool IsKeyDown(Keys key) {
                return KeyStates.Down == (GetKeyState(key) & KeyStates.Down);
            }

            public static bool IsKeyToggled(Keys key) {
                return KeyStates.Toggled == (GetKeyState(key) & KeyStates.Toggled);
            }
        }
    }
}