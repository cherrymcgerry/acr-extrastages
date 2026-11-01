#!/usr/bin/env python3
"""acr-autoshot: start a stage in Assetto Corsa Rally, take screenshots of it, close the game (Linux dev tool).

    acr-autoshot.py run --stage AcrXsNz01r5[,AcrXsSc03r2...] [--out DIR] [--shots 3] [--interval 2] [--settle 5]
    acr-autoshot.py set-stage --stage AcrXsNz01r5     only write the selection into the game's save
    acr-autoshot.py restore                           put back the save from before the last run
    acr-autoshot.py probe [--save FILE]               capture the screen, print which menu screen it is
    acr-autoshot.py key Enter [E Down ...]            press keys (only while the game is in front)
    acr-autoshot.py close                             close the game
    acr-autoshot.py record [--launch]                 record your own key presses + the screen before each

How (docs/autoshot.md):
1. The stage, car and time of day are written into the game's own save (PlayerDataSaveSlot.sav) while the game is
   closed, so its menus open with that stage selected. The save layout and the approach are from acr-daily's
   saveslot.py (github.com/khanhonthetrack/acr-daily, MIT, commit b68dc23); see THIRD_PARTY_NOTICES.md.
2. The game is started through Steam. Its menus are recognised from screenshots (spectacle) by where the game's red
   highlight is, also after acr-daily (autodrive.py), and the menu keys are pressed through a uinput keyboard
   (python-evdev). A key is only ever pressed while the game's window is the active one (XWayland
   _NET_ACTIVE_WINDOW), on a screen recognised twice in a row.
3. Once the stage is loaded it waits --settle seconds, takes --shots screenshots, closes the game and restores the
   save as it was.

Parts adapted from acr-daily: Copyright (c) 2026 Ngoc Khanh Nguyen, MIT License (text in THIRD_PARTY_NOTICES.md).

Needs: Linux (KDE Plasma Wayland, game under Proton/XWayland), python3, Pillow, python-evdev, spectacle, xprop,
write access to /dev/uinput (logind gives it to the seat's user on Bazzite).
"""
import argparse
import os
import re
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import time

from PIL import Image

STEAM_APP = 3917090
WM_CLASS = 'steam_app_%d' % STEAM_APP
GAME_EXE = 'acr.exe'
STEAM_ROOT = os.path.expanduser('~/.local/share/Steam')
SAVE_DIR = os.path.join(STEAM_ROOT, 'steamapps/compatdata/%d/pfx/drive_c/users/steamuser/AppData/Local/acr/Saved/SaveGames'
                        % STEAM_APP)
SAVE = os.path.join(SAVE_DIR, 'PlayerDataSaveSlot.sav')
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.realpath(__file__))))
WORK = os.path.join(REPO, 'work', 'autoshot')
BACKUPS = os.path.join(WORK, 'save-backups')
STAGE_ID_RE = re.compile(r'^AcrXs[A-Z][a-z]\d\dr\d+$')


def log(text):
    print('%s  %s' % (time.strftime('%H:%M:%S'), text), flush=True)


class Fail(Exception):
    def __init__(self, text, img=None):
        super().__init__(text)
        self.img = img            # the last capture, kept when a run stops


# ---------------------------------------------------------------------------------------------------------------------
# The game's save: the current selection (after acr-daily saveslot.py)
#
# PlayerDataSaveSlot.sav is a GVAS file whose game data is one block ("PlayerSaveGameData" + its byte size). In it,
# after a map of per-mode settings ("DefaultFreePractice", "DefaultTimeAttack"... only the modes the player has used),
# comes the current selection: stage FString (e.g. AcrXsNz01r5FullForward), car FString, int32 n, n x (key, value)
# FStrings (/Script/acr.WeatherOptions/StartingTime = (TimeSeconds=52200.000000), ...). Only those strings are
# replaced and the block size corrected; anything not as expected = nothing is written.

MODE_RE = re.compile(rb'^Default[A-Za-z]+$')
KEY_TIME = b'/Script/acr.WeatherOptions/StartingTime'


def _i(b, o):
    return struct.unpack_from('<i', b, o)[0]


def _fstring_at(b, o):
    """ASCII FString at o (int32 length with the NUL, then the text) -> (bytes, end) or None."""
    if o + 4 > len(b):
        return None
    n = _i(b, o)
    if not 1 <= n <= 300 or o + 4 + n > len(b) or b[o + 4 + n - 1] != 0:
        return None
    s = b[o + 4:o + 4 + n - 1]
    if any(c < 32 or c > 126 for c in s):
        return None
    return s, o + 4 + n


