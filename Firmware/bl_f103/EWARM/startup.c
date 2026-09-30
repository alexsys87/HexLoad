#pragma language=extended

//*****************************************************************************
//
// Minimal startup for the STM32F103 bootloader (IAR EWARM).
//
// The bootloader uses no interrupts: the main loop polls the USART IDLE flag
// and the SysTick COUNTFLAG, and no interrupt is ever enabled. The core then
// fetches only the first four vector table entries:
//   0 - initial SP, 1 - Reset, 2 - NMI, 3 - HardFault.
// MemManage, BusFault and UsageFault are disabled after reset (SHCSR), so
// they escalate to HardFault. The rest of the 59-entry STM32F103xB table
// (system exceptions and IRQ0..42) is never read, so it is omitted. This saves
// 220 bytes of flash, and the bootloader fits into one 1 KB flash page.
//
// Do NOT enable any interrupt (NVIC_EnableIRQ, SysTick TICKINT, ...) in the
// bootloader without restoring the full table: the core would fetch a handler
// address from the code that follows the table.
//
//*****************************************************************************

static void ResetISR(void);
static void NmiISR(void);
static void FaultISR(void);

extern void __iar_program_start(void);

// Linker-defined symbol: end (top) of the stack block
extern void CSTACK$$Limit(void);

//*****************************************************************************
//
// A union that describes the entries of the vector table.
//
//*****************************************************************************
typedef union
{
    void (*pfnHandler)(void);
    unsigned long ulPtr;
} uVectorEntry;

__root const uVectorEntry __vector_table[] @ ".intvec" =
{
    { .ulPtr = (unsigned long)&CSTACK$$Limit },  // Initial stack pointer
    ResetISR,                                                                   // -15 Reset
    NmiISR,                                                                     // -14 NMI
    FaultISR,                                                                   // -13 HardFault
};

//*****************************************************************************
//
// Reset handler
//
//*****************************************************************************
static void ResetISR(void)
{
    __iar_program_start();
}

//*****************************************************************************
//
// Fault handlers
//
//*****************************************************************************
static void NmiISR(void)          { while(1); }
static void FaultISR(void)        { while(1); }
