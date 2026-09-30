# HexLoad

UART bootloaders for STM32 and Artery AT32 microcontrollers plus a Windows host application that flashes firmware
through them. All bootloaders use the same protocol:

| Part | Folder | Target |
|---|---|---|
| Bootloader STM32F030 | [`Firmware/bl_f030`](Firmware/bl_f030) | STM32F030F4 |
| Bootloader STM32F103 | [`Firmware/bl_f103`](Firmware/bl_f103) | STM32F103C8, "Blue Pill" board |
| Bootloader STM32F103VC | [`Firmware/bl_f103vc`](Firmware/bl_f103vc) | STM32F103VCT6, HY-MiniSTM32V board |
| Bootloader STM32F401 | [`Firmware/bl_f401`](Firmware/bl_f401) | STM32F401CC / CE, WeAct Studio "Black Pill" board |
| Bootloader STM32F411 | [`Firmware/bl_f411`](Firmware/bl_f411) | STM32F411CE, WeAct Studio "Black Pill" board |
| Bootloader AT32F403A | [`Firmware/bl_at32f403a`](Firmware/bl_at32f403a) | Artery AT32F403ACGU7, WeAct Studio "BlackPill" AT32F403A core board |
| Host application | [`WpfApp`](WpfApp) | Windows WPF (.NET 8) programmer plus firmware file viewer and converter |

