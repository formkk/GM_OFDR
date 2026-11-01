#ifndef QT_ACQ_H
#define QT_ACQ_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct qt_acq_handle_t qt_acq_handle_t;
typedef qt_acq_handle_t *qt_acq_handle;

#define QT_ACQ_OK                     0
#define QT_ACQ_ERR_UNKNOWN            (-1)
#define QT_ACQ_ERR_SDK                (-2)
#define QT_ACQ_ERR_INVALID_ARGUMENT   (-3)
#define QT_ACQ_ERR_INVALID_HANDLE     (-4)
#define QT_ACQ_ERR_NULL_POINTER       (-5)
#define QT_ACQ_ERR_BUFFER_TOO_SMALL   (-6)
#define QT_ACQ_ERR_NOT_CONFIGURED     (-7)

#define QT_ACQ_TRIG_SOFTWARE      0
#define QT_ACQ_TRIG_EXT_RISING    1
#define QT_ACQ_TRIG_EXT_FALLING   2

int qt_acq_open(uint32_t card_index, qt_acq_handle *out_handle);

int qt_acq_close(qt_acq_handle handle);

int qt_acq_configure(qt_acq_handle handle, uint32_t sample_rate_mhz,
                     uint32_t acq_time_ms, uint32_t seg_count, int trigger,
                     uint32_t trigger_delay_ms);

int qt_acq_start(qt_acq_handle handle);

int qt_acq_read(qt_acq_handle handle, void *buffer, uint32_t capacity,
                uint32_t *out_len);

int qt_acq_stop(qt_acq_handle handle);

int qt_acq_calc_buffer_bytes(uint32_t sample_rate_mhz, uint32_t acq_time_ms,
                             uint32_t seg_count, uint32_t *out_bytes);

int qt_acq_last_error(char *buf, size_t cap);

int qt_acq_version(char *buf, size_t len);

#ifdef __cplusplus
}
#endif

#endif
