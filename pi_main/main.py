import sys
import machine
import framebuf
import time
import select
import micropython
import os

FW_VERSION = "v1.0.0"
ANNOUNCE_INTERVAL_MS = 2000
UPDATE_MAGIC = b"CMJUPD::"
UPDATE_HEADER_LEN = 11
CMD_UPDATE_BEGIN = 1
CMD_UPDATE_CHUNK = 2
CMD_UPDATE_END = 3
UPDATE_TMP_PATH = "main.new.py"
UPDATE_BAK_PATH = "main.bak.py"


class SH1122:
    def __init__(self, width, height, spi, dc, rst, cs):
        self.width, self.height = width, height
        self.spi, self.dc, self.rst, self.cs = spi, dc, rst, cs
        self.buffer = bytearray(self.width * self.height // 2)
        self.framebuf = framebuf.FrameBuffer(self.buffer, width, height, framebuf.GS4_HMSB)

    def write_cmd(self, cmd):
        self.dc.value(0)
        self.cs.value(0)
        self.spi.write(bytearray([cmd]))
        self.cs.value(1)

    def init_display(self):
        self.rst.value(0)
        time.sleep(0.1)
        self.rst.value(1)
        time.sleep(0.1)
        cmds = [
            0xAE, 0x00, 0x10, 0x40, 0xB0, 0x81, 0x80, 0xA0, 0xA4,
            0xA6, 0xA8, 0x3F, 0xAD, 0x81, 0xD3, 0x00, 0xD5, 0x50,
            0xD9, 0x22, 0xDB, 0x40, 0xDC, 0x03, 0xAF,
        ]
        for c in cmds:
            self.write_cmd(c)

    def show(self):
        self.write_cmd(0x00)
        self.write_cmd(0x10)
        self.write_cmd(0xB0)
        self.dc.value(1)
        self.cs.value(0)
        self.spi.write(self.buffer)
        self.cs.value(1)


brightness_buf = bytearray(1)
frame_buf = bytearray(1)


@micropython.viper
def expand_1bit_to_4bit(src: ptr8, dst: ptr8, brightness_ptr: ptr8, frame_ptr: ptr8):
    b = int(brightness_ptr[0])
    base = b >> 4
    frac = b & 0x0F
    frame = int(frame_ptr[0])

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
        dst[idx] = (on_h if bb & 0x80 else 0) | (on_l if bb & 0x40 else 0)
        dst[idx + 1] = (on_h if bb & 0x20 else 0) | (on_l if bb & 0x10 else 0)
        dst[idx + 2] = (on_h if bb & 0x08 else 0) | (on_l if bb & 0x04 else 0)
        dst[idx + 3] = (on_h if bb & 0x02 else 0) | (on_l if bb & 0x01 else 0)


spi = machine.SPI(0, baudrate=10000000)
oled = SH1122(
    256,
    64,
    spi,
    machine.Pin(20, machine.Pin.OUT),
    machine.Pin(21, machine.Pin.OUT),
    machine.Pin(17, machine.Pin.OUT),
)
oled.init_display()


def send_fw_version_once():
    sys.stdout.write("FW:%s\n" % FW_VERSION)


def announce_fw_version_startup():
    time.sleep_ms(250)
    for _ in range(6):
        send_fw_version_once()
        time.sleep_ms(150)


def show_idle_screen():
    oled.framebuf.fill(0)
    oled.framebuf.text("CHIRAMOJI", 56, 20, 15)
    oled.framebuf.text(FW_VERSION, 92, 36, 15)
    oled.show()


def _close_update_file():
    global update_file
    if update_file is not None:
        try:
            update_file.close()
        except Exception:
            pass
        update_file = None


def _begin_update():
    global update_file, is_update_mode
    _close_update_file()
    try:
        os.remove(UPDATE_TMP_PATH)
    except Exception:
        pass
    update_file = open(UPDATE_TMP_PATH, "wb")
    is_update_mode = True
    sys.stdout.write("UPD:BEGIN\n")


def _append_update_chunk(payload):
    if (not is_update_mode) or (update_file is None):
        return
    update_file.write(payload)


def _finish_update():
    global is_update_mode
    if (not is_update_mode) or (update_file is None):
        sys.stdout.write("UPD:ERR:no session\n")
        return

    try:
        _close_update_file()
        try:
            os.remove(UPDATE_BAK_PATH)
        except Exception:
            pass
        try:
            os.rename("main.py", UPDATE_BAK_PATH)
        except Exception:
            pass
        os.rename(UPDATE_TMP_PATH, "main.py")
        sys.stdout.write("UPD:OK\n")
        time.sleep_ms(250)
        machine.soft_reset()
    except Exception as e:
        is_update_mode = False
        sys.stdout.write("UPD:ERR:%s\n" % str(e))


show_idle_screen()
announce_fw_version_startup()

micropython.kbd_intr(-1)
poll = select.poll()
poll.register(sys.stdin, select.POLLIN)

FRAME_SIZE = 2049
buffer = bytearray()
next_announce = time.ticks_add(time.ticks_ms(), ANNOUNCE_INTERVAL_MS)
update_file = None
is_update_mode = False

while True:
    events = poll.poll(500)

    if events:
        chunk = sys.stdin.buffer.read(FRAME_SIZE - len(buffer))
        if chunk:
            buffer.extend(chunk)

        while True:
            if len(buffer) >= FRAME_SIZE and buffer[0:8] == UPDATE_MAGIC:
                update_frame = buffer[:FRAME_SIZE]
                buffer = bytearray(buffer[FRAME_SIZE:])

                payload_len = (update_frame[9] << 8) | update_frame[10]
                max_payload = FRAME_SIZE - UPDATE_HEADER_LEN
                if payload_len > max_payload:
                    sys.stdout.write("UPD:ERR:payload too large\n")
                    continue

                cmd = update_frame[8]
                payload = bytes(update_frame[UPDATE_HEADER_LEN:UPDATE_HEADER_LEN + payload_len])

                if cmd == CMD_UPDATE_BEGIN:
                    _begin_update()
                elif cmd == CMD_UPDATE_CHUNK:
                    _append_update_chunk(payload)
                elif cmd == CMD_UPDATE_END:
                    _finish_update()
                continue

            if len(buffer) >= FRAME_SIZE:
                frame = buffer[:FRAME_SIZE]
                buffer = bytearray(buffer[FRAME_SIZE:])

                brightness = frame[0]
                pixels = bytearray(frame[1:])

                brightness_buf[0] = brightness
                frame_buf[0] = (frame_buf[0] + 1) % 16
                expand_1bit_to_4bit(pixels, oled.buffer, brightness_buf, frame_buf)
                oled.show()
                continue

            break

    now = time.ticks_ms()
    if time.ticks_diff(now, next_announce) >= 0:
        send_fw_version_once()
        next_announce = time.ticks_add(now, ANNOUNCE_INTERVAL_MS)