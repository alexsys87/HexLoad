/*
 * Common section layout for the bootloaders (GNU ld).
 * Included by the per-target linker script, which defines the ROM and RAM
 * memory regions and STACK_SIZE.
 */

ENTRY(ResetISR)

SECTIONS
{
    .isr_vector ORIGIN(ROM) :
    {
        KEEP(*(.isr_vector))
    } > ROM

    .text :
    {
        *(.text*)
        *(.rodata*)
        . = ALIGN(4);
    } > ROM

    .data :
    {
        . = ALIGN(4);
        _sdata = .;
        *(.data*)
        . = ALIGN(4);
        _edata = .;
    } > RAM AT > ROM
    _sidata = LOADADDR(.data);

    .bss (NOLOAD) :
    {
        . = ALIGN(4);
        _sbss = .;
        *(.bss*)
        *(COMMON)
        . = ALIGN(4);
        _ebss = .;
    } > RAM

    .stack (NOLOAD) :
    {
        . = ALIGN(8);
        . = . + STACK_SIZE;
        . = ALIGN(8);
        _estack = .;
    } > RAM

    /DISCARD/ :
    {
        *(.ARM.exidx*)
        *(.ARM.extab*)
    }
}
