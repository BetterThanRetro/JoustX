using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// Ground movement and object drawing (JOUSTRV4.ASM, SYSTEM.ASM).
///
/// On the ground a bird is driven by a STATE table: each state has a wait time, a callback
/// that picks the animation frame and how far to step, and the next state for joystick
/// back / centre / forward. The callbacks here are those step routines. Drawing is queued as
/// DMA blocks per screen quadrant; the IRQ blits them when the beam is elsewhere.
/// Each method is the body of one original routine, excluding its final RTS.
/// </summary>
public sealed class Ground
{
    // Process block offsets
    private readonly int _posX, _posY, _frame, _feet, _decision, _face, _image, _horse, _rider;
    private readonly int _picH, _picR, _colY1, _colY2, _colX, _lantz;

    // Decision block offsets (sounds)
    private readonly int _sndSkid, _sndRun1, _sndRun2;

    // DMA block offsets
    private readonly int _wcDest, _wcLen, _wcSrc;

    private readonly ushort _runFrames, _stopRun, _exec, _dmaEnd, _dmaFree, _clsX, _clsY;
    private readonly int _erase, _write;
    private readonly byte _stand, _skid;
    private readonly ushort _vectorSnd;
    private readonly SoundQueue _sound;

    public Ground(Symbols s, SoundQueue sound)
    {
        _posX = s["PPOSX"]; _posY = s["PPOSY"]; _frame = s["PFRAME"]; _feet = s["PFEET"];
        _decision = s["PDECSN"]; _face = s["PFACE"]; _image = s["PIMAGE"]; _horse = s["PHORSE"];
        _rider = s["PRIDER"]; _picH = s["PPICH"]; _picR = s["PPICR"]; _colY1 = s["PCOLY1"];
        _colY2 = s["PCOLY2"]; _colX = s["PCOLX"]; _lantz = s["PLANTZ"];
        _sndSkid = s["DSNSK"]; _sndRun1 = s["DSNRU1"]; _sndRun2 = s["DSNRU2"];
        _wcDest = s["WCDEST"]; _wcLen = s["WCLEN"]; _wcSrc = s["WCSRC"];
        _runFrames = s["ORRUN"]; _stopRun = s["STPRUN"]; _exec = s["PEXEC"];
        _dmaEnd = s["DMAEND"]; _dmaFree = s["DMAFRE"]; _clsX = s["CLSX"]; _clsY = s["CLSY"];
        _erase = s["ERASE"]; _write = s["WRITE"];
        _stand = (byte)s.Value("STAND"); _skid = (byte)s.Value("SKID");
        _vectorSnd = s["VSND"];
        _sound = sound;
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    /// <summary>JSR VSND (via the JMP vector) with X = sound table, including SND's RTS.</summary>
    public void CallSound(Tx t)
    {
        t.Cycles += 8 + Ext(_vectorSnd) - 1 + 4;                 // JSR VSND / JMP SND
        _sound.Run(t);
        t.Cycles += 5;
    }

    // ------------------------------------------------------------------ state callbacks
    // Each returns A = animation frame (offset into the image set) and B = pixels to step.

    /// <summary>REVR: turn around, then stand.</summary>
    public void Reverse(Tx t)
    {
        int face = t.U + _face;
        t.Write(face, Com8(ref t.CC, t.Read(face)));
        t.Cycles += 6 + Ix(_face);
        Stand(t);
    }

    /// <summary>STANDR: standing still.</summary>
    public void Stand(Tx t)
    {
        t.D = (ushort)(_stand << 8);
        t.CC = Logic16(t.CC, t.D);
        t.Write(t.U + _frame, 0);
        t.CC = Clr(t.CC);
        t.Cycles += 3 + 6 + Ix(_frame);
    }

    /// <summary>
    /// RUNR: next running frame. PFRAME counts 4,3,2,1; the step size per frame comes from the
    /// ORRUN table, and the "thump" frame (1) alternates the two footstep sounds.
    /// </summary>
    public void Run(Tx t)
    {
        int frameAddr = t.U + _frame;
        t.B = t.Read(frameAddr);
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4 + Ix(_frame) + 3;                          // LDB PFRAME,U / BLE
        if (Le(t.CC))
        {
            // RUNRST: starting to run from a stand
            t.B = 4;
            t.CC = Logic8(t.CC, 4);
            t.Write(frameAddr, 4);
            t.CC = Logic8(t.CC, 4);
            t.D = t.Read16(_runFrames);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 2 + 4 + Ix(_frame) + 5 + Ext(_runFrames);
            return;
        }
        t.B = Dec(ref t.CC, t.B);
        t.Cycles += 2 + 3;                                       // DECB / BGT
        if (!(!Eq(t.CC) && !Lt(t.CC)))
        {
            t.B = 4;                                             // wrap 1 -> 4
            t.CC = Logic8(t.CC, 4);
            t.Cycles += 2;
        }
        t.Write(frameAddr, t.B);
        t.CC = Logic8(t.CC, t.B);
        Sub8(ref t.CC, t.B, 1);
        t.Cycles += 4 + Ix(_frame) + 2 + 3;                      // STB / CMPB #1 / BNE
        if (t.B == 1)
        {
            // Footstep: alternate feet
            t.X = t.Read16(t.U + _decision);
            t.CC = Logic16(t.CC, t.X);
            int feetAddr = t.U + _feet;
            t.Write(feetAddr, Inc(ref t.CC, t.Read(feetAddr)));
            t.A = t.Read(feetAddr);
            t.CC = Logic8(t.CC, t.A);
            t.A = Ror8(ref t.CC, t.A);
            t.Cycles += 5 + Ix(_decision) + 6 + Ix(_feet) + 4 + Ix(_feet) + 2 + 3; // ... BCC
            int sound = Cs(t.CC) ? _sndRun1 : _sndRun2;
            t.X = t.Read16(t.X + sound);
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 5 + Ix(sound) + (Cs(t.CC) ? 3 : 0);      // LDX DSNRUx,X (+ BRA .2S)
            CallSound(t);
            t.B = t.Read(frameAddr);
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 4 + Ix(_frame);
        }
        // .1S: look up this frame's image and step
        t.B = Asl8(ref t.CC, t.B);
        t.X = (ushort)(_runFrames + t.B);                        // LDX #ORRUN / ABX
        t.D = t.Read16(t.X);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 3 + 3 + 5;
    }

    /// <summary>RUNSR: stop running; the STPRUN table gives the last step for each frame.</summary>
    public void StopRunning(Tx t)
    {
        t.X = _stopRun;
        t.CC = Logic16(t.CC, t.X);
        t.B = t.Read(t.U + _frame);
        t.CC = Logic8(t.CC, t.B);
        t.B = t.Read(t.X + (sbyte)t.B);
        t.CC = Logic8(t.CC, t.B);
        t.A = _stand;
        t.CC = Logic8(t.CC, t.A);
        t.Write(t.U + _frame, 0);
        t.CC = Clr(t.CC);
        t.Cycles += 3 + 4 + Ix(_frame) + 5 + 2 + 6 + Ix(_frame);
    }

    /// <summary>SKIDR: skidding (PFRAME = -1); the skid sound starts on the first frame.</summary>
    public void Skid(Tx t)
    {
        int frameAddr = t.U + _frame;
        t.A = t.Read(frameAddr);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_frame) + 3;                          // LDA PFRAME,U / BMI
        if (!Mi(t.CC))
        {
            t.X = t.Read16(t.U + _decision);
            t.CC = Logic16(t.CC, t.X);
            t.X = t.Read16(t.X + _sndSkid);
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 5 + Ix(_decision) + 5 + Ix(_sndSkid);
            CallSound(t);
        }
        t.A = 0xFF;
        t.CC = Logic8(t.CC, t.A);
        t.Write(frameAddr, 0xFF);
        t.CC = Logic8(t.CC, t.A);
        t.D = (ushort)((_skid << 8) | 2);
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 2 + 4 + Ix(_frame) + 3;
    }

