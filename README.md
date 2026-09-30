# HexLoad

UART bootloaders for the STM32F030 and the STM32F103 (Blue Pill), plus a Windows host application
that flashes firmware through them. Both bootloaders use the same protocol.

| Part | Folder | Description |
|---|---|---|
| Bootloader STM32F030 | [`Firmware/bl_f030`](Firmware/bl_f030) | STM32F030F4 firmware, register level (no HAL), IAR EWARM project |
| Bootloader STM32F103 | [`Firmware/bl_f103`](Firmware/bl_f103) | STM32F103C8 (Blue Pill) firmware, register level (no HAL), IAR EWARM project |
| Host application | [`WpfApp`](WpfApp) | Windows WPF (.NET 8) programmer plus firmware file viewer and converter |

**[WpfApp/README.md](WpfApp/README.md)** is the main reference. This file does not repeat it. It covers:

- host application features, building and quick start;
- the full bootloader protocol: [framing](WpfApp/README.md#framing), [packet](WpfApp/README.md#packet),
  [commands](WpfApp/README.md#commands), [status](WpfApp/README.md#status),
  [device information](WpfApp/README.md#device-information),
  [programming sequence](WpfApp/README.md#programming-sequence), [CRC](WpfApp/README.md#crc),
  [timing](WpfApp/README.md#timing);
- [requirements for the user application](WpfApp/README.md#application-requirements);
- supported [file formats](WpfApp/README.md#file-formats).

## Repository layout

```
HexLoad/
├── Firmware/
│   ├── bl_f030/                STM32F030F4 bootloader
│   │   ├── App/main.c          UART + DMA reception, flash erase/program/verify, CRC
│   │   ├── Drivers/CMSIS/...   STM32F0 device headers
│   │   └── EWARM/
│   │       ├── bl_f030.eww/.ewp  IAR EWARM workspace and project
│   │       ├── bl_f030.icf       linker configuration
│   │       └── startup.c         4-entry vector table and reset handler
│   └── bl_f103/                STM32F103C8 (Blue Pill) bootloader, same structure
│       ├── App/main.c
│       ├── Drivers/CMSIS/...   STM32F1 device headers (STMicroelectronics, Apache-2.0)
│       └── EWARM/              bl_f103.eww/.ewp/.ewd, bl_f103.icf, startup.c
└── WpfApp/                     host application (HexLoad.sln), see WpfApp/README.md
```

## Bootloaders

Both bootloaders share the same code structure and protocol; only the hardware layer differs.

### Target and resources

| Item | `bl_f030` | `bl_f103` |
|---|---|---|
| MCU | STM32F030F4 (Cortex-M0, 16 KB flash, 4 KB SRAM) | STM32F103C8 (Cortex-M3, 64 KB flash, 20 KB SRAM) |
| Board | custom | Blue Pill |
| Clock | HSI 8 MHz after reset (no PLL setup) | HSI 8 MHz after reset (no PLL setup) |
| UART | USART1, PA2 = TX, PA3 = RX (AF1, pull-up on RX) | USART1, PA9 = TX, PA10 = RX (pull-up on RX) |
| Reception | DMA1 Channel 3 | DMA1 Channel 5 |
| Flash page | 1 KB | 1 KB |
| Application area | 14 pages | 62 pages |

Common to both: 115200 8N1; a 16 + 1024 byte DMA buffer where the USART IDLE line marks the end of
a packet; the STM32 hardware CRC unit; no interrupts (the main loop polls the USART and SysTick flags).

### Flash map

| Address | `bl_f030` | `bl_f103` |
|---|---|---|
| `0x08000000` - `0x080007FF` | bootloader, 2 KB | bootloader, 2 KB |
| `0x08000800` - ... | application, 14 KB (up to `0x08003FFF`) | application, 62 KB (up to `0x0800FFFF`) |

The application address is the same, so the default HexLoad settings work for both boards; the
host takes the size of the application area from the device (`CMD_GETINFO`).

### Startup behaviour

1. After reset the bootloader waits 3 s for `CMD_CONNECT` from the host.
2. If the host connects, the bootloader stays active until `CMD_RESET`.
3. Otherwise it starts the application. It does this only if the application vector table looks
   valid; if not, it keeps waiting for the host, so a board can always be reflashed.

How the application gets control differs: the STM32F030 has no `VTOR`, so the application must
relocate its vector table to SRAM; on the STM32F103 the bootloader sets `VTOR` itself. See
[application requirements](WpfApp/README.md#application-requirements).

### Vector table and size

The bootloader never enables an interrupt, so the core fetches only the first four vector table
words: initial SP, Reset, NMI and HardFault. `EWARM/startup.c` therefore has a 4-entry table
instead of the full one (STM32F030: 48 entries, 16 system plus 32 IRQs; STM32F103xB: 59 entries,
16 system plus 43 IRQs):

| Firmware | Full vector table | 4-entry table (fits into one 1 KB page) |
|---|---|---|
| `bl_f030` | 1076 bytes | 900 bytes |
| `bl_f103` | 1072 bytes | 852 bytes |

Sizes are measured with GCC `-Os` and gc-sections.

Figures from other compilers (IAR) differ slightly because of their runtime startup code.
If an interrupt is ever needed in the bootloader, restore the full vector table first.
Otherwise the core would fetch the handler address from the code placed after the table.

The flash map above still reserves 2 KB for the bootloader. To use the saved page for the
application, change `APPLICATION_ADDRESS` to `0x08000400` and increase `BLOCK_SIZE` by one in `main.c`,
the ROM region in the `.icf` file, the application linker script and the *App address* setting
of HexLoad. The protocol does not change.

### Building

Open `Firmware/bl_f030/EWARM/bl_f030.eww` or `Firmware/bl_f103/EWARM/bl_f103.eww` in IAR Embedded
Workbench for ARM (the projects were last saved with 9.70) and build. Flash the resulting image at
`0x08000000` with any SWD programmer (ST-LINK, J-Link). The `bl_f103` project is set up for ST-LINK.

### Blue Pill notes

- Keep the BOOT0 jumper at 0 (boot from main flash). BOOT1 is not used.
- Connect the USB-UART adapter to PA9 (TX of the board, to RX of the adapter), PA10 (RX, to TX of
  the adapter) and GND. The USB connector of the board is not used by the bootloader.
- The bootloader uses the 64 KB of the STM32F103C8. For a STM32F103CB (128 KB) change `BLOCK_SIZE`
  in `main.c` to `126` and select the STM32F103CB device in the IAR project options.

## Host application

Build `WpfApp/HexLoad.sln` with Visual Studio 2022 or `dotnet build` (the .NET 8 SDK is required).
Usage and settings are covered in [WpfApp/README.md](WpfApp/README.md#quick-start).

Typical flashing session:

1. Connect a USB-UART adapter to the bootloader UART (STM32F030: PA2/PA3, Blue Pill: PA9/PA10) and GND.
2. In HexLoad select the COM port under **Options...**, open the firmware file, then choose **Target -> Connect**.
3. Reset the board: the bootloader answers within its 3 s window.
4. Choose **Target -> Program**. HexLoad erases, writes, verifies and resets the device, which then starts the new application.
