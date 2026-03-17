import sys
import machine
import framebuf
import time
import select
import micropython

# 指定された「動くコード」の構造を100%継承
class SH1122:
    def __init__(self, width, height, spi, dc, rst, cs):
        self.width, self.height = width, height
        self.spi, self.dc, self.rst, self.cs = spi, dc, rst, cs
        self.buffer = bytearray(self.width * self.height // 2)
        self.framebuf = framebuf.FrameBuffer(self.buffer, width, height, framebuf.GS4_HMSB)

    def write_cmd(self, cmd):
        self.dc.value(0); self.cs.value(0)
        self.spi.write(bytearray([cmd]))
        self.cs.value(1)

    def init_display(self):
        self.rst.value(0); time.sleep(0.1); self.rst.value(1); time.sleep(0.1)
        # 確実に映る標準コマンドセット
        cmds = [0xAE, 0x00, 0x10, 0x40, 0xB0, 0x81, 0x80, 0xA0, 0xA4, 0xA6, 0xA8, 0x3F, 0xAD, 0x81, 0xD3, 0x00, 0xD5, 0x50, 0xD9, 0x22, 0xDB, 0x40, 0xDC, 0x03, 0xAF]
        for c in cmds: self.write_cmd(c)

    def show(self):
        self.write_cmd(0x00); self.write_cmd(0x10); self.write_cmd(0xB0)
        self.dc.value(1); self.cs.value(0)
        self.spi.write(self.buffer)
        self.cs.value(1)

@micropython.viper
def expand_1bit_to_4bit(src: ptr8, dst: ptr8):
    for i in range(2048):
        b = int(src[i])
        idx = i << 2
        # MSB first で展開
        dst[idx]   = (0xF0 if b & 0x80 else 0) | (0x0F if b & 0x40 else 0)
        dst[idx+1] = (0xF0 if b & 0x20 else 0) | (0x0F if b & 0x10 else 0)
        dst[idx+2] = (0xF0 if b & 0x08 else 0) | (0x0F if b & 0x04 else 0)
        dst[idx+3] = (0xF0 if b & 0x02 else 0) | (0x0F if b & 0x01 else 0)

# セットアップ
spi = machine.SPI(0, baudrate=10000000)
oled = SH1122(256, 64, spi, machine.Pin(20, machine.Pin.OUT), machine.Pin(21, machine.Pin.OUT), machine.Pin(17, machine.Pin.OUT))
oled.init_display()
oled.framebuf.fill(0)
oled.framebuf.text("READY", 0, 0, 15)
oled.show()

# 通信の安定化
micropython.kbd_intr(-1) # 通信中のCtrl-Cによるフリーズを回路側で無視
poll = select.poll()
poll.register(sys.stdin, select.POLLIN)

FRAME_SIZE = 2048
buffer = bytearray()

while True:
    # タイムアウトを 500ms に。
    # これにより、PC側の送信が少し遅れてもバッファが破棄されず、ノイズが止まります。
    events = poll.poll(500)
    
    if events:
        chunk = sys.stdin.buffer.read(FRAME_SIZE - len(buffer))
        if chunk:
            buffer.extend(chunk)
            if len(buffer) == FRAME_SIZE:
                expand_1bit_to_4bit(buffer, oled.buffer)
                oled.show()
                buffer = bytearray() 
    else:
        if len(buffer) > 0:
            buffer = bytearray() # 0.5秒音沙汰がない時だけリセット
