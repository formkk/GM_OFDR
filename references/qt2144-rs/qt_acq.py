import ctypes
import os
from pathlib import Path
from typing import List, Optional

DEFAULT_CARD_INDEX = 0

QT_ACQ_OK = 0
STATUS = {
    0: "OK",
    -1: "UNKNOWN / I/O error",
    -2: "SDK",
    -3: "INVALID_ARGUMENT",
    -4: "INVALID_HANDLE",
    -5: "NULL_POINTER",
    -6: "BUFFER_TOO_SMALL",
    -7: "NOT_CONFIGURED",
}

TRIGGER_SOFTWARE = 0
TRIGGER_EXT_RISING = 1
TRIGGER_EXT_FALLING = 2


class QtAcqError(RuntimeError):
    pass


def _dll_candidates() -> List[str]:
    here = Path(__file__).resolve().parent
    paths = []
    env = os.environ.get("QT_ACQ_DLL")
    if env:
        paths.append(env)
    paths += [
        str(here / "qt_acq.dll"),
        str(here.parent / "target" / "release" / "qt_acq.dll"),
        "qt_acq.dll",
    ]
    return paths


def _load_dll() -> ctypes.CDLL:
    last = None
    for cand in _dll_candidates():
        if not os.path.isfile(cand):
            continue
        try:
            return ctypes.CDLL(cand)
        except OSError as e:
            last = e
    raise QtAcqError(
        f"cannot load qt_acq.dll (searched: {_dll_candidates()}); "
        f"last error: {last}"
    ) from last


class _Lib:
    def __init__(self) -> None:
        dll = _load_dll()
        self.handle_t = ctypes.c_void_p

        dll.qt_acq_open.restype = ctypes.c_int
        dll.qt_acq_open.argtypes = [ctypes.c_uint32, ctypes.POINTER(self.handle_t)]

        dll.qt_acq_close.restype = ctypes.c_int
        dll.qt_acq_close.argtypes = [self.handle_t]

        dll.qt_acq_configure.restype = ctypes.c_int
        dll.qt_acq_configure.argtypes = [
            self.handle_t,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_int,
            ctypes.c_uint32,
        ]

        dll.qt_acq_start.restype = ctypes.c_int
        dll.qt_acq_start.argtypes = [self.handle_t]

        dll.qt_acq_read.restype = ctypes.c_int
        dll.qt_acq_read.argtypes = [
            self.handle_t,
            ctypes.c_void_p,
            ctypes.c_uint32,
            ctypes.POINTER(ctypes.c_uint32),
        ]

        dll.qt_acq_stop.restype = ctypes.c_int
        dll.qt_acq_stop.argtypes = [self.handle_t]

        dll.qt_acq_calc_buffer_bytes.restype = ctypes.c_int
        dll.qt_acq_calc_buffer_bytes.argtypes = [
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.POINTER(ctypes.c_uint32),
        ]

        dll.qt_acq_last_error.restype = ctypes.c_int
        dll.qt_acq_last_error.argtypes = [ctypes.c_char_p, ctypes.c_size_t]

        dll.qt_acq_version.restype = ctypes.c_int
        dll.qt_acq_version.argtypes = [ctypes.c_char_p, ctypes.c_size_t]

        self.dll = dll

    def last_error(self) -> str:
        buf = ctypes.create_string_buffer(512)
        n = self.dll.qt_acq_last_error(buf, len(buf))
        if n <= 0:
            return ""
        return buf.value.decode("utf-8", "replace")


_LIB = None


def _lib() -> _Lib:
    global _LIB
    if _LIB is None:
        _LIB = _Lib()
    return _LIB


