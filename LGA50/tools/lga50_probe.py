"""LGA50-D 远程控制协议探测工具（仅标准库）。

用途：连上 192.168.2.26:4300，抓取原始字节，验证厂商 PDF 中未写清的帧格式。

用法示例：
  python lga50_probe.py --host 192.168.2.26 --hello
  python lga50_probe.py --host 192.168.2.26 --send LENGTH --wait 2 --save length.bin
  python lga50_probe.py --host 192.168.2.26 --script "RAW_DATA_ON|START|MODE_1" --gap 0.5 --duration 5 --save raw.bin

所有收发字节原样落盘，不做任何解码，便于事后按 10/12 字节切帧反推。
"""

import argparse
import socket
import sys
import time


def hexdump(data: bytes, limit: int = 4096) -> str:
    """返回十六进制 + ASCII 视图，超长只显示首尾。"""
    shown = data[:limit]
    lines = []
    for off in range(0, len(shown), 16):
        chunk = shown[off:off + 16]
        hexpart = " ".join(f"{b:02X}" for b in chunk)
        asc = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        lines.append(f"{off:08X}  {hexpart:<47}  |{asc}|")
    if len(data) > limit:
        lines.append(f"... 共 {len(data)} 字节，仅显示前 {limit} 字节；尾部 32 字节：")
        tail = data[-32:]
        lines.append(" ".join(f"{b:02X}" for b in tail))
        lines.append("".join(chr(b) if 32 <= b < 127 else "." for b in tail))
    return "\n".join(lines)


def recv_until_idle(sock: socket.socket, idle_s: float, max_s: float) -> bytes:
    """持续收包，直到 idle_s 秒内没有新数据，或超过 max_s 秒。"""
    buf = bytearray()
    start = time.time()
    sock.settimeout(idle_s)
    while time.time() - start < max_s:
        try:
            chunk = sock.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        buf += chunk
    return bytes(buf)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="192.168.2.26")
    ap.add_argument("--port", type=int, default=4300)
    ap.add_argument("--hello", action="store_true", help="只连接并读取服务端主动下发的字节")
    ap.add_argument("--send", action="append", default=[], help="要发送的指令，可重复")
    ap.add_argument("--script", default=None, help="竖线分隔的指令序列，按顺序发送")
    ap.add_argument("--gap", type=float, default=0.5, help="指令之间的间隔（秒）")
    ap.add_argument("--wait", type=float, default=1.0, help="单条指令后收包的空闲判定时长（秒）")
    ap.add_argument("--duration", type=float, default=3.0, help="最后一段持续收包的总时长（秒）")
    ap.add_argument("--save", default=None, help="把收到的全部字节写入该文件")
    ap.add_argument("--raw-stream", action="store_true", help="只打印字节数与首尾摘要，避免刷屏")
    ap.add_argument("--no-greeting", action="store_true",
                    help="连接后不先读握手，立即发送指令（扫描进行中时用它抢在数据流前面发 STOP）")
    args = ap.parse_args()

    cmds = list(args.send)
    if args.script:
        cmds += [c for c in args.script.split("|") if c]

    sock = socket.create_connection((args.host, args.port), timeout=5)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    print(f"[connected] {args.host}:{args.port}")

    received = bytearray()

    def show(tag: str, data: bytes) -> None:
        if not data:
            print(f"[{tag}] <无数据>")
            return
        print(f"[{tag}] {len(data)} 字节")
        if args.raw_stream:
            print("  首 64 字节:", " ".join(f"{b:02X}" for b in data[:64]))
            print("  尾 64 字节:", " ".join(f"{b:02X}" for b in data[-64:]))
        else:
            print(hexdump(data))

    greeting = b""
    if not args.no_greeting:
        greeting = recv_until_idle(sock, idle_s=1.0, max_s=3.0)
        received += greeting
        show("greeting", greeting)

    if not args.hello:
        for i, cmd in enumerate(cmds):
            payload = cmd.encode("ascii")
            sock.sendall(payload)
            print(f"[sent] {cmd!r} ({len(payload)} 字节)")
            gap = args.duration if i == len(cmds) - 1 else args.wait
            data = recv_until_idle(sock, idle_s=args.wait, max_s=gap)
            received += data
            show(f"after {cmd}", data)
            if i != len(cmds) - 1:
                time.sleep(args.gap)

    sock.close()

    print(f"[total] {len(received)} 字节")
    if len(received) > 1:
        print(f"  (total-1)/10 = {(len(received) - 1) / 10}")
        print(f"  (total-1)/12 = {(len(received) - 1) / 12}")
    if args.save:
        with open(args.save, "wb") as f:
            f.write(received)
        print(f"[saved] {args.save}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
