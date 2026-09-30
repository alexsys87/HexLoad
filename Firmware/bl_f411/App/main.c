/*****************************************************************************
 * @file     main.c
 * @brief    Bootloader for STM32F401 / STM32F411 (Cortex-M4, WeAct Studio
 *           "Black Pill" boards) with UART/DMA/Flash/CRC.
 *           The same source is used by Firmware/bl_f401 and Firmware/bl_f411.
 *
 * @protocol Same protocol as the STM32F030 / STM32F103 bootloaders and the
 *   HexLoad host; full description in WpfApp/README.md.
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
 *     0x03  Erase            - erases all application sectors, returns status.
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
 *     blkSize: number of application pages (240 for 256 KB, 496 for 512 KB flash).
 *     pgSize : page size (1024).
 *
 *   The STM32F4 flash is divided into sectors of 16/64/128 KB, not pages.
 *   A "page" of the protocol is the 1 KB write unit of CMD_PROG; the
 *   application area size is pages * page_size. CMD_ERASE erases every
 *   sector of the application area.
 *
 *   CRC calculation (STM32 CRC unit: poly 0x04C11DB7, init 0xFFFFFFFF,
 *   32-bit words, no reflection, no final XOR):
 *     - Host requests without data : CRC of the first 12 header bytes (not checked).
 *     - Host requests with data    : CRC of the data (checked).
 *     - Responses without data     : CRC of the 16 header bytes with crc = 0.
 *     - Responses with data        : CRC of the data (P_Info).
 *
 *   Flow:
 *     1. Host sends Connect; bootloader replies with cmd|0x40.
 *     2. Host sends GetInfo; bootloader replies with header + P_Info.
 *     3. Host sends Erase; bootloader erases all application sectors and replies.
 *     4. For each page the host sends WritePage with data; the bootloader checks
 *        the range and the CRC, programs, verifies and replies with the status.
 *     5. Host sends Reset.
 *     6. If Connect is not received within COMM_TIMEOUT (3000 ms), the bootloader
 *        jumps to the application - only if its vector table looks valid;
 *        otherwise it keeps waiting for the host.
 *
 * @hardware
 *   STM32F401CC: 256 KB flash, 64 KB SRAM; STM32F401CE: 512 KB / 96 KB;
 *   STM32F411CE: 512 KB / 128 KB. The flash size is read from the F_SIZE
 *   register at startup, so one image serves all of them.
 *   Flash sectors: 0-3 = 16 KB, 4 = 64 KB, 5-7 = 128 KB.
 *   Clock: HSI 16 MHz (reset default), flash 0 wait states, caches off.
 *   Flash programming with 32-bit parallelism (VDD 2.7..3.6 V).
 *   USART1: PA9 = TX, PA10 = RX (AF7, pull-up). RX DMA: DMA2 Stream 2 Channel 4.
 *   BOOT0 = 0 (boot from main flash).
 *
 * @implementation
 *   No interrupts are used: the main loop polls the USART IDLE flag and the
 *   SysTick COUNTFLAG. Only the first four vector table entries (initial SP,
 *   Reset, NMI, HardFault) are ever fetched, so startup.c provides a 4-entry
 *   vector table. Do not enable any interrupt here without restoring the full
 *   vector table.
 *
 * @note The bootloader occupies flash sector 0 (0x08000000-0x08003FFF, 16 KB),
 *       the application starts at sector 1, 0x08004000.
 * @note The application is started in the reset state as far as possible:
 *       peripherals used by the bootloader are reset, the core runs from HSI,
 *       PRIMASK = 0 (interrupts enabled, none of them is pending) and VTOR
 *       points to the application vector table at APPLICATION_ADDRESS.
 *****************************************************************************/

#include "stm32f4xx.h"

#define COMM_TIMEOUT             3000       /* ms */
#define CPU_FREQ                 16000000   /* HSI */
#define BAUD_RATE                115200
#define FLASH_START              0x08000000
#define APPLICATION_ADDRESS      0x08004000 /* sector 0 (16 KB) for bootloader */
#define FLASH_PAGE_SIZE          1024       /* write unit of the protocol */
#define FLASH_SIZE_MIN_KB        128        /* smallest STM32F401/F411 */
#define FLASH_SIZE_MAX_KB        512        /* largest STM32F401/F411 */
#define BOOT_VERSION             0x00000001
#define PRODUCT_ID               0x12345678
#define BOOT_HEADER_SIZE         sizeof(P_Header)
#define DMA_BUFF_SIZE            (FLASH_PAGE_SIZE + BOOT_HEADER_SIZE)

#ifndef FLASH_KEY1
#define FLASH_KEY1 0x45670123
#endif
#ifndef FLASH_KEY2
#define FLASH_KEY2 0xCDEF89AB
#endif

#define SRAM_START               0x20000000
#define SRAM_SIZE_MAX            (128 * 1024)  /* upper bound for STM32F401/F411 */