def _fstring(s):
    s = s.encode('ascii')
    return struct.pack('<i', len(s) + 1) + s + b'\x00'


def _payload(b):
    """-> offset of the PlayerSaveGameData size field (the block must run exactly to the end of the file)."""
    if b[:4] != b'GVAS':
        raise Fail('the save is not a GVAS file')
    i = b.find(b'PlayerSaveGameData\x00')
    if i < 0:
        raise Fail('the save has no PlayerSaveGameData')
    so = i + len(b'PlayerSaveGameData\x00')
    if so + 4 + _i(b, so) != len(b):
        raise Fail('unexpected save layout (PlayerSaveGameData size)')
    return so


def _modes(b, o, count):
    """Walk the per-mode settings map from its first entry at o -> end offset, or None if it is not one. Per mode:
    FString name, int32 contexts, per context: FString, int32 n x (16-byte GUID + int32), int32 m x (FString, int32
    size, size bytes)."""
    try:
        for _k in range(count):
            name = _fstring_at(b, o)
            if not name or not MODE_RE.match(name[0]):
                return None
            o = name[1]
            contexts = _i(b, o)
            o += 4
            if not 0 <= contexts <= 8:
                return None
            for _c in range(contexts):
                ctx = _fstring_at(b, o)
                if not ctx:
                    return None
                versions = _i(b, ctx[1])
                if not 0 <= versions <= 32:
                    return None
                o = ctx[1] + 4 + 20 * versions
                parts = _i(b, o)
                o += 4
                if not 0 <= parts <= 32:
                    return None
                for _p in range(parts):
                    part = _fstring_at(b, o)
                    if not part:
                        return None
                    size = _i(b, part[1])
                    o = part[1] + 4 + size
                    if size < 0 or o > len(b):
                        return None
    except struct.error:
        return None
    return o


def _selection_at(b):
    o = b.find(b'Default')
    while o >= 0:
        start = o - 4
        count = _i(b, start - 4) if start >= 4 else 0
        name = _fstring_at(b, start)
        if name and MODE_RE.match(name[0]) and 1 <= count <= 32:
            end = _modes(b, start, count)
            stage = end and _fstring_at(b, end)
            car = stage and _fstring_at(b, stage[1])
            if car and 0 <= _i(b, car[1]) <= 16:
                return end
        o = b.find(b'Default', o + 1)
    raise Fail('the save has no stage and car selection yet: start any free practice once by hand')


def read_selection(b):
    """-> {'stage', 'car'[, 'time']: (offset, value, end)}"""
    _payload(b)
    o = _selection_at(b)
    stage = _fstring_at(b, o)
    car = _fstring_at(b, stage[1])
    found = {'stage': (o, stage[0].decode(), stage[1]), 'car': (stage[1], car[0].decode(), car[1])}
    o = car[1] + 4
    for _k in range(_i(b, car[1])):
        key = _fstring_at(b, o)
        val = key and _fstring_at(b, key[1])
        if not val:
            raise Fail('the save does not look as expected (weather options)')
        if key[0] == KEY_TIME:
            found['time'] = (key[1], val[0].decode(), val[1])
        o = val[1]
    return found


def with_selection(b, stage=None, car=None, seconds=None):
    """-> new save bytes with the selection's stage / car / starting time replaced (None = keep)."""
    found = read_selection(b)
    new = {}
    if stage:
        new['stage'] = stage
    if car:
        new['car'] = car
    if seconds is not None:
        if 'time' not in found:
            raise Fail('the save has no starting time yet (drive one free practice by hand first), or leave out --time')
        new['time'] = '(TimeSeconds=%.6f)' % seconds
    out = bytearray(b)
    for name in sorted(new, key=lambda n: -found[n][0]):       # from the end backwards: earlier offsets stay valid
        o, _old, e = found[name]
        out[o:e] = _fstring(new[name])
    so = _payload(b)
    struct.pack_into('<i', out, so, len(out) - so - 4)
    back = read_selection(bytes(out))
    for name, v in new.items():
        if back[name][1] != v:
            raise Fail('save verification failed for %s' % name)
    return bytes(out)


def backup_save():
    os.makedirs(BACKUPS, exist_ok=True)
    path = os.path.join(BACKUPS, 'PlayerDataSaveSlot-%s.sav' % time.strftime('%Y%m%d-%H%M%S'))
    shutil.copy2(SAVE, path)
    for old in sorted(n for n in os.listdir(BACKUPS) if n.endswith('.sav'))[:-20]:
        os.remove(os.path.join(BACKUPS, old))
    return path


def write_save(data):
    tmp = SAVE + '.autoshot.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, SAVE)


