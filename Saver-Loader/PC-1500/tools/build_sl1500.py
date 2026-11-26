#!/usr/bin/env python3
"""Builds Basic/sl1500.txt (the BASIC installer of the PC-1500 Saver/Loader) from asm/uart1500_sl.asm.
The code is assembled with tools/lh5801asm.py. The installer POKEs it from DATA lines (RX port PB2 by default) and then patches
the 5 bytes that depend on the RX port (PB0 / PB2)."""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import lh5801asm as A

LOAD_OFF = 0x100           # LOAD at RAM start + &100 (&4100 on a PC-1500A)
SAVE_OFF = 0x200           # SAVE at RAM start + &200 (&4200 on a PC-1500A)
NEW_OFF = 0x300            # BASIC must start at RAM start + &300 or later (NEW &4300)


CE_LOAD = 0x3E00           # CE-161 version: LOAD at the top of the module (&3E00), SAVE at &3F00
CE_SAVE = 0x3F00
CE_LIMH = 0x3E             # LOAD stops (error 4) when the program would reach &3E00, i.e. the code of the SL

GUARD_AT = ' BCS+ FULL           ; the next byte would not fit into the RAM\n'
GUARD = GUARD_AT + ' CPI A,LIMH         ; CE-161 version: the program must not reach the code of the SL (&3E00)\n BZS+ FULL\n'


def source(ce161=False):
    src = open(os.path.join(ROOT, 'asm', 'uart1500_sl.asm')).read()
    if ce161:
        assert src.count(GUARD_AT) == 1
        src = src.replace(GUARD_AT, GUARD)
    return src


def assemble(ce161=False):
    src = source(ce161)
    k = {'LIMH': CE_LIMH} if ce161 else {}
    code, lab, lst = A.assemble(src, k)                       # PB2
    c0, _, _ = A.assemble(src, dict(k, DB=0xFB, BM=4))
    c1, _, _ = A.assemble(src, dict(k, DB=0xFE, BM=1))       # PB0
    c2, _, _ = A.assemble(src, dict(k, DB=0x12, BM=4))
    db_pos = [i for i in range(len(c0)) if c0[i] != c2[i]]
    bm_pos = [i for i in range(len(c0)) if c0[i] != c1[i] and i not in db_pos]
    return code, lab, db_pos, bm_pos


def build():
    code, lab, db_pos, bm_pos = assemble()
    n = len(code)
    assert LOAD_OFF + lab['SAVE'] <= SAVE_OFF and SAVE_OFF + (n - lab['SAVE']) <= NEW_OFF, 'code does not fit'
    L = []

    def add(num, text):
        L.append((num, text))
    add(10, 'REM SL1500 4800 BPS')
    add(20, 'A=(PEEK &7863)*256:L=A+%d:S=A+%d' % (LOAD_OFF, SAVE_OFF))
    add(30, 'B=(PEEK &7865)*256+PEEK &7866')
    add(40, 'IF B<A+%d PRINT "NEW &4300 IN PRO MODE!":BEEP 3:END' % NEW_OFF)
    add(50, 'P=2:INPUT "RX PORT (DEFAULT PB2) ";P')
    add(60, 'M=0:IF P=0 LET M=1')
    add(70, 'IF P=2 LET M=4')
    add(80, 'IF M=0 BEEP 1:GOTO 50')
    add(85, 'CLS:WAIT 0:PRINT "INSTALLING..."')
    add(90, 'RESTORE 1000:C=0')
    add(100, 'FOR I=0 TO %d:READ V:POKE L+I,V:C=C+V:NEXT I' % (lab['SAVE'] - 1))
    add(105, 'FOR I=0 TO %d:READ V:POKE S+I,V:C=C+V:NEXT I' % (n - lab['SAVE'] - 1))
    add(110, 'READ V:IF V<>C PRINT "DATA ERROR":BEEP 3:END')
    add(120, 'POKE L+%d,255-M' % db_pos[0])
    pk = ':'.join('POKE L+%d,M' % p for p in bm_pos)
    add(130, pk)
    add(135, 'P$="PB2":IF M=1 LET P$="PB0"')
    add(140, 'CLS:WAIT 100:PRINT "SL1500 READY 4800 ";P$')
    add(150, 'N=L:GOSUB 200:PRINT "LOAD CALL &";H$;" NEW 1ST"')
    add(160, 'N=S:GOSUB 200:PRINT "SAVE CALL &";H$')
    add(170, 'WAIT 0:END')
    add(200, 'H$="":T=N:FOR K=1 TO 4:D=T-16*INT (T/16)')
    add(210, 'H$=MID$ ("0123456789ABCDEF",D+1,1)+H$:T=INT (T/16):NEXT K:RETURN')
    nl = 1000
    row = []
    for v in code:
        if row and len('DATA ' + ','.join(row + [str(v)])) > 72:
            add(nl, 'DATA ' + ','.join(row)); nl += 10; row = []
        row.append(str(v))
    row.append(str(sum(code)))
    add(nl, 'DATA ' + ','.join(row))
    return L, code, lab, db_pos, bm_pos


def build_ce161():
    """installer for a PC-1500(A) with the CE-161 (RAM from &0000): the code goes to the top of the module (LOAD &3E00, SAVE &3F00)"""
    code, lab, db_pos, bm_pos = assemble(True)
    n = len(code)
    assert lab['SAVE'] <= 0x100 and (n - lab['SAVE']) <= 0x100, 'code does not fit'
    L = []

    def add(num, text):
        L.append((num, text))
    add(10, 'REM SL1500 CE-161 &3E00')
    add(20, 'L=&3E00:S=&3F00')
    add(30, 'A=PEEK &7863:IF A<>0 PRINT "CE-161 NEEDED":BEEP 3:END')
    add(40, 'P=2:INPUT "RX PORT (DEFAULT PB2) ";P')
    add(50, 'M=0:IF P=0 LET M=1')
    add(60, 'IF P=2 LET M=4')
    add(70, 'IF M=0 BEEP 1:GOTO 40')
    add(80, 'CLS:WAIT 0:PRINT "INSTALLING..."')
    add(90, 'RESTORE 1000:C=0')
    add(100, 'FOR I=0 TO %d:READ V:POKE L+I,V:C=C+V' % (lab['SAVE'] - 1))
    add(102, 'Q=PEEK (L+I):IF Q<>V GOTO 300')
    add(104, 'NEXT I')
    add(105, 'FOR I=0 TO %d:READ V:POKE S+I,V:C=C+V' % (n - lab['SAVE'] - 1))
    add(107, 'Q=PEEK (S+I):IF Q<>V GOTO 300')
    add(108, 'NEXT I')
    add(110, 'READ V:IF V<>C PRINT "DATA ERROR":BEEP 3:END')
    add(120, 'POKE L+%d,255-M' % db_pos[0])
    pk = ':'.join('POKE L+%d,M' % p for p in bm_pos)
    add(130, pk)
    add(135, 'P$="PB2":IF M=1 LET P$="PB0"')
    add(140, 'CLS:WAIT 100:PRINT "SL1500 READY 4800 ";P$')
    add(150, 'PRINT "LOAD CALL &3E00 NEW 1ST"')
    add(160, 'PRINT "SAVE CALL &3F00"')
    add(165, 'PRINT "KEEP MEM ABOVE 6700"')
    add(170, 'WAIT 0:END')
    add(300, 'CLS:WAIT 0:PRINT "WRITE PROTECT ON?":BEEP 3:END')
    nl = 1000
    row = []
    for v in code:
        if row and len('DATA ' + ','.join(row + [str(v)])) > 72:
            add(nl, 'DATA ' + ','.join(row)); nl += 10; row = []
        row.append(str(v))
    row.append(str(sum(code)))
    add(nl, 'DATA ' + ','.join(row))
    return L, code, lab, db_pos, bm_pos


def main():
    ce = len(sys.argv) > 1 and sys.argv[1] == 'ce161'
    L, code, lab, db_pos, bm_pos = build_ce161() if ce else build()
    text = ''.join('%d %s\n' % (n, t) for n, t in L)
    open(os.path.join(ROOT, 'Basic', 'sl1500_ce161.txt' if ce else 'sl1500.txt'), 'w').write(text)
    print('code %d bytes: LOAD %d, SAVE %d; DB at %s, BM at %s; %d lines, %d characters' % (
        len(code), lab['SAVE'], len(code) - lab['SAVE'], db_pos, bm_pos, len(L), len(text)))
    print('longest line: %d' % max(len('%d %s' % (n, t)) for n, t in L))


if __name__ == '__main__':
    main()
