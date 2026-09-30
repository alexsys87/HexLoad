/*****************************************************************************
 * @file     main.c
 * @brief    Bootloader for Artery AT32F403ACGU7 (Cortex-M4, WeAct Studio
 *           "BlackPill" AT32F403A core board) with UART/DMA/Flash/CRC.
 *
 * @protocol Same protocol as the other bootloaders of this project
 *   (Firmware/bl_*) and the HexLoad host; full description in WpfApp/README.md.
 *   UART, 115200 8N1:
 *   - Host sends a command packet (P_Header + optional data).
 *   - Bootloader replies with a response packet (P_Header + optional data).
 *
 *   Framing:
 *     USART RX runs through DMA into buff[]; the USART IDLE-line flag marks
 *     the end of a packet. The host MUST send header and data as one continuous
 *     burst - a pause longer than one character time splits the packet in two.
 *     An IDLE event with no bytes received is ignored; bytes beyond
 *     DMA_BUFF_SIZE are dropped.
 *
 *   Packet structure (P_Header, 16 bytes, little-endian):
 *     +--------+--------+--------+--------+
 *     |  cmd   |  addr  |  size  |  crc   |
 *     +--------+--------+--------+--------+
 *     cmd  : 32-bit command code.
 *     addr : 32-bit offset from APPLICATION_ADDRESS (WritePage only).
 *     size : 32-bit data size in bytes (WritePage: <= FLASH_PAGE_SIZE, multiple of 4).
 *     crc  : 32-bit CRC (see below).
 *
 *   Command codes:
 *     0x01  Connect          - no data, returns cmd|0x40, disables the timeout.
 *     0x02  GetInfo          - returns P_Info structure (16 bytes).
 *     0x03  Erase            - erases the application area, returns status.
 *     0x04  WritePage        - writes data at APPLICATION_ADDRESS + addr, verifies.
 *     0x05  Reset            - resets the MCU, no reply.
 *     other                  - a single byte 0x80 is sent (no header).
 *
 *   Status bits (ORed with the command code in the response):
 *     0x40  OK               - operation succeeded.
 *     0x80  Error            - operation failed (CRC mismatch, range, verify fail).
 *
 *   P_Info structure (16 bytes):
 *     +--------+--------+--------+--------+
 *     |  ver   | prodId | blkSize| pgSize |
 *     +--------+--------+--------+--------+
 *     ver    : bootloader version (e.g., 0x00000001).
 *     prodId : product identifier (e.g., 0x12345678).
 *     blkSize: number of application pages (1022).
 *     pgSize : page size (1024).
 *
 *   The AT32F403A erases flash in 2 KB sectors. A "page" of the protocol is
 *   the 1 KB write unit of CMD_PROG, so all bootloaders of this project keep
 *   the same 16 + 1024 byte receive buffer; the application area size is
 *   pages * page_size. CMD_ERASE erases the application sectors of bank 1
 *   and the whole bank 2.
 *
 *   CRC calculation (CRC unit with its reset configuration: poly 0x04C11DB7,
 *   init 0xFFFFFFFF, 32-bit words, no reflection, no final XOR - the same
 *   algorithm as the STM32 CRC unit):
 *     - Host requests without data : CRC of the first 12 header bytes (not checked).
 *     - Host requests with data    : CRC of the data (checked).
 *     - Responses without data     : CRC of the 16 header bytes with crc = 0.
 *     - Responses with data        : CRC of the data (P_Info).
 *
 *   Flow:
 *     1. Host sends Connect; bootloader replies with cmd|0x40.
 *     2. Host sends GetInfo; bootloader replies with header + P_Info.
 *     3. Host sends Erase; bootloader erases the application area and replies.
 *     4. For each page the host sends WritePage with data; the bootloader checks
 *        the range and the CRC, programs, verifies and replies with the status.
 *     5. Host sends Reset.
 *     6. If Connect is not received within COMM_TIMEOUT (3000 ms), the bootloader
 *        jumps to the application - only if its vector table looks valid;
 *        otherwise it keeps waiting for the host.
 *
 * @hardware
 *   AT32F403ACGU7: 1024 KB flash in two banks of 512 KB (bank 1 at 0x08000000,
 *   bank 2 at 0x08080000, 2 KB sectors), 96 KB SRAM (224 KB with EOPB0).
 *   Clock: HICK 8 MHz (reset default; the HICK must be on for flash programming).
 *   USART1: PA9 = TX, PA10 = RX (pull-up), no remap. RX DMA: DMA1 Channel 5
 *   (fixed mapping, flexible DMA mapping is off after reset).
 *   BOOT0 = 0 (boot from main flash).
 *   The peripheral registers are accessed through the Artery device and
 *   library headers (register definitions only, no library code is used).
 *
 * @implementation
 *   No interrupts are used: the main loop polls the USART IDLE flag and the
 *   SysTick COUNTFLAG. Only the first four vector table entries (initial SP,
 *   Reset, NMI, HardFault) are ever fetched, so startup.c provides a 4-entry
 *   vector table. Do not enable any interrupt here without restoring the full
 *   vector table.
 *
 * @note The bootloader occupies the first 2 KB flash sector (0x08000000-0x080007FF),
 *       the application starts at 0x08000800 (1022 KB) - the same application
 *       address as on the STM32F030 / STM32F103 bootloaders.
 * @note The application is started in the reset state as far as possible:
 *       peripherals used by the bootloader are reset, the core runs from HICK,
 *       PRIMASK = 0 (interrupts enabled, none of them is pending) and VTOR
 *       points to the application vector table at APPLICATION_ADDRESS.
 *****************************************************************************/