def set_stage(stage_id, car=None, seconds=None):
    """Write the selection (game closed). -> (backup path, previous selection)."""
    if game_pids():
        raise Fail('close Assetto Corsa Rally first (it rewrites its save when it exits)')
    if not os.path.exists(SAVE):
        raise Fail('no game save at %s' % SAVE)
    with open(SAVE, 'rb') as f:
        b = f.read()
    before = {k: v[1] for k, v in read_selection(b).items()}
    row = stage_id if stage_id.endswith(('Forward', 'Reverse')) else stage_id + 'FullForward'
    data = with_selection(b, row, car, seconds)
    path = backup_save()
    if data != b:
        write_save(data)
    return path, before


def parse_time(text):
    """'14:30' or seconds -> seconds since midnight."""
    if ':' in text:
        h, m = text.split(':', 1)
        return int(h) * 3600 + int(m) * 60
    return float(text)


# ---------------------------------------------------------------------------------------------------------------------
# The game: processes, window, launch, close

def game_pids():
    """Pids of the game's acr.exe (the Windows process under wine), not of tools that mention it."""
    me = os.getpid()
    pids = []
    for d in os.listdir('/proc'):
        if not d.isdigit() or int(d) == me:
            continue
        try:
            with open('/proc/%s/cmdline' % d, 'rb') as f:
                argv = f.read().split(b'\0')
        except OSError:
            continue
        exe = argv[0].replace(b'\\', b'/').rsplit(b'/', 1)[-1].lower() if argv else b''
        if exe == GAME_EXE.encode():
            pids.append(int(d))
    return pids


def _xprop(*args):
    try:
        return subprocess.run(['xprop'] + list(args), capture_output=True, text=True, timeout=5).stdout
    except (OSError, subprocess.TimeoutExpired):
        return ''


def game_in_front():
    """Is the active window the game's? (XWayland: the game runs as an X client with WM_CLASS steam_app_<id>.)"""
    m = re.search(r'window id # (0x[0-9a-f]+)', _xprop('-root', '_NET_ACTIVE_WINDOW'))
    if not m or int(m.group(1), 16) == 0:
        return False
    return ('"%s"' % WM_CLASS) in _xprop('-id', m.group(1), 'WM_CLASS')


def active_window():
    """WM_CLASS and title of the active window, for messages when it is not the game."""
    m = re.search(r'window id # (0x[0-9a-f]+)', _xprop('-root', '_NET_ACTIVE_WINDOW'))
    if not m or int(m.group(1), 16) == 0:
        return 'none (a Wayland-native window, e.g. a browser or the Claude app, or no window)'
    cls = _xprop('-id', m.group(1), 'WM_CLASS').partition('=')[2].strip()
    name = _xprop('-id', m.group(1), '_NET_WM_NAME').partition('=')[2].strip()
    return '%s %s' % (cls or '?', name)


def not_in_front(why):
    """Fail with the window that has the focus and the screen as it is (the game may still show on screen)."""
    try:
        img = capture()
    except Exception:
        img = None
    return Fail('%s (active window: %s; keys are only sent to the game, so do not use the PC during a run)'
                % (why, active_window()), img)


def launch_game():
    subprocess.Popen(['steam', 'steam://rungameid/%d' % STEAM_APP], stdin=subprocess.DEVNULL,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)


def close_game(timeout=30):
    pids = game_pids()
    if not pids:
        return
    log('closing the game')
    for sig, wait in ((signal.SIGTERM, timeout), (signal.SIGKILL, 10)):
        for p in pids:
            try:
                os.kill(p, sig)
            except ProcessLookupError:
                pass
        end = time.monotonic() + wait
        while time.monotonic() < end and game_pids():
            time.sleep(0.5)
        if not game_pids():
            break
    if game_pids():
        raise Fail('the game did not close')
    time.sleep(3)          # let Proton / Steam notice, so a next launch is a clean one


# ---------------------------------------------------------------------------------------------------------------------
# Screen captures

def capture(path=None):
    """Full screen (spectacle, KWin's screenshot interface) -> RGB image; also saved to path if given."""
    keep = path is not None
    if not keep:
        fd, path = tempfile.mkstemp(suffix='.png', prefix='acr-autoshot-')
        os.close(fd)
    try:
        r = subprocess.run(['spectacle', '-b', '-n', '-f', '-o', path], capture_output=True, timeout=20)
        if r.returncode != 0 or not os.path.getsize(path):
            raise Fail('spectacle failed: %s' % r.stderr.decode(errors='replace').strip())
        img = Image.open(path)
        img.load()
        return img.convert('RGB')
    finally:
        if not keep:
            try:
                os.remove(path)
            except OSError:
                pass


