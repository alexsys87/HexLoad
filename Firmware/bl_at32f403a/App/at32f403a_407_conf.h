/*****************************************************************************
 * @file     at32f403a_407_conf.h
 * @brief    Artery library configuration for the bootloader.
 *           at32f403a_407.h includes this file. Only the modules whose
 *           register definitions the bootloader uses are enabled; none of
 *           the library driver functions is compiled or called.
 *****************************************************************************/

#ifndef __AT32F403A_407_CONF_H
#define __AT32F403A_407_CONF_H

#ifdef __cplusplus
extern "C" {
#endif

#if !defined  HEXT_VALUE
#define HEXT_VALUE               ((uint32_t)8000000) /*!< high speed external crystal, hz */
#endif

#define HEXT_STARTUP_TIMEOUT     ((uint16_t)0x3000)  /*!< time out for hext start up */
#define HICK_VALUE               ((uint32_t)8000000) /*!< high speed internal clock, hz */
#define LEXT_VALUE               ((uint32_t)32768)   /*!< low speed external clock, hz */

/* module define -------------------------------------------------------------*/
#define CRM_MODULE_ENABLED
#define GPIO_MODULE_ENABLED
#define USART_MODULE_ENABLED
#define DMA_MODULE_ENABLED
#define FLASH_MODULE_ENABLED
#define CRC_MODULE_ENABLED

/* includes ------------------------------------------------------------------*/
#include "at32f403a_407_crm.h"
#include "at32f403a_407_gpio.h"
#include "at32f403a_407_usart.h"
#include "at32f403a_407_dma.h"
#include "at32f403a_407_flash.h"
#include "at32f403a_407_crc.h"

#ifdef __cplusplus
}
#endif

#endif
