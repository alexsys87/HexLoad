/*****************************************************************************
 * @file     main.c
 * @brief    Bootloader for STM32F030 (Cortex-M0) with UART/DMA/Flash/CRC.
 *
 * @protocol Bootloader communication protocol (UART, 115200 8N1):
 *   - Host sends a command packet (P_Header + optional data).
 *   - Bootloader replies with a response packet (P_Header + optional data).
 *
 *   Framing:
 *     USART RX runs through DMA into buff[]; the USART IDLE-line flag marks
 *     the end of a packet. The host MUST send header and data as one continuous
 *     burst - a pause longer than one character time splits the packet in two.
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
 *     0x03  Erase            - erases all application pages, returns status.
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
 *     blkSize: number of application pages (e.g., 14).
 *     pgSize : flash page size (e.g., 1024).
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
 *     3. Host sends Erase; bootloader erases all application pages and replies.
 *     4. For each page the host sends WritePage with data; the bootloader checks
 *        the range and the CRC, programs, verifies and replies with the status.
 *     5. Host sends Reset.
 *     6. If Connect is not received within COMM_TIMEOUT (3000 ms), the bootloader
 *        jumps to the application - only if its vector table looks valid;
 *        otherwise it keeps waiting for the host.
 *
 * @implementation
 *   No interrupts are used: the main loop polls the USART IDLE flag and the
 *   SysTick COUNTFLAG. Only the first vector table entries (initial SP,
 *   Reset_Handler, NMI, HardFault) are ever fetched, so the startup file may
 *   use a 4-entry vector table.
 *
 * @note The bootloader occupies the first 2 KB of flash (0x08000000-0x080007FF),
 *       the application starts at 0x08000800.
 * @note On entry to the application PRIMASK is set (interrupts disabled) and the
 *       vector table is still mapped from flash; the application must relocate
 *       its vector table to SRAM, remap SRAM to 0x0 (SYSCFG clock must be enabled
 *       for that) and call __enable_irq() itself.
 *****************************************************************************/

#include "stm32f0xx.h"

#define COMM_TIMEOUT             3000       /* ms */
#define CPU_FREQ                 8000000
#define BAUD_RATE                115200
#define APPLICATION_ADDRESS      0x08000800 /* 2k for bootloader */
#define FLASH_PAGE_SIZE          1024
#define BLOCK_SIZE               14
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
#define SRAM_SIZE_MAX            (32 * 1024)   /* upper bound for the whole STM32F030 family */

/* Reset values used to hand the chip over to the application */
#define RCC_AHBENR_RESET         0x00000014    /* SRAM + FLITF clocks */

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

/* Accessed as P_Header and uint32_t words: Cortex-M0 faults on unaligned word access */
static uint8_t buff[DMA_BUFF_SIZE] __attribute__((aligned(4)));

/**
  * @brief  Computes the 32-bit CRC of a buffer of 32-bit words.
  *         The CRC unit INIT register keeps its reset value 0xFFFFFFFF.
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

/* Unlock is always called with the flash locked: a key write to an unlocked
   FPEC would lock it until the next reset. */
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
        while (!(USART1->ISR & USART_ISR_TXE)) {}
        USART1->TDR = *d++;
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
    for (uint32_t p = APPLICATION_ADDRESS; p < APPLICATION_FLASH_END; p += FLASH_PAGE_SIZE)
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
    /* Clocks: GPIOA, DMA1, CRC, USART1 */
    RCC->AHBENR  |= RCC_AHBENR_GPIOAEN | RCC_AHBENR_DMA1EN | RCC_AHBENR_CRCEN;
    RCC->APB2ENR |= RCC_APB2ENR_USART1EN;

    /* PA2/PA3 alternate function AF1 (USART1). Runs right after reset, so the
       registers hold their reset values and can be written directly
       (MODER keeps PA13/PA14 in AF mode for SWD). */
    GPIOA->MODER  = 0x28000000 | GPIO_MODER_MODER2_1 | GPIO_MODER_MODER3_1;
    GPIOA->AFR[0] = (1 << (2 * 4)) | (1 << (3 * 4));

    /* DMA1 Channel3: USART1_RX -> buff, memory increment, 8-bit */
    DMA1_Channel3->CPAR  = (uint32_t)&(USART1->RDR);
    DMA1_Channel3->CMAR  = (uint32_t)buff;
    DMA1_Channel3->CNDTR = DMA_BUFF_SIZE;
    DMA1_Channel3->CCR   = DMA_CCR_MINC | DMA_CCR_EN;

    USART1->BRR = CPU_FREQ / BAUD_RATE;
    USART1->CR3 = USART_CR3_DMAR;
    USART1->CR1 = USART_CR1_TE | USART_CR1_RE | USART_CR1_UE;

    /* Wait for the idle frame after enabling the receiver and discard it */
    while (!(USART1->ISR & USART_ISR_IDLE)) {}
    USART1->ICR = USART_ICR_IDLECF;

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

    DMA1_Channel3->CCR = 0;

    /* Return USART1 and GPIOA to their reset state */
    RCC->APB2RSTR = RCC_APB2RSTR_USART1RST;
    RCC->APB2RSTR = 0;
    RCC->AHBRSTR  = RCC_AHBRSTR_GPIOARST;
    RCC->AHBRSTR  = 0;

    RCC->APB2ENR = 0;
    RCC->AHBENR  = RCC_AHBENR_RESET;
}

__attribute__((noreturn))
static void jumpToApp(void)
{
    __disable_irq();          /* the application starts with PRIMASK = 1, as before */
    DeInitHardware();

    const volatile uint32_t *app = (const volatile uint32_t *)APPLICATION_ADDRESS;
    uint32_t stack = app[0];  // Initial MSP from the application vector table
    uint32_t start = app[1];  // Application reset handler address

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
        if (USART1->ISR & USART_ISR_IDLE) {
            USART1->ICR = USART_ICR_IDLECF;

            DMA1_Channel3->CCR   = DMA_CCR_MINC;
            DMA1_Channel3->CNDTR = DMA_BUFF_SIZE;
            DMA1_Channel3->CCR   = DMA_CCR_MINC | DMA_CCR_EN;

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
