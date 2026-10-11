//*****************************************************************************
//
// Minimal startup for the bootloaders (GCC, arm-none-eabi).
//
// Same layout as EWARM/startup.c: the bootloaders use no interrupts, so the
// vector table holds only the first four entries:
//   0 - initial SP, 1 - Reset, 2 - NMI, 3 - HardFault.
//
// Do NOT enable any interrupt in the bootloader without restoring the full
// table: the core would fetch a handler address from the code that follows.
//
//*****************************************************************************

#include <stdint.h>

void ResetISR(void);
static void NmiISR(void);
static void FaultISR(void);

int main(void);

// Linker-defined symbols (gcc/sections.ld)
extern uint32_t _estack;
extern uint32_t _sidata, _sdata, _edata;
extern uint32_t _sbss, _ebss;

typedef union
{
    void (*pfnHandler)(void);
    unsigned long ulPtr;
} uVectorEntry;

__attribute__((section(".isr_vector"), used))
const uVectorEntry __vector_table[] =
{
    { .ulPtr = (unsigned long)&_estack },   // Initial stack pointer
    { ResetISR },                           // -15 Reset
    { NmiISR },                             // -14 NMI
    { FaultISR },                           // -13 HardFault
};

void ResetISR(void)
{
    uint32_t *src = &_sidata;
    uint32_t *dst = &_sdata;

    while (dst < &_edata)
        *dst++ = *src++;
    for (dst = &_sbss; dst < &_ebss; )
        *dst++ = 0;

    main();
    while (1);
}

static void NmiISR(void)          { while(1); }
static void FaultISR(void)        { while(1); }
