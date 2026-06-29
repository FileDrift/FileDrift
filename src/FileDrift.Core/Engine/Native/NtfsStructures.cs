// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace FileDrift.Core.Engine.Native;

/// <summary>Input to FSCTL_ENUM_USN_DATA — tells the kernel where to start the MFT scan.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftEnumDataV0
{
    public ulong StartFileReferenceNumber; // 0 to begin; updated from output on each call
    public long LowUsn;                    // 0 = all records
    public long HighUsn;                   // long.MaxValue = all records
}

/// <summary>Fixed-size header of a USN_RECORD_V2 record returned by FSCTL_ENUM_USN_DATA.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UsnRecordV2
{
    public uint RecordLength;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public ulong FileReferenceNumber;
    public ulong ParentFileReferenceNumber;
    public long Usn;
    public long TimeStamp;          // last-write FILETIME
    public uint Reason;
    public uint SourceInfo;
    public uint SecurityId;
    public uint FileAttributes;
    public ushort FileNameLength;   // bytes, not chars
    public ushort FileNameOffset;   // offset from start of record to FileName chars
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32FileAttributeData
{
    public uint FileAttributes;
    public long CreationTime;       // FILETIME
    public long LastAccessTime;     // FILETIME
    public long LastWriteTime;      // FILETIME
    public uint FileSizeHigh;
    public uint FileSizeLow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

internal static class NtfsFileAttributes
{
    public const uint Directory    = 0x00000010;
    public const uint ReparsePoint = 0x00000400;
}
