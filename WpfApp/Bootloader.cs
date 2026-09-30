/*
 * STM32 Bootloader Protocol Description
 * =======================================
 *
 * This bootloader communicates over UART at a fixed baud rate (default 115200)
 * using a simple packet-based protocol. Each packet consists of a 16-byte header
 * followed by optional data. All multi-byte fields are transmitted in little-endian
 * order (least significant byte first).
 *
 * Packet Header (16 bytes):
 *   Bytes 0-3:   Command code (uint32_t)
 *   Bytes 4-7:   Address (uint32_t) – used only for Program command
 *   Bytes 8-11:  Data size (uint32_t) – number of bytes in the data field
 *   Bytes 12-15: CRC (uint32_t) – checksum of either the data (if present) or the
 *                first 12 bytes of the header (cmd+addr+size) if no data.
 *
 * Commands (sent by host):
 *   CMD_CONNECT = 0x01 – Establish connection and check bootloader presence.
 *   CMD_GETINFO = 0x02 – Request device information (version, product ID, flash layout).
 *   CMD_ERASE   = 0x03 – Erase all application flash pages.
 *   CMD_PROG    = 0x04 – Program a page of data at specified address.
 *   CMD_RESET   = 0x05 – Reset the MCU and jump to application.
 *
 * Response codes (in the header of the reply):
 *   CMD_OK      = 0x40 – ORed with the original command to indicate success.
 *   CMD_ERROR   = 0x80 – ORed with the original command to indicate an error.
 *
 * Data fields:
 *   - For CMD_GETINFO response: 16-byte structure containing:
 *       version   (uint32_t): Bootloader version.
 *       product   (uint32_t): Product identifier.
 *       pages     (uint32_t): Total number of flash pages.
 *       page_size (uint32_t): Size of each flash page in bytes.
 *   - For CMD_PROG request: the firmware data (size specified in header).
 *     The data is not aligned – the host sends exactly the number of bytes
 *     indicated in the size field.
 *
 * Communication flow:
 *   1. Host sends CMD_CONNECT, bootloader replies with CMD_CONNECT|CMD_OK.
 *   2. Host sends CMD_GETINFO, bootloader replies with info structure and CRC.
 *   3. Host sends CMD_ERASE, bootloader erases flash and replies with CMD_ERASE|CMD_OK.
 *   4. Host repeatedly sends CMD_PROG for each page, with address and data.
 *      Bootloader verifies CRC, programs the page, and replies with CMD_PROG|CMD_OK.
 *   5. Host sends CMD_RESET, bootloader resets and (optionally) jumps to application.
 *
 * CRC Calculation:
 *   The CRC is computed using the STM32 hardware CRC algorithm (polynomial 0x04C11DB7)
 *   with initial value 0xFFFFFFFF. The implementation processes data byte-by-byte
 *   (or word-by-word) and yields the same result as the STM32 hardware peripheral.
 *   For commands without data, the CRC is calculated over the first 12 bytes of the header.
 *   For commands with data (e.g., CMD_PROG request, CMD_GETINFO response), the CRC is
 *   calculated over the data field only. The header's CRC field is then set to that value.
 *
 * Timeouts:
 *   The bootloader uses a 1-second timeout after reset. If no connection is established
 *   within that time, it assumes no host is present and jumps to the application.
 *
 * This protocol is used by the HexLoad application and emulated by the corresponding
 * Windows emulator for testing.
 */

