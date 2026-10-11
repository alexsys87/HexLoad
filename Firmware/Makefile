# Builds all bootloaders with arm-none-eabi-gcc.
#   make              all bootloaders
#   make bl_f103      one bootloader
#   make clean
# Output: bl_*/GCC/build/bl_*.elf, .hex, .bin

BOOTLOADERS := bl_f030 bl_f103 bl_f103vc bl_f401 bl_f411 bl_at32f403a

.PHONY: all clean $(BOOTLOADERS)

all: $(BOOTLOADERS)

$(BOOTLOADERS):
	$(MAKE) -C $@/GCC

clean:
	@for b in $(BOOTLOADERS); do $(MAKE) -C $$b/GCC clean; done