/* All error and status flags of FLASH->SR (write 1 to clear) */
#ifdef FLASH_SR_RDERR
#define FLASH_SR_ALL   (FLASH_SR_EOP | FLASH_SR_OPERR | FLASH_SR_WRPERR | FLASH_SR_PGAERR | \
                        FLASH_SR_PGPERR | FLASH_SR_PGSERR | FLASH_SR_RDERR)
#else
#define FLASH_SR_ALL   (FLASH_SR_EOP | FLASH_SR_OPERR | FLASH_SR_WRPERR | FLASH_SR_PGAERR | \
                        FLASH_SR_PGPERR | FLASH_SR_PGSERR)
#endif

/* All interrupt flags of DMA2 Stream 2 (LIFCR, write 1 to clear) */
#define DMA_LIFCR_STREAM2  (DMA_LIFCR_CFEIF2 | DMA_LIFCR_CDMEIF2 | DMA_LIFCR_CTEIF2 | \
                            DMA_LIFCR_CHTIF2 | DMA_LIFCR_CTCIF2)

/* DMA2 Stream 2, Channel 4 = USART1_RX; peripheral to memory, 8-bit, memory increment */
#define DMA_RX_CR          ((4UL << DMA_SxCR_CHSEL_Pos) | DMA_SxCR_MINC)

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

/* Filled at startup: the number of pages depends on the flash size */
static P_Info bootInfo;
static uint32_t appSize;        /* application area size, bytes */
static uint32_t lastSector;     /* last sector of the flash */
static const uint32_t erasedWord = 0xFFFFFFFF;

/* Accessed as P_Header and uint32_t words; CRC->DR takes whole words */
static uint8_t buff[DMA_BUFF_SIZE] __attribute__((aligned(4)));

/**
  * @brief  Computes the 32-bit CRC of a buffer of 32-bit words.
  *         The STM32F4 CRC unit always starts from 0xFFFFFFFF after reset.
  * @param  pBuffer: word-aligned data
  * @param  len: length in bytes (processed in whole words)
  * @retval 32-bit CRC
  */
static uint32_t CRC_Calc(const uint32_t *pBuffer, uint32_t len)
{
    CRC->CR = CRC_CR_RESET;
    for (; len; len -= 4)
        CRC->DR = *pBuffer++;
    return CRC->DR;
}

static void FlashWaitBusy(void)
{
    while (FLASH->SR & FLASH_SR_BSY) {}
}

/* Unlock is always called with the flash locked: a wrong key sequence locks
   the flash interface until the next reset. Error flags left by a previous
   operation are cleared, otherwise the next operation is not started. */
static void FlashUnlock(void)
{
    FLASH->KEYR = FLASH_KEY1;
    FLASH->KEYR = FLASH_KEY2;
    FLASH->SR   = FLASH_SR_ALL;
}

