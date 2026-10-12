param(
    [Parameter(Mandatory = $true)] [string] $SourceExe,
    [Parameter(Mandatory = $true)] [string] $DestinationIco
)

$ErrorActionPreference = 'Stop'
$native = @'
using System;
using System.Runtime.InteropServices;

public static class NteIconResources
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")]
    public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    public static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    public static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);
    public delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
}
'@

Add-Type $native

function Read-IconResource([IntPtr] $Module, [IntPtr] $Name, [IntPtr] $Type) {
    $resource = [NteIconResources]::FindResource($Module, $Name, $Type)
    if ($resource -eq [IntPtr]::Zero) { throw "Icon resource not found: $Name" }
    $length = [int][NteIconResources]::SizeofResource($Module, $resource)
    $pointer = [NteIconResources]::LockResource([NteIconResources]::LoadResource($Module, $resource))
    if ($pointer -eq [IntPtr]::Zero) { throw "Unable to read icon resource: $Name" }
    $bytes = [byte[]]::new($length)
    [Runtime.InteropServices.Marshal]::Copy($pointer, $bytes, 0, $length)
    return ,$bytes
}

$module = [NteIconResources]::LoadLibraryEx($SourceExe, [IntPtr]::Zero, 2)
if ($module -eq [IntPtr]::Zero) { throw "Unable to load EXE icon resources: $SourceExe" }

try {
    $names = [System.Collections.Generic.List[IntPtr]]::new()
    $callback = [NteIconResources+EnumResNameProc]{
        param($m, $t, $n, $p)
        $names.Add($n)
        return $true
    }
    [void][NteIconResources]::EnumResourceNames($module, [IntPtr]14, $callback, [IntPtr]::Zero)
    if ($names.Count -lt 1) { throw 'Official EXE has no icon group.' }

    $group = Read-IconResource $module $names[0] ([IntPtr]14)
    $count = [BitConverter]::ToUInt16($group, 4)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + 14 * $i
        $iconId = [BitConverter]::ToUInt16($group, $entry + 12)
        $images.Add((Read-IconResource $module ([IntPtr]$iconId) ([IntPtr]3)))
    }

    $stream = [IO.File]::Create($DestinationIco)
    try {
        $writer = [IO.BinaryWriter]::new($stream)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$count)
        $offset = 6 + 16 * $count
        for ($i = 0; $i -lt $count; $i++) {
            $entry = 6 + 14 * $i
            $writer.Write($group, $entry, 12)
            $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write($image) }
        $writer.Dispose()
    } finally { $stream.Dispose() }
} finally { [void][NteIconResources]::FreeLibrary($module) }
