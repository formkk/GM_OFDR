"""LGA50-D 参数响应矩阵探针：逐个用例建连、设置、采集 2 秒结果帧并汇总。

用法： python probe_matrix.py --host 192.168.2.26
"""

import argparse
import socket
import time

FRAME_LEN = 25


def read_for(sock: socket.socket, seconds: float, idle: float = 0.8) -> bytes:
    buf = bytearray()
    start = time.time()
    sock.settimeout(idle)
    while time.time() - start < seconds:
        try:
            chunk = sock.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        buf += chunk
    return bytes(buf)


def parse_frames(data: bytes):
    """按 '!' 切帧，返回 (长度, 回损) 列表；非 25 字节帧丢弃。"""
    out = []
    pos = 0
    while True:
        i = data.find(b"!", pos)
        if i < 0:
            break
        body = data[pos:i]
        pos = i + 1
        if len(body) != FRAME_LEN - 1:
            continue
        try:
            out.append((float(body[:11]), float(body[11:23])))
        except ValueError:
            pass
    return out


def run_case(host: str, port: int, cmds, capture_s: float = 2.0):
    sock = socket.create_connection((host, port), timeout=5)
    sock.sendall(b"STOP")
    read_for(sock, 0.4)
    replies = []
    for cmd in cmds:
        sock.sendall(cmd.encode("ascii"))
        replies.append((cmd, read_for(sock, 0.4)))
    sock.sendall(b"START")
    data = read_for(sock, capture_s)
    sock.sendall(b"STOP")
    read_for(sock, 0.3)
    sock.close()
    frames = parse_frames(data)
    return replies, frames, len(data)


def summarize(frames):
    if not frames:
        return "无有效帧"
    lens = [f[0] for f in frames]
    rls = [f[1] for f in frames]
    return (f"帧数={len(frames)} 长度={lens[0]:.6f}~{lens[-1]:.6f}m "
            f"回损={rls[0]:.3f}~{rls[-1]:.3f}dB")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="192.168.2.26")
    ap.add_argument("--port", type=int, default=4300)
    args = ap.parse_args()

    cases = [
        ("基线", ["RAW_DATA_OFF"]),
        ("起点0.000", ["RAW_DATA_OFF", "FIBER_START_0.000"]),
        ("起点0.500", ["RAW_DATA_OFF", "FIBER_START_0.500"]),
        ("起点2.000", ["RAW_DATA_OFF", "FIBER_START_2.000"]),
        ("起点3.000", ["RAW_DATA_OFF", "FIBER_START_3.000"]),
        ("回损点0.50/窗口1.00", ["RAW_DATA_OFF", "FIBER_START_0.000", "WIN_LEN_1.00", "RT_LOCATION_0.50"]),
        ("回损点1.10/窗口1.00", ["RAW_DATA_OFF", "FIBER_START_0.000", "WIN_LEN_1.00", "RT_LOCATION_1.10"]),
        ("回损点2.11/窗口0.10", ["RAW_DATA_OFF", "FIBER_START_0.000", "WIN_LEN_0.10", "RT_LOCATION_2.11"]),
        ("回损点2.11/窗口0.50", ["RAW_DATA_OFF", "FIBER_START_0.000", "WIN_LEN_0.50", "RT_LOCATION_2.11"]),
        ("回损点2.11/窗口2.00", ["RAW_DATA_OFF", "FIBER_START_0.000", "WIN_LEN_2.00", "RT_LOCATION_2.11"]),
        ("模式2", ["RAW_DATA_OFF", "FIBER_START_0.000", "MODE_2"]),
        ("延时5000ms", ["RAW_DATA_OFF", "FIBER_START_0.000", "DELAY_5000"]),
        ("延时0ms", ["RAW_DATA_OFF", "FIBER_START_0.000", "DELAY_0"]),
    ]

    for name, cmds in cases:
        try:
            replies, frames, nbytes = run_case(args.host, args.port, cmds)
            bad = [(c, r) for c, r in replies if r not in (b"SETOK",)]
            tag = "" if not bad else f" 异常回执={bad}"
        except Exception as exc:  # 连接层异常直接暴露，便于判断是否掉线
            print(f"{name}: 失败 {exc}")
            continue
        fps = len(frames) / 2.0
        print(f"{name}: {summarize(frames)} | {nbytes}字节 {fps:.1f}帧/秒{tag}")

    # 收尾：恢复模式 1、起点 0，停止扫描
    sock = socket.create_connection((args.host, args.port), timeout=5)
    for cmd in (b"STOP", b"MODE_1", b"FIBER_START_0.000", b"RAW_DATA_OFF"):
        sock.sendall(cmd)
        read_for(sock, 0.3)
    sock.close()
    print("收尾完成：STOP / MODE_1 / FIBER_START_0.000 / RAW_DATA_OFF")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