/* Clears PG/SER and sets LOCK in one write */
static void FlashLock(void)
{
    FLASH->CR = FLASH_CR_LOCK;
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
        while (!(USART1->SR & USART_SR_TXE)) {}
        USART1->DR = *d++;
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

/* Erases sectors 1..lastSector (sector 0 holds the bootloader) */
static uint32_t Erase(void)
{
    FlashUnlock();
    for (uint32_t s = 1; s <= lastSector; s++)
    {
        FLASH->CR = FLASH_CR_PSIZE_1 | FLASH_CR_SER | (s << FLASH_CR_SNB_Pos);
        FLASH->CR |= FLASH_CR_STRT;
        FlashWaitBusy();
    }
    FlashLock();

    return FlashMatches(&erasedWord, APPLICATION_ADDRESS, appSize, 0)
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
        offset > appSize - len ||
        CRC_Calc(data, len) != pkt->crc)
    {
        return Command_Error;
    }

    uint32_t addr = APPLICATION_ADDRESS + offset;
    const uint32_t *src = data;
    volatile uint32_t *dst = (volatile uint32_t *)addr;

    FlashUnlock();
    FLASH->CR = FLASH_CR_PSIZE_1 | FLASH_CR_PG;   /* 32-bit words, PG stays set for the whole page */
    for (uint32_t n = len; n; n -= 4)
    {
        *dst++ = *src++;
        FlashWaitBusy();
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
           (start & 1) != 0 && start >= APPLICATION_ADDRESS && start < APPLICATION_ADDRESS + appSize;
}

/**
  * @brief  Derives the application area from the flash size register (F_SIZE, KB).
  *         Sectors: 4 x 16 KB + 64 KB (= 128 KB), then 128 KB each.
  */
static void InitFlashLayout(void)
{
    uint32_t sizeKb = *(const volatile uint16_t *)FLASHSIZE_BASE;

    if (sizeKb < FLASH_SIZE_MIN_KB || sizeKb > FLASH_SIZE_MAX_KB)
        sizeKb = FLASH_SIZE_MIN_KB;         /* unexpected value: use the safe minimum */

    lastSector = 4 + (sizeKb - 128) / 128;
    appSize    = sizeKb * 1024 - (APPLICATION_ADDRESS - FLASH_START);

    bootInfo.v = BOOT_VERSION;
    bootInfo.p = PRODUCT_ID;
    bootInfo.b = appSize / FLASH_PAGE_SIZE;
    bootInfo.s = FLASH_PAGE_SIZE;
}

/* Restarts USART1 RX DMA for the next packet: the stream must be fully
   stopped and its flags cleared before it is enabled again */
static void StartRxDma(void)
{
    DMA2_Stream2->CR = DMA_RX_CR;
    while (DMA2_Stream2->CR & DMA_SxCR_EN) {}
    DMA2->LIFCR = DMA_LIFCR_STREAM2;
    DMA2_Stream2->NDTR = DMA_BUFF_SIZE;
    DMA2_Stream2->CR   = DMA_RX_CR | DMA_SxCR_EN;
}

static void InitHardware(void)
{
    /* Clocks: GPIOA, DMA2, CRC, USART1 */
    RCC->AHB1ENR |= RCC_AHB1ENR_GPIOAEN | RCC_AHB1ENR_DMA2EN | RCC_AHB1ENR_CRCEN;
    RCC->APB2ENR |= RCC_APB2ENR_USART1EN;
    (void)RCC->APB2ENR;                   /* read back: the clocks are running before the first access */

    /* PA9 = USART1_TX, PA10 = USART1_RX: alternate function AF7, pull-up on RX
       (an unconnected RX line stays idle). SWD pins PA13/PA14 are not touched. */
    GPIOA->AFR[1] = (GPIOA->AFR[1] & ~(GPIO_AFRH_AFSEL9 | GPIO_AFRH_AFSEL10))
                    | (7UL << GPIO_AFRH_AFSEL9_Pos) | (7UL << GPIO_AFRH_AFSEL10_Pos);
    GPIOA->PUPDR  = (GPIOA->PUPDR & ~GPIO_PUPDR_PUPD10) | (1UL << GPIO_PUPDR_PUPD10_Pos);
    GPIOA->MODER  = (GPIOA->MODER & ~(GPIO_MODER_MODER9 | GPIO_MODER_MODER10))
                    | (2UL << GPIO_MODER_MODER9_Pos) | (2UL << GPIO_MODER_MODER10_Pos);

    /* DMA2 Stream2 Channel4: USART1_RX -> buff */
    DMA2_Stream2->PAR  = (uint32_t)&(USART1->DR);
    DMA2_Stream2->M0AR = (uint32_t)buff;
    StartRxDma();

    /* 16 MHz / 115200 = 138.9 -> 139: 115108 baud, -0.08% */
    USART1->BRR = (CPU_FREQ + BAUD_RATE / 2) / BAUD_RATE;
    USART1->CR3 = USART_CR3_DMAR;
    USART1->CR1 = USART_CR1_TE | USART_CR1_RE | USART_CR1_UE;

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

    DMA2_Stream2->CR = 0;
    while (DMA2_Stream2->CR & DMA_SxCR_EN) {}

    /* Return USART1, GPIOA, DMA2 and CRC to their reset state */
    RCC->APB2RSTR |= RCC_APB2RSTR_USART1RST;
    RCC->APB2RSTR &= ~RCC_APB2RSTR_USART1RST;
    RCC->AHB1RSTR |= RCC_AHB1RSTR_GPIOARST | RCC_AHB1RSTR_DMA2RST | RCC_AHB1RSTR_CRCRST;
    RCC->AHB1RSTR &= ~(RCC_AHB1RSTR_GPIOARST | RCC_AHB1RSTR_DMA2RST | RCC_AHB1RSTR_CRCRST);

    RCC->APB2ENR &= ~RCC_APB2ENR_USART1EN;
    RCC->AHB1ENR &= ~(RCC_AHB1ENR_GPIOAEN | RCC_AHB1ENR_DMA2EN | RCC_AHB1ENR_CRCEN);
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

    InitFlashLayout();
    InitHardware();

    while(1) {

        /* Startup timeout: runs until Connect or until the application is started */
        if (timeout && (SysTick->CTRL & SysTick_CTRL_COUNTFLAG_Msk) && --timeout == 0) {
            if (IsAppValid())
                jumpToApp();
            /* no valid application: the timeout stays disabled, keep waiting for the host */
        }

        /* End of packet: restart DMA for the next one, then process this one */
        if (USART1->SR & USART_SR_IDLE) {
            /* The SR read above followed by this DR read clears IDLE and ORE.
               It also drops a byte left in DR after an overlong packet (DMA
               stopped at NDTR = 0), otherwise it would become the first byte
               of the next packet. */
            (void)USART1->DR;

            /* Nothing received (the first idle frame after enabling the
               receiver): not a packet, buff[] still holds old data */
            if (DMA2_Stream2->NDTR == DMA_BUFF_SIZE)
                continue;

            StartRxDma();

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
