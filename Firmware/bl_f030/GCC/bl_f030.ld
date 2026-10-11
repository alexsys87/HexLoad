/* bl_f030 memory layout (GNU ld), same as EWARM/bl_f030.icf */

MEMORY
{
    ROM (rx)  : ORIGIN = 0x08000000, LENGTH = 2K
    RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 4K
}

STACK_SIZE = 0x800;

INCLUDE sections.ld