#include "at32f403a_407.h"

#define COMM_TIMEOUT             3000       /* ms */
#define CPU_FREQ                 8000000    /* HICK after reset */
#define BAUD_RATE                115200
#define APPLICATION_ADDRESS      0x08000800 /* one 2 KB sector for bootloader */
#define FLASH_PAGE_SIZE          1024       /* write unit of the protocol */
#define FLASH_SECTOR_SIZE        2048       /* erase unit */
#define FLASH_BANK2_START        0x08080000
#define FLASH_END                0x08100000 /* 1024 KB */
#define BLOCK_SIZE               ((FLASH_END - APPLICATION_ADDRESS) / FLASH_PAGE_SIZE)  /* 1022 */
#define APPLICATION_SIZE         (FLASH_PAGE_SIZE * BLOCK_SIZE)
#define APPLICATION_FLASH_END    (APPLICATION_ADDRESS + APPLICATION_SIZE)
#define BOOT_VERSION             0x00000001
#define PRODUCT_ID               0x12345678
#define BOOT_HEADER_SIZE         sizeof(P_Header)
#define DMA_BUFF_SIZE            (FLASH_PAGE_SIZE + BOOT_HEADER_SIZE)

#define SRAM_START               0x20000000
#define SRAM_SIZE_MAX            (224 * 1024)  /* largest SRAM configuration (EOPB0) */

/* Register bits (AT32F403A/407 reference manual; the Artery headers only
   provide bit-field structures, whole-register access is used here) */
#define USART_STS_IDLEF          (1UL << 4)
#define USART_STS_TDBE           (1UL << 7)
#define USART_CTRL1_REN          (1UL << 2)
#define USART_CTRL1_TEN          (1UL << 3)
#define USART_CTRL1_UEN          (1UL << 13)
#define USART_CTRL3_DMAREN       (1UL << 6)

#define DMA_CTRL_CHEN            (1UL << 0)
#define DMA_CTRL_MINCM           (1UL << 7)

#define CRC_CTRL_RST             (1UL << 0)