class QtAcq:
    def __init__(self, card_index: int = DEFAULT_CARD_INDEX) -> None:
        self._handle = None
        self.card_index = card_index
        self._cfg = None
        self.open()

    def open(self) -> None:
        if self._handle is not None:
            return
        lib = _lib()
        out = ctypes.c_void_p()
        rc = lib.dll.qt_acq_open(self.card_index, ctypes.byref(out))
        if rc != QT_ACQ_OK:
            self._raise(rc)
        self._handle = out.value

    def close(self) -> None:
        if self._handle is not None:
            _lib().dll.qt_acq_close(self._handle)
            self._handle = None

    def __enter__(self) -> "QtAcq":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def configure(
        self,
        sample_rate_mhz: int,
        acq_time_ms: int,
        seg_count: int,
        trigger: int = TRIGGER_SOFTWARE,
        trigger_delay_ms: int = 0,
    ) -> None:
        self._check(
            _lib().dll.qt_acq_configure(
                self._h(),
                sample_rate_mhz,
                acq_time_ms,
                seg_count,
                trigger,
                trigger_delay_ms,
            )
        )
        self._cfg = (sample_rate_mhz, acq_time_ms, seg_count)

    def configure_file(self, path: str) -> None:
        with open(path, "r", encoding="utf-8") as fp:
            lines = [ln.strip() for ln in fp if ln.strip()]
        if len(lines) < 5:
            raise QtAcqError(
                f"{path}: expected at least 5 lines "
                "(sample rate MHz, acq time ms, seg count, trigger, trigger delay ms)"
            )
        self.configure(
            sample_rate_mhz=int(lines[0]),
            acq_time_ms=int(lines[1]),
            seg_count=int(lines[2]),
            trigger=int(lines[3]),
            trigger_delay_ms=int(lines[4]),
        )

    def start(self) -> None:
        self._check(_lib().dll.qt_acq_start(self._h()))

    def read(self, capacity: Optional[int] = None) -> bytes:
        if capacity is None:
            if self._cfg is None:
                raise QtAcqError("call configure() first or pass capacity explicitly")
            capacity = self.required_bytes(*self._cfg)
        buf = ctypes.create_string_buffer(capacity)
        n = ctypes.c_uint32()
        rc = _lib().dll.qt_acq_read(
            self._h(), ctypes.byref(buf), capacity, ctypes.byref(n)
        )
        if rc != QT_ACQ_OK:
            self._raise(rc)
        return buf.raw[: n.value]

    def stop(self) -> None:
        self._check(_lib().dll.qt_acq_stop(self._h()))

    @staticmethod
    def required_bytes(sample_rate_mhz: int, acq_time_ms: int, seg_count: int) -> int:
        out = ctypes.c_uint32()
        rc = _lib().dll.qt_acq_calc_buffer_bytes(
            sample_rate_mhz, acq_time_ms, seg_count, ctypes.byref(out)
        )
        if rc != QT_ACQ_OK:
            msg = _lib().last_error()
            name = STATUS.get(rc, f"code {rc}")
            if msg:
                raise QtAcqError(f"qt_acq error {name} ({rc}): {msg}")
            raise QtAcqError(f"qt_acq error {name} ({rc})")
        return out.value

    def version(self) -> str:
        buf = ctypes.create_string_buffer(64)
        _lib().dll.qt_acq_version(buf, len(buf))
        return buf.value.decode("utf-8", "replace")

    def _h(self) -> ctypes.c_void_p:
        if self._handle is None:
            raise QtAcqError("handle is closed")
        return self._handle

    def _check(self, rc: int) -> None:
        if rc != QT_ACQ_OK:
            self._raise(rc)

    def _raise(self, rc: int) -> None:
        msg = _lib().last_error()
        name = STATUS.get(rc, f"code {rc}")
        if msg:
            raise QtAcqError(f"qt_acq error {name} ({rc}): {msg}")
        raise QtAcqError(f"qt_acq error {name} ({rc})")


def _demo() -> None:
    import time

    with QtAcq() as dev:
        print(f"qt_acq version: {dev.version()}")
        print(f"board {dev.card_index} opened")
        dev.configure(sample_rate_mhz=250, acq_time_ms=10, seg_count=1)
        n = dev.required_bytes(250, 10, 1)
        print(f"required bytes: {n}")

        t0 = time.time()
        dev.start()
        data = dev.read(n)
        dt = time.time() - t0
        print(f"acquired {len(data)} bytes in {dt:.2f}s")
        dev.stop()


if __name__ == "__main__":
    _demo()