# ---------------------------------------------------------------------------------------------------------------------
# Menu screens, told apart by where the game's red highlight is (after acr-daily autodrive.py)
#
# Regions are in the menu's own units: the menus are laid out in a 16:9 box as high as the screen (wider screens)
# and centred left to right, so a point is (x - width / 2) / height, y / height. R() takes pixels of a 1280 x 536
# (21:9) capture, which is how acr-daily measured them; probe --save writes captures to measure new ones on.
# Narrower than 16:9 screens are not supported here.
H0, CX0 = 536.0, 640.0
ON, OFF = 0.7, 0.2       # a region is "red" with at least ON of its pixels red, "not red" with at most OFF


def R(x0, y0, x1, y1):
    return ((x0 - CX0) / H0, y0 / H0, (x1 - CX0) / H0, y1 / H0)


def _red(p):
    return p[0] > 165 and p[1] < 80 and p[2] < 80


def _white(p):
    return p[0] > 200 and p[1] > 200 and p[2] > 200


def _hud(p):
    """The stage HUD's timer box: grey-blue, not bright."""
    return p[2] > p[0] + 15 and max(p) < 140


def _box(p):
    """The title's PRESS ANY BUTTON box: dark grey."""
    return max(p) < 120 and max(p) - min(p) < 40


TESTS = {'red': _red, 'not': _red, 'white': _white, 'not white': _white, 'logo': _red, 'box': _box}

# Measured on 3440 x 1440 captures of game v0.6 (2026-11-01, `record`): the path to a Free Practice is
# title -> Enter, HOME -> E, RACING -> Down to FREE PRACTICE (third tile) -> Enter, Free Practice set-up -> Enter on
# START SESSION, (loading) SERVICE PARK -> Enter on START SESSION, (loading, intro with "Enter SKIP") -> Enter, stage.
RACING_TAB = R(263, 77, 268, 89)
TILES = {'rally': R(228, 246, 242, 262), 'time_attack': R(228, 337, 242, 353), 'free_practice': R(228, 428, 242, 444)}
FP_BUTTONS = [R(230, 266, 280, 282), R(660, 266, 710, 282), R(225, 390, 260, 408), R(510, 390, 545, 408),
              R(795, 390, 830, 408)]                         # CHANGE RALLY STAGE / CAR, the four settings bars
START_BAR = [R(222, 434, 240, 462), R(1040, 434, 1058, 462)]  # Free Practice set-up: START SESSION, both ends
SCREENS = [              # (name, [(region, test)]); the first full match wins
    # title: "PRESS ANY BUTTON" (a dark box, centre bottom) first; after a key it says "Loading" (bottom right) until
    # HOME shows, with the logo still up: only the first one takes a key
    ('title',  [(R(500, 71, 790, 110), 'logo'), (R(500, 118, 790, 130), 'not'), (R(556, 448, 724, 470), 'box')]),
    ('title_loading', [(R(500, 71, 790, 110), 'logo'), (R(500, 118, 790, 130), 'not')]),
    ('home',   [(R(215, 77, 221, 89), 'red'), (RACING_TAB, 'not')]),
    ('racing_rally', [(RACING_TAB, 'red'), (TILES['rally'], 'red'), (TILES['free_practice'], 'not')]),
    ('racing_time_attack', [(RACING_TAB, 'red'), (TILES['time_attack'], 'red'), (TILES['rally'], 'not'),
                            (TILES['free_practice'], 'not')]),
    ('racing_free_practice', [(RACING_TAB, 'red'), (TILES['free_practice'], 'red'), (TILES['rally'], 'not'),
                              (TILES['time_attack'], 'not')]),
    ('racing', [(RACING_TAB, 'red'), (R(215, 77, 221, 89), 'not')]),           # tiles still sliding in
    ('fp_setup', [(r, 'red') for r in START_BAR] + [(r, 'not') for r in FP_BUTTONS]),
    # the mouse pointer over another button lights that one and START SESSION turns white: Down until it is red
    ('fp_setup_other', [(r, 'white') for r in START_BAR] + [(FP_BUTTONS, 'any red')]),
    # SERVICE PARK: START SESSION (the first tab, x 218-424) lit, CAR SERVICE not
    ('park',   [(R(224, 76, 240, 96), 'red'), (R(400, 76, 418, 96), 'red'), (R(450, 76, 560, 96), 'not'),
                (R(620, 440, 660, 460), 'not')]),
]
GUARD = [(R(213, 20, 600, 30), 'not'), (R(560, 360, 720, 380), 'not')]   # never red on any menu screen
STAGE_TIMER = R(212, 40, 340, 64)    # the stage HUD's 00:00:000 timer box (top left)


