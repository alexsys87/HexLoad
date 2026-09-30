# HexLoad

Host application for flashing STM32 microcontrollers through a custom UART bootloader,
combined with a firmware file viewer and converter.

Windows, WPF, .NET 8. The matching bootloader firmware (see the [project README](../README.md)),
all with the same protocol:
[`../Firmware/bl_f030`](../Firmware/bl_f030) for STM32F030,
[`../Firmware/bl_f103`](../Firmware/bl_f103) for STM32F103C8 (Blue Pill),
[`../Firmware/bl_f103vc`](../Firmware/bl_f103vc) for STM32F103VCT6 (HY-MiniSTM32V),
[`../Firmware/bl_f401`](../Firmware/bl_f401) and [`../Firmware/bl_f411`](../Firmware/bl_f411)
for STM32F401 / STM32F411 (WeAct Black Pill),
[`../Firmware/bl_at32f403a`](../Firmware/bl_at32f403a) for Artery AT32F403ACGU7 (WeAct BlackPill AT32F403A).

## Features

- One-click programming over UART: `connect -> info -> erase -> program -> reset`, with progress and cancel.
- Device information: bootloader version, product ID, number of application pages, page size.
- Separate Erase and Reset commands.
- Opens **BIN**, **Intel HEX**, **Motorola S-record** and **TI-TXT** files with full extended
  addressing support (record types 02/04, S1/S2/S3) and checksum validation.
- Saves **BIN**, **Intel HEX**, **Motorola S-record**, **TI-TXT** and **C array**, preserving the image base address.
- Load address check: warns if a HEX/S-record image is not linked for the application address,
  and can strip the bootloader area from a combined bootloader + application image.
- Hex view with x1 / x2 / x4 grouping, ASCII column and go-to-address.
- Operation log.

## Building

```
git clone https://github.com/<user>/HexLoad.git
cd HexLoad
dotnet build -c Release
```

Requires the .NET 8 SDK. The only dependency, `System.IO.Ports`, is restored from NuGet.

Single-file publish:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Quick start

1. **Options...** - select the COM port, baud rate (115200) and application address
   (`0x08000800` for STM32F030 / STM32F103 / AT32F403A, `0x08004000` for STM32F401 / STM32F411).
2. **File -> Open...** - open a firmware file. The format is detected by extension, or by content if ambiguous.
3. **Target -> Connect** - the host retries until cancelled. Reset the board: the bootloader waits
   3 seconds for the host after reset, then starts the application.
4. **Target -> Program** - erase, write and reset in one operation.

A raw `.bin` file has no address information and is assumed to start at the application address.
HEX / S-record / TI-TXT images must be linked for the application address (`0x08000800` by default,
`0x08004000` for the Black Pill bootloaders).

## Source layout

| File | Purpose |
|---|---|
| `Bootloader.cs` | Host side of the protocol: packets, timeouts, command sequence |
| `Stm32Crc32.cs` | Software model of the STM32 hardware CRC unit |
| `HexFile.cs` | Intel HEX / S-record / TI-TXT / C array reader and writer, `FirmwareImage` |
| `MainWindow.xaml(.cs)` | Main window: menu, hex view, log, programming |
| `SettingsWindow.xaml(.cs)` | COM port, baud rate and application address |
| `ProgressWindow.xaml(.cs)` | Programming progress with cancel |
| `../Firmware/bl_f030/App/main.c` | Bootloader firmware for STM32F030 (register level, no HAL) |
| `../Firmware/bl_f103/App/main.c` | Bootloader firmware for STM32F103C8 / Blue Pill (register level, no HAL) |
| `../Firmware/bl_f103vc/App/main.c` | Bootloader firmware for STM32F103VCT6 / HY-MiniSTM32V (register level, no HAL) |
| `../Firmware/bl_f401/App/main.c`, `../Firmware/bl_f411/App/main.c` | Bootloader firmware for STM32F401 / STM32F411 / Black Pill (same source) |
| `../Firmware/bl_at32f403a/App/main.c` | Bootloader firmware for Artery AT32F403ACGU7 / BlackPill AT32F403A (register level, no library code) |

Tunable `Bootloader` properties:

| Property | Default | Meaning |
|---|---|---|
| `CommandTimeoutMs` | 1000 | Timeout of a regular command |
| `EraseTimeoutMs` | 30000 | Timeout of the erase command |
| `InterPageDelayMs` | 0 | Delay between pages; not needed, the reply comes after programming and verify |

---

# Bootloader protocol

