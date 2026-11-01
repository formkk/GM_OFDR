#ifndef DETECT_H
#define DETECT_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define DETECT_API __declspec(dllexport)
#else
#define DETECT_API __attribute__((visibility("default")))
#endif

/** 插损/回损计算配置（对应 Sample.ipynb 的可调参数） */
typedef struct {
    double nf_q;                 /**< 基线分位数 (%)，越高越贴近噪声上包络 */
    double nf_radius_m;          /**< 滑动基线窗口半径 (m) */
    size_t pk_baseline_win;      /**< 峰前基线窗口点数 */
    double pk_min_prominence_db; /**< 最小峰突出度 (dB) */
    double pk_min_distance_m;    /**< 相邻峰最小间距 (m) */
    double pk_exclude_radius_m;  /**< 峰排除窗口半径 (m) */
    double pk_seg_percentile;    /**< 段内噪声顶分位数 (%) */
    double pk_last_seg_tail_m;   /**< 最后一段噪声顶结束距离偏移 (m) */
} DetectConfig;

/** 检测结果，由 detect_result_free 释放 */
typedef struct {
    int32_t error;         /**< 错误码：0 成功，非 0 失败 */
    const char *error_msg; /**< 错误消息（失败时有效），由 detect_result_free 释放 */
    double *ps_db;         /**< dB 功率谱，长度 len */
    double *baseline_db;   /**< 滑动分位数基线 (dB)，长度 len */
    size_t len;            /**< ps_db / baseline_db 长度 */
    size_t n_peaks;        /**< 峰数量 */
    double *peak_loc;      /**< 峰位置 (m)，长度 n_peaks */
    double *peak_height;   /**< 峰高 (dB)，长度 n_peaks */
    double *peak_prominence; /**< 峰突出度 (dB)，长度 n_peaks */
    double *peak_base;     /**< 峰前基线窗口均值 (dB)，长度 n_peaks */
    size_t n_segments;     /**< 基线段数量 */
    size_t *seg_start;     /**< 段起始索引（含），长度 n_segments */
    size_t *seg_end;       /**< 段结束索引（不含），长度 n_segments */
    double *seg_noise_floor; /**< 段内噪声顶 (dB)，长度 n_segments */
    size_t n_events;       /**< 事件数量（= n_peaks） */
    double *event_loc;     /**< 事件位置 (m)，长度 n_events */
    double *event_peak;    /**< 事件峰高 (dB)，长度 n_events */
    double *event_il;      /**< 事件插损 (dB)，长度 n_events；首段无前基线时为 NAN */
    double *event_rl;      /**< 事件回损 (dB)，长度 n_events */
    double *event_base_before; /**< 事件前基线段噪声顶 (dB)，长度 n_events；首段为 NAN */
    double *event_base_after;  /**< 事件后基线段噪声顶 (dB)，长度 n_events；段数不足时为 NAN */
} DetectResult;

/** 返回检测默认配置 */
DETECT_API DetectConfig detect_config_default(void);

/**
 * 完整检测流程：线性幅值谱输入
 *
 * @param dist 距离轴 (m)，须严格递增且等间距
 * @param ps   联合功率谱（线性幅值，与 ofdr 库输出一致）
 * @param len  数组长度
 * @param cfg  检测配置（不可为 NULL）
 * @return 检测结果，使用后必须调用 detect_result_free 释放
 */
DETECT_API DetectResult detect_process(const double *dist, const double *ps,
                                       size_t len, const DetectConfig *cfg);

/**
 * 完整检测流程：dB 谱输入
 *
 * @param dist  距离轴 (m)，须严格递增且等间距
 * @param ps_db 功率谱 (dB)
 * @param len   数组长度
 * @param cfg   检测配置（不可为 NULL）
 * @return 检测结果，使用后必须调用 detect_result_free 释放
 */
DETECT_API DetectResult detect_process_db(const double *dist, const double *ps_db,
                                          size_t len, const DetectConfig *cfg);

/** 释放 detect_process / detect_process_db 返回的结果 */
DETECT_API void detect_result_free(DetectResult *result);

#ifdef __cplusplus
}
#endif

#endif /* DETECT_H */
