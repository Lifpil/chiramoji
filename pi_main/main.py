import sys
import machine
import framebuf
import time
import select
import micropython

# 謖・ｮ壹＆繧後◆縲悟虚縺上さ繝ｼ繝峨阪・讒矩繧・00%邯呎価
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
        # 遒ｺ螳溘↓譏繧区ｨ呎ｺ悶さ繝槭Φ繝峨そ繝・ヨ
        cmds = [0xAE, 0x00, 0x10, 0x40, 0xB0, 0x81, 0x80, 0xA0, 0xA4, 0xA6, 0xA8, 0x3F, 0xAD, 0x81, 0xD3, 0x00, 0xD5, 0x50, 0xD9, 0x22, 0xDB, 0x40, 0xDC, 0x03, 0xAF]
        for c in cmds: self.write_cmd(c)

    def show(self):
        self.write_cmd(0x00); self.write_cmd(0x10); self.write_cmd(0xB0)
        self.dc.value(1); self.cs.value(0)
        self.spi.write(self.buffer)
        self.cs.value(1)

brightness_buf = bytearray(1)  # 譏弱ｋ縺募・譛峨ヰ繝・ヵ繧｡ (0-255, viper縺ｮptr8蟇ｾ蠢・
frame_buf = bytearray(1)       # 繝輔Ξ繝ｼ繝繧ｫ繧ｦ繝ｳ繧ｿ (0-15)

@micropython.viper
def expand_1bit_to_4bit(src: ptr8, dst: ptr8, brightness_ptr: ptr8, frame_ptr: ptr8):
    # brightness: 荳贋ｽ・繝薙ャ繝・= 蝓ｺ貅冶ｼ晏ｺｦ(0-15), 荳倶ｽ・繝薙ャ繝・= 繧ｵ繝悶せ繝・ャ繝・
    b = int(brightness_ptr[0])
    base = b >> 4          # 0-15: 蝓ｺ貅冶ｼ晏ｺｦ
    frac = b & 0x0F        # 0-15: 縺薙・蜑ｲ蜷医〒 base+1 繧剃ｽｿ縺・
    frame = int(frame_ptr[0])  # 0-15: 繝輔Ξ繝ｼ繝繧ｫ繧ｦ繝ｳ繧ｿ
    
    # 繝・Φ繝昴Λ繝ｫ繝・ぅ繧ｶ: frame縺掲rac譛ｪ貅縺ｪ繧叡ase+1繧剃ｽｿ逕ｨ
    if frame < frac:
        b_val = base + 1
        if b_val > 15:
            b_val = 15
    else:
        b_val = base
    
    on_h = b_val << 4
    on_l = b_val
    for i in range(2048):
        bb = int(src[i])
        idx = i << 2
        # MSB first縺ｧ螻暮幕 + 霈晏ｺｦ繧ｹ繧ｱ繝ｼ繝ｪ繝ｳ繧ｰ
        dst[idx]   = (on_h if bb & 0x80 else 0) | (on_l if bb & 0x40 else 0)
        dst[idx+1] = (on_h if bb & 0x20 else 0) | (on_l if bb & 0x10 else 0)
        dst[idx+2] = (on_h if bb & 0x08 else 0) | (on_l if bb & 0x04 else 0)
        dst[idx+3] = (on_h if bb & 0x02 else 0) | (on_l if bb & 0x01 else 0)

# 繧ｻ繝・ヨ繧｢繝・・
spi = machine.SPI(0, baudrate=10000000)
oled = SH1122(256, 64, spi, machine.Pin(20, machine.Pin.OUT), machine.Pin(21, machine.Pin.OUT), machine.Pin(17, machine.Pin.OUT))
oled.init_display()
sys.stdout.write("FW:v1.0.2\n")
sys.stdout.flush()

def show_idle_screen():
    oled.framebuf.fill(0)
    # MicroPython built-in text is ASCII-only, so use romanized label here.
    oled.framebuf.text("CHIRAMOJI", 56, 20, 15)
    oled.framebuf.text("v1.0.1", 92, 36, 15)
    oled.show()

show_idle_screen()

# kbd_intr(-1): 繝舌う繝翫Μ蜿嶺ｿ｡荳ｭ縺ｫCtrl-C繝舌う繝・0x03)縺悟牡繧願ｾｼ縺ｾ縺ｪ縺・ｈ縺・↓縺吶ｋ
# 縺薙ｌ縺後↑縺・→繝舌う繝翫Μ繝・・繧ｿ縺碁比ｸｭ縺ｧ蛻・妙縺輔ｌREADY縺九ｉ騾ｲ縺ｾ縺ｪ縺・
micropython.kbd_intr(-1)
poll = select.poll()
poll.register(sys.stdin, select.POLLIN)

FRAME_SIZE = 2049
buffer = bytearray()

while True:
    events = poll.poll(500)
    
    if events:
        chunk = sys.stdin.buffer.read(FRAME_SIZE - len(buffer))
        if chunk:
            buffer.extend(chunk)
        
        # 繝舌ャ繝輔ぃ縺ｫ螳悟・縺ｪ繝輔Ξ繝ｼ繝縺梧純縺｣縺溘ｉ蜃ｦ逅・ｼ・=縺ｧ隍・焚繝輔Ξ繝ｼ繝繧ょｯｾ蠢懶ｼ・
        while len(buffer) >= FRAME_SIZE:
            frame = buffer[:FRAME_SIZE]
            buffer = bytearray(buffer[FRAME_SIZE:])  # 谿九ｊ繧呈ｬ｡繝輔Ξ繝ｼ繝逕ｨ縺ｫ菫晄戟
            
            brightness = frame[0]
            # viper縺ｮptr8縺ｯ繧ｹ繝ｩ繧､繧ｹ繧貞女縺大叙繧後↑縺・・縺ｧbytearray縺ｫ繧ｳ繝斐・
            pixels = bytearray(frame[1:])
            
            brightness_buf[0] = brightness
            # 繝輔Ξ繝ｼ繝繧ｫ繧ｦ繝ｳ繧ｿ繧帝ｲ繧√ｋ (0-15縺ｮ繝ｫ繝ｼ繝・
            frame_buf[0] = (frame_buf[0] + 1) % 16
            expand_1bit_to_4bit(pixels, oled.buffer, brightness_buf, frame_buf)
            oled.show()
    else:
        # 繧ｿ繧､繝繧｢繧ｦ繝・ 荳榊ｮ悟・縺ｪ繝舌ャ繝輔ぃ繧堤ｴ譽・＠縺ｦ蜷梧悄繧偵Μ繧ｻ繝・ヨ
        if len(buffer) > 0:
            buffer = bytearray()






