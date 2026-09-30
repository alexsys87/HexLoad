#pragma language=extended

static void ResetISR(void);
static void NmiISR(void);
static void FaultISR(void);
static void SVCallISR(void);
static void PendSVISR(void);
static void IntDefaultHandler(void);

void SysTickISR(void);
void USART1_IRQHandler(void);
void DMA1_USART1_IRQHandler(void);

extern void __iar_program_start(void);

// Символ, определяемый линкером для конца стека
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
    { .ulPtr = (unsigned long)&CSTACK$$Limit },  // Вершина стека
    ResetISR,                                                                   // -15
    NmiISR,                                                                     // -14
    FaultISR,                                                                   // -13
    0,                                                                          // -12
    0,                                                                          // -11
    0,                                                                          // -10
    0,                                                                          // -9
    0,                                                                          // -8
    0,                                                                          // -7
    0,                                                                          // -6
    SVCallISR,                                                                  // -5
    0,                                                                          // -4
    0,                                                                          // -3
    PendSVISR,                                                                  // -2
    SysTickISR,                                                                 // -1

    IntDefaultHandler,                                                          // 0
    IntDefaultHandler,                                                          // 1
    IntDefaultHandler,                                                          // 2
    IntDefaultHandler,                                                          // 3
    IntDefaultHandler,                                                          // 4
    IntDefaultHandler,                                                          // 5
    IntDefaultHandler,                                                          // 6
    IntDefaultHandler,                                                          // 7
    IntDefaultHandler,                                                          // 8
    IntDefaultHandler,                                                          // 9
    IntDefaultHandler,                                                          // 10
    IntDefaultHandler,                                                          // 11
    IntDefaultHandler,                                                          // 12
    IntDefaultHandler,                                                          // 13
    IntDefaultHandler,                                                          // 14
    IntDefaultHandler,                                                          // 15
    IntDefaultHandler,                                                          // 16
    IntDefaultHandler,                                                          // 17
    0,                                                                          // 18
    IntDefaultHandler,                                                          // 19
    IntDefaultHandler,                                                          // 20
    IntDefaultHandler,                                                          // 21
    IntDefaultHandler,                                                          // 22
    IntDefaultHandler,                                                          // 23
    IntDefaultHandler,                                                          // 24
    IntDefaultHandler,                                                          // 25
    IntDefaultHandler,                                                          // 26
    USART1_IRQHandler,                                                          // 27
    IntDefaultHandler,                                                          // 28
    0,                                                                          // 29
    IntDefaultHandler,                                                          // 30
    0,                                                                          // 31
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
// Default exception handlers
//
//*****************************************************************************
static void NmiISR(void)          { while(1); }
static void FaultISR(void)        { while(1); }
static void SVCallISR(void)       { while(1); }
static void PendSVISR(void)       { while(1); }
static void IntDefaultHandler(void) { while(1); }

// Weak handlers
__weak void SysTickISR(void)          { }
__weak void USART1_IRQHandler(void)   { }