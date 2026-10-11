# Common build rules for the bootloaders (arm-none-eabi-gcc).
#
# A per-target Makefile (bl_*/GCC/Makefile) sets:
#   TARGET   output name
#   CPU      -mcpu=... option
#   DEFS     preprocessor defines (device selection)
#   INCS     include directories (relative to bl_*/GCC)

FW_DIR    := $(abspath $(dir $(lastword $(MAKEFILE_LIST)))..)

PREFIX    ?= arm-none-eabi-
CC        := $(PREFIX)gcc
OBJCOPY   := $(PREFIX)objcopy
SIZE      := $(PREFIX)size

BUILD_DIR ?= build
LDSCRIPT  ?= $(TARGET).ld

SRCS      := ../App/main.c $(FW_DIR)/gcc/startup.c
OBJS      := $(addprefix $(BUILD_DIR)/,$(notdir $(SRCS:.c=.o)))

ARCH      := $(CPU) -mthumb -mfloat-abi=soft
CFLAGS    := $(ARCH) -std=gnu11 -Os -g -Wall \
             -ffunction-sections -fdata-sections -fno-common \
             -fno-tree-loop-distribute-patterns \
             $(addprefix -D,$(DEFS)) \
             $(addprefix -I,$(INCS)) -I$(FW_DIR)/CMSIS/Core/Include \
             -MMD -MP
LDFLAGS   := $(ARCH) -nostartfiles -nostdlib \
             -T$(LDSCRIPT) -L$(FW_DIR)/gcc \
             -Wl,--gc-sections -Wl,-Map=$(BUILD_DIR)/$(TARGET).map \
             -Wl,--print-memory-usage
LDLIBS    := -lgcc

vpath %.c $(sort $(dir $(SRCS)))

.PHONY: all clean

all: $(BUILD_DIR)/$(TARGET).elf $(BUILD_DIR)/$(TARGET).hex $(BUILD_DIR)/$(TARGET).bin
	$(SIZE) $(BUILD_DIR)/$(TARGET).elf

$(BUILD_DIR)/%.o: %.c | $(BUILD_DIR)
	$(CC) $(CFLAGS) -c $< -o $@

$(BUILD_DIR)/$(TARGET).elf: $(OBJS) $(LDSCRIPT) $(FW_DIR)/gcc/sections.ld
	$(CC) $(LDFLAGS) $(OBJS) $(LDLIBS) -o $@

$(BUILD_DIR)/%.hex: $(BUILD_DIR)/%.elf
	$(OBJCOPY) -O ihex $< $@

$(BUILD_DIR)/%.bin: $(BUILD_DIR)/%.elf
	$(OBJCOPY) -O binary $< $@

$(BUILD_DIR):
	mkdir -p $@

clean:
	rm -rf $(BUILD_DIR)

-include $(OBJS:.o=.d)
