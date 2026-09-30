/*****************************************************************************
 * @file     main.c
 * @brief    Bootloader for STM32F103VCT6 (Cortex-M3, high density,
 *           HY-MiniSTM32V board) with UART/DMA/Flash/CRC.
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
 *     blkSize: number of application pages (254).
 *     pgSize : page size (1024).
 *
 *   The STM32F103VC (high density) erases flash in 2 KB pages. A "page" of
 *   the protocol is the 1 KB write unit of CMD_PROG, so all bootloaders of
 *   this project keep the same 16 + 1024 byte receive buffer; the
 *   application area size is pages * page_size. CMD_ERASE erases every
 *   2 KB page of the application area.
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
 *     3. Host sends Erase; bootloader erases the application area and replies.
 *     4. For each page the host sends WritePage with data; the bootloader checks
 *        the range and the CRC, programs, verifies and replies with the status.
 *     5. Host sends Reset.
 *     6. If Connect is not received within COMM_TIMEOUT (3000 ms), the bootloader
 *        jumps to the application - only if its vector table looks valid;
 *        otherwise it keeps waiting for the host.
 *
 * @hardware
 *   STM32F103VCT6: 256 KB flash (2 KB pages, single bank), 48 KB SRAM.
 *   Clock: HSI 8 MHz (reset default; the HSI must be on for flash programming).
 *   USART1: PA9 = TX, PA10 = RX (pull-up), no remap. On the HY-MiniSTM32V
 *   these pins are connected to the on-board PL2303 USB-UART converter.
 *   RX DMA: DMA1 Channel 5.
 *   BOOT0 = 0 (boot from main flash).
 *
 * @implementation
 *   No interrupts are used: the main loop polls the USART IDLE flag and the
 *   SysTick COUNTFLAG. Only the first four vector table entries (initial SP,
 *   Reset, NMI, HardFault) are ever fetched, so startup.c provides a 4-entry
 *   vector table instead of the full 76-entry one (16 + 60 IRQs). Do not enable any interrupt
 *   here without restoring the full vector table.
 *
 * @note The bootloader occupies the first 2 KB flash page (0x08000000-0x080007FF),
 *       the application starts at 0x08000800 (254 KB) - the same application
 *       address as on the STM32F030 and the STM32F103C8.
 * @note The application is started in the reset state as far as possible:
 *       peripherals used by the bootloader are reset, the core runs from HSI,
 *       PRIMASK = 0 (interrupts enabled, none of them is pending) and VTOR
 *       points to the application vector table at APPLICATION_ADDRESS.
 *****************************************************************************/

#include "stm32f1xx.h"

#define COMM_TIMEOUT             3000       /* ms */
#define CPU_FREQ                 8000000
#define BAUD_RATE                115200
#define APPLICATION_ADDRESS      0x08000800 /* 2k for bootloader */
#define FLASH_PAGE_SIZE          1024       /* write unit of the protocol */
#define FLASH_ERASE_SIZE         2048       /* erase page of the high-density STM32F103 */
#define BLOCK_SIZE               254        /* 256 KB flash - 2 KB bootloader, in 1 KB pages */
#define APPLICATION_SIZE         (FLASH_PAGE_SIZE * BLOCK_SIZE)
#define APPLICATION_FLASH_END    (APPLICATION_ADDRESS + APPLICATION_SIZE)
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
#define SRAM_SIZE_MAX            (96 * 1024)   /* upper bound for the whole STM32F103 family */

/* Reset values used to hand the chip over to the application */
#define RCC_AHBENR_RESET         0x00000014    /* SRAM + FLITF clocks */
#define GPIO_CRH_RESET           0x44444444    /* all pins floating inputs */

/* GPIOA CRH fields: PA9 = AF push-pull output 2 MHz, PA10 = input with pull-up/down */
#define GPIO_CRH_PA9_AF_PP       (0xAUL << GPIO_CRH_MODE9_Pos)
#define GPIO_CRH_PA10_IN_PULL    (0x8UL << GPIO_CRH_MODE10_Pos)

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

/* Accessed as P_Header and uint32_t words; CRC->DR takes whole words */
static uint8_t buff[DMA_BUFF_SIZE] __attribute__((aligned(4)));

/**
  * @brief  Computes the 32-bit CRC of a buffer of 32-bit words.
  *         The STM32F1 CRC unit always starts from 0xFFFFFFFF after reset.
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
   the FPEC until the next reset. */