#define FLASH_STS_OBF            (1UL << 0)
#define FLASH_STS_PRGMERR        (1UL << 2)
#define FLASH_STS_EPPERR         (1UL << 4)
#define FLASH_STS_ODF            (1UL << 5)
#define FLASH_STS_CLEAR          (FLASH_STS_PRGMERR | FLASH_STS_EPPERR | FLASH_STS_ODF)
#define FLASH_CTRL_FPRGM         (1UL << 0)
#define FLASH_CTRL_SECERS        (1UL << 1)
#define FLASH_CTRL_BANKERS       (1UL << 2)
#define FLASH_CTRL_ERSTR         (1UL << 6)
#define FLASH_CTRL_OPLK          (1UL << 7)

#define CRM_AHBEN_DMA1           (1UL << 0)
#define CRM_AHBEN_CRC            (1UL << 6)
#define CRM_APB2_GPIOA           (1UL << 2)    /* apb2en / apb2rst */
#define CRM_APB2_USART1          (1UL << 14)   /* apb2en / apb2rst */

#define GPIO_CFGHR_RESET         0x44444444    /* all pins floating inputs */

/* GPIOA CFGHR fields: PA9 = mux push-pull output, moderate drive; PA10 = input with pull-up/down */
#define GPIO_CFGHR_PA9_MUX_PP    (0xAUL << 4)
#define GPIO_CFGHR_PA10_IN_PULL  (0x8UL << 8)
#define GPIO_CFGHR_PA9_PA10_MASK (0xFFUL << 4)
#define GPIO_PIN10               (1UL << 10)

#ifndef FLASH_UNLOCK_KEY1
#define FLASH_UNLOCK_KEY1 0x45670123
#endif
#ifndef FLASH_UNLOCK_KEY2
#define FLASH_UNLOCK_KEY2 0xCDEF89AB
#endif

/* Status, control and address registers of one flash bank: bank 1 at
   FLASH->sts, bank 2 at FLASH->sts2 - same layout, same bits */
typedef struct {
    __IO uint32_t sts;
    __IO uint32_t ctrl;
    __IO uint32_t addr;
} FlashBank;

#define FLASH_BANK1              ((FlashBank *)&FLASH->sts)
#define FLASH_BANK2              ((FlashBank *)&FLASH->sts2)

enum eCommand {
    Command_Connect    = 0x01,
    Command_GetInfo    = 0x02,
    Command_Erase      = 0x03,
    Command_WritePage  = 0x04,
    Command_Reset      = 0x05,

    Command_OK         = 0x40,
    Command_Error      = 0x80,
};

typedef struct {
    uint32_t cmd;
    uint32_t addr;
    uint32_t size;
    uint32_t crc;
} P_Header;

typedef struct {
    uint32_t v;
    uint32_t p;
    uint32_t b;
    uint32_t s;
} P_Info;

static const P_Info bootInfo = { BOOT_VERSION, PRODUCT_ID, BLOCK_SIZE, FLASH_PAGE_SIZE };
static const uint32_t erasedWord = 0xFFFFFFFF;

/* Accessed as P_Header and uint32_t words; CRC->dt takes whole words */
static uint8_t buff[DMA_BUFF_SIZE] __attribute__((aligned(4)));

/**
  * @brief  Computes the 32-bit CRC of a buffer of 32-bit words.
  *         The reset bit reloads the initial value (IDT register, 0xFFFFFFFF).
  * @param  pBuffer: word-aligned data
  * @param  len: length in bytes (processed in whole words)
  * @retval 32-bit CRC
  */
static uint32_t CRC_Calc(const uint32_t *pBuffer, uint32_t len)
{
    CRC->ctrl = CRC_CTRL_RST;
    for (; len; len -= 4)
        CRC->dt = *pBuffer++;
    return CRC->dt;
}

/* Bank that holds the given flash address */
static FlashBank *FlashBankOf(uint32_t addr)
{
    return addr < FLASH_BANK2_START ? FLASH_BANK1 : FLASH_BANK2;
}

static void FlashWaitBusy(FlashBank *bank)
{
    while (bank->sts & FLASH_STS_OBF) {}
}

