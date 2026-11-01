using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Pine3LightControl.Core;
using Pine3LightControl.Core.PcRgb;

namespace Pine3LightControl.PcRgb;

/// <summary>
/// SMBus Intel (i801) através do driver PawnIO, como o OpenRGB
/// (i2c_smbus/Windows/i2c_smbus_pawnio.cpp, release_1.1): PawnIOLib.dll + módulo
/// assinado SmbusI801.bin junto ao executável. Precisa de administrador.
/// Partilha o barramento com outros programas (ex.: Pine3 Monitor / LHM) pelo
/// mutex global habitual.
/// </summary>
internal sealed class PawnIoSmbus : ISmbus
{
    private const string Lib = "PawnIOLib.dll";
    private const string ModuleFile = "SmbusI801.bin";
    private const string MutexName = "Global\\Access_SMBUS.HTP.Method";
    private const int SmbusRead = 1, SmbusWrite = 0;
    private const int SizeByteData = 2, SizeWordData = 3, SizeBlockData = 5;
    private const int DataSize = 34;   // union i2c_smbus_data

    [DllImport(Lib)] private static extern int pawnio_version(out uint version);
    [DllImport(Lib)] private static extern int pawnio_open(out IntPtr handle);
    [DllImport(Lib)] private static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport(Lib)] private static extern int pawnio_close(IntPtr handle);
    [DllImport(Lib, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int pawnio_execute(IntPtr handle, string name, ulong[] input, UIntPtr inSize,
        ulong[]? output, UIntPtr outSize, out UIntPtr returnSize);

    private IntPtr _handle;
    private readonly Mutex? _mutex;
    private readonly object _gate = new();

    private PawnIoSmbus(IntPtr handle)
    {
        _handle = handle;
        try { _mutex = new Mutex(false, MutexName); }
        catch (Exception e) { Log.Write($"[SMBus] mutex global indisponível: {e.Message}"); }
        // Modo "sleep" (menos CPU; a latência não interessa para LEDs)
        Exec("ioctl_set_sleep_mode", new ulong[] { 2 }, 0);
    }

    /// <summary>Abre o SMBus; devolve null e uma explicação se não for possível.</summary>
    public static PawnIoSmbus? Open(out string problem)
    {
        problem = "";
        var module = Path.Combine(AppContext.BaseDirectory, ModuleFile);
        if (!File.Exists(module)) { problem = $"{ModuleFile} missing next to the app"; return null; }
        try
        {
            if (pawnio_version(out _) != 0) { problem = "PawnIO library not working"; return null; }
            int st = pawnio_open(out var h);
            if (st != 0)
            {
                problem = st == unchecked((int)0x80070005)
                    ? "access denied: the app must run as administrator"
                    : "PawnIO driver not installed (winget install namazso.PawnIO)";
                return null;
            }
            var blob = File.ReadAllBytes(module);
            st = pawnio_load(h, blob, (UIntPtr)blob.Length);
            if (st != 0)
            {
                pawnio_close(h);
                problem = "no Intel SMBus controller (i801) found";
                return null;
            }
            var bus = new PawnIoSmbus(h);
            // Só chipsets Intel (vendor PCI 0x8086)
            var id = bus.Exec("ioctl_identity", new ulong[] { 0 }, 3);
            if (id is null || (id[2] & 0xFFFF) != 0x8086)
            {
                bus.Dispose();
                problem = "SMBus controller is not Intel";
                return null;
            }
            return bus;
        }
        catch (DllNotFoundException) { problem = $"{Lib} missing next to the app"; return null; }
        catch (Exception e) { problem = e.Message; return null; }
    }

    private ulong[]? Exec(string fn, ulong[] input, int outCount)
    {
        var output = outCount > 0 ? new ulong[outCount] : null;
        int st = pawnio_execute(_handle, fn, input, (UIntPtr)input.Length, output, (UIntPtr)outCount, out _);
        return st == 0 ? output ?? Array.Empty<ulong>() : null;
    }

    private int Xfer(byte addr, int readWrite, byte command, int size, byte[] data)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return -1;
            bool locked = false;
            try
            {
                if (_mutex is not null)
                {
                    try { locked = _mutex.WaitOne(2000); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked) return -1;
                }
                var input = new ulong[9];
                input[0] = addr;
                input[1] = (ulong)readWrite;
                input[2] = command;
                input[3] = (ulong)size;
                var inBytes = new byte[5 * 8];
                Buffer.BlockCopy(data, 0, inBytes, 0, DataSize);
                Buffer.BlockCopy(inBytes, 0, input, 4 * 8, inBytes.Length);
                var output = new ulong[5];
                int st = pawnio_execute(_handle, "ioctl_smbus_xfer", input, (UIntPtr)9, output, (UIntPtr)5, out _);
                var outBytes = new byte[5 * 8];
                Buffer.BlockCopy(output, 0, outBytes, 0, outBytes.Length);
                Buffer.BlockCopy(outBytes, 0, data, 0, DataSize);
                return st == 0 ? 0 : -1;
            }
            finally
            {
                if (locked) _mutex!.ReleaseMutex();
            }
        }
    }

    public int ReadByteData(byte addr, byte command)
    {
        var d = new byte[DataSize];
        return Xfer(addr, SmbusRead, command, SizeByteData, d) == 0 ? d[0] : -1;
    }

    public int WriteByteData(byte addr, byte command, byte value)
    {
        var d = new byte[DataSize];
        d[0] = value;
        return Xfer(addr, SmbusWrite, command, SizeByteData, d);
    }

    public int WriteWordData(byte addr, byte command, ushort value)
    {
        var d = new byte[DataSize];
        d[0] = (byte)value;
        d[1] = (byte)(value >> 8);
        return Xfer(addr, SmbusWrite, command, SizeWordData, d);
    }

    public int WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data)
    {
        var d = new byte[DataSize];
        int n = Math.Min(data.Length, 32);
        d[0] = (byte)n;
        data[..n].CopyTo(d.AsSpan(1));
        return Xfer(addr, SmbusWrite, command, SizeBlockData, d);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
            {
                try { pawnio_close(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
        _mutex?.Dispose();
    }
}
