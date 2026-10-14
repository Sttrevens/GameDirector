using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using GameDirector.Core.Dsl;
using Microsoft.Win32.SafeHandles;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    /// <summary>Unity's .NET Standard 2.1 has no FileSystemInfo.LinkTarget.
    /// On Unix ReparsePoint denotes a symlink. On Windows query the actual
    /// reparse tag: NAME_SURROGATE rejects links, not OneDrive placeholders.
    /// A failed query fails closed, rather than trusting an unknown reparse point.</summary>
    internal static class RuntimeLinks
    {
        internal static bool IsLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return false;
            if (Application.platform != RuntimePlatform.WindowsEditor) return true;
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException(DirectorUi.T("inspectReparse") + path, new Win32Exception());
            var buffer = new byte[16384];
            if (!DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, buffer, buffer.Length, out var bytes, IntPtr.Zero) || bytes < 8)
                throw new IOException(DirectorUi.T("readReparse") + path, new Win32Exception());
            return RuntimeFilePolicy.IsWindowsLinkTag(BitConverter.ToUInt32(buffer, 0));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int bytes, IntPtr overlapped);
    }
}