/* Unlock is always called with both banks locked: a wrong key sequence locks
   the flash controller until the next reset. Status flags left by a previous
   operation are cleared. */
static void FlashUnlock(void)
{
    FLASH->unlock  = FLASH_UNLOCK_KEY1;
    FLASH->unlock  = FLASH_UNLOCK_KEY2;
    FLASH->unlock2 = FLASH_UNLOCK_KEY1;
    FLASH->unlock2 = FLASH_UNLOCK_KEY2;
    FLASH_BANK1->sts = FLASH_STS_CLEAR;
    FLASH_BANK2->sts = FLASH_STS_CLEAR;
}

/* Clears FPRGM/SECERS/BANKERS and sets OPLK in one write per bank */
static void FlashLock(void)
{
    FLASH_BANK1->ctrl = FLASH_CTRL_OPLK;
    FLASH_BANK2->ctrl = FLASH_CTRL_OPLK;
}

/**
  * @brief  Compares flash with a source buffer.
  * @param  step: 1 - compare with src[], 0 - compare every word with *src
  * @retval 1 if equal, 0 otherwise
  */
static uint32_t FlashMatches(const uint32_t *src, uint32_t addr, uint32_t len, uint32_t step)
{
    const volatile uint32_t *p = (const volatile uint32_t *)addr;
    for (; len; len -= 4, src += step)
    {
        if (*p++ != *src)
            return 0;
    }
    return 1;
}

static void UART_send(const void *data, uint32_t len)
{
    const uint8_t *d = (const uint8_t *)data;
    for (; len; --len)
    {
        while (!(USART1->sts & USART_STS_TDBE)) {}
        USART1->dt = *d++;
    }
}

/* Header-only reply: CRC over the 16 header bytes with crc = 0 */
static void SendStatus(uint32_t cmd)
{
    P_Header *pkt = (P_Header *)buff;

    pkt->cmd  = cmd;
    pkt->addr = 0;
    pkt->size = 0;
    pkt->crc  = 0;
    pkt->crc  = CRC_Calc((const uint32_t *)pkt, sizeof(P_Header));

    UART_send(buff, sizeof(P_Header));
}

static void GetInfo(void)
{
    P_Header *pkt = (P_Header *)buff;

    pkt->cmd  = (uint32_t)Command_GetInfo | (uint32_t)Command_OK;
    pkt->addr = 0;
    pkt->size = sizeof(P_Info);
    pkt->crc  = CRC_Calc((const uint32_t *)&bootInfo, sizeof(P_Info));

    UART_send(buff, sizeof(P_Header));
    UART_send(&bootInfo, sizeof(P_Info));
}

/* Erases the application sectors of bank 1 (sector 0 holds the bootloader)
   and the whole bank 2 with one bank erase */
static uint32_t Erase(void)
{
    FlashBank *bank = FLASH_BANK1;

    FlashUnlock();
    for (uint32_t p = APPLICATION_ADDRESS; p < FLASH_BANK2_START; p += FLASH_SECTOR_SIZE)
    {
        bank->ctrl = FLASH_CTRL_SECERS;
        bank->addr = p;
        bank->ctrl = FLASH_CTRL_SECERS | FLASH_CTRL_ERSTR;
        FlashWaitBusy(bank);
    }

    bank = FLASH_BANK2;
    bank->ctrl = FLASH_CTRL_BANKERS;
    bank->ctrl = FLASH_CTRL_BANKERS | FLASH_CTRL_ERSTR;
    FlashWaitBusy(bank);
    FlashLock();

    return FlashMatches(&erasedWord, APPLICATION_ADDRESS, APPLICATION_SIZE, 0)
           ? Command_OK : Command_Error;
}