Every bootloader is register-level C code (no HAL) with an IAR EWARM project.

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
│   │   ├── Drivers/CMSIS/Device/ST/STM32F0xx/
│   │   │                       STM32F0 device headers (STMicroelectronics, Apache-2.0)
│   │   └── EWARM/
│   │       ├── bl_f030.eww/.ewp/.ewd  IAR EWARM workspace, project, debugger settings
│   │       ├── bl_f030.icf            linker configuration
│   │       └── startup.c              4-entry vector table and reset handler
│   ├── bl_f103/                STM32F103C8 (Blue Pill) bootloader, same structure (STM32F1xx headers)
│   ├── bl_f103vc/              STM32F103VCT6 (HY-MiniSTM32V) bootloader, same structure (STM32F1xx headers)
│   ├── bl_f401/                STM32F401 (Black Pill) bootloader, same structure (STM32F4xx headers)
│   ├── bl_f411/                STM32F411 (Black Pill) bootloader, same structure (STM32F4xx headers)
│   └── bl_at32f403a/           AT32F403ACGU7 (WeAct BlackPill AT32F403A) bootloader, same structure
│       ├── App/                main.c + at32f403a_407_conf.h (Artery library configuration)
│       └── Drivers/            Artery device headers and the register definitions of the
│                               used peripherals (AT32F403A_407 library v2.2.2, BSD-3-Clause)
└── WpfApp/                     host application (HexLoad.sln), see WpfApp/README.md
```

`bl_f401/App/main.c` and `bl_f411/App/main.c` are the same source. The two projects differ in the
selected device, the CMSIS device define and the RAM size in the linker configuration.

CMSIS device headers: `cmsis_device_f0` v2.3.7, `cmsis_device_f1` v4.3.5, `cmsis_device_f4`
v2.6.11 from [STMicroelectronics](https://github.com/STMicroelectronics). The Cortex-M core
headers (`core_cm0.h`, `core_cm3.h`, `core_cm4.h`) come from the IAR toolkit.
Artery headers: `AT32F403A_407_Firmware_Library` v2.2.2 from [ArteryTek](https://github.com/ArteryTek).

## Bootloaders

All bootloaders share the same code structure and protocol; only the hardware layer differs.

### Target and resources

| Item | `bl_f030` | `bl_f103` | `bl_f103vc` | `bl_f401` / `bl_f411` | `bl_at32f403a` |
|---|---|---|---|---|---|
| MCU | STM32F030F4, Cortex-M0 | STM32F103C8, Cortex-M3 | STM32F103VCT6, Cortex-M3 | STM32F401CC/CE, STM32F411CE, Cortex-M4 | AT32F403ACGU7, Cortex-M4 |
| Flash / SRAM | 16 KB / 4 KB | 64 KB / 20 KB | 256 KB / 48 KB | 256 or 512 KB / 64, 96 or 128 KB | 1024 KB (2 banks) / 96 KB |
| Board | custom | Blue Pill | HY-MiniSTM32V | WeAct Studio Black Pill | WeAct Studio BlackPill AT32F403A |
| Clock | HSI 8 MHz | HSI 8 MHz | HSI 8 MHz | HSI 16 MHz | HICK 8 MHz |
| UART | USART1, PA2 = TX, PA3 = RX | USART1, PA9 = TX, PA10 = RX | USART1, PA9 = TX, PA10 = RX (on-board PL2303) | USART1, PA9 = TX, PA10 = RX | USART1, PA9 = TX, PA10 = RX |
| RX DMA | DMA1 Channel 3 | DMA1 Channel 5 | DMA1 Channel 5 | DMA2 Stream 2 Channel 4 | DMA1 Channel 5 |
| Erase unit | 1 KB page | 1 KB page | 2 KB page | 16 / 64 / 128 KB sector | 2 KB sector |
| Bootloader area | 2 KB | 2 KB | 2 KB | sector 0, 16 KB | 2 KB |
| Application address | `0x08000800` | `0x08000800` | `0x08000800` | `0x08004000` | `0x08000800` |
| Application area | 14 KB (14 pages) | 62 KB (62 pages) | 254 KB (254 pages) | 240 KB or 496 KB (240 or 496 pages) | 1022 KB (1022 pages) |

A protocol "page" is always the 1 KB write unit of `CMD_PROG`. Where the erase unit is larger
(STM32F103VC, STM32F4, AT32F403A), `CMD_ERASE` erases the application area in the native pages or sectors.

Common to all: 115200 8N1 with a pull-up on RX; a 16 + 1024 byte DMA buffer where the USART IDLE
line marks the end of a packet; the hardware CRC unit (the same algorithm on STM32 and AT32); no interrupts (the main loop polls
the USART and SysTick flags); the core clock after reset is used as is (no PLL setup).

### Flash map

| Address | `bl_f030` | `bl_f103` | `bl_f103vc` | `bl_f401` / `bl_f411` | `bl_at32f403a` |
|---|---|---|---|---|---|
| `0x08000000` | bootloader, 2 KB | bootloader, 2 KB | bootloader, 2 KB (one page) | bootloader, sector 0 (16 KB) | bootloader, 2 KB (one sector) |
| `0x08000800` | application, 14 KB (up to `0x08003FFF`) | application, 62 KB (up to `0x0800FFFF`) | application, 254 KB (up to `0x0803FFFF`) | - | application, 1022 KB (up to `0x080FFFFF`, bank 2 from `0x08080000`) |
| `0x08004000` | - | - | - | application, sectors 1..5 (256 KB flash) or 1..7 (512 KB flash) | - |

The host takes the size of the application area from the device (`CMD_GETINFO`), so it needs no
per-board settings except the **App address** in HexLoad **Options...**. It is `0x08000800` by default.
For the Black Pill set it to **`0x08004000`**. The setting only drives the load address check of
HEX / S-record images: the bootloader always writes at its own application address.

### STM32F401 / STM32F411 specifics

- The flash of the STM32F4 is divided into sectors (4 x 16 KB, 1 x 64 KB, then 128 KB each), and
  the smallest one is 16 KB. The bootloader therefore takes the whole sector 0 and the application
  starts at `0x08004000` (sector 1).
- A protocol "page" is the 1 KB write unit of `CMD_PROG`, not an erase unit. The bootloader
  reports `pages = application area / 1 KB`. `CMD_ERASE` erases all application sectors.
  On a 512 KB device this takes a few seconds, well below the 30 s erase timeout of the host.
- The flash size is read from the `F_SIZE` register at startup, so one binary works on the
  STM32F401CC (256 KB) as well as on the STM32F401CE / STM32F411CE (512 KB).
- Flash programming uses 32-bit parallelism, which requires VDD 2.7..3.6 V (the Black Pill runs at 3.3 V).

### Startup behaviour

1. After reset the bootloader waits 3 s for `CMD_CONNECT` from the host.
2. If the host connects, the bootloader stays active until `CMD_RESET`.
3. Otherwise it starts the application. It does this only if the application vector table looks
   valid; if not, it keeps waiting for the host, so a board can always be reflashed.

How the application gets control depends on the core. The STM32F030 (Cortex-M0) has no `VTOR`, so
the application must relocate its vector table to SRAM. On the STM32F103, STM32F103VC,
STM32F401/F411 and AT32F403A the bootloader sets `VTOR` itself. See [application requirements](WpfApp/README.md#application-requirements).

### Vector table and size

The bootloader never enables an interrupt, so the core fetches only the first four vector table
words: initial SP, Reset, NMI and HardFault. `EWARM/startup.c` therefore has a 4-entry table
instead of the full one (STM32F030: 48 entries, STM32F103xB: 59, STM32F103xE: 76, STM32F401: 101,
STM32F411: 102, AT32F403A: 97):

| Firmware | Full vector table | 4-entry table |
|---|---|---|
| `bl_f030` | 1076 bytes | 900 bytes, fits into one 1 KB page |
| `bl_f103` | 1072 bytes | 852 bytes, fits into one 1 KB page |
| `bl_f103vc` | 1140 bytes | 852 bytes (the flash page is 2 KB) |
| `bl_f401` / `bl_f411` | 1448 bytes | 1060 bytes (sector 0 is 16 KB) |
| `bl_at32f403a` | 1332 bytes | 960 bytes (the flash sector is 2 KB) |

Sizes are measured with GCC `-Os` and gc-sections.

Figures from other compilers (IAR) differ slightly because of their runtime startup code.
If an interrupt is ever needed in the bootloader, restore the full vector table first.
Otherwise the core would fetch the handler address from the code placed after the table.

The flash map above still reserves 2 KB for the STM32F030 and STM32F103 bootloaders. To use the
saved page for the application, change `APPLICATION_ADDRESS` to `0x08000400` and increase
`BLOCK_SIZE` by one in `main.c`, then update the ROM region in the `.icf` file, the application
linker script and the *App address* setting of HexLoad. The protocol does not change.

### Building

Open `Firmware/<bl_xxx>/EWARM/<bl_xxx>.eww` in IAR Embedded Workbench for ARM (the projects were
last saved with 9.70) and build. Flash the resulting image at `0x08000000` with any SWD programmer
(ST-LINK, J-Link). The `bl_f103`, `bl_f103vc`, `bl_f401` and `bl_f411` projects are set up for ST-LINK,
`bl_at32f403a` for CMSIS-DAP (Artery AT-Link, DAPLink).

### Blue Pill notes

- Keep the BOOT0 jumper at 0 (boot from main flash). BOOT1 is not used.
- Connect the USB-UART adapter to PA9 (TX of the board, to RX of the adapter), PA10 (RX, to TX of
  the adapter) and GND. The USB connector of the board is not used by the bootloader.
- The bootloader uses the 64 KB of the STM32F103C8. For a STM32F103CB (128 KB) change `BLOCK_SIZE`
  in `main.c` to `126` and select the STM32F103CB device in the IAR project options.

### HY-MiniSTM32V notes

- USART1 (PA9 = TX, PA10 = RX) is connected to the on-board PL2303 USB-UART converter: connect
  the board to the PC with a USB cable and select the PL2303 COM port in HexLoad. No external
  adapter is needed.
- BOOT0 = 0 (boot from main flash).
- The flash page of the high-density STM32F103 is 2 KB, so the 2 KB bootloader area is exactly
  one page and the application starts at `0x08000800`, as on the other STM32F0/F1 bootloaders.
- The bootloader itself is written through the JTAG/SWD connector (ST-LINK, J-Link) or with the ST
  ROM bootloader over the same PL2303 port (BOOT0 = 1, then STM32CubeProgrammer or `stm32flash`).

### Black Pill notes

- Use `bl_f401` for boards with the STM32F401CCU6 or STM32F401CEU6 and `bl_f411` for the STM32F411CEU6.
  For a STM32F401CE board, select STM32F401CE in the IAR project and use the `STM32F401xE` define
  (the header is included). The binary built for the CC works too, because the flash size is detected at runtime.
- Connect the USB-UART adapter to PA9 (TX of the board, to RX of the adapter), PA10 (RX, to TX of
  the adapter) and GND. The USB-C connector is not used by the bootloader.
- Normal start: BOOT0 released. The bootloader window is 3 s after pressing NRST.
- Without an SWD probe the bootloader itself can be written over USB with the ST ROM DFU loader:
  hold BOOT0, press and release NRST, release BOOT0, then program the image at `0x08000000` with
  STM32CubeProgrammer or `dfu-util`.

### BlackPill AT32F403A notes

- IAR EWARM needs the Artery device support package (the AT32 IAR add-on from
  [ArteryTek](https://www.arterychip.com) or the `AT32F403A_407_Firmware_Library` download page),
  which adds the ArteryTek devices, their debugger descriptions and flash loaders. Without it the
  project still builds after selecting a generic Cortex-M4 core in the project options.
- The AT32F403A peripherals used by the bootloader (USART, DMA, CRC, flash controller, GPIO) are
  register compatible with the STM32F103; the flash has two banks of 512 KB with separate
  control registers. `CMD_ERASE` erases the application sectors of bank 1 one by one and bank 2
  with a single bank erase.
- SRAM is 96 KB by default (224 KB after changing the EOPB0 user option); the bootloader accepts
  an initial stack pointer anywhere in the 224 KB range.
- Connect the USB-UART adapter to PA9 (TX of the board, to RX of the adapter), PA10 (RX, to TX of
  the adapter) and GND. The USB-C connector is not used by the bootloader.
- Normal start: BOOT0 released. The bootloader window is 3 s after pressing NRST.
- Without an SWD probe the bootloader itself can be written with the Artery ROM bootloader: hold
  BOOT0, press and release NRST, then use the Artery ISP Programmer over USB (DFU) or USART1.

## Host application

Build `WpfApp/HexLoad.sln` with Visual Studio 2022 or `dotnet build` (the .NET 8 SDK is required).
Usage and settings are covered in [WpfApp/README.md](WpfApp/README.md#quick-start).

Typical flashing session:

1. Connect a USB-UART adapter to the bootloader UART (STM32F030: PA2/PA3, Blue Pill, Black Pill and
   BlackPill AT32F403A: PA9/PA10) and GND.
   The HY-MiniSTM32V only needs a USB cable to its PL2303 port.
2. In HexLoad open **Options...**, select the COM port and check the **App address**
   (`0x08000800` for STM32F030, Blue Pill, HY-MiniSTM32V and BlackPill AT32F403A,
   `0x08004000` for the STM32F4 Black Pill).
3. Open the firmware file, then choose **Target -> Connect**.
4. Reset the board: the bootloader answers within its 3 s window.
5. Choose **Target -> Program**. HexLoad erases, writes, verifies and resets the device, which then starts the new application.
