#pragma language=extended

//*****************************************************************************
//
// Minimal startup for the STM32F411 bootloader (IAR EWARM).
//
// The bootloader uses no interrupts: the main loop polls the USART IDLE flag
// and the SysTick COUNTFLAG, and no interrupt is ever enabled. The core then
// fetches only the first four vector table entries:
//   0 - initial SP, 1 - Reset, 2 - NMI, 3 - HardFault.
// MemManage, BusFault and UsageFault are disabled after reset (SHCSR), so
// they escalate to HardFault. The rest of the 102-entry STM32F411 table
// (system exceptions and IRQ0..85) is never read, so it is omitted.
// Flash sector 0 (16 KB) is reserved for the bootloader anyway; the short
// table is kept for consistency with the other bootloaders of this project.
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
