#ifndef OFDR_H
#define OFDR_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define OFDR_API __declspec(dllexport)
#else
#define OFDR_API __attribute__((visibility("default")))
#endif

/** OFDR 处理配置 */
typedef struct {
    double fs;           /**< 采样率 (Hz) */
    double dist_scale;   /**< 距离轴校准系数（默认 1.0），用于补偿辅助支路长度误差 */
} OfdrConfig;

/** OFDR 处理结果，由 ofdr_result_free 释放 */
typedef struct {
    int32_t error;      /**< 错误码：0 成功，非 0 失败 */
    const char *error_msg; /**< 错误消息（失败时有效），由 ofdr_result_free 释放 */
    double *dist;       /**< 距离轴 (m)，长度 len */
    double *freq;       /**< 频率轴 (Hz)，长度 len */
    double *ps;         /**< 联合功率谱 (线性幅值)，长度 len */
    size_t len;         /**< 各数组长度 */
} OfdrResult;

/** 返回 OFDR 默认配置 */
OFDR_API OfdrConfig ofdr_config_default(void);

/**
 * OFDR 处理
 *
 * @param p     P 路信号
 * @param s     S 路信号
 * @param aux   辅助通道信号
 * @param len   各输入信号的长度（三路等长）
 * @param cfg   处理配置（不可为 NULL）
 * @return 处理结果，使用后必须调用 ofdr_result_free 释放
 */
OFDR_API OfdrResult ofdr_process(const double *p, const double *s,
                                 const double *aux, size_t len,
                                 const OfdrConfig *cfg);

/** 释放 ofdr_process 返回的结果 */
OFDR_API void ofdr_result_free(OfdrResult *result);

#ifdef __cplusplus
}
#endif

#endif /* OFDR_H */
