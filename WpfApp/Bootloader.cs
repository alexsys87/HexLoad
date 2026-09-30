/*
 * STM32 Bootloader Protocol (host side)
 * =====================================
 *
 * Reference firmware: main.c, STM32F030 (Cortex-M0), UART 115200 8N1.
 *
 * Framing
 *   The bootloader receives into a DMA buffer (16 + 1024 bytes) and treats the
 *   USART IDLE-line event as the end of a packet. Therefore a packet (header plus
 *   optional data) MUST be transmitted as one continuous burst: any pause longer
 *   than one character time (~87 us at 115200) splits it into two packets.
 *   This host always writes a complete packet with a single Write() call.
 *
 * Packet header (16 bytes, all fields uint32 little-endian)
 *   cmd  : command code (in replies: command | status)
 *   addr : offset from the application start address (CMD_PROG only, else 0)
 *   size : number of data bytes following the header
 *   crc  : see "CRC" below
 *
 * Commands
 *   0x01 CMD_CONNECT - no data, reply cmd|0x40; stops the bootloader timeout
 *   0x02 CMD_GETINFO - no data, reply header + 16-byte info
 *   0x03 CMD_ERASE   - no data, erases the whole application area, reply status
 *   0x04 CMD_PROG    - data = one page (size <= page_size, multiple of 4),
 *                      written at APPLICATION_ADDRESS + addr and verified
 *   0x05 CMD_RESET   - no data, MCU resets immediately, NO reply
 *
 * Status bits (ORed into cmd of the reply)
 *   0x40 OK, 0x80 error (CRC mismatch, erase/verify failure)
 *   An unknown command is answered with a single byte 0x80 (no header).
 *
 * Info structure (reply to CMD_GETINFO)
 *   version, product, pages (application pages), page_size
 *
 * CRC (STM32 hardware CRC: poly 0x04C11DB7, init 0xFFFFFFFF, 32-bit words)
 *   host request without data : CRC of the first 12 header bytes (not checked by firmware)
 *   host request with data    : CRC of the data (checked by firmware)
 *   reply without data        : CRC of the 16 header bytes with crc field = 0
 *   reply with data (info)    : CRC of the 16 info bytes (checked by host)
 *
 * Timing
 *   After reset the bootloader waits COMM_TIMEOUT = 3000 ms for CMD_CONNECT,
 *   then jumps to the application.
 */