UART, 115200 baud, 8 data bits, no parity, 1 stop bit, no flow control.
All multi-byte fields are **little-endian**.

## Framing

The bootloader receives through DMA into a 16 + 1024 byte buffer and uses the **USART IDLE-line
interrupt** as the end-of-packet marker. Consequences for the host:

- A packet (header + data) **must be sent as one continuous burst**. A pause longer than one
  character time (about 87 us at 115200) splits it into two packets. HexLoad always sends a whole
  packet with a single `Write()` call.
- Packets longer than 1040 bytes are truncated.
- An IDLE event with no bytes received is ignored.
- The host must wait for the reply before sending the next packet.

## Packet

Every packet is a 16-byte header followed by optional data:

```
 0        4        8        12       16
 +--------+--------+--------+--------+--------------------+
 |  cmd   |  addr  |  size  |  crc   |   data (size)      |
 +--------+--------+--------+--------+--------------------+
   uint32   uint32   uint32   uint32
```

| Field | Description |
|---|---|
| `cmd` | Command code (in replies: command code ORed with the status) |
| `addr` | Offset from the application start address; `CMD_PROG` only, otherwise 0 |
| `size` | Number of data bytes following the header |
| `crc` | See [CRC](#crc) |

## Commands

| Command | Code | Request data | Reply |
|---|---|---|---|
| `CMD_CONNECT` | `0x01` | none | header |
| `CMD_GETINFO` | `0x02` | none | header + 16-byte info |
| `CMD_ERASE` | `0x03` | none | header |
| `CMD_PROG` | `0x04` | one page | header |
| `CMD_RESET` | `0x05` | none | **no reply**, the MCU resets immediately |
| any other | - | - | a single byte `0x80`, no header |

## Status

The reply `cmd` is the request command ORed with a status mask:

| Mask | Value | Meaning |
|---|---|---|
| `CMD_OK` | `0x40` | Success |
| `CMD_ERROR` | `0x80` | CRC mismatch, out-of-range page, erase or verify failure |

Example: a successful `CMD_PROG` (`0x04`) returns `0x44`, a failed one `0x84`.

## Device information

The reply to `CMD_GETINFO` carries 16 data bytes (`size = 16`, `crc` = CRC of these bytes):

```c
struct boot_info {
    uint32_t version;    /* bootloader version, bytes major.minor.patch.build */
    uint32_t product;    /* product identifier */
    uint32_t pages;      /* number of flash pages in the application area */
    uint32_t page_size;  /* page size in bytes */
};
```

The application area size is `pages * page_size`. The reference firmware reports:

| Firmware | Pages | Page size | Layout |
|---|---|---|---|
| `bl_f030` (STM32F030F4) | 14 | 1024 | 16 KB device, 2 KB bootloader, 14 KB application |
| `bl_f103` (STM32F103C8) | 62 | 1024 | 64 KB device, 2 KB bootloader, 62 KB application |
| `bl_f103vc` (STM32F103VCT6) | 254 | 1024 | 256 KB device, 2 KB bootloader (one 2 KB flash page), 254 KB application |
| `bl_f401` (STM32F401CC) | 240 | 1024 | 256 KB device, 16 KB bootloader (sector 0), 240 KB application |
| `bl_f401` / `bl_f411` (STM32F401CE, STM32F411CE) | 496 | 1024 | 512 KB device, 16 KB bootloader (sector 0), 496 KB application |
| `bl_at32f403a` (AT32F403ACGU7) | 1022 | 1024 | 1024 KB device, 2 KB bootloader (one 2 KB sector), 1022 KB application |

On the STM32F103VC the flash is erased in 2 KB pages, on the AT32F403A in 2 KB sectors, on the
STM32F401 / STM32F411 in sectors of 16 to 128 KB. There a "page" is only the write unit of `CMD_PROG`, and `CMD_ERASE` erases all
pages / sectors of the application area.

## Programming sequence

```
host                                    device
 |  CMD_CONNECT                           |
 | -------------------------------------> |
 |                      CMD_CONNECT|OK    |
 | <------------------------------------- |
 |  CMD_GETINFO                           |
 | -------------------------------------> |
 |           CMD_GETINFO|OK + 16 bytes    |
 | <------------------------------------- |
 |  CMD_ERASE                             |
 | -------------------------------------> |
 |                                        |  erase all application pages
 |                        CMD_ERASE|OK    |
 | <------------------------------------- |
 |  CMD_PROG addr=0x0000 + page           |
 | -------------------------------------> |
 |                                        |  check CRC and range, program, verify
 |                         CMD_PROG|OK    |
 | <------------------------------------- |
 |  ... one page at a time ...            |
 |  CMD_RESET                             |
 | -------------------------------------> |
 |                              (no reply)
```

Page rules:

- `addr` is the **offset from the application start address**, not an absolute address.
  The device writes at `APPLICATION_ADDRESS + addr` (`0x08000800 + addr`; `0x08004000 + addr` on the STM32F401 / STM32F411).
- `size` must be non-zero, not larger than `page_size`, and a multiple of 4; `addr` must be a
  multiple of 4; the page must fit into the application area. Otherwise the reply is `0x84`.
- The last, partial page is padded with `0xFF` up to a multiple of 4 bytes.
- The `crc` of `CMD_PROG` is calculated over the transmitted data including the padding.

## CRC

The STM32 hardware CRC algorithm is used:

| Parameter | Value |
|---|---|
| Polynomial | `0x04C11DB7` |
| Initial value | `0xFFFFFFFF` |
| Input / output reflection | none |
| Final XOR | none |
| Unit | 32-bit word read little-endian from memory |

What the `crc` field covers:

| Packet | CRC over | Checked by |
|---|---|---|
| Host request without data | first 12 header bytes (`cmd`, `addr`, `size`) | nobody |
| Host request with data (`CMD_PROG`) | the data | device |
| Device reply without data | all 16 header bytes with `crc` = 0 | nobody |
| Device reply with data (`CMD_GETINFO`) | the 16 info bytes | host |

Check values:

| Input | CRC |
|---|---|
| `00 00 00 00` | `0xC704DD7B` |
| `"1234"` | `0xC2091428` |
| `01 02 ... 0C` | `0x61B3C1AF` |

Portable reference implementation (matches the hardware unit; a trailing 1..3 bytes are packed
into the high-order bytes of a zero-padded word):

```c
#include <stddef.h>
#include <stdint.h>

uint32_t stm32_crc32(const void *buf, size_t len)
{
    const uint32_t poly = 0x04C11DB7u;
    const uint8_t *p = (const uint8_t *)buf;
    uint32_t crc = 0xFFFFFFFFu;
    size_t words = len / 4;
    size_t tail = len & 3u;
    int b;

    while (words--) {
        uint32_t w = (uint32_t)p[0]
                   | ((uint32_t)p[1] << 8)
                   | ((uint32_t)p[2] << 16)
                   | ((uint32_t)p[3] << 24);
        p += 4;
        crc ^= w;
        for (b = 0; b < 32; b++)
            crc = (crc & 0x80000000u) ? ((crc << 1) ^ poly) : (crc << 1);
    }

    if (tail) {
        uint32_t last = 0;
        if (tail == 1)
            last = (uint32_t)p[0] << 24;
        else if (tail == 2)
            last = ((uint32_t)p[0] | ((uint32_t)p[1] << 8)) << 16;
        else
            last = (((uint32_t)p[0] | ((uint32_t)p[1] << 8)) << 8)
                 | ((uint32_t)p[2] << 24);
        crc ^= last;
        for (b = 0; b < 32; b++)
            crc = (crc & 0x80000000u) ? ((crc << 1) ^ poly) : (crc << 1);
    }

    return crc;
}
```

## Timing

- After reset the bootloader waits **3 seconds** for `CMD_CONNECT`. After a successful connect it
  stays in the bootloader until `CMD_RESET`.
- On timeout it starts the application only if the vector table looks valid (initial SP in SRAM,
  reset handler inside the application area). Otherwise it keeps waiting for the host, so a board
  with an empty or partially written application can always be reflashed.
- Host timeouts: 1 s for regular commands, 30 s for erase.

## Bootloader implementation notes

- No interrupts are used: the main loop polls the USART IDLE flag and the SysTick `COUNTFLAG`.
  Only the first four vector table entries (initial SP, Reset, NMI, HardFault) are ever fetched,
  so `startup.c` uses a 4-entry vector table instead of the full one
  (48 entries on the STM32F030, 59 on the STM32F103C8, 76 on the STM32F103VC, 101 / 102 on the
  STM32F401 / STM32F411, 97 on the AT32F403A).
- Size with GCC `-Os -ffunction-sections -fdata-sections -Wl,--gc-sections` (vectors + code):

  | Firmware | Full vector table | 4-entry table |
  |---|---|---|
  | `bl_f030` (Cortex-M0) | 1076 bytes | 900 bytes |
  | `bl_f103` (Cortex-M3) | 1072 bytes | 852 bytes |
  | `bl_f103vc` (Cortex-M3) | 1140 bytes | 852 bytes |
  | `bl_f401` / `bl_f411` (Cortex-M4) | 1448 bytes | 1060 bytes |
  | `bl_at32f403a` (Cortex-M4) | 1332 bytes | 960 bytes |

  With the short table the STM32F030 / STM32F103C8 bootloader fits into one 1 KB page; the application area could then
  start at `0x08000400` (`APPLICATION_ADDRESS`, `BLOCK_SIZE` + 1, application linker script and the
  HexLoad *App address* setting changed accordingly) - the protocol itself does not change.

## Application requirements

All bootloaders start the application with the core running from the internal RC oscillator
(HSI 8 MHz on the STM32F0/F1, HSI 16 MHz on the STM32F4, HICK 8 MHz on the AT32F403A) and the
peripherals used by the bootloader returned to their reset state. The application address is
`0x08000800` on the STM32F030 / STM32F103 / AT32F403A and `0x08004000` on the STM32F401 / STM32F411;
set the HexLoad **App address** accordingly.

STM32F030 (`bl_f030`):

- Link the application for `0x08000800` (flash origin `0x08000800`, length 14 KB).
- The Cortex-M0 has no `VTOR`: the application must copy its vector table to the start of SRAM
  (reserve 192 bytes there), remap SRAM to address 0 via `SYSCFG->CFGR1.MEM_MODE`, and then call
  `__enable_irq()` - the bootloader starts the application with interrupts disabled (`PRIMASK = 1`).

STM32F103 (`bl_f103`, `bl_f103vc`):

- Link the application for `0x08000800` (flash origin `0x08000800`, length 62 KB for the
  STM32F103C8, 254 KB for the STM32F103VC).
- The bootloader sets `SCB->VTOR = 0x08000800` and starts the application with interrupts enabled
  (`PRIMASK = 0`, as after reset). If the application's `SystemInit()` writes `VTOR` itself (older
  CMSIS / CubeF1 versions do it unconditionally), set `VECT_TAB_OFFSET` to `0x800`
  (in newer versions also define `USER_VECT_TAB_ADDRESS`).