static void FlashUnlock(void)
{
    FLASH->KEYR = FLASH_KEY1;
    FLASH->KEYR = FLASH_KEY2;
}

/* Clears PG/PER and sets LOCK in one write */
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

static uint32_t Erase(void)
{
    FlashUnlock();
    FLASH->CR = FLASH_CR_PER;
    for (uint32_t p = APPLICATION_ADDRESS; p < APPLICATION_FLASH_END; p += FLASH_ERASE_SIZE)
    {
        FLASH->AR = p;
        FLASH->CR = FLASH_CR_PER | FLASH_CR_STRT;
        FlashWaitBusy();
    }
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
    const uint16_t *src = (const uint16_t *)data;
    volatile uint16_t *dst = (volatile uint16_t *)addr;

    FlashUnlock();
    FLASH->CR = FLASH_CR_PG;              /* PG stays set for the whole page */
    for (uint32_t n = len; n; n -= 2)
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
           (start & 1) != 0 && start >= APPLICATION_ADDRESS && start < APPLICATION_FLASH_END;
}

static void InitHardware(void)
{
    /* Clocks: DMA1, CRC, GPIOA, USART1 */
    RCC->AHBENR  |= RCC_AHBENR_DMA1EN | RCC_AHBENR_CRCEN;
    RCC->APB2ENR |= RCC_APB2ENR_IOPAEN | RCC_APB2ENR_USART1EN;
    (void)RCC->APB2ENR;                   /* read back: the clocks are running before the first access */

    /* PA9 = USART1_TX (AF push-pull), PA10 = USART1_RX (input, pull-up via ODR).
       Runs right after reset, so the registers hold their reset values and can
       be written directly. The pull-up keeps an unconnected RX line idle.
       SWD pins PA13/PA14 (CRH) are owned by the debug port and not affected. */
    GPIOA->CRH = (GPIO_CRH_RESET & ~(GPIO_CRH_MODE9 | GPIO_CRH_CNF9 | GPIO_CRH_MODE10 | GPIO_CRH_CNF10))
                 | GPIO_CRH_PA9_AF_PP | GPIO_CRH_PA10_IN_PULL;
    GPIOA->ODR = GPIO_ODR_ODR10;

    /* DMA1 Channel5: USART1_RX -> buff, memory increment, 8-bit */
    DMA1_Channel5->CPAR  = (uint32_t)&(USART1->DR);
    DMA1_Channel5->CMAR  = (uint32_t)buff;
    DMA1_Channel5->CNDTR = DMA_BUFF_SIZE;
    DMA1_Channel5->CCR   = DMA_CCR_MINC | DMA_CCR_EN;

    USART1->BRR = CPU_FREQ / BAUD_RATE;   /* 69 = 4 + 5/16: 115942 baud, +0.6% */
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

    DMA1_Channel5->CCR = 0;

    /* Return USART1 and GPIOA to their reset state */
    RCC->APB2RSTR = RCC_APB2RSTR_USART1RST | RCC_APB2RSTR_IOPARST;
    RCC->APB2RSTR = 0;

    RCC->APB2ENR = 0;
    RCC->AHBENR  = RCC_AHBENR_RESET;
}

__attribute__((noreturn))
static void jumpToApp(void)
{
    DeInitHardware();

    const volatile uint32_t *app = (const volatile uint32_t *)APPLICATION_ADDRESS;
    uint32_t stack = app[0];  // Initial MSP from the application vector table
    uint32_t start = app[1];  // Application reset handler address

    /* Cortex-M3 has VTOR: interrupts of the application go straight to its own
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
        if (USART1->SR & USART_SR_IDLE) {
            /* The SR read above followed by this DR read clears IDLE and ORE.
               It also drops a byte left in DR after an overlong packet (DMA
               stopped at CNDTR = 0), otherwise it would become the first byte
               of the next packet. */
            (void)USART1->DR;

            /* Nothing received (the first idle frame after enabling the
               receiver): not a packet, buff[] still holds old data */
            if (DMA1_Channel5->CNDTR == DMA_BUFF_SIZE)
                continue;

            DMA1_Channel5->CCR   = DMA_CCR_MINC;
            DMA1_Channel5->CNDTR = DMA_BUFF_SIZE;
            DMA1_Channel5->CCR   = DMA_CCR_MINC | DMA_CCR_EN;

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