    // ------------------------------------------------------------------ image & collision box

    /// <summary>
    /// SRCADG/SRCADH/SRCADA/SRCADR: set the collision box (taller on the ground) and pick the
    /// horse and rider images for frame PIMAGE (A, when storing) and facing. Running and flying
    /// riders reuse the standing rider; the skidding rider sits 2 pixels lower (PLANTZ).
    /// </summary>
    public void SelectImage(Tx t, bool storeImage, bool onGround)
    {
        if (storeImage)
        {
            t.Write(t.U + _image, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4 + Ix(_image);
        }
        t.B = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 4 + Ix(_posY + 1);
        if (onGround)
        {
            t.Write(t.U + _colY1, t.B);
            t.CC = Logic8(t.CC, t.B);
            t.B = Sub8(ref t.CC, t.B, 0x14 - 1);
            t.Write(t.U + _colY2, t.B);
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 4 + Ix(_colY1) + 2 + 4 + Ix(_colY2) + 3; // ... BRA SRCCOM
        }
        else
        {
            t.B = Sub8(ref t.CC, t.B, 6);
            t.Write(t.U + _colY1, t.B);
            t.CC = Logic8(t.CC, t.B);
            t.B = Sub8(ref t.CC, t.B, 0x14 - 1 - 6);
            t.Write(t.U + _colY2, t.B);
            t.CC = Logic8(t.CC, t.B);
            t.Cycles += 2 + 4 + Ix(_colY1) + 2 + 4 + Ix(_colY2);
        }

        // SRCCOM
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.D = Add16(ref t.CC, t.D, 18 - 1 - 1);
        t.Write16(t.U + _colX, t.D);                             // right edge of the box
        t.CC = Logic16(t.CC, t.D);
        t.Write(t.U + _lantz, 0);
        t.CC = Clr(t.CC);
        t.B = 0;
        t.CC = Clr(t.CC);
        t.A = t.Read(t.U + _face);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ix(_posX) + 4 + 5 + Ix(_colX) + 6 + Ix(_lantz) + 2 + 4 + Ix(_face) + 3;
        if (Mi(t.CC))
        {
            t.B = 6;                                             // left-facing images follow the right ones
            t.CC = Logic8(t.CC, 6);
            t.Cycles += 2;
        }
        t.B = Add8(ref t.CC, t.B, t.Read(t.U + _image), withCarry: false);
        t.X = t.Read16(t.U + _horse);
        t.CC = Logic16(t.CC, t.X);
        t.X = (ushort)(t.X + t.B);
        t.Write16(t.U + _picH, t.X);
        t.CC = Logic16(t.CC, t.X);
        t.X = t.Read16(t.U + _rider);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 4 + Ix(_image) + 5 + Ix(_horse) + 3 + 5 + Ix(_picH) + 5 + Ix(_rider) + 3;
        if (t.X == 0)
        {
            WriteRider(t);                                       // WPNORI: no rider
            return;
        }
        Sub8(ref t.CC, t.B, _stand);
        t.Cycles += 2 + 3;                                       // CMPB #STAND / BLO
        if (Cs(t.CC))
        {
            // SRCSKP: skidding frame
            t.X = (ushort)(t.X + t.B);
            t.Write16(t.U + _picR, t.X);
            t.CC = Logic16(t.CC, t.X);
            t.B = 2;
            t.CC = Logic8(t.CC, 2);
            t.Write(t.U + _lantz, 2);
            t.CC = Logic8(t.CC, 2);
            t.Cycles += 3 + 5 + Ix(_picR) + 2 + 4 + Ix(_lantz);
            return;
        }
        t.B = Sub8(ref t.CC, t.B, t.Read(t.U + _image));
        t.B = Add8(ref t.CC, t.B, _stand, withCarry: false);
        t.X = (ushort)(t.X + t.B);
        t.Cycles += 4 + Ix(_image) + 2 + 3;
        WriteRider(t);
    }

    private void WriteRider(Tx t)
    {
        t.Write16(t.U + _picR, t.X);
        t.CC = Logic16(t.CC, t.X);
        t.Cycles += 5 + Ix(_picR);
    }

    // ------------------------------------------------------------------ DMA queue

    /// <summary>
    /// CL1CLS/WR1CLS/CL2CLS/WR2CLS: append one or two 10-byte DMA blocks to this screen
    /// quadrant's erase or write list, and work out the screen byte address (CLSX, CLSY) and
    /// the blitter control (odd X positions use the shifted "flavoured" blit).
    /// Returns X = the first block.
    /// </summary>
    public void Allocate(Tx t, bool write, int blocks)
    {
        t.B = t.Read(t.U + _posY + 1);
        t.CC = Logic8(t.CC, t.B);
        t.Write(t.Var(_clsY), t.B);
        t.CC = Logic8(t.CC, t.B);
        t.B = (byte)(t.B & 0xC0);                                // quadrant
        t.CC = Logic8(t.CC, t.B);
        t.X = (ushort)(_dmaEnd + (write ? _write : _erase));
        t.CC = Logic16(t.CC, t.X);
        t.X = (ushort)(t.X + t.B);
        t.D = t.Read16(t.Var(_dmaFree));
        t.CC = Logic16(t.CC, t.D);
        t.Cycles += 4 + Ix(_posY + 1) + 4 + Ext(_clsY) + 2 + 3 + 3 + 5 + Ext(_dmaFree);
        for (int i = 0; i < blocks; i++)
        {
            t.Write16(t.Read16(t.X), t.D);                       // link the list's last block to this one
            t.CC = Logic16(t.CC, t.D);
            t.D = Add16(ref t.CC, t.D, 8);
            t.Write16(t.X, t.D);                                 // new end: this block's link field
            t.CC = Logic16(t.CC, t.D);
            t.D = Add16(ref t.CC, t.D, 2);
            t.Cycles += 8 + 4 + 5 + 4;
        }
        t.X = t.Read16(t.Var(_dmaFree));
        t.CC = Logic16(t.CC, t.X);
        t.Write16(t.Var(_dmaFree), t.D);
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _posX);
        t.CC = Logic16(t.CC, t.D);
        t.A = Ror8(ref t.CC, t.A);
        t.B = Ror8(ref t.CC, t.B);                               // byte column; carry = odd pixel
        t.Write(t.Var(_clsX), t.B);
        t.CC = Logic8(t.CC, t.B);
        bool odd = Cs(t.CC);
        t.A = write ? (byte)0x0A : (byte)0x1A;                   // write: zero suppress; erase: constant 0
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ext(_dmaFree) + 5 + Ext(_dmaFree) + 5 + Ix(_posX) + 2 + 2 + 4 + Ext(_clsX) + 2 + 3;
        if (odd)
        {
            t.A = (byte)(t.A | 0x20);                            // shift the image half a byte
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 2;
        }
        if (write)
        {
            t.Write(t.X, t.A);
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4;
            if (blocks == 2)
            {
                t.Write(t.X + 10, t.A);
                t.CC = Logic8(t.CC, t.A);
                t.Cycles += 5;
            }
        }
        else
        {
            t.B = 0;
            t.CC = Clr(t.CC);
            t.Write16(t.X, t.D);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 2 + 5;
            if (blocks == 2)
            {
                t.Write16(t.X + 10, t.D);
                t.CC = Logic16(t.CC, t.D);
                t.Cycles += 6;
            }
        }
    }

    /// <summary>
    /// WPLYR (write) / CPLYR (erase): queue the rider and horse images at the object's
    /// position. WPLYR works on the executing process (PEXEC).
    /// </summary>
    public void DrawObject(Tx t, bool write)
    {
        if (write)
        {
            t.U = t.Read16(t.Var(_exec));
            t.CC = Logic16(t.CC, t.U);
            t.Cycles += 5 + Ext(_exec);
        }
        t.Y = t.Read16(t.U + _picR);
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Ix(_picR) + 3;                           // LDY PPICR,U / BEQ
        if (t.Y != 0)
        {
            t.Cycles += 8 + 4;                                   // JSR VxR2CLS / JMP
            Allocate(t, write, blocks: 2);
            t.Cycles += 5;
            if (!write) t.Cycles += 3;                           // BRA WRRIDR
            QueueImage(t);                                       // WRRIDR: rider
            t.X = (ushort)(t.X + 10);
            t.CC = Set(t.CC, Z, t.X == 0);
            t.Y = t.Read16(t.U + _picH);
            t.CC = Logic16(t.CC, t.Y);
            t.Cycles += 5 + 6 + Ix(_picH);                       // LEAX 10,X / LDY PPICH,U
            QueueImage(t);                                       // WRHOR2: horse
            return;
        }
        t.Y = t.Read16(t.U + _picH);
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + Ix(_picH) + 3;                           // LDY PPICH,U / BEQ
        if (t.Y == 0) return;
        t.Cycles += 8 + 4;
        Allocate(t, write, blocks: 1);
        t.Cycles += 5 + 3;                                       // RTS / BRA WRHOR2
        QueueImage(t);
    }

    /// <summary>Fill a DMA block from an image descriptor: destination, size, source.</summary>
    public void QueueImage(Tx t)
    {
        t.D = t.Read16(t.Y + 2);                                 // image offset from the object's position
        t.CC = Logic16(t.CC, t.D);
        t.A = Add8(ref t.CC, t.A, t.Read(t.Var(_clsX)), withCarry: false);
        t.B = Add8(ref t.CC, t.B, t.Read(t.Var(_clsY)), withCarry: false);
        t.Write16(t.X + _wcDest, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Y = t.Read16(t.Y + 4);                                 // image data: size then pixels
        t.CC = Logic16(t.CC, t.Y);
        t.D = t.Read16(t.Y);
        t.CC = Logic16(t.CC, t.D);
        t.Y = (ushort)(t.Y + 2);
        t.Write16(t.X + _wcLen, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.X + _wcSrc, t.Y);
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 6 + 4 + Ext(_clsX) + 4 + Ext(_clsY) + 5 + Ix(_wcDest) + 7 + 8
                    + 5 + Ix(_wcLen) + 6 + Ix(_wcSrc);
    }
}