static uint32_t WritePage(void)
{
    const P_Header *pkt  = (const P_Header *)buff;
    const uint32_t *data = (const uint32_t *)&buff[BOOT_HEADER_SIZE];
    uint32_t offset = pkt->addr;
    uint32_t len    = pkt->size;

    /* The page must fit into the receive buffer and the application area,
       and be word aligned. Checked before the CRC so that CRC_Calc never
       reads beyond buff[]. */
    if (len == 0 || len > FLASH_PAGE_SIZE || ((len | offset) & 3) != 0 ||
        offset > APPLICATION_SIZE - len ||
        CRC_Calc(data, len) != pkt->crc)
    {
        return Command_Error;
    }

    uint32_t addr = APPLICATION_ADDRESS + offset;
    const uint32_t *src = data;
    volatile uint32_t *dst = (volatile uint32_t *)addr;

    FlashUnlock();
    FLASH_BANK1->ctrl = FLASH_CTRL_FPRGM;     /* FPRGM stays set for the whole page, */
    FLASH_BANK2->ctrl = FLASH_CTRL_FPRGM;     /* in both banks: a page may cross 0x08080000 */
    for (uint32_t n = len; n; n -= 4)
    {
        FlashBank *bank = FlashBankOf((uint32_t)dst);
        *dst++ = *src++;                      /* 32-bit word programming */
        FlashWaitBusy(bank);
    }
    FlashLock();

    return FlashMatches(data, addr, len, 1) ? Command_OK : Command_Error;
}

/**
  * @brief  Checks that the application vector table looks valid:
  *         initial SP inside SRAM, reset handler inside the application area (Thumb).
  * @retval 1 if the application can be started, 0 otherwise
  */
static uint32_t IsAppValid(void)
{
    const volatile uint32_t *app = (const volatile uint32_t *)APPLICATION_ADDRESS;
    uint32_t stack = app[0];
    uint32_t start = app[1];

    return stack > SRAM_START && stack <= SRAM_START + SRAM_SIZE_MAX && (stack & 3) == 0 &&
           (start & 1) != 0 && start >= APPLICATION_ADDRESS && start < APPLICATION_FLASH_END;
}

static void InitHardware(void)
{
    /* Clocks: DMA1, CRC, GPIOA, USART1 */
    CRM->ahben  |= CRM_AHBEN_DMA1 | CRM_AHBEN_CRC;
    CRM->apb2en |= CRM_APB2_GPIOA | CRM_APB2_USART1;
    (void)CRM->apb2en;                    /* read back: the clocks are running before the first access */

    /* PA9 = USART1_TX (mux push-pull), PA10 = USART1_RX (input, pull-up via ODT).
       Runs right after reset, so the registers hold their reset values and can
       be written directly. The pull-up keeps an unconnected RX line idle.
       SWD pins PA13/PA14 (CFGHR) are owned by the debug port and not affected. */
    GPIOA->cfghr = (GPIO_CFGHR_RESET & ~GPIO_CFGHR_PA9_PA10_MASK)
                   | GPIO_CFGHR_PA9_MUX_PP | GPIO_CFGHR_PA10_IN_PULL;
    GPIOA->odt   = GPIO_PIN10;

    /* DMA1 Channel5: USART1_RX -> buff, memory increment, 8-bit */
    DMA1_CHANNEL5->paddr = (uint32_t)&(USART1->dt);
    DMA1_CHANNEL5->maddr = (uint32_t)buff;
    DMA1_CHANNEL5->dtcnt = DMA_BUFF_SIZE;
    DMA1_CHANNEL5->ctrl  = DMA_CTRL_MINCM | DMA_CTRL_CHEN;

    USART1->baudr = CPU_FREQ / BAUD_RATE; /* 69: 115942 baud, +0.6% */
    USART1->ctrl3 = USART_CTRL3_DMAREN;
    USART1->ctrl1 = USART_CTRL1_TEN | USART_CTRL1_REN | USART_CTRL1_UEN;

    /* The first IDLE event after enabling the receiver carries no data; it is
       skipped in main(). Not waiting for it here: with the RX line held low
       (e.g. an unpowered USB-UART adapter) it never comes, and the bootloader
       would never start the application. */

    /* SysTick: 1 ms period, polled via COUNTFLAG (no interrupt) */
    SysTick->LOAD = CPU_FREQ / 1000 - 1;
    SysTick->VAL  = 0;
    SysTick->CTRL = SysTick_CTRL_CLKSOURCE_Msk | SysTick_CTRL_ENABLE_Msk;
}

