#!/usr/bin/env python3
"""Builds Basic/sl_v4.0.txt (the BASIC installer of the Saver/Loader) from asm/uart1600_sl.asm. Needs z80asm."""
import os, re, subprocess, sys, tempfile
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def assemble():
    with tempfile.TemporaryDirectory() as d:
        b = os.path.join(d, 'u.bin')
        r = subprocess.run(['z80asm', '-L', '-o', b, os.path.join(ROOT, 'asm', 'uart1600_sl.asm')], capture_output=True, text=True)
        if r.returncode:
            sys.exit(r.stderr)
        lab = {}
        for l in (r.stdout + '\n' + r.stderr).splitlines():
            m = re.match(r'(\w+):\s+equ \$([0-9a-f]+)', l)
            if m:
                lab[m.group(1)] = int(m.group(2), 16)
        return bytearray(open(b, 'rb').read()), lab


def build():
    code, lab = assemble()
    # fixed places: LOAD (receive into the PC-1600) at &C100 = 49408, SAVE (send from the PC-1600) at &C200 = 49664.
    # The code is position independent; the offsets are relative to 49408. LOAD (about 207 bytes) must end before SAVE.
    CODE_OFF = 3072
    LOAD_AT, SAVE_AT = 0, 256
    assert lab['ld_fin'] - lab['LOAD'] <= SAVE_AT - LOAD_AT
    segs = [(lab['LOAD'], lab['ld_fin'], 0), (0, lab['sv_fin'], SAVE_AT - LOAD_AT)]    # (source from, to, offset in the PC-1600)
    L = []

    def add(n, t):
        L.append((n, t))
    # patch sites (offsets from A): the operands of LD D,N (delay count) and of JR (number of NOPs skipped)
    S = SAVE_AT - LOAD_AT
    ls = lambda name: lab[name] - lab['LOAD'] + 1          # in LOAD
    sv = lambda name: S + lab[name] + 1                    # in SAVE
    add(10, 'REM SL V4.0: ADDRESSES SHOWN AT THE END')
    add(30, 'CLEAR:WAIT 0:BEEP OFF')
    # F865H/F866H = the start of the BASIC program in the notation of the PC-1500 (high byte first, bit 15 inverted), FE3CH/FE3DH the same
    # in the plain form. The code goes behind the text of this installer (3072 bytes from the start); the start of the program is then
    # moved behind the code, so a program of any size can be loaded without touching it (NEW keeps the start).
    add(40, 'H=PEEK(63589):S=(H+128-256*INT(H/128))*256+PEEK(63590)')
    add(42, 'IF MEM<%d THEN PRINT "NO ROOM":BEEP 3:END' % (CODE_OFF + 576 + 1500))
    add(44, 'A=S+%d' % CODE_OFF)
    add(50, 'D=0:INPUT "BAUD (ENTER=9600) ";D')
    add(52, 'IF D=0 THEN LET D=9600')
    add(54, 'IF D<>1200 AND D<>2400 AND D<>4800 AND D<>9600 AND D<>19200 THEN GOTO 50')
    add(60, 'PRINT "INSTALLING"')
    add(70, 'RESTORE 1000:READ N:C=0')
    add(80, 'FOR S=1 TO N:READ O,L')
    add(90, 'FOR I=0 TO L-1:READ B:POKE A+O+I,B:C=C+B:NEXT I')
    add(100, 'NEXT S')
    add(110, 'READ X:IF X<>C THEN GOTO 900')
    add(112, 'T=3000000/D')
    add(114, 'Q=INT((T-100)/4+.5):GOSUB 200:POKE A+%d,U:POKE A+%d,V' % (sv('sv_dn'), sv('sv_jd')))
    add(116, 'Q=INT((T-99)/4+.5):GOSUB 200:POKE A+%d,U:POKE A+%d,U' % (ls('ld_bn'), ls('ld_sn')))
    add(117, 'POKE A+%d,V:POKE A+%d,V' % (ls('ld_bj'), ls('ld_sj')))
    add(118, 'Q=INT((T/2-105)/4+.5):GOSUB 200:POKE A+%d,U:POKE A+%d,V' % (ls('ld_hn'), ls('ld_hj')))
    add(150, 'CLS:PRINT "SAVER/LOADER READY ";D')
    add(160, 'PRINT "LOAD: CALL&";HEX$(A);", NEW first"')
    add(170, 'PRINT "SAVE: CALL&";HEX$(A+256)')
    add(180, 'GOSUB 300:END')
    add(200, 'U=INT((Q+3)/4):IF U<1 THEN LET U=1')
    add(205, 'V=4*U-Q:IF V>8 THEN LET V=8')
    add(207, 'RETURN')
    # the new start of the program = behind the code: the marker FFH there, the pointers (start and end) in both notations
    add(300, 'N=A+576:POKE N,255:K=INT(N/256):L=N-256*K:W=K+128-256*INT(K/128)')
    add(310, 'POKE 65084,L:POKE 65085,K:POKE 65087,L:POKE 65088,K')
    add(320, 'POKE 63589,W:POKE 63590,L:POKE 63591,W:POKE 63592,L')
    add(330, 'RETURN')
    add(900, 'PRINT "DATA ERROR":BEEP 3:END')
    add(1000, 'DATA %d' % len(segs))
    n = 1010
    tot = 0
    for a, b, off in segs:
        add(n, 'DATA %d,%d' % (off, b - a))
        n += 10
        vals = list(code[a:b]); tot += sum(vals)
        # pack as many numbers as fit into one line (<= 72 characters, so that the whole line is <= 80): the text is received over RS-232 and the
        # Sharp accepts only about 2 KB per LOAD, so the listing must stay short
        row = []
        for v in vals:
            if row and len('DATA ' + ','.join(row + [str(v)])) > 72:
                add(n, 'DATA ' + ','.join(row))
                n += 10
                row = []
            row.append(str(v))
        if row:
            add(n, 'DATA ' + ','.join(row))
            n += 10
    add(n, 'DATA %d' % tot)
    L.sort()
    assert len({k for k, _ in L}) == len(L)
    return ''.join('%d %s\n' % (k, t) for k, t in L), code, lab


if __name__ == '__main__':
    text, code, lab = build()
    open(os.path.join(ROOT, 'Basic', 'sl_v4.0.txt'), 'w', newline='\n').write(text)
    print('Basic/sl_v4.0.txt written;', len(text.splitlines()), 'lines,', len(text), 'bytes, longest', max(len(l) for l in text.splitlines()),
          '; code behind the installer (+3072)')
    assert len(text) < 2900, 'the installer must be shorter than the 3072 bytes reserved for it'
