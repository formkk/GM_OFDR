import ctypes as C
from pathlib import Path

_HERE = Path(__file__).resolve().parent
DLL_PATH = _HERE / "ofdr.dll"
if not DLL_PATH.exists():
    DLL_PATH = _HERE.parent / "target" / "release" / "ofdr.dll"


class OfdrConfig(C.Structure):
    """与 include/ofdr.h 中 OfdrConfig 字段一一对应"""
    _fields_ = [
        ("fs", C.c_double),
        ("dist_scale", C.c_double),
    ]


class OfdrResult(C.Structure):
    """与 include/ofdr.h 中 OfdrResult 字段一一对应"""
    _fields_ = [
        ("error", C.c_int32),
        ("error_msg", C.c_void_p),
        ("dist", C.POINTER(C.c_double)),
        ("freq", C.POINTER(C.c_double)),
        ("ps", C.POINTER(C.c_double)),
        ("len", C.c_size_t),
    ]


ofdr = C.CDLL(str(DLL_PATH))
ofdr.ofdr_config_default.restype = OfdrConfig
ofdr.ofdr_config_default.argtypes = []
ofdr.ofdr_process.restype = OfdrResult
ofdr.ofdr_process.argtypes = [
    C.POINTER(C.c_double),
    C.POINTER(C.c_double),
    C.POINTER(C.c_double),
    C.c_size_t,
    C.POINTER(OfdrConfig),
]
ofdr.ofdr_result_free.restype = None
ofdr.ofdr_result_free.argtypes = [C.POINTER(OfdrResult)]