def _pixels(img):
    return img.get_flattened_data() if hasattr(img, 'get_flattened_data') else img.getdata()


def _frac(img, region, test):
    w, h = img.size
    u0, v0, u1, v1 = region
    x0, x1 = int(w / 2 + u0 * h), int(w / 2 + u1 * h)
    y0, y1 = int(v0 * h), int(v1 * h)
    if x0 < 0 or y0 < 0 or x1 > w or y1 > h or x1 <= x0 or y1 <= y0:
        return None
    px = _pixels(img.crop((x0, y0, x1, y1)))
    return sum(1 for p in px if test(p)) / float(len(px))


def _matches(img, checks):
    for region, want in checks:
        if want == 'any red':
            fs = [_frac(img, r, _red) for r in region]
            if None in fs or max(fs) < ON:
                return False
            continue
        f = _frac(img, region, TESTS[want])
        if f is None:
            return False
        if want in ('red', 'white', 'box') and f < ON or want in ('not', 'not white') and f > OFF or want == 'logo' and f < 0.25:
            return False
    return True


def screen(img):
    """-> the name of the menu screen in the capture, or None."""
    w, h = img.size
    if w * 9 < h * 16:
        return None
    if h > H0 * 1.2:
        img = img.resize((max(1, int(w * H0 / h)), int(H0)))
    for name, checks in SCREENS:
        if _matches(img, checks + GUARD):
            return name
    hud = _frac(img, STAGE_TIMER, _hud)
    digits = _frac(img, STAGE_TIMER, lambda p: min(p) > 200)
    if hud is not None and hud > 0.4 and 0.08 < digits < 0.45:
        return 'stage'
    return None


def brightness(img):
    """Mean luma of a small copy: a black loading / fade frame is near 0."""
    small = img.convert('L').resize((64, 27))
    px = list(_pixels(small))
    return sum(px) / float(len(px))


# ---------------------------------------------------------------------------------------------------------------------
# Keys: a uinput keyboard (python-evdev)

KEYS = {'Enter': 'KEY_ENTER', 'Escape': 'KEY_ESC', 'SpaceBar': 'KEY_SPACE', 'Tab': 'KEY_TAB', 'BackSpace': 'KEY_BACKSPACE',
        'Up': 'KEY_UP', 'Down': 'KEY_DOWN', 'Left': 'KEY_LEFT', 'Right': 'KEY_RIGHT', 'Home': 'KEY_HOME',
        'F1': 'KEY_F1', 'F12': 'KEY_F12'}
KEYS.update({c: 'KEY_' + c for c in 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'})


class Keyboard:
    def __init__(self):
        from evdev import UInput, ecodes
        self.ec = ecodes
        self.codes = {k: getattr(ecodes, v) for k, v in KEYS.items()}
        try:
            self.ui = UInput({ecodes.EV_KEY: sorted(set(self.codes.values()))}, name='acr-autoshot keyboard')
        except (OSError, PermissionError) as e:
            raise Fail('cannot open /dev/uinput (%s)' % e)
        time.sleep(1.0)    # until the compositor has picked the new device up

    def press(self, name, hold=0.08):
        if name not in self.codes:
            raise Fail('unknown key %s (known: %s)' % (name, ', '.join(sorted(self.codes))))
        if not game_in_front():
            raise Fail('the game is not the active window: no key pressed')
        c = self.codes[name]
        self.ui.write(self.ec.EV_KEY, c, 1)
        self.ui.syn()
        time.sleep(hold)
        self.ui.write(self.ec.EV_KEY, c, 0)
        self.ui.syn()
        time.sleep(0.15)

    def close(self):
        self.ui.close()


# ---------------------------------------------------------------------------------------------------------------------
# The drive from the game's start to the stage

KEY_FOR = {'title': 'Enter', 'home': 'E', 'racing_rally': 'Down', 'racing_time_attack': 'Down',
           'racing_free_practice': 'Enter', 'fp_setup_other': 'Down', 'fp_setup': 'Enter', 'park': 'Enter'}
RACING = {'racing', 'racing_rally', 'racing_time_attack', 'racing_free_practice'}
# what may come after a key on a screen (the main menu opens on the tab and tile it was left on)
NEXT = {None: {'title', 'home'} | RACING, 'title': {'home'} | RACING, 'home': RACING,
        'racing_rally': {'racing_time_attack', 'racing_free_practice'}, 'racing_time_attack': {'racing_free_practice'},
        'racing_free_practice': {'fp_setup', 'fp_setup_other'}, 'fp_setup_other': {'fp_setup', 'fp_setup_other'},
        'fp_setup': {'park'}, 'park': {'stage'}}
REPEAT = {'title': 4, 'fp_setup_other': 4}    # screens a key may be pressed on again, after REPEAT_AFTER_S
REPEAT_AFTER_S = {'title': 8}                  # the title still asking 8 s after a key: it missed it
START_WAIT_S = 180       # launch -> first menu screen
STEP_WAIT_S = 20         # a key -> the next screen
LOAD_WAIT_S = 150        # START SESSION -> the next screen (loads the stage)
SKIP_EVERY_S = 4         # intro ("Enter SKIP"): Enter this often until the stage HUD shows
MAX_KEYS = 40


def drive_to_stage(kb, say=log):
    """Press the menu keys from the game's start to the stage (its HUD on screen). -> the last capture."""
    pressed_on, seen, keys, repeats = None, [], 0, 0
    since = time.monotonic()
    last_skip = 0.0
    while True:
        now = time.monotonic()
        if not game_pids():
            if pressed_on is not None or now - since > START_WAIT_S:
                raise Fail('the game closed' if pressed_on else 'the game did not start')
            time.sleep(1)                       # Steam is still starting it
            continue
        if not game_in_front():
            # the same wait as for the next screen: a stage loads up to LOAD_WAIT_S after START SESSION
            wait = LOAD_WAIT_S if pressed_on in ('fp_setup', 'park') else STEP_WAIT_S
            if keys and now - since > wait:
                raise not_in_front('the game is not the active window')
            time.sleep(0.5)
            continue
        img = capture()
        s = screen(img)
        if not seen or seen[-1] != s:
            say('screen: %s' % s)
        seen = (seen + [s])[-2:]
        stable = len(seen) == 2 and seen[0] == s
        if s == 'stage' and stable and pressed_on in ('park', None):
            return img
        limit = START_WAIT_S if pressed_on is None else LOAD_WAIT_S if pressed_on in ('fp_setup', 'park') else STEP_WAIT_S
        waited = now - since
        if waited > limit:
            raise Fail('stuck: no expected screen after %s (%d s); last seen %s' % (pressed_on, waited, s), img)
        if pressed_on == 'park' and s != 'stage':
            # the stage loads, then an intro plays ("Enter SKIP"): Enter on a lit, steady non-menu frame
            if stable and s is None and brightness(img) > 25 and now - last_skip > SKIP_EVERY_S and waited > 6:
                kb.press('Enter')
                say('key Enter (skip intro)')
                last_skip = time.monotonic()
            continue
        if not stable or s is None or s not in KEY_FOR:
            continue
        if s == pressed_on:
            if s in REPEAT and repeats + 1 < REPEAT[s] and waited > REPEAT_AFTER_S.get(s, 3):
                pass                                      # pressed, nothing yet: again (title listens late)
            else:
                continue
        elif s not in NEXT[pressed_on]:
            continue                                      # a fade / transition frame: wait for an expected one
        if keys >= MAX_KEYS:
            raise Fail('too many keys', img)
        kb.press(KEY_FOR[s])
        say('key %s on %s' % (KEY_FOR[s], s))
        keys += 1
        repeats = repeats + 1 if s == pressed_on else 0
        pressed_on, seen, since = s, [], time.monotonic()


def mangohud_toggle_key():
    """MangoHud's toggle_hud key (from its config) when MangoHud is in the game's launch options, else None."""
    conf = os.path.expanduser('~/.config/MangoHud/MangoHud.conf')
    try:
        with open(conf) as f:
            text = f.read()
    except OSError:
        return None
    if re.search(r'^\s*no_display\b', text, re.M):
        return None
    m = re.search(r'^\s*toggle_hud\s*=\s*(\S+)', text, re.M)
    key = m.group(1).split('+')[-1] if m else 'F12'
    return key if key in KEYS else None


def shoot_stage(stage, a, out):
    """One stage: save, launch, drive, screenshots. -> list of files. The save is restored and the game closed."""
    seconds = parse_time(a.time) if a.time else None
    backup, before = set_stage(stage, a.car, seconds)
    log('%s: selection set (was %s %s)' % (stage, before.get('stage'), before.get('car')))
    files = []
    kb = None
    last = None
    try:
        launch_game()
        log('game starting')
        kb = Keyboard()
        last = drive_to_stage(kb)
        log('on the stage; settling %.0f s' % a.settle)
        time.sleep(a.settle)
        if a.mangohud == 'hide':
            key = mangohud_toggle_key()
            if key:
                kb.press(key)
                time.sleep(0.5)
        for k in a.pre_keys:
            kb.press(k)
            time.sleep(0.8)
        stamp = time.strftime('%Y%m%d-%H%M%S')
        for i in range(a.shots):
            if i:
                time.sleep(a.interval)
            if not game_in_front():
                raise not_in_front('the game is not the active window')
            path = os.path.join(out, '%s-%s-%d.png' % (stage, stamp, i + 1))
            last = capture(path)
            files.append(path)
            log('screenshot %s' % path)
    except Fail as e:
        img = e.img if e.img is not None else last
        if img is not None:
            path = os.path.join(out, '%s-failed.jpg' % stage)
            img.save(path, quality=90)
            log('last screen saved: %s' % path)
        raise Fail('%s: %s' % (stage, e))
    finally:
        if kb:
            kb.close()
        if not a.keep_open:
            close_game()
            if not a.keep_save:
                shutil.copy2(backup, SAVE)
                log('save restored')
    return files


def cmd_run(a):
    stages = [s.strip() for s in a.stage.split(',') if s.strip()]
    for s in stages:
        if not STAGE_ID_RE.match(s) and not s.endswith(('Forward', 'Reverse')):
            raise Fail('not a stage id: %s (e.g. AcrXsNz01r5)' % s)
    if a.keep_open and len(stages) > 1:
        raise Fail('--keep-open takes one stage')
    if game_pids():
        raise Fail('close Assetto Corsa Rally first')
    out = os.path.realpath(a.out or os.path.join(WORK, 'shots'))
    os.makedirs(out, exist_ok=True)
    failed = []
    for s in stages:
        try:
            shoot_stage(s, a, out)
        except Fail as e:
            log('error: %s' % e)
            failed.append(s)
            if game_pids():
                close_game()
    if failed:
        raise Fail('failed: %s' % ', '.join(failed))


# ---------------------------------------------------------------------------------------------------------------------
# Commands

def cmd_set_stage(a):
    path, before = set_stage(a.stage, a.car, parse_time(a.time) if a.time else None)
    log('selection was %s; backup %s' % (before, path))
    with open(SAVE, 'rb') as f:
        log('selection now %s' % {k: v[1] for k, v in read_selection(f.read()).items()})


def restore_latest():
    olds = sorted(n for n in os.listdir(BACKUPS) if n.endswith('.sav')) if os.path.isdir(BACKUPS) else []
    if not olds:
        raise Fail('no backup yet')
    if game_pids():
        raise Fail('close Assetto Corsa Rally first')
    shutil.copy2(os.path.join(BACKUPS, olds[-1]), SAVE)
    return olds[-1]


def cmd_restore(_a):
    log('restored %s' % restore_latest())


def cmd_probe(a):
    img = capture(a.save)
    log('screen: %s  (brightness %.0f, in front: %s)' % (screen(img), brightness(img), game_in_front()))
    if a.save:
        log('saved %s' % a.save)


def cmd_key(a):
    kb = Keyboard()
    try:
        for k in a.keys:
            kb.press(k)
            log('pressed %s' % k)
            time.sleep(a.gap)
    finally:
        kb.close()


def cmd_close(_a):
    close_game()


def _input_devices():
    """Readable keyboards / mice (EV_KEY), not our own uinput keyboard."""
    import evdev
    out = []
    for path in evdev.list_devices():
        try:
            d = evdev.InputDevice(path)
        except OSError:
            continue
        if d.name.startswith('acr-autoshot') or evdev.ecodes.EV_KEY not in d.capabilities():
            d.close()
            continue
        out.append(d)
    return out


def cmd_record(a):
    """Record the player's own key presses with the screen just before each one (to learn a menu path). Stops when
    the game exits, after --duration, or on Ctrl+C."""
    import select
    import threading
    import evdev
    out = a.out or os.path.join(WORK, 'record-' + time.strftime('%Y%m%d-%H%M%S'))
    os.makedirs(out, exist_ok=True)
    devs = _input_devices()
    if not devs:
        raise Fail('no readable keyboard (needs read access to /dev/input/event*)')
    log('listening on: %s' % ', '.join(sorted(set(d.name.strip() for d in devs))))
    if a.launch and not game_pids():
        launch_game()
        log('game started')
    latest = {'img': None, 't': 0.0, 'screen': None}
    lock = threading.Lock()
    stop = threading.Event()
    trace = open(os.path.join(out, 'trace.txt'), 'a')

    def note(text):
        log(text)
        trace.write('%s  %s\n' % (time.strftime('%H:%M:%S'), text))
        trace.flush()

    def save(img, name):
        img.save(os.path.join(out, name), quality=90)

    def grab():
        n = 0
        while not stop.is_set():
            try:
                img = capture()
            except Fail:
                time.sleep(1)
                continue
            s = screen(img) if game_in_front() else 'not-in-front'
            with lock:
                changed = s != latest['screen']
                latest.update(img=img, t=time.monotonic(), screen=s)
            if changed:       # also keep what the screen turned into (e.g. the stage after the last key)
                n += 1
                save(img, 'screen-%03d-%s.jpg' % (n, s))
                note('screen -> %s (brightness %.0f)' % (s, brightness(img)))

    threading.Thread(target=grab, daemon=True).start()
    fds = {d.fd: d for d in devs}
    count, t0, seen_game = 0, time.monotonic(), bool(game_pids())
    try:
        while True:
            if a.duration and time.monotonic() - t0 > a.duration:
                break
            if game_pids():
                seen_game = True
            elif seen_game:
                note('the game exited')
                break
            r, _w, _x = select.select(list(fds), [], [], 1.0)
            for fd in r:
                for ev in fds[fd].read():
                    if ev.type != evdev.ecodes.EV_KEY or ev.value != 1:
                        continue
                    if not game_in_front():
                        continue                   # never record typing in other windows
                    name = evdev.ecodes.KEY.get(ev.code) or evdev.ecodes.BTN.get(ev.code) or str(ev.code)
                    name = name[0] if isinstance(name, list) else name
                    with lock:
                        img, age, s = latest['img'], time.monotonic() - latest['t'], latest['screen']
                    count += 1
                    note('key %s on screen %s (capture %.1f s old)' % (name, s, age))
                    if img is not None:
                        save(img, 'key-%03d-%s-%s.jpg' % (count, s, name))
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        for d in devs:
            d.close()
        trace.close()
    log('recorded %d key presses in %s' % (count, out))


def main(argv=None):
    p = argparse.ArgumentParser(prog='acr-autoshot', description=__doc__.split('\n\n')[0])
    sub = p.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('run', help='start the stage(s), take screenshots, close the game')
    s.add_argument('--stage', required=True, help='AcrXsNz01r5, or several: AcrXsNz01r5,AcrXsSc03r2')
    s.add_argument('--car', help='car id, e.g. Hyundaii20NRally2 (default: as in the save)')
    s.add_argument('--time', help='time of day, e.g. 14:30 (default: as in the save)')
    s.add_argument('--out', help='folder for the screenshots (default work/autoshot/shots)')
    s.add_argument('--shots', type=int, default=1)
    s.add_argument('--interval', type=float, default=2.0, help='seconds between screenshots')
    s.add_argument('--settle', type=float, default=5.0, help='seconds on the stage before the first screenshot')
    s.add_argument('--mangohud', choices=('hide', 'keep'), default='hide', help='press MangoHud\'s toggle key first')
    s.add_argument('--pre-keys', nargs='*', default=[], metavar='KEY', help='keys to press on the stage before the shots')
    s.add_argument('--keep-open', action='store_true', help='leave the game running on the stage')
    s.add_argument('--keep-save', action='store_true', help='do not restore the save afterwards')
    s.set_defaults(fn=cmd_run)
    s = sub.add_parser('set-stage', help='write the stage selection into the game save')
    s.add_argument('--stage', required=True, help='AcrXsNz01r5 (FullForward is added) or a full row id')
    s.add_argument('--car', help='car id, e.g. Hyundaii20NRally2 (default: keep)')
    s.add_argument('--time', help='starting time of day, e.g. 14:30 (default: keep)')
    s.set_defaults(fn=cmd_set_stage)
    s = sub.add_parser('restore', help='put back the save from before the last set-stage / run')
    s.set_defaults(fn=cmd_restore)
    s = sub.add_parser('probe', help='capture the screen and print which menu screen it is')
    s.add_argument('--save', help='also write the capture to this PNG')
    s.set_defaults(fn=cmd_probe)
    s = sub.add_parser('key', help='press keys in the game (only while it is in front)')
    s.add_argument('keys', nargs='+')
    s.add_argument('--gap', type=float, default=0.6, help='seconds between keys')
    s.set_defaults(fn=cmd_key)
    s = sub.add_parser('close', help='close the game')
    s.set_defaults(fn=cmd_close)
    s = sub.add_parser('record', help='record your own key presses with the screen before each (to learn a menu path)')
    s.add_argument('--launch', action='store_true', help='start the game first')
    s.add_argument('--out', help='folder (default work/autoshot/record-<time>)')
    s.add_argument('--duration', type=float, default=0, help='stop after this many seconds (default: when the game exits)')
    s.set_defaults(fn=cmd_record)
    a = p.parse_args(argv)
    try:
        a.fn(a)
    except Fail as e:
        log('error: %s' % e)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