STM32F401 / STM32F411 (`bl_f401`, `bl_f411`):

- Link the application for `0x08004000` (flash origin `0x08004000`, length 240 KB for the
  STM32F401CC, 496 KB for the STM32F401CE / STM32F411CE).
- As on the STM32F103, the bootloader sets `SCB->VTOR = 0x08004000` and starts the application with
  interrupts enabled. If `SystemInit()` writes `VTOR`, set `VECT_TAB_OFFSET` to `0x4000`
  (CubeF4: also define `USER_VECT_TAB_ADDRESS`).

AT32F403A (`bl_at32f403a`):

- Link the application for `0x08000800` (flash origin `0x08000800`, length 1022 KB).
- As on the STM32F103, the bootloader sets `SCB->VTOR = 0x08000800` and starts the application with
  interrupts enabled. The Artery `SystemInit()` writes `VTOR` from `VECT_TAB_OFFSET`
  (`system_at32f403a_407.c`); set it to `0x800`.

---

# File formats

## Reading

| Format | Extensions | Notes |
|---|---|---|
| Binary | `.bin`, `.img`, `.rom` | No address; assumed to start at the application address |
| Intel HEX | `.hex`, `.ihx`, `.ihex` | Record types 00, 01, 02, 03, 04, 05; per-line checksum |
| Motorola S-record | `.s19`, `.s28`, `.s37`, `.mot`, `.srec` | S1 / S2 / S3, checksum |
| TI-TXT | `.txt`, `.ti-txt` | Multiple `@address` sections, `q` terminator |

Scattered segments are merged into one contiguous image; gaps are filled with `0xFF`
(erased flash) and reported in the log. Records with errors are skipped and logged, parsing
continues. Images larger than 64 MB are rejected, which protects against building one buffer
from unrelated memory regions.

## Writing

All writers keep the image base address. Intel HEX emits extended linear address records
(type 04) at every 64 KB boundary. S-record picks the smallest sufficient address width
(S1/S2/S3 with matching S9/S8/S7 terminators). TI-TXT writes the `@` directive.
The C array writer also defines address and size macros:

```c
/* Generated by HexLoad */
/* Base address: 0x08000800, size: 2501 bytes */

#define firmware_ADDRESS 0x08000800UL
#define firmware_SIZE 2501UL

const unsigned char firmware[2501] = {
    0x01, 0x08, 0x0F, 0x16, ...
};
```

---

# License

Add the project license (for example MIT) in the `LICENSE` file.