static void DeInitHardware(void)
{
    SysTick->CTRL = 0;
    SysTick->LOAD = 0;
    SysTick->VAL  = 0;

    DMA1_CHANNEL5->ctrl = 0;

    /* Return USART1 and GPIOA to their reset state, stop the clocks
       enabled by the bootloader */
    CRM->apb2rst |= CRM_APB2_USART1 | CRM_APB2_GPIOA;
    CRM->apb2rst &= ~(CRM_APB2_USART1 | CRM_APB2_GPIOA);

    CRM->apb2en &= ~(CRM_APB2_GPIOA | CRM_APB2_USART1);
    CRM->ahben  &= ~(CRM_AHBEN_DMA1 | CRM_AHBEN_CRC);
}

__attribute__((noreturn))
static void jumpToApp(void)
{
    DeInitHardware();

    const volatile uint32_t *app = (const volatile uint32_t *)APPLICATION_ADDRESS;
    uint32_t stack = app[0];  // Initial MSP from the application vector table
    uint32_t start = app[1];  // Application reset handler address

    /* Cortex-M4 has VTOR: interrupts of the application go straight to its own
       vector table. No interrupt was ever enabled here, so none is pending. */
    SCB->VTOR = APPLICATION_ADDRESS;
    __DSB();
    __ISB();

    __set_MSP(stack);         // Switch to the application stack (CMSIS)

    // Jump to the application reset handler
    __asm volatile("bx %0" : : "r"(start));
    __builtin_unreachable();
}

void exit(int status) {
    (void)status;
    while(1);
}

int main(void) {

    const P_Header *pkt = (const P_Header *)buff;
    uint32_t timeout = COMM_TIMEOUT;   /* ms left; 0 = timeout disabled */

    InitHardware();

    while(1) {

        /* Startup timeout: runs until Connect or until the application is started */
        if (timeout && (SysTick->CTRL & SysTick_CTRL_COUNTFLAG_Msk) && --timeout == 0) {
            if (IsAppValid())
                jumpToApp();
            /* no valid application: the timeout stays disabled, keep waiting for the host */
        }

        /* End of packet: restart DMA for the next one, then process this one */
        if (USART1->sts & USART_STS_IDLEF) {
            /* The STS read above followed by this DT read clears IDLEF and ROERR.
               It also drops a byte left in DT after an overlong packet (DMA
               stopped at DTCNT = 0), otherwise it would become the first byte
               of the next packet. */
            (void)USART1->dt;

            /* Nothing received (the first idle frame after enabling the
               receiver): not a packet, buff[] still holds old data */
            if (DMA1_CHANNEL5->dtcnt == DMA_BUFF_SIZE)
                continue;

            DMA1_CHANNEL5->ctrl  = DMA_CTRL_MINCM;
            DMA1_CHANNEL5->dtcnt = DMA_BUFF_SIZE;
            DMA1_CHANNEL5->ctrl  = DMA_CTRL_MINCM | DMA_CTRL_CHEN;

            switch (pkt->cmd) {
                case Command_Connect:
                    timeout = 0;
                    SendStatus((uint32_t)Command_Connect | (uint32_t)Command_OK);
                    break;
                case Command_GetInfo:
                    GetInfo();
                    break;
                case Command_Erase:
                    SendStatus((uint32_t)Command_Erase | Erase());
                    break;
                case Command_WritePage:
                    SendStatus((uint32_t)Command_WritePage | WritePage());
                    break;
                case Command_Reset:
                    NVIC_SystemReset();
                    break;
                default: {
                    static const uint8_t err = Command_Error;
                    UART_send(&err, 1);
                }
            }
        }
    }
}
