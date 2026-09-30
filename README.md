# HexLoad

UART bootloader for the STM32F030 and a Windows host application that flashes firmware through it.

| Part | Folder | Description |
|---|---|---|
| Bootloader | [`Firmware/bl_f030`](Firmware/bl_f030) | STM32F030F4 firmware, register level (no HAL), IAR EWARM project |
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
├── Firmware/bl_f030/
│   ├── App/main.c              bootloader: UART + DMA reception, flash erase/program/verify, CRC
│   ├── Drivers/CMSIS/...       STM32F0 device headers
│   └── EWARM/
│       ├── bl_f030.eww/.ewp    IAR EWARM workspace and project (STM32F030F4)
│       ├── bl_f030.icf         linker configuration
│       └── startup.c           4-entry vector table and reset handler
└── WpfApp/                     host application (HexLoad.sln), see WpfApp/README.md
```

## Bootloader

### Target and resources

| Item | Value |
|---|---|
| MCU | STM32F030F4 (Cortex-M0, 16 KB flash, 4 KB SRAM) |
| Clock | HSI 8 MHz after reset (no PLL setup) |
| UART | USART1, PA2 = TX, PA3 = RX (AF1, pull-up on RX), 115200 8N1 |
| Reception | DMA1 Channel 3, 16 + 1024 byte buffer, the USART IDLE line marks the end of a packet |
| CRC | STM32 hardware CRC unit |
| Interrupts | none, the main loop polls the USART and SysTick flags |

### Flash map

| Address | Size | Content |
|---|---|---|
| `0x08000000` - `0x080007FF` | 2 KB | bootloader |
| `0x08000800` - `0x08003FFF` | 14 KB (14 pages x 1 KB) | application |

### Startup behaviour

1. After reset the bootloader waits 3 s for `CMD_CONNECT` from the host.
2. If the host connects, the bootloader stays active until `CMD_RESET`.
3. Otherwise it starts the application. It does this only if the application vector table looks
   valid; if not, it keeps waiting for the host, so a board can always be reflashed.

The application gets control with interrupts disabled and must relocate its vector table to
SRAM. See [application requirements](WpfApp/README.md#application-requirements).

### Vector table and size

The bootloader never enables an interrupt, so the core fetches only the first four vector table
words: initial SP, Reset, NMI and HardFault. `EWARM/startup.c` therefore has a 4-entry table
instead of the full 48-entry Cortex-M0 table (16 system entries plus 32 IRQs). That saves 176 bytes:

| Vector table | Image size (GCC `-Os`, gc-sections) |
|---|---|
| 48 entries | 1076 bytes |
| 4 entries | 900 bytes, fits into one 1 KB flash page |

Figures from other compilers (IAR) differ slightly because of their runtime startup code.
If an interrupt is ever needed in the bootloader, restore the full vector table first.
Otherwise the core would fetch the handler address from the code placed after the table.

The flash map above still reserves 2 KB for the bootloader. To use the saved page for the
application, change `APPLICATION_ADDRESS` to `0x08000400` and `BLOCK_SIZE` to `15` in `main.c`,
the ROM region in `bl_f030.icf`, the application linker script and the *App address* setting
of HexLoad. The protocol does not change.

### Building

Open `Firmware/bl_f030/EWARM/bl_f030.eww` in IAR Embedded Workbench for ARM (the project was last
saved with 9.70) and build. Flash the resulting image at `0x08000000` with any SWD programmer
(ST-LINK, J-Link).

## Host application

Build `WpfApp/HexLoad.sln` with Visual Studio 2022 or `dotnet build` (the .NET 8 SDK is required).
Usage and settings are covered in [WpfApp/README.md](WpfApp/README.md#quick-start).

Typical flashing session:

1. Connect a USB-UART adapter to PA2/PA3 and GND.
2. In HexLoad select the COM port under **Options...**, open the firmware file, then choose **Target -> Connect**.
3. Reset the board: the bootloader answers within its 3 s window.
4. Choose **Target -> Program**. HexLoad erases, writes, verifies and resets the device, which then starts the new application.
