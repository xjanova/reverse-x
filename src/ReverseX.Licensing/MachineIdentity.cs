using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ReverseX.Licensing;

/// <summary>
/// Who this PC is to the xman studio license API (same scheme as Anti X / WinXTools / BrainX):
/// machine_id from the Windows installation (MachineGuid) + the motherboard's SMBIOS UUID,
/// hardware_hash from the board identifiers that survive a Windows reinstall (the trial is bound to it).
/// Read straight from the firmware table — no WMI, so it never hangs.
/// </summary>
internal static class MachineIdentity
{
    private static readonly Lazy<Snapshot> Current = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>64 lowercase hex characters (the server accepts 32–64).</summary>
    public static string MachineId => Current.Value.MachineId;

    /// <summary>Null when the firmware only reports placeholders, so unrelated PCs are never lumped together.</summary>
    public static string? HardwareHash => Current.Value.HardwareHash;

    /// <summary>Secret for sealing the local license file: the PC's own ids, never written anywhere.</summary>
    public static string SealSecret => Current.Value.SealSecret;

    public static string MachineName => Environment.MachineName;

    public static string OsVersion
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return Environment.OSVersion.VersionString;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var name = key?.GetValue("ProductName") as string;
                var build = key?.GetValue("CurrentBuild") as string;
                return string.IsNullOrEmpty(name) ? Environment.OSVersion.VersionString : $"{name} (build {build})";
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }
    }

    private sealed record Snapshot(string MachineId, string? HardwareHash, string SealSecret);

    private static Snapshot Build()
    {
        var machineGuid = ReadMachineGuid();
        var smbios = SmbiosReader.Read();
        var uuid = smbios.SystemUuid is { } raw && !IsPlaceholderUuid(raw) ? Convert.ToHexString(raw) : null;
        var boardSerial = CleanSerial(smbios.BoardSerial);
        var systemSerial = CleanSerial(smbios.SystemSerial);

        var machineId = Sha256Hex($"reversex-machine-v1|{machineGuid ?? Environment.MachineName}|{uuid ?? ""}");
        string? hardwareHash = uuid != null || boardSerial != null || systemSerial != null
            ? Sha256Hex($"reversex-hardware-v1|{uuid ?? ""}|{boardSerial ?? ""}|{systemSerial ?? ""}")
            : null;
        var seal = $"{machineGuid ?? Environment.MachineName}|{uuid ?? ""}|{Environment.UserName}";
        return new Snapshot(machineId, hardwareHash, seal);
    }

    private static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = (key?.GetValue("MachineGuid") as string)?.Trim().ToLowerInvariant();
            return Guid.TryParse(value, out var guid) && guid != Guid.Empty ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static readonly byte[] DefaultUuid =
        [0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0x09];

    private static bool IsPlaceholderUuid(byte[] uuid) =>
        uuid.Length != 16 || uuid.All(b => b == uuid[0]) || uuid.AsSpan().SequenceEqual(DefaultUuid);

    private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "TO BE FILLED BY O.E.M.", "TO BE FILLED BY OEM", "DEFAULT STRING", "NONE", "N/A", "NA", "NOT APPLICABLE",
        "NOT SPECIFIED", "NOT AVAILABLE", "SYSTEM SERIAL NUMBER", "BASE BOARD SERIAL NUMBER", "BASEBOARD SERIAL NUMBER",
        "SERIAL", "SERIALNUMBER", "SN", "OEM", "O.E.M.", "INVALID", "UNKNOWN", "123456789", "1234567890", "0123456789",
        "XXXXXXXXXX", "CHASSIS SERIAL NUMBER",
    };

    private static string? CleanSerial(string? serial)
    {
        var value = serial?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length < 4 || PlaceholderSerials.Contains(value)) return null;
        if (value.All(c => c == value[0] || c is ' ' or '-' or '.')) return null;
        return value.ToUpperInvariant();
    }

    /// <summary>SMBIOS type 1 (system UUID + serial) and type 2 (baseboard serial) via GetSystemFirmwareTable('RSMB').</summary>
    private static class SmbiosReader
    {
        public sealed record Result(byte[]? SystemUuid, string? SystemSerial, string? BoardSerial);

        private const uint Rsmb = 0x52534D42;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);

        public static Result Read()
        {
            if (!OperatingSystem.IsWindows()) return new Result(null, null, null);
            try
            {
                var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
                if (size == 0 || size > 1024 * 1024) return new Result(null, null, null);
                var buffer = new byte[size];
                return GetSystemFirmwareTable(Rsmb, 0, buffer, size) == size ? Parse(buffer) : new Result(null, null, null);
            }
            catch
            {
                return new Result(null, null, null);
            }
        }

        private static Result Parse(byte[] raw)
        {
            if (raw.Length < 8) return new Result(null, null, null);
            var tableLength = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - 8));
            int offset = 8, end = 8 + tableLength;
            byte[]? uuid = null;
            string? systemSerial = null, boardSerial = null;
            while (offset + 4 <= end)
            {
                var type = raw[offset];
                var length = raw[offset + 1];
                if (length < 4 || offset + length > end) break;
                var strings = offset + length;
                var next = strings;
                while (next + 1 < end && !(raw[next] == 0 && raw[next + 1] == 0)) next++;
                next += 2;
                if (type == 1 && length >= 0x19 && uuid == null)
                {
                    uuid = raw.AsSpan(offset + 8, 16).ToArray();
                    systemSerial = ReadString(raw, strings, end, raw[offset + 7]);
                }
                else if (type == 2 && length >= 0x08 && boardSerial == null)
                {
                    boardSerial = ReadString(raw, strings, end, raw[offset + 7]);
                }
                else if (type == 127)
                {
                    break;
                }
                offset = next;
            }
            return new Result(uuid, systemSerial, boardSerial);
        }

        private static string? ReadString(byte[] raw, int start, int end, byte index)
        {
            if (index == 0) return null;
            var position = start;
            for (var i = 1; position < end; i++)
            {
                var stop = position;
                while (stop < end && raw[stop] != 0) stop++;
                if (stop == position) return null;
                if (i == index) return Encoding.ASCII.GetString(raw, position, stop - position);
                position = stop + 1;
            }
            return null;
        }
    }
}
