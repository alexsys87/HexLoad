/* bl_f411 memory layout (GNU ld), same as EWARM/bl_f411.icf */

MEMORY
{
    ROM (rx)  : ORIGIN = 0x08000000, LENGTH = 16K
    RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 128K
}

STACK_SIZE = 0x800;

INCLUDE sections.ld