using System;
using System.Buffers.Binary;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace HexLoad
{
    public struct BootHeader
    {
        public uint Cmd;
        public uint Addr;
        public uint Size;
        public uint Crc;
    }

    public struct BootInfo
    {
        public uint Version;
        public uint Product;
        public uint Pages;      // number of pages in the application area
        public uint PageSize;

        public uint FlashSize => Pages * PageSize;

        public string VersionString =>
            $"{(Version >> 24) & 0xFF}.{(Version >> 16) & 0xFF}.{(Version >> 8) & 0xFF}.{Version & 0xFF}";
    }

    public static class BootCommands
    {
        public const uint CmdConnect = 1;
        public const uint CmdInfo = 2;
        public const uint CmdErase = 3;
        public const uint CmdProg = 4;
        public const uint CmdReset = 5;

        public const uint CompleteMask = 0x40;
        public const uint ErrorMask = 0x80;
    }

    /// <summary>Protocol-level error: the device replied with an error status or an unexpected packet.</summary>
    public sealed class BootloaderException : Exception
    {
        public uint ResponseCmd { get; }

        public BootloaderException(string message, uint responseCmd = 0) : base(message)
        {
            ResponseCmd = responseCmd;
        }
    }

    /// <summary>
    /// Host side of the STM32 UART bootloader protocol.
    /// Blocking serial I/O runs on the thread pool; access to the port is serialized.
    /// </summary>
    public sealed class Bootloader : IDisposable
    {
        public const int HeaderSize = 16;
        private const int InfoSize = 16;

        private readonly SerialPort _port;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly byte[] _rxHeader = new byte[HeaderSize];
        private readonly byte[] _infoBuffer = new byte[InfoSize];
        private byte[] _frame = new byte[HeaderSize];   // header + data, sent with one Write()
        private bool _disposed;

        public string PortName { get; }
        public int BaudRate { get; }

        public BootInfo DeviceInfo { get; private set; }
        public bool HasDeviceInfo { get; private set; }

        /// <summary>Timeout of a regular command (connect/info/prog), ms.</summary>
        public int CommandTimeoutMs { get; set; } = 1000;

        /// <summary>Timeout of the flash erase command, ms.</summary>
        public int EraseTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// Delay between pages, ms. The bootloader replies to CMD_PROG only after the page
        /// has been programmed and verified, so no delay is needed by default.
        /// </summary>
        public int InterPageDelayMs { get; set; }

        public Bootloader(string portName, int baudRate)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("COM port name is not set.", nameof(portName));
            if (baudRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(baudRate), "Baud rate must be positive.");

            PortName = portName;
            BaudRate = baudRate;

            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = CommandTimeoutMs,
                WriteTimeout = CommandTimeoutMs,
                ReadBufferSize = 8192,
                WriteBufferSize = 8192,
            };
        }

        public bool IsOpen => !_disposed && _port.IsOpen;

        public void Open()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_port.IsOpen)
            {
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
            }
        }

        public void Close()
        {
            if (!_disposed && _port.IsOpen)
            {
                try { _port.Close(); } catch (Exception) { /* the adapter may have been unplugged */ }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_port.IsOpen) _port.Close(); } catch (Exception) { }
            _port.Dispose();
            _gate.Dispose();
        }

        // ==================== Low level (runs on the thread pool) ====================

        /// <summary>
        /// Builds header + data in one buffer and sends it with a single Write() call,
        /// so the packet leaves the adapter as one burst without an IDLE gap inside.
        /// </summary>
        private void SendPacket(uint command, uint addr, byte[]? data, int dataLength)
        {
            int total = HeaderSize + dataLength;
            if (_frame.Length < total) _frame = new byte[total];

            Span<byte> hdr = _frame.AsSpan(0, HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0), command);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4), addr);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8), (uint)dataLength);

            uint crc;
            if (dataLength > 0)
            {
                Buffer.BlockCopy(data!, 0, _frame, HeaderSize, dataLength);
                crc = Stm32Crc32.Compute(Stm32Crc32.InitialValue, _frame.AsSpan(HeaderSize, dataLength));
            }
            else
            {
                crc = Stm32Crc32.Compute(Stm32Crc32.InitialValue, hdr.Slice(0, 12));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12), crc);

            _port.Write(_frame, 0, total);
        }

        private void ReadExact(byte[] buffer, int count, long deadline, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                token.ThrowIfCancellationRequested();

                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0)
                    throw new TimeoutException($"No response from device ({read} of {count} bytes received).");

                _port.ReadTimeout = remaining;

                int n;
                try
                {
                    n = _port.Read(buffer, read, count - read);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"No response from device ({read} of {count} bytes received).");
                }

                if (n <= 0)
                    throw new TimeoutException($"Port closed while reading ({read} of {count} bytes received).");

                read += n;
            }
        }

        /// <summary>Waits until the transmit buffer is physically sent (required before closing the port).</summary>
        private void DrainOutput(long deadline)
        {
            while (_port.BytesToWrite > 0 && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
        }

        /// <summary>Sends a packet and receives the reply header. CMD_RESET has no reply.</summary>
        private BootHeader Transact(uint command, uint addr, byte[]? data, int dataLength,
                                    int timeoutMs, CancellationToken token)
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            _port.WriteTimeout = timeoutMs;
            _port.DiscardInBuffer();     // drop stray bytes (e.g. a 1-byte 0x80 reply to garbage)

            SendPacket(command, addr, data, dataLength);

            if (command == BootCommands.CmdReset)
            {
                // No reply, but the packet must actually leave the adapter:
                // closing the port right after the call would truncate it.
                DrainOutput(deadline);
                Thread.Sleep(HeaderSize * 10 * 1000 / BaudRate + 2);
                return new BootHeader { Cmd = BootCommands.CmdReset };
            }

            ReadExact(_rxHeader, HeaderSize, deadline, token);

            return new BootHeader
            {
                Cmd = BinaryPrimitives.ReadUInt32LittleEndian(_rxHeader.AsSpan(0)),
                Addr = BinaryPrimitives.ReadUInt32LittleEndian(_rxHeader.AsSpan(4)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(_rxHeader.AsSpan(8)),
                Crc = BinaryPrimitives.ReadUInt32LittleEndian(_rxHeader.AsSpan(12)),
            };
        }

        private static void EnsureOk(BootHeader response, uint command, string operation)
        {
            if (response.Cmd == (BootCommands.CompleteMask | command)) return;

            if (response.Cmd == (BootCommands.ErrorMask | command))
                throw new BootloaderException($"{operation}: device reported an error.", response.Cmd);

            throw new BootloaderException(
                $"{operation}: unexpected response 0x{response.Cmd:X8} " +
                $"(expected 0x{BootCommands.CompleteMask | command:X8}).", response.Cmd);
        }

        /// <summary>Acquires the port and runs a blocking exchange on the thread pool.</summary>
        private async Task<T> RunExclusiveAsync<T>(Func<T> action, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(action, token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        // ==================== Commands ====================

        /// <summary>Establishes the connection. Exceptions are propagated so the caller can report the cause.</summary>
        public Task<bool> ConnectAsync(CancellationToken token = default)
        {
            return RunExclusiveAsync(() =>
            {
                Open();
                var response = Transact(BootCommands.CmdConnect, 0, null, 0, CommandTimeoutMs, token);
                return response.Cmd == (BootCommands.CompleteMask | BootCommands.CmdConnect);
            }, token);
        }

        public Task<BootInfo> GetInfoAsync(CancellationToken token = default)
        {
            return RunExclusiveAsync(() =>
            {
                Open();
                long deadline = Environment.TickCount64 + CommandTimeoutMs;

                var response = Transact(BootCommands.CmdInfo, 0, null, 0, CommandTimeoutMs, token);
                EnsureOk(response, BootCommands.CmdInfo, "Get info");

                if (response.Size != InfoSize)
                    throw new BootloaderException(
                        $"Get info: invalid data size {response.Size}, expected {InfoSize}.");

                ReadExact(_infoBuffer, InfoSize, deadline, token);

                uint crc = Stm32Crc32.Compute(Stm32Crc32.InitialValue, _infoBuffer);
                if (crc != response.Crc)
                    throw new BootloaderException(
                        $"Get info: CRC mismatch (expected {response.Crc:X8}, got {crc:X8}).");

                var info = new BootInfo
                {
                    Version = BinaryPrimitives.ReadUInt32LittleEndian(_infoBuffer.AsSpan(0)),
                    Product = BinaryPrimitives.ReadUInt32LittleEndian(_infoBuffer.AsSpan(4)),
                    Pages = BinaryPrimitives.ReadUInt32LittleEndian(_infoBuffer.AsSpan(8)),
                    PageSize = BinaryPrimitives.ReadUInt32LittleEndian(_infoBuffer.AsSpan(12)),
                };

                if (info.PageSize == 0 || info.Pages == 0 || (info.PageSize & 3) != 0)
                    throw new BootloaderException(
                        $"Device reported an invalid flash layout: {info.Pages} pages of {info.PageSize} bytes.");

                DeviceInfo = info;
                HasDeviceInfo = true;
                return info;
            }, token);
        }

        public Task EraseAsync(CancellationToken token = default)
        {
            return RunExclusiveAsync<object?>(() =>
            {
                Open();
                var response = Transact(BootCommands.CmdErase, 0, null, 0, EraseTimeoutMs, token);
                EnsureOk(response, BootCommands.CmdErase, "Erase");
                return null;
            }, token);
        }

        public Task ResetAsync(CancellationToken token = default)
        {
            return RunExclusiveAsync<object?>(() =>
            {
                Open();
                Transact(BootCommands.CmdReset, 0, null, 0, CommandTimeoutMs, token);
                HasDeviceInfo = false;
                return null;
            }, token);
        }

        /// <summary>
        /// Programs the image page by page. addr of every page is its offset from the
        /// start of the image; the bootloader writes it at APPLICATION_ADDRESS + addr.
        /// progress receives values 0..100.
        /// </summary>
        public async Task ProgramAsync(byte[] firmware, IProgress<int>? progress, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(firmware);
            if (!HasDeviceInfo)
                throw new InvalidOperationException("No device info - call GetInfoAsync first.");
            if (firmware.Length == 0)
                throw new ArgumentException("Firmware image is empty.", nameof(firmware));

            uint pageSize = DeviceInfo.PageSize;
            uint flashSize = DeviceInfo.FlashSize;
            if ((uint)firmware.Length > flashSize)
                throw new ArgumentException(
                    $"Image size {firmware.Length} bytes exceeds the application area of {flashSize} bytes.",
                    nameof(firmware));

            uint totalSize = (uint)firmware.Length;
            uint written = 0;
            int lastPercent = -1;

            // One page buffer for the whole image (page_size is a multiple of 4, checked in GetInfoAsync)
            byte[] pageData = new byte[pageSize];

            while (written < totalSize)
            {
                token.ThrowIfCancellationRequested();

                uint chunkSize = Math.Min(pageSize, totalSize - written);
                int alignedSize = (int)((chunkSize + 3) & ~3u);   // firmware programs whole 32-bit words

                Buffer.BlockCopy(firmware, (int)written, pageData, 0, (int)chunkSize);
                pageData.AsSpan((int)chunkSize, alignedSize - (int)chunkSize).Fill(0xFF);

                uint address = written;
                int size = alignedSize;

                var response = await RunExclusiveAsync(() =>
                {
                    Open();
                    return Transact(BootCommands.CmdProg, address, pageData, size, CommandTimeoutMs, token);
                }, token).ConfigureAwait(false);

                if (response.Cmd != (BootCommands.CompleteMask | BootCommands.CmdProg))
                    throw new BootloaderException(
                        $"Page write failed at offset 0x{written:X8}: response 0x{response.Cmd:X8}.",
                        response.Cmd);

                if (InterPageDelayMs > 0)
                    await Task.Delay(InterPageDelayMs, token).ConfigureAwait(false);

                written += chunkSize;

                int percent = (int)(written * 100L / totalSize);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent);
                }
            }
        }
    }
}
