using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TrenchHQ.Helpers
{
    internal enum ApplicationWindowPinMode { NativeLock, EmbeddedLock }
    internal sealed record ApplicationWindowSelection(ApplicationWindowChoice Window, ApplicationWindowPinMode Mode);

    internal static class ApplicationWindowNative
    {
        internal const string StateProperty = "TrenchHQ.WindowPin.State.v1";
        private static readonly Lazy<IntPtr> Library = new(() =>
        {
            var file = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrenchHQ.WindowPin.version")).Trim();
            if (Path.GetFileName(file) != file || !file.StartsWith("TrenchHQ.WindowPin.", StringComparison.Ordinal) || !file.EndsWith(".dll", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid window-pin helper version.");
            // Foreign apps retain this module until they exit. Never lock the deployment layout.
            var localAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref localAppData, 0x10000, IntPtr.Zero, out var folder));
            try
            {
                var cache = Path.Combine(Marshal.PtrToStringUni(folder)!, "TrenchHQ", "WindowPin");
                return NativeLibrary.Load(StageLibrary(Path.Combine(AppContext.BaseDirectory, file), cache));
            }
            finally { Marshal.FreeCoTaskMem(folder); }
        });

        internal static string StageLibrary(string source, string cache)
        {
            var bytes = File.ReadAllBytes(source);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var directory = Path.Combine(cache, hash);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (!File.Exists(destination))
            {
                var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    try { File.Move(temporary, destination); }
                    catch (IOException) when (File.Exists(destination)) { } // Another process published this version.
                }
                finally { File.Delete(temporary); }
            }
            // Atomic publication can still hold a rename (DELETE) handle. Permit that,
            // while denying writers for the entire hash verification.
            using var published = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!SHA256.HashData(published).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new InvalidOperationException("The cached window-pin helper is damaged. Restart TrenchHQ after removing its WindowPin cache.");
            return destination;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool AttachCall(IntPtr hwnd, IntPtr owner, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool PositionCall(IntPtr hwnd, IntPtr owner, ref Rect bounds);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool DetachCall(IntPtr hwnd, IntPtr owner);
        private static T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library.Value, name));

        internal static void Attach(IntPtr hwnd, IntPtr owner, ApplicationWindowPinMode mode)
        {
            if (!Export<AttachCall>("AttachWindow")(hwnd, owner, (int)mode))
                throw new InvalidOperationException("This app did not accept the pinning method. Try another method or application.");
        }
        internal static bool Position(IntPtr hwnd, IntPtr owner, WindowPinBounds bounds)
        {
            var rect = new Rect { Left = bounds.X, Top = bounds.Y, Right = checked(bounds.X + bounds.Width), Bottom = checked(bounds.Y + bounds.Height) };
            return Export<PositionCall>("PositionWindow")(hwnd, owner, ref rect);
        }
        internal static void Detach(IntPtr hwnd, IntPtr owner) => Export<DetachCall>("DetachWindow")(hwnd, owner);
        internal static bool IsAttached(IntPtr hwnd) => GetProp(hwnd, StateProperty) != IntPtr.Zero;
        internal static bool SupportsProcess(IntPtr process) => RuntimeInformation.ProcessArchitecture == Architecture.X64
            && IsWow64Process2(process, out var machine, out var nativeMachine)
            && (machine == 0 ? nativeMachine : machine) == 0x8664;

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr hwnd, string name);
        [DllImport("kernel32.dll")] private static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort nativeMachine);
        // KF_FLAG_NO_PACKAGE_REDIRECTION keeps the injected copy outside package install/state folders.
        [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
    }
}
