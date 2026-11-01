import ctypes as C
from pathlib import Path

_HERE = Path(__file__).resolve().parent
DLL_PATH = _HERE / "detect.dll"
if not DLL_PATH.exists():
    DLL_PATH = _HERE.parent / "target" / "release" / "detect.dll"


class DetectConfig(C.Structure):
    """与 include/detect.h 中 DetectConfig 字段一一对应"""
    _fields_ = [
        ("nf_q", C.c_double),
        ("nf_radius_m", C.c_double),
        ("pk_baseline_win", C.c_size_t),
        ("pk_min_prominence_db", C.c_double),
        ("pk_min_distance_m", C.c_double),
        ("pk_exclude_radius_m", C.c_double),
        ("pk_seg_percentile", C.c_double),
        ("pk_last_seg_tail_m", C.c_double),
    ]


class DetectResult(C.Structure):
    """与 include/detect.h 中 DetectResult 字段一一对应"""
    _fields_ = [
        ("error", C.c_int32),
        ("error_msg", C.c_void_p),
        ("ps_db", C.POINTER(C.c_double)),
        ("baseline_db", C.POINTER(C.c_double)),
        ("len", C.c_size_t),
        ("n_peaks", C.c_size_t),
        ("peak_loc", C.POINTER(C.c_double)),
        ("peak_height", C.POINTER(C.c_double)),
        ("peak_prominence", C.POINTER(C.c_double)),
        ("peak_base", C.POINTER(C.c_double)),
        ("n_segments", C.c_size_t),
        ("seg_start", C.POINTER(C.c_size_t)),
        ("seg_end", C.POINTER(C.c_size_t)),
        ("seg_noise_floor", C.POINTER(C.c_double)),
        ("n_events", C.c_size_t),
        ("event_loc", C.POINTER(C.c_double)),
        ("event_peak", C.POINTER(C.c_double)),
        ("event_il", C.POINTER(C.c_double)),
        ("event_rl", C.POINTER(C.c_double)),
        ("event_base_before", C.POINTER(C.c_double)),
        ("event_base_after", C.POINTER(C.c_double)),
    ]


detect = C.CDLL(str(DLL_PATH))
detect.detect_config_default.restype = DetectConfig
detect.detect_config_default.argtypes = []
detect.detect_process.restype = DetectResult
detect.detect_process.argtypes = [
    C.POINTER(C.c_double),
    C.POINTER(C.c_double),
    C.c_size_t,
    C.POINTER(DetectConfig),
]
detect.detect_process_db.restype = DetectResult
detect.detect_process_db.argtypes = [
    C.POINTER(C.c_double),
    C.POINTER(C.c_double),
    C.c_size_t,
    C.POINTER(DetectConfig),
]
detect.detect_result_free.restype = None
detect.detect_result_free.argtypes = [C.POINTER(DetectResult)]


def as_array(ptr, n):
    if n == 0:
        return None
    return C.cast(ptr, C.POINTER(C.c_double * n)).contents[:].copy()


def as_index(ptr, n):
    if n == 0:
        return None
    return C.cast(ptr, C.POINTER(C.c_size_t * n)).contents[:].copy()


def run(dist, ps, cfg=None):
    """运行完整检测流程，返回结果字典；线性幅值谱输入。"""
    if cfg is None:
        cfg = detect.detect_config_default()
    ptr = lambda a: a.ctypes.data_as(C.POINTER(C.c_double))
    res = detect.detect_process(ptr(dist), ptr(ps), len(ps), C.byref(cfg))
    return _to_dict(res)


def run_db(dist, ps_db, cfg=None):
    """运行完整检测流程，返回结果字典；已为 dB 谱输入。"""
    if cfg is None:
        cfg = detect.detect_config_default()
    ptr = lambda a: a.ctypes.data_as(C.POINTER(C.c_double))
    res = detect.detect_process_db(ptr(dist), ptr(ps_db), len(ps_db), C.byref(cfg))
    return _to_dict(res)


def _to_dict(res):
    assert res.error == 0, f"检测失败: error={res.error}"
    out = {
        "len": res.len,
        "ps_db": as_array(res.ps_db, res.len),
        "baseline_db": as_array(res.baseline_db, res.len),
        "n_peaks": res.n_peaks,
        "peak_loc": as_array(res.peak_loc, res.n_peaks),
        "peak_height": as_array(res.peak_height, res.n_peaks),
        "peak_prominence": as_array(res.peak_prominence, res.n_peaks),
        "peak_base": as_array(res.peak_base, res.n_peaks),
        "n_segments": res.n_segments,
        "seg_start": as_index(res.seg_start, res.n_segments),
        "seg_end": as_index(res.seg_end, res.n_segments),
        "seg_noise_floor": as_array(res.seg_noise_floor, res.n_segments),
        "n_events": res.n_events,
        "event_loc": as_array(res.event_loc, res.n_events),
        "event_peak": as_array(res.event_peak, res.n_events),
        "event_il": as_array(res.event_il, res.n_events),
        "event_rl": as_array(res.event_rl, res.n_events),
        "event_base_before": as_array(res.event_base_before, res.n_events),
        "event_base_after": as_array(res.event_base_after, res.n_events),
    }
    detect.detect_result_free(C.byref(res))
    return out