using System.IO.Ports;

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
        public uint Pages;
        public uint PageSize;
    }

    // Команды и маски
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
    public class Bootloader : IDisposable
    {
        private SerialPort _serialPort;
        private readonly int _readTimeout = 1000;  // T1, T2, T3 в мс

        public string PortName { get; private set; }
        public int BaudRate { get; private set; }
        public BootInfo DeviceInfo { get; private set; }

        public Bootloader(string portName, int baudRate)
        {
            PortName = portName;
            BaudRate = baudRate;
            _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _serialPort.ReadTimeout = _readTimeout;
            _serialPort.WriteTimeout = _readTimeout;
        }

        public void Open()
        {
            if (!_serialPort.IsOpen)
                _serialPort.Open();
        }

        public void Close()
        {
            if (_serialPort?.IsOpen == true)
                _serialPort.Close();
        }

        public void Dispose() => Close();

        // Отправка заголовка и приём ответа
        private BootHeader SendCommandAndReceive(uint command, uint addr, byte[]? data = null)
        {
            var header = new BootHeader
            {
                Cmd = command,
                Addr = addr,
                Size = (uint)(data?.Length ?? 0),
                //Crc = 0
            };

            if (data != null && data.Length > 0)
            {
                header.Crc = Stm32Crc32.Compute(0xFFFFFFFF, data, 0, data.Length);
            }
            else
            {
                byte[] headerBytes = new byte[12];
                Buffer.BlockCopy(BitConverter.GetBytes(header.Cmd),  0, headerBytes, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(header.Addr), 0, headerBytes, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(header.Size), 0, headerBytes, 8, 4);
                header.Crc = Stm32Crc32.Compute(0xFFFFFFFF, headerBytes, 0, 12);
            }

            byte[] headerOut = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(header.Cmd),  0, headerOut, 0,  4);
            Buffer.BlockCopy(BitConverter.GetBytes(header.Addr), 0, headerOut, 4,  4);
            Buffer.BlockCopy(BitConverter.GetBytes(header.Size), 0, headerOut, 8,  4);
            Buffer.BlockCopy(BitConverter.GetBytes(header.Crc),  0, headerOut, 12, 4);
            _serialPort.Write(headerOut, 0, 16);

            if (command == 5) return new BootHeader { Cmd = 5, Addr = 0, Size = 0, Crc = 0 };

            if (data != null && data.Length > 0)
                _serialPort.Write(data, 0, data.Length);

            byte[] responseHeader = new byte[16];
            int read = 0;
            while (read < 16)
                read += _serialPort.Read(responseHeader, read, 16 - read);

            BootHeader response;
            response.Cmd = BitConverter.ToUInt32(responseHeader, 0);
            response.Addr = BitConverter.ToUInt32(responseHeader, 4);
            response.Size = BitConverter.ToUInt32(responseHeader, 8);
            response.Crc = BitConverter.ToUInt32(responseHeader, 12);
            return response;
        }

        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    Open();
                    var response = SendCommandAndReceive(BootCommands.CmdConnect, 0);
                    return response.Cmd == (BootCommands.CompleteMask | BootCommands.CmdConnect);
                }
                catch
                {
                    return false;
                }
            }, token);
        }

        public async Task<BootInfo> GetInfoAsync(CancellationToken token = default)
        {
            // Отправляем команду и получаем заголовок ответа
            var response = SendCommandAndReceive(BootCommands.CmdInfo, 0);
            if (response.Cmd != (BootCommands.CompleteMask | BootCommands.CmdInfo))
                throw new Exception("Info command failed");
            if (response.Size != 16)
                throw new Exception($"Invalid info size: {response.Size}, expected 16");

            // Асинхронно читаем 16 байт информации
            byte[] infoBytes = new byte[16];
            int read = 0;
            while (read < 16)
            {
                token.ThrowIfCancellationRequested();
                int bytesRead = await _serialPort.BaseStream.ReadAsync(infoBytes, read, 16 - read, token).ConfigureAwait(false);
                if (bytesRead == 0)
                    throw new TimeoutException("Timeout reading info data");
                read += bytesRead;
            }

            // Вычисляем CRC полученных данных и сравниваем с ожидаемым
            uint crcCalc = Stm32Crc32.Compute(0xFFFFFFFF, infoBytes, 0, 16);
            if (crcCalc != response.Crc)
                throw new Exception($"Info CRC mismatch: expected {response.Crc:X8}, got {crcCalc:X8}");

            // Сохраняем информацию об устройстве
            DeviceInfo = new BootInfo
            {
                Version = BitConverter.ToUInt32(infoBytes, 0),
                Product = BitConverter.ToUInt32(infoBytes, 4),
                Pages = BitConverter.ToUInt32(infoBytes, 8),
                PageSize = BitConverter.ToUInt32(infoBytes, 12)
            };
            return DeviceInfo;
        }

        public async Task EraseAsync(CancellationToken token = default)
        {
            await Task.Run(() =>
            {
                var response = SendCommandAndReceive(BootCommands.CmdErase, 0);
                if (response.Cmd != (BootCommands.CompleteMask | BootCommands.CmdErase))
                    throw new Exception("Erase failed");
            }, token);
        }

        public async Task ProgramAsync(byte[] firmware, IProgress<int> progress, CancellationToken token)
        {
            if (DeviceInfo.Equals(default(BootInfo)))
                throw new InvalidOperationException("Device info not loaded. Call GetInfoAsync first.");

            uint pageSize = DeviceInfo.PageSize;
            uint totalSize = (uint)firmware.Length;
            uint written = 0;

            while (written < totalSize)
            {
                token.ThrowIfCancellationRequested();

                uint chunkSize = Math.Min(pageSize, totalSize - written);
                uint alignedSize = (chunkSize + 3) & ~3u; // округление вверх до кратного 4
                byte[] pageData = new byte[alignedSize];
                Array.Copy(firmware, written, pageData, 0, (int)chunkSize);
                for (int i = (int)chunkSize; i < alignedSize; i++)
                    pageData[i] = 0xFF; // заполняем хвост 0xFF

                // Отправляем команду с данными (CRC вычисляется от pageData, размер в заголовке = alignedSize)
                var response = await Task.Run(() => SendCommandAndReceive(BootCommands.CmdProg, written, pageData), token).ConfigureAwait(false);

                if (response.Cmd != (BootCommands.CompleteMask | BootCommands.CmdProg))
                {
                    throw new Exception($"Program page failed at offset 0x{written:X}, response command: 0x{response.Cmd:X2}");
                }

                // Небольшая задержка между страницами
                await Task.Delay(10, token).ConfigureAwait(false);

                written += chunkSize; // увеличиваем на реальный размер (не дополненный)
                progress?.Report((int)(written * 100 / totalSize));
            }
        }

        public async Task ResetAsync(CancellationToken token = default)
        {
            await Task.Run(() =>
            {
                var response = SendCommandAndReceive(BootCommands.CmdReset, 0);
                // После сброса ответ может не прийти, поэтому не проверяем
            }, token);
        }
    }
}