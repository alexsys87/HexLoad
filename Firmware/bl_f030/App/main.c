/*****************************************************************************
 * @file     main.c
 * @brief    Bootloader for STM32F030 (Cortex-M0) with UART/DMA/Flash/CRC.
 *
 * @protocol Bootloader communication protocol (UART, 115200 8N1):
 *   - Host sends command packet (P_Header + optional data).
 *   - Bootloader replies with response packet (P_Header + optional data).
 *
 *   Packet structure (P_Header, 16 bytes):
 *     +--------+--------+--------+--------+
 *     |  cmd   |  addr  |  size  |  crc   |
 *     +--------+--------+--------+--------+
 *     cmd  : 32-bit command code.
 *     addr : 32-bit address (for write/erase).
 *     size : 32-bit data size in bytes (must be multiple of 4).
 *     crc  : 32-bit CRC of the relevant fields (see below).
 *
 *   Command codes:
 *     0x01  Connect          – no data, returns cmd|0x40.
 *     0x02  GetInfo          – returns P_Info structure (16 bytes).
 *     0x03  Erase            – erases all application pages, returns status.
 *     0x04  WritePage        – writes data (size ≤ 1024) at APPLICATION_ADDRESS+addr.
 *     0x05  Reset            – resets MCU, no reply.
 *
 *   Status bits (ORed with command code in response):
 *     0x40  OK               – operation succeeded.
 *     0x80  Error            – operation failed (CRC mismatch, verify fail, etc.).
 *
 *   P_Info structure (16 bytes):
 *     +--------+--------+--------+--------+
 *     |  ver   | prodId | blkSize|pgSize  |
 *     +--------+--------+--------+--------+
 *     ver    : bootloader version (e.g., 0x00000001).
 *     prodId : product identifier (e.g., 0x12345678).
 *     blkSize: block size in pages (e.g., 14).
 *     pgSize : flash page size (e.g., 1024).
 *
 *   CRC calculation:
 *     - For requests and responses WITHOUT data (Connect, Erase, Reset, WritePage reply):
 *         crc = CRC32(cmd, addr, size)   // first three 32‑bit words of P_Header
 *     - For requests with data (WritePage data):
 *         crc = CRC32(data)               // data only (size bytes)
 *     - For responses with data (GetInfo):
 *         crc = CRC32(info)                // info structure
 *
 *   CRC algorithm: standard CRC-32 (Ethernet) with polynomial 0x04C11DB7,
 *   initial value 0xFFFFFFFF, no final XOR (hardware CRC of STM32F0).
 *
 *   Flow:
 *     1. Host sends Connect (cmd=0x01, addr=0, size=0, crc from header).
 *     2. Bootloader replies with cmd|0x40 (or cmd|0x80 on error).
 *     3. Host sends GetInfo; bootloader replies with P_Info + header.
 *     4. Host sends Erase; bootloader erases all pages and replies with status.
 *     5. For each page to write, host sends WritePage with data; bootloader
 *        verifies CRC, programs flash, verifies, and replies with status.
 *     6. After all pages, host may send Reset.
 *     7. If no command received within COMM_TIMEOUT (1000 ms), bootloader
 *        jumps to application at APPLICATION_ADDRESS.
 *
 * @note The bootloader occupies first 2 KB of flash (0x08000000–0x080007FF),
 *       application starts at 0x08000800.
 *****************************************************************************/

#include "stm32f0xx.h"

#define SYSCFG_CFGR1_MEMMODE_FLASH   (0 << 0) /* 00: Main Flash at 0x00000000 */
#define SYSCFG_CFGR1_MEMMODE_SYSTEM  (1 << 0) /* 01: System Flash at 0x00000000 */
#define SYSCFG_CFGR1_MEMMODE_SRAM    (3 << 0) /* 11: Embedded SRAM at 0x00000000 */

#define COMM_TIMEOUT             3000
#define CPU_FREQ                 8000000
#define BAUD_RATE                115200
#define APPLICATION_FLASH_START  0x08000000
#define APPLICATION_ADDRESS      0x08000800 // 2k for bootloader
#define APPLICATION_FLASH_END    (APPLICATION_ADDRESS + (FLASH_PAGE_SIZE * BLOCK_SIZE))

#define FLASH_PAGE_SIZE          1024
#define BLOCK_SIZE               14
#define BOOT_VERSION             0x00000001
#define PRODUCT_ID               0x12345678
#define BOOT_HEADER_SIZE         sizeof(P_Header)
#define DMA_BUFF_SIZE            FLASH_PAGE_SIZE + BOOT_HEADER_SIZE

#define FLASH_KEY1 0x45670123
#define FLASH_KEY2 0xCDEF89AB

enum eCommand {
    Command_Connect    = 0x01,
    Command_GetInfo    = 0x02,
    Command_Erase      = 0x03,
    Command_WritePage  = 0x04,
    Command_Reset      = 0x05,

    Command_OK         = 0x40,
    Command_Error      = 0x80,
};

enum eState {
    State_Start = 0x01,
    State_Done  = 0x02,
};

enum eError {
    Error_None = 0,
    Error_CRC  = 1,
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

typedef void (*pFunction)(void);

static volatile uint32_t timeTick;

static uint8_t buff[DMA_BUFF_SIZE];
static volatile enum eState     State;
static volatile uint8_t         connected;

/**
  * @brief  Initializes the INIT register.
  * @note   After resetting CRC calculation unit, CRC_InitValue is stored in DR register
  * @param  CRC_InitValue: Programmable initial CRC value
  * @retval None
  */
void CRC_Init(uint32_t CRC_InitValue)
{
  CRC->INIT = CRC_InitValue;
  /* Reset CRC generator */
  CRC->CR = CRC_CR_RESET;
}

/**
  * @brief  Computes the 32-bit CRC of a given buffer of data word(32-bit).
  * @param  pBuffer: pointer to the buffer containing the data to be computed
  * @param  BufferLength: length of the buffer to be computed
  * @retval 32-bit CRC
  */
uint32_t CRC_Calc(const uint32_t *pBuffer, int32_t BufferLength)
{
  /* Reset CRC generator */
  CRC->CR = CRC_CR_RESET;
  
  while( BufferLength > 0 )
  {
    CRC->DR = *pBuffer++;
    
    BufferLength-=4;
  }
  return (CRC->DR);
}

void FlashWaitBusy(void) {
    while(FLASH->SR & FLASH_SR_BSY) {}
}

/**
  * @brief  This function prepares the flash to be erased or programmed.
  *         It first checks no flash operation is on going,
  *         then unlocks the flash if it is locked.
  * @param  None
  * @retval None
  */
void FlashUnlock(void) {
  /* (1) Wait till no operation is on going */
  /* (2) Check that the Flash is unlocked */
  /* (3) Perform unlock sequence */    
    FlashWaitBusy();
    FLASH->KEYR = FLASH_KEY1;
    FLASH->KEYR = FLASH_KEY2;
}

void FlashLock(void) {
    FlashWaitBusy();
    FLASH->CR |= FLASH_CR_LOCK;
    FlashWaitBusy();
}

/**
  * @brief  This function erases a page of flash.
  *         The Page Erase bit (PER) is set at the beginning and reset at the end
  *         of the function, in case of successive erase, these two operations
  *         could be performed outside the function.
  * @param  page_addr is an address inside the page to erase
  * @retval None
  */
void FlashErase(uint32_t page_addr) {
  /* (1) Set the PER bit in the FLASH_CR register to enable page erasing */
  /* (2) Program the FLASH_AR register to select a page to erase */
  /* (3) Set the STRT bit in the FLASH_CR register to start the erasing */
  /* (4) Wait until the BSY bit is reset in the FLASH_SR register */
  /* (5) Check the EOP flag in the FLASH_SR register */
  /* (6) Clear EOP flag by software by writing EOP at 1 */
  /* (7) Reset the PER Bit to disable the page erase */
  FLASH->CR |= FLASH_CR_PER; /* (1) */    
  FLASH->AR =  page_addr; /* (2) */    
  FLASH->CR |= FLASH_CR_STRT; /* (3) */    

  FlashWaitBusy(); /* (4) */ 

  if ((FLASH->SR & FLASH_SR_EOP) != 0)  /* (5) */
  {  
    FLASH->SR |= FLASH_SR_EOP; /* (6)*/
  }
  
  FLASH->CR &= ~FLASH_CR_PER; /* (7) */
}

/**
  * @brief  This function checks that the whole page has been correctly erased
  *         A word is erased while all its bits are set.
  * @param  first_page_addr is the first address of the page to erase
  * @retval -1 if error or 0 if not
  */
uint8_t CheckFlashErase(uint32_t first_page_addr)
{
uint32_t i;  

  for (i=FLASH_PAGE_SIZE; i > 0; i-=4) /* Check the erasing of the page by reading all the page value */
  {
    if (*(uint32_t *)(first_page_addr + i - 4) != (uint32_t)0xFFFFFFFF) /* compare with erased value, all bits at1 */
    {
      return 0; /* report the error to the main progran */
    }
  }
  
  return 1;
}

/**
  * @brief  This function programs a 16-bit word.
  *         The Programming bit (PG) is set at the beginning and reset at the end
  *         of the function, in case of successive programming, these two operations
  *         could be performed outside the function.
  *         This function waits the end of programming, clears the appropriate bit in 
  *         the Status register and eventually reports an error. 
  * @param  flash_addr is the address to be programmed
  *         data is the 16-bit word to program
  * @retval None
  */
void FlashWord16Prog(uint32_t flash_addr, uint16_t data)
{    
  /* (1) Set the PG bit in the FLASH_CR register to enable programming */
  /* (2) Perform the data write (half-word) at the desired address */
  /* (3) Wait until the BSY bit is reset in the FLASH_SR register */
  /* (4) Check the EOP flag in the FLASH_SR register */
  /* (5) clear it by software by writing it at 1 */
  /* (6) Reset the PG Bit to disable programming */
  FLASH->CR |= FLASH_CR_PG; /* (1) */
  
  *(__IO uint16_t*)(flash_addr) = data; /* (2) */
  
  FlashWaitBusy(); /* (3) */

  if ((FLASH->SR & FLASH_SR_EOP) != 0)  /* (4) */
  {
    FLASH->SR |= FLASH_SR_EOP; /* (5) */
  }
  
  FLASH->CR &= ~FLASH_CR_PG; /* (6) */
}

void FlashProgram (const uint32_t *pulData, uint32_t ulAddress, uint32_t ulCount )
{
  for(int i=0; i<ulCount;  i+=4, ulAddress += 4,  pulData++)
  {
      FlashWord16Prog(ulAddress, (uint16_t)*pulData);
      FlashWord16Prog(ulAddress + 2, (uint16_t)(*pulData >> 16));   
  }
}

uint8_t FlashVerify (const uint32_t *pulData, uint32_t ulAddress, uint32_t ulCount )
{
  for(int i=0; i<ulCount;  i+=4, ulAddress += 4,  pulData++)
  {  
    if(*pulData != (*(__IO uint32_t*) (ulAddress)))
    {
      return 0;
    }
  }
  return 1;
}

void UART_sendByte(uint8_t b) {
    while(!(USART1->ISR & USART_ISR_TXE)) {}

    USART1->TDR = b;
}

void UART_send(const uint8_t* d, uint32_t l) {
    for(; l > 0; ++d, --l)
        UART_sendByte(*d);
}

void SysTickISR(void) {
    ++timeTick;
}

void USART1_IRQHandler(void)
{   
  if((USART1->ISR & USART_ISR_IDLE) == USART_ISR_IDLE)
  {
      USART1->ICR |= USART_ICR_IDLECF; /* Clear Idle line flag */
  
      DMA1_Channel3->CCR &=~ DMA_CCR_EN;
      DMA1_Channel3->CNDTR = DMA_BUFF_SIZE;/* Data size */
      DMA1_Channel3->CCR |= DMA_CCR_EN;
  
      State = State_Start;   
  }
}

static void jumpToApp(void) {
  
    __disable_irq();
    
    SYSCFG->CFGR1 = (SYSCFG->CFGR1 & ~SYSCFG_CFGR1_MEM_MODE) | SYSCFG_CFGR1_MEMMODE_SRAM;
    
    __DSB(); // Обеспечиваем завершение всех операций с памятью
    
    volatile uint32_t* appBegin = (volatile uint32_t*)APPLICATION_ADDRESS;
    uint32_t stack = appBegin[0];  // Начальное значение MSP из вектора приложения
    uint32_t start = appBegin[1];  // Адрес обработчика сброса приложения
    
    __set_MSP(stack);   // Устанавливаем новый стек с помощью CMSIS-функции
    
    // Переход по адресу сброса приложения
    __asm volatile("bx %0" : : "r"(start));
}

static void InitHardware(void) {
 
  //RCC->APB2RSTR |= RCC_APB2ENR_SYSCFGEN; 
  SYSCFG->CFGR1 = (SYSCFG->CFGR1 & ~SYSCFG_CFGR1_MEM_MODE) | SYSCFG_CFGR1_MEMMODE_FLASH;
  /* Enable the peripheral clock of GPIOA */
  RCC->AHBENR   |=   RCC_AHBENR_GPIOAEN;
  /* Enable the peripheral clock USART1 */
  RCC->APB2ENR  |=   RCC_APB2ENR_USART1EN;
  /* Enable the peripheral clock DMA1 */
  RCC->AHBENR   |=   RCC_AHBENR_DMA1EN;
  /* Enable the peripheral clock of CRC */
  RCC->AHBENR   |=   RCC_AHBENR_CRCEN;
  
  CRC_Init(0xFFFFFFFF);
  
  /* GPIO configuration for USART1 signals */
  /* (1) Select AF mode on PA2 and PA3 */
  /* (2) AF1 for USART1 signals */
  GPIOA->MODER = (GPIOA->MODER & ~(GPIO_MODER_MODER2 | GPIO_MODER_MODER3))\
                 | (GPIO_MODER_MODER2_1 | GPIO_MODER_MODER3_1); /* (1) */
  
  GPIOA->AFR[0] = (GPIOA->AFR[0] &~ (GPIO_AFRL_AFR2 | GPIO_AFRL_AFR3))\
                  | (1 << (2 * 4)) | (1 << (3 * 4)); /* (2) */
  
  /* DMA1 Channel2 USART_RX config */
  /* (4)  Peripheral address */
  /* (5)  Memory address */
  /* (6)  Data size */
  /* (7)  Memory increment */
  /*      Peripheral to memory*/
  /*      8-bit transfer */
  DMA1_Channel3->CPAR = (uint32_t)&(USART1->RDR); /* (4) */
  DMA1_Channel3->CMAR = (uint32_t)buff; /* (5) */
  DMA1_Channel3->CNDTR = DMA_BUFF_SIZE; /* (6) */
  DMA1_Channel3->CCR |= DMA_CCR_MINC | DMA_CCR_EN; /* (7) */ 
  
  /* Configure USART1 */
  USART1->BRR = CPU_FREQ / BAUD_RATE;
  /* Enable DMA in reception */
  USART1->CR3 = USART_CR3_DMAR;   
  /* Enable Uart */
  /* Enable IDLE interrupt */ 
  USART1->CR1 = USART_CR1_TE | USART_CR1_RE | USART_CR1_UE | USART_CR1_IDLEIE;
  
  /* polling idle frame Transmission */
  while((USART1->ISR & USART_ISR_IDLE) != USART_ISR_IDLE)
  { 
    /* add time out here for a robust application */
  }
  USART1->ICR |= USART_ICR_IDLECF;/* Clear TC flag */

  /* Configure IT */
  
  /*  Set priority for SysTick_IRQn */
  /*  Enable SysTick_IRQn */ 
  SysTick_Config(CPU_FREQ / 1000);
  NVIC_SetPriority(SysTick_IRQn, 2); 
  NVIC_EnableIRQ(SysTick_IRQn);

  /*  Set priority for USART1_IRQn */
  /*  Enable USART1_IRQn */
  NVIC_SetPriority(USART1_IRQn, 1);
  NVIC_EnableIRQ(USART1_IRQn);   

   __enable_irq ();  
}

static void DeInitHardware(void) {
    
    NVIC_DisableIRQ(SysTick_IRQn);
    NVIC_DisableIRQ(USART1_IRQn);

    // all changed registers to their reset-values
    SysTick->CTRL = 0;
    SysTick->LOAD = 0;
    SysTick->VAL  = 0;
    
    USART1->CR1  = 0;
    USART1->BRR  = 0;

    GPIOA->MODER = 0x28000000;
    GPIOA->PUPDR = 0x24000000;
    GPIOA->AFR[0]= 0;
    GPIOA->AFR[1]= 0;
    GPIOA->ODR   = 0;

    RCC->APB2ENR = 0;
    RCC->AHBENR  = 0x00000014;

}

static void Connect(void)
{
    P_Header *pkt  = (P_Header *)&buff[0];
    connected = 1;
   
    pkt->cmd  = (uint32_t)Command_Connect | (uint32_t)Command_OK;
    pkt->addr = 0;
    pkt->size = 0;
    pkt->crc  = 0;
    pkt->crc  = CRC_Calc((const uint32_t*)pkt, sizeof(P_Header));
    
    UART_send((const uint8_t*)buff, (sizeof(P_Header)));
}

static void GetInfo(void) {
    P_Header *pkt  = (P_Header *)&buff[0];
    P_Info   *info = (P_Info   *)&buff[BOOT_HEADER_SIZE];
    
    info->v = BOOT_VERSION;
    info->p = PRODUCT_ID;
    info->b = BLOCK_SIZE;
    info->s = FLASH_PAGE_SIZE;
    
    pkt->cmd  = (uint32_t)Command_GetInfo | (uint32_t)Command_OK;
    pkt->addr = 0;
    pkt->size = sizeof(P_Info);
    pkt->crc  = 0;
    pkt->crc  = CRC_Calc((const uint32_t*)info, sizeof(P_Info));

    UART_send((const uint8_t*)buff, (sizeof(P_Header) + sizeof(P_Info)));
}

static void Erase(void) {
    P_Header *pkt  = (P_Header *)&buff[0];
    uint8_t ret = 1;
    
    FlashUnlock();
    for(uint32_t p = APPLICATION_ADDRESS; p < APPLICATION_FLASH_END; p += FLASH_PAGE_SIZE)
        FlashErase(p);
    FlashLock();
    
    for(uint32_t p = APPLICATION_ADDRESS; p < APPLICATION_FLASH_END; p += FLASH_PAGE_SIZE)
       ret &= CheckFlashErase(p);
    
    pkt->cmd  = (uint32_t)Command_Erase | (ret ? (uint32_t)Command_OK : (uint32_t)Command_Error);
    pkt->addr = 0;
    pkt->size = 0;
    pkt->crc  = 0;
    pkt->crc  = CRC_Calc((const uint32_t*)pkt, sizeof(P_Header));
    
    UART_send((const uint8_t*)buff, (sizeof(P_Header)));
}

static void WritePage(void) {
    P_Header *pkt  = (P_Header *)&buff[0];
  
    const uint32_t* data = (const uint32_t*)&buff[BOOT_HEADER_SIZE];
    uint32_t writeAddr = APPLICATION_ADDRESS + pkt->addr;
    int32_t writeLen = pkt->size;
    
    uint8_t ret = 1;
    
    uint32_t crc = CRC_Calc(data, writeLen);
    
    if(crc != pkt->crc)
    {
        ret = 0;
    }
    
    if(writeAddr <= APPLICATION_FLASH_END && writeAddr >= APPLICATION_ADDRESS && ret)
    {   
      FlashUnlock();
        FlashProgram(data, writeAddr, writeLen);
      FlashLock();
      
      ret = FlashVerify(data, writeAddr, writeLen);
    }
    
    pkt->cmd  = (uint32_t)Command_WritePage | (ret ? (uint32_t)Command_OK : (uint32_t)Command_Error);
    pkt->addr = 0;
    pkt->size = 0;
    pkt->crc  = 0;
    pkt->crc  = CRC_Calc((const uint32_t*)pkt, sizeof(P_Header));
    
    UART_send((const uint8_t*)buff, (sizeof(P_Header)));
}

static void Reset(void) {
    NVIC_SystemReset();
}

void exit(int status) {
    (void)status; 
    while(1);
}

int main(void) {
  
    P_Header *pkt  = (P_Header *)&buff[0];
    
    connected = 0;
  
    InitHardware();
       
    while(1) {
   
        if(timeTick >= COMM_TIMEOUT) {
            if(!connected) {
                DeInitHardware();
                jumpToApp();
             }
        }
   
        if(State == State_Start) {
            switch(pkt->cmd) {
                case Command_Connect:    
                  Connect();      break;
                case Command_GetInfo:    
                  GetInfo();      break;
                case Command_Erase:      
                  Erase();        break;
                case Command_WritePage:  
                  WritePage();    break;
                case Command_Reset:      
                  Reset();        break;
                default: 
                  UART_sendByte((uint8_t)Command_Error);
            }
            State = State_Done;
        }
    }
}