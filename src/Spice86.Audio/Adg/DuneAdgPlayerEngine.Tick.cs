namespace Spice86.Audio.Adg;

using System;

/// <summary>
/// Tick processing, event dispatch, and all DNADG event handlers.
/// Faithfully ported from <c>AdgDriverCode.cs</c> (<c>AdgSchedulerTick_0756</c> and related handlers).
/// </summary>
public sealed partial class DuneAdgPlayerEngine {

    /// <summary>
    /// Runtime routing resolver bytes mirroring DNADG segment 564B at offset 0x08ED.
    /// The driver reads <c>AdgWord(0x08ED + shiftedBX)</c> and <c>AdgWord(0x08EF + shiftedBX)</c>
    /// where <c>shiftedBX = (~fadeScratch and 0x01C0) &gt;&gt; 4</c>. Valid shiftedBX values are
    /// 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, and 0x1C — never below 4.
    /// <c>ReadRoutingResolverWord(0, resolverIndex)</c> reads the AX word (channelRoute|primaryRoute);
    /// <c>ReadRoutingResolverWord(2, resolverIndex)</c> reads the BX_new word (stateMask).
    /// Indices 0–3 are unused padding so that resolverIndex directly offsets the array.
    /// Data captured from live dump at linear 0x56DA1 (segment 564B, offset 0x08F1).
    /// </summary>
    private static readonly byte[] RoutingResolverTable = {
        // Indices 0-3: unused padding
        0x00, 0x00, 0x00, 0x00,
        // BX=0x04: AX=0x0610 (channelRoute=0x06, primaryRoute=0x10), stateMask=0x0040
        0x10, 0x06, 0x40, 0x00,
        // BX=0x08: AX=0x0711 (channelRoute=0x07, primaryRoute=0x11), stateMask=0x0080
        0x11, 0x07, 0x80, 0x00,
        // BX=0x0C: AX=0x0711, stateMask=0x0080
        0x11, 0x07, 0x80, 0x00,
        // BX=0x10: AX=0x0812 (channelRoute=0x08, primaryRoute=0x12), stateMask=0x0100
        0x12, 0x08, 0x00, 0x01,
        // BX=0x14: AX=0x0812, stateMask=0x0100
        0x12, 0x08, 0x00, 0x01,
        // BX=0x18: AX=0x0812, stateMask=0x0100
        0x12, 0x08, 0x00, 0x01,
        // BX=0x1C: AX=0x0812, stateMask=0x0100 — all-bits-free startup case
        0x12, 0x08, 0x00, 0x01
    };

    /// <summary>
    /// Copies the fixed initial routing tables into the per-channel mutable arrays.
    /// Called once at playback start so routing matches the OPL3 Gold channel layout.
    /// </summary>
    private void InitializeRoutingTables() {
        for (int i = 0; i < ChannelCount; i++) {
            _channelRoutingTable[i]   = InitialChannelRoutes[i];
            _channelRouteShadow[i]    = InitialRouteShadows[i];
            _channelPrimaryRoute[i]   = InitialPrimaryRoutes[i];
            _channelSecondaryRoute[i] = InitialSecondaryRoutes[i];
        }
    }

    /// <summary>
    /// Builds the 18-channel runtime state table from the current song header.
    /// Mirrors <c>AdgBuildChannelTable_068A</c>.
    /// </summary>
    private void BuildChannelTable() {
        for (int i = 0; i < ChannelCount; i++) {
            ushort relative = SongWord(_dataBase + i * 2);
            _channelStartOffset[i] = relative == 0 ? (ushort)0 : (ushort)(relative + _dataBase);
            _channelInstrument[i]  = 0xFF;
            _channelNote[i]        = 0;
            _channelStateScratch[i]= 0;
        }

        _measure     = 1;
        _subdivision = 0x60;

        for (int ch = 0; ch < ChannelCount; ch++) {
            ushort ptr = _channelStartOffset[ch];
            _channelEventPointer[ch] = ptr;
            _channelWait[ch]         = 0xFFFF;
            if (ptr != 0) {
                ReadWaitValue(ch);
                _channelWait[ch] = (ushort)(_channelWait[ch] + 1);
            }
        }

        _fadeScratch  = 0;
        _fadeScratch2 = 0;
    }

    /// <summary>
    /// Main ADG scheduler tick. Advances tempo, checks the loop point, and iterates all 18 channels.
    /// Mirrors <c>AdgSchedulerTick_0756</c>.
    /// </summary>
    private void ProcessTick() {
        _tempoAccumulator = (ushort)(_tempoAccumulator + SongWord(_dataBase + 0x30));
        LoopPointCheck();

        for (int ch = 0; ch < ChannelCount; ch++) {
            _channelWait[ch] = (ushort)(_channelWait[ch] - 1);

            if (_channelWait[ch] != 0) {
                AdvancePitchModulation(ch);
                continue;
            }

            while (_channelWait[ch] == 0) {
                ushort eventPointer = _channelEventPointer[ch];
                if (eventPointer == 0) { break; }

                ushort eventWord = SongWord16(eventPointer);
                _channelEventPointer[ch] = (ushort)(eventPointer + 2);
                DispatchEvent(ch, eventWord);
            }
        }

        _subdivision = (byte)(_subdivision - 1);
        if (_subdivision == 0) {
            _subdivision = 0x60;
            _measure     = (ushort)(_measure + 1);
        }
    }

    /// <summary>
    /// Checks and manages ADG loop snapshot save/restore transitions.
    /// Mirrors <c>AdgCheckLoopPoint_07DA</c>.
    /// </summary>
    private void LoopPointCheck() {
        ushort loopStartMeasure  = SongWord(_dataBase + 0x2A);
        ushort loopEndMeasure    = SongWord(_dataBase + 0x2C);
        ushort loopRepeatCount   = SongWord(_dataBase + 0x2E);

        if (_loopCounter == 0) {
            if (loopStartMeasure == _measure && _subdivision == 0x60) {
                for (int i = 0; i < ChannelCount; i++) {
                    _snapshotWait[i]    = _channelWait[i];
                    _snapshotPointer[i] = _channelEventPointer[i];
                }
                _loopCounter = (byte)(loopRepeatCount - 1);
            }
        } else {
            if (loopEndMeasure == _measure) {
                _loopCounter--;
                for (int i = 0; i < ChannelCount; i++) {
                    _channelWait[i]         = _snapshotWait[i];
                    _channelEventPointer[i] = _snapshotPointer[i];
                }
                _measure = loopStartMeasure;
            }
        }
    }

    /// <summary>
    /// Dispatches an ADG event word to the appropriate handler.
    /// Mirrors <c>AdgDispatchObservedEvent_0756</c> (call word ptr DS:[BX+0x012E]).
    /// Handler index = (eventWord &amp; 0x0070) &gt;&gt; 4.
    /// </summary>
    private void DispatchEvent(int ch, ushort eventWord) {
        int handlerIndex = (eventWord & 0x0070) >> 4;
        switch (handlerIndex) {
            case 0: ReadWaitValue(ch);          break;   // 0x00: wait
            case 1: NoteOff(ch, eventWord);     break;   // 0x10: note off
            case 2: NoteOn(ch, eventWord);      break;   // 0x20: note on
            case 3: ProgramChange(ch, eventWord); break; // 0x30: program change
            case 4: VolumeModulation(ch, eventWord); break; // 0x40: volume modulation
            case 5: PitchBend(ch, eventWord);   break;   // 0x50: pitch bend
            case 6: EndOfTrack(ch);             break;   // 0x60: end of track
            default:                            break;   // 0x70: unused
        }
    }

    /// <summary>
    /// Decodes one ADG variable-length wait value from the event stream into channel state.
    /// Mirrors <c>AdgReadWaitValue_0E7E</c>.
    /// Value = (value &lt;&lt; 7) | (byte &amp; 0x7F) until sign bit clears.
    /// </summary>
    private void ReadWaitValue(int ch) {
        ushort ptr    = _channelEventPointer[ch];
        uint   value  = 0;
        bool overflow = false;

        while (true) {
            byte current = SongByte16(ptr);
            ptr++;
            value = (value << 7) | (uint)(current & 0x7F);
            if (value > ushort.MaxValue) { overflow = true; }
            if ((current & 0x80) == 0) { break; }
        }

        _channelWait[ch]         = overflow ? ushort.MaxValue : (ushort)value;
        _channelEventPointer[ch] = ptr;
    }

    /// <summary>
    /// Advances the per-channel pitch modulation counter and fires a pitch bend if due.
    /// Mirrors <c>AdgAdvancePitchModulation_07AD</c>.
    /// </summary>
    private void AdvancePitchModulation(int ch) {
        if (_channelPitchBendCounter[ch] == 0) { return; }

        _channelPitchBendCounter[ch] = (byte)(_channelPitchBendCounter[ch] - 1);
        if (_channelPitchBendCounter[ch] != 0) { return; }

        byte speed      = _channelPitchBendCounterInit[ch];
        byte accumulator = _channelPitchAccumulator[ch];
        PitchBendBody(ch, Make16(accumulator, speed));
    }

    /// <summary>
    /// Clears the per-channel state-scratch mask back into the global fade-scratch words.
    /// Mirrors <c>AdgClearScratchMask_0ACD</c>.
    /// </summary>
    private void ClearScratchMask(int ch) {
        if (_channelWait[ch] < 0x0030) { return; }
        ushort ptr = _channelEventPointer[ch];
        if (ptr == 0) { return; }
        byte nextByte = SongByte16(ptr);
        if (nextByte != 0xFF && (nextByte & 0xF0) != 0xC0) { return; }

        ushort scratch = _channelStateScratch[ch];
        _channelStateScratch[ch] = 0;
        ushort mask = (ushort)~scratch;
        if ((mask & 0x8000) == 0) {
            _fadeScratch2 = (ushort)(_fadeScratch2 & mask);
        } else {
            _fadeScratch = (ushort)(_fadeScratch & mask);
        }
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    /// <summary>
    /// NoteOn event handler. Reads velocity, sets up envelope, writes frequency with key-on.
    /// Mirrors <c>AdgNoteOn_0A82</c>.
    /// </summary>
    private void NoteOn(int ch, ushort eventWord) {
        ushort si = _channelEventPointer[ch];
        byte velocity = SongByte16(si);
        si = (ushort)(si + 1);
        _channelEventPointer[ch] = si;
        ReadWaitValue(ch);

        EnvelopeSetup(ch, velocity);

        if (_channelNote[ch] != 0) {
            NoteOffOpl(ch);
        }

        byte note = (byte)(Hi8(eventWord) + _channelPitchTranspose[ch]);
        _channelNote[ch]             = note;
        _channelPitchBendCounter[ch] = _channelPitchBendCounterInit[ch];
        _channelPitchAccumulator[ch] = 0x40;

        NoteOnOpl(ch, (ushort)(note - 0x48));
        ChannelEventDispatched?.Invoke(ch, "NoteOn",
            $"note={note:X2} vel={velocity:X2} inst={_channelInstrument[ch]:X2} tr={_channelPitchTranspose[ch]:X2}",
            _totalTickCount);
    }

    /// <summary>
    /// NoteOff event handler. Skips velocity byte, reads wait, clears note if matching.
    /// Mirrors <c>AdgNoteOff_0AB6</c>.
    /// </summary>
    private void NoteOff(int ch, ushort eventWord) {
        _channelEventPointer[ch] = (ushort)(_channelEventPointer[ch] + 1);
        ReadWaitValue(ch);

        byte note = (byte)(Hi8(eventWord) + _channelPitchTranspose[ch]);
        if (_channelNote[ch] != note) { return; }

        _channelNote[ch] = 0;
        ClearScratchMask(ch);
        NoteOffOpl(ch);
        ChannelEventDispatched?.Invoke(ch, "NoteOff",
            $"note={note:X2} inst={_channelInstrument[ch]:X2}",
            _totalTickCount);
    }

    // ── Routing resolver helpers ──────────────────────────────────────────────

    private static ushort ReadRoutingResolverWord(int baseOffset, int index) {
        int address = baseOffset + index;
        if ((uint)(address + 1) >= RoutingResolverTable.Length) { return 0; }
        return (ushort)(RoutingResolverTable[address] | (RoutingResolverTable[address + 1] << 8));
    }

    private static byte ReadPitchBendFraction(byte index) {
        int bounded = index % PitchBendFractions.Length;
        return PitchBendFractions[bounded];
    }

    private static ushort ReadFrequencyWord(byte semitone) {
        int bounded = semitone % FrequencyLookupTable.Length;
        return FrequencyLookupTable[bounded];
    }

    /// <summary>
    /// Resolves the complex DNADG routing fallback branch observed at 564B:09CD–0A7F.
    /// Direct control-flow port of runtime bytes from spice86dumpMemoryDump.bin.
    /// </summary>
#pragma warning disable S1541
    private static void ResolveComplexRoutingBranch(
        ref ushort bp, ref ushort cx, out ushort routePair, out ushort stateMask) {
        ushort ax;
        ushort bx = (ushort)~cx;

        byte bl = Lo8(bx);
        byte bh = bl;
        bh = (byte)(bh >> 1);
        bh = (byte)(bh >> 1);
        bh = (byte)(bh >> 1);
        bh = (byte)(bh ^ bl);
        bh = (byte)(bh & 0x07);
        if (bh != 0) { goto Label0A2E; }

        ax = bp;
        byte alHash = Lo8(ax);
        byte ahHash = alHash;
        ahHash = (byte)(ahHash >> 1);
        ahHash = (byte)(ahHash >> 1);
        ahHash = (byte)(ahHash >> 1);
        alHash = (byte)(alHash ^ ahHash);
        alHash = (byte)(alHash & 0x07);
        if (alHash != 0) {
            bx = (ushort)~bp;
            bool carry = (alHash & 0x01) != 0;
            alHash = (byte)(alHash >> 1);
            if (carry)  { goto Label0A01; }
            carry = (alHash & 0x01) != 0;
            alHash = (byte)(alHash >> 1);
            if (carry)  { goto Label0A0F; }
            goto Label0A1E;
        }

        bx = (ushort)(bx & 0x003F);
        if (bx != 0) { goto Label0A46; }

        bx = (ushort)~bp;
        if ((bx & 0x0024) != 0) { goto Label0A1E; }
        if ((bx & 0x0012) != 0) { goto Label0A0F; }

    Label0A01:
        ax = 0;
        bx = (ushort)(bx & 0x0001);
        if (bx != 0) { goto Label09A9; }
        ax = 0x0308;
        bx = Make16(0x08, Hi8(bx));
        goto Label09AB;

    Label0A0F:
        ax = 0x0101;
        bx = (ushort)(bx & 0x0002);
        if (bx != 0) { goto Label09A9; }
        ax = 0x0409;
        bx = Make16(0x10, Hi8(bx));
        goto Label09AB;

    Label0A1E:
        ax = 0x0202;
        bx = (ushort)(bx & 0x0004);
        if (bx == 0) {
            ax = 0x050A;
            bx = Make16(0x20, Hi8(bx));
        }
        goto Label09AB;

    Label0A2E:
        bh = Hi8(bx);
        bool carryBh = (bh & 0x01) != 0;
        bh = (byte)(bh >> 1);
        bx = Make16(Lo8(bx), bh);
        if (carryBh) { goto Label0A52; }
        carryBh = (bh & 0x01) != 0;
        bh = (byte)(bh >> 1);
        bx = Make16(Lo8(bx), bh);
        if (carryBh) { goto Label0A62; }
        goto Label0A72;

    Label0A46:
        if ((bx & 0x0024) != 0) { goto Label0A72; }
        if ((bx & 0x0012) != 0) { goto Label0A62; }
        goto Label0A52;

    Label0A52:
        ax = 0x8080;
        bx = (ushort)(bx & 0x0001);
        if (bx == 0) { ax = 0x8388; bx = Make16(0x08, Hi8(bx)); }
        goto Label0992;

    Label0A62:
        ax = 0x8181;
        bx = (ushort)(bx & 0x0002);
        if (bx == 0) { ax = 0x8489; bx = Make16(0x10, Hi8(bx)); }
        goto Label0992;

    Label0A72:
        ax = 0x8282;
        bx = (ushort)(bx & 0x0004);
        if (bx == 0) { ax = 0x858A; bx = Make16(0x20, Hi8(bx)); }
        goto Label0992;

    Label0992:
        cx = (ushort)(cx | bx);
        bx = Make16(Lo8(bx), (byte)(Hi8(bx) | 0x80));
        goto Label09AB;

    Label09A9:
        bp = (ushort)(bp | bx);

    Label09AB:
        routePair = ax;
        stateMask = bx;
    }
#pragma warning restore S1541

    /// <summary>
    /// Configures routing for the current instrument patch.
    /// Mirrors the tested branches of <c>AdgConfigureInstrumentRouting_090D</c>.
    /// </summary>
    private void ConfigureInstrumentRouting(int ch, ushort patchOffset) {
        ushort bp           = _fadeScratch;
        ushort cx           = _fadeScratch2;
        ushort stateScratch = _channelStateScratch[ch];
        ushort scratchMask  = (ushort)~stateScratch;

        if ((scratchMask & 0x8000) == 0) {
            cx = (ushort)(cx & scratchMask);
        } else {
            bp = (ushort)(bp & scratchMask);
        }

        byte patchType = SongByte16(patchOffset);
        if (patchType == 0x04) {
            _fadeScratch  = bp;
            _fadeScratch2 = cx;
            return;
        }

        ushort routePair;
        ushort stateMask;

        ushort freeFromPrimary = (ushort)(~bp & 0x01C0);
        if (freeFromPrimary != 0) {
            int resolverIndex = freeFromPrimary >> 4;
            routePair = ReadRoutingResolverWord(0, resolverIndex);
            stateMask = ReadRoutingResolverWord(2, resolverIndex);
            bp        = (ushort)(bp | stateMask);
        } else {
            ushort freeFromSecondary = (ushort)~cx;
            if ((freeFromSecondary & 0x01C0) == 0) {
                ResolveComplexRoutingBranch(ref bp, ref cx, out routePair, out stateMask);
            } else {
                int resolverIndex = (freeFromSecondary & 0x01C0) >> 4;
                routePair = ReadRoutingResolverWord(0, resolverIndex);
                stateMask = ReadRoutingResolverWord(2, resolverIndex);
                routePair = (ushort)(routePair | 0x8080);
                cx        = (ushort)(cx | stateMask);
                stateMask = Make16(Lo8(stateMask), (byte)(Hi8(stateMask) | 0x80));
            }
        }

        if (routePair == 0 || stateMask == 0) {
            _fadeScratch  = bp;
            _fadeScratch2 = cx;
            return;
        }

        _channelStateScratch[ch]   = stateMask;
        _fadeScratch               = bp;
        _fadeScratch2              = cx;
        _channelRoutingTable[ch]   = Hi8(routePair);
        _channelPrimaryRoute[ch]   = Lo8(routePair);

        routePair = (ushort)(routePair + 0x0303);
        _channelRouteShadow[ch]    = Hi8(routePair);
        _channelSecondaryRoute[ch] = Lo8(routePair);
    }

    // ── Remaining event handlers ─────────────────────────────────────────────

    /// <summary>
    /// ProgramChange event handler. Loads instrument patch and writes to OPL3 Gold.
    /// Mirrors <c>AdgProgramChange_0831</c>.
    /// </summary>
    private void ProgramChange(int ch, ushort eventWord) {
        ReadWaitValue(ch);

        byte instrument = Hi8(eventWord);
        _channelInstrument[ch] = instrument;

        ushort patchOffset = (ushort)(_eventBase + instrument * 0x28);
        ConfigureInstrumentRouting(ch, patchOffset);

        _channelPitchMode[ch]      = SongWord16((ushort)(patchOffset + 0x21));
        _channelPitchTranspose[ch] = Hi8(_channelPitchMode[ch]);

        ushort ax = Make16(SongByte16((ushort)(patchOffset + 0x0A)), SongByte16((ushort)(patchOffset + 0x17)));
        ushort bx = Make16(SongByte16((ushort)(patchOffset + 0x0F)), SongByte16((ushort)(patchOffset + 0x02)));
        bx = (ushort)(bx & 0x0303);
        bx = RotateRight16(bx, 2);
        ax = (ushort)(ax | bx);
        _channelEnvShaping[ch] = ax;

        _channelTlShaping[ch]      = SongWord16((ushort)(patchOffset + 0x1E));
        _channelVolModShaping[ch]  = SongWord16((ushort)(patchOffset + 0x26));

        ax = Make16((byte)~SongByte16((ushort)(patchOffset + 0x0E)), SongByte16((ushort)(patchOffset + 0x04)));
        ax = RotateRight16(ax, 1);
        ax = (ushort)(ax << 1);
        ax = Make16(SongByte16((ushort)(patchOffset + 0x20)), Hi8(ax));
        _channelConnShaping[ch] = ax;

        ax = Make16(SongByte16((ushort)(patchOffset + 0x1B)), Hi8(ax));
        _channelConnModulation[ch] = ax;

        ax = SongWord16((ushort)(patchOffset + 0x23));
        _channelPitchBendCounterInit[ch] = Hi8(ax);
        _channelPitchBendCounter[ch]     = 0;

        byte patchType = SongByte16(patchOffset);
        _channelPatchType[ch] = patchType;

        WriteInstrumentPatch(patchOffset, ch);
        ChannelEventDispatched?.Invoke(ch, "PgmChg",
            $"inst={instrument:X2}",
            _totalTickCount);
    }

    /// <summary>
    /// VolumeModulation event handler. Applies velocity-based modulation to operator TL registers.
    /// Mirrors <c>AdgVolumeModulation_0B2E</c>.
    /// </summary>
    private void VolumeModulation(int ch, ushort eventWord) {
        ReadWaitValue(ch);

        byte directVelocity  = Hi8(eventWord);
        byte inverseVelocity = (byte)(0x80 - directVelocity);
        ushort operatorLevel = _channelCurrentOperatorLevel[ch];
        ushort volumeShape   = _channelVolModShaping[ch];

        if (Lo8(volumeShape) != 0) {
            byte shaping = Lo8(volumeShape);
            byte scale   = directVelocity;
            if ((shaping & 0x80) != 0) {
                shaping = (byte)(0 - shaping);
                scale   = inverseVelocity;
            }
            shaping = (byte)(0 - (byte)(shaping - 4));
            scale   = (byte)(scale >> shaping);
            byte value = (byte)(Lo8(operatorLevel) & 0x3F);
            value = value >= scale ? (byte)(value - scale) : (byte)0;
            value = (byte)((Lo8(operatorLevel) & 0xC0) | value);
            operatorLevel = Make16(value, Hi8(operatorLevel));
            WriteRelativeGoldRegister(0x40, value, unchecked((sbyte)_channelPrimaryRoute[ch]));
        }

        if (Hi8(volumeShape) != 0) {
            byte shaping = Hi8(volumeShape);
            byte scale   = directVelocity;
            if ((shaping & 0x80) != 0) {
                shaping = (byte)(0 - shaping);
                scale   = inverseVelocity;
            }
            byte shift = (byte)(4 - shaping);
            scale = (byte)(scale >> shift);
            byte value = (byte)(Hi8(operatorLevel) & 0x3F);
            value = value >= scale ? (byte)(value - scale) : (byte)0;
            value = (byte)((Hi8(operatorLevel) & 0xC0) | value);
            operatorLevel = Make16(Lo8(operatorLevel), value);
            WriteRelativeGoldRegister(0x40, value, unchecked((sbyte)_channelSecondaryRoute[ch]));
        }

        _channelCurrentOperatorLevel[ch] = operatorLevel;

        ushort connectionModulation = _channelConnModulation[ch];
        if (Lo8(connectionModulation) == 0) { return; }

        byte shapingMode     = Lo8(connectionModulation);
        byte scaleConnection = directVelocity;
        if ((shapingMode & 0x80) != 0) {
            shapingMode     = (byte)(0 - shapingMode);
            scaleConnection = inverseVelocity;
        }
        shapingMode     = (byte)(0 - (byte)(shapingMode - 6));
        scaleConnection = (byte)(scaleConnection >> shapingMode);
        scaleConnection = (byte)(scaleConnection & 0xFE);
        scaleConnection = (byte)(scaleConnection + Hi8(connectionModulation));
        if (scaleConnection > 0x0F) {
            scaleConnection = (byte)((scaleConnection & 0x0F) | 0x0E);
        }
        scaleConnection = (byte)((scaleConnection & 0x0F) | (Hi8(connectionModulation) & 0x30));
        _channelConnectionCurrent[ch] = scaleConnection;
        WriteRelativeGoldRegister(0xC0, scaleConnection, unchecked((sbyte)_channelRoutingTable[ch]));
    }

    /// <summary>
    /// Envelope setup on note-on. Scales TL values by velocity and writes to OPL3 Gold.
    /// Mirrors <c>AdgEnvelopeSetup_0C47</c>.
    /// </summary>
    private void EnvelopeSetup(int ch, byte velocity) {
        byte directVelocity  = velocity;
        byte inverseVelocity = (byte)(0x80 - velocity);
        ushort operatorLevel = _channelCurrentOperatorLevel[ch];
        ushort tlShaping     = _channelTlShaping[ch];

        if (Lo8(tlShaping) != 0) {
            byte shaping = Lo8(tlShaping);
            byte scale   = inverseVelocity;
            if ((shaping & 0x80) != 0) {
                shaping = (byte)(0 - shaping);
                scale   = directVelocity;
            }
            shaping = (byte)(0 - (byte)(shaping - 4));
            scale   = (byte)(scale >> shaping);
            byte value = (byte)((Lo8(operatorLevel) & 0x3F) + scale);
            if (value > 0x3F) { value = 0x3F; }
            value = (byte)((Lo8(operatorLevel) & 0xC0) | value);
            operatorLevel = Make16(value, Hi8(operatorLevel));
            WriteRelativeGoldRegister(0x40, value, unchecked((sbyte)_channelPrimaryRoute[ch]));
        }

        if (Hi8(tlShaping) != 0) {
            byte shaping = Hi8(tlShaping);
            byte scale   = inverseVelocity;
            if ((shaping & 0x80) != 0) {
                shaping = (byte)(0 - shaping);
                scale   = directVelocity;
            }
            byte shift = (byte)(4 - shaping);
            scale = (byte)(scale >> shift);
            byte value = (byte)((Hi8(operatorLevel) & 0x3F) + scale);
            if (value > 0x3F) { value = 0x3F; }
            value = (byte)((Hi8(operatorLevel) & 0xC0) | value);
            operatorLevel = Make16(Lo8(operatorLevel), value);
            WriteRelativeGoldRegister(0x40, value, unchecked((sbyte)_channelSecondaryRoute[ch]));
        }

        _channelCurrentOperatorLevel[ch] = operatorLevel;

        ushort connectionShape = _channelConnShaping[ch];
        if (Lo8(connectionShape) == 0) {
            _channelConnectionCurrent[ch] = Hi8(connectionShape);
            return;
        }

        byte connectionScaleMode = Lo8(connectionShape);
        byte connectionScale     = inverseVelocity;
        if ((connectionScaleMode & 0x80) != 0) {
            connectionScaleMode = (byte)(0 - connectionScaleMode);
            connectionScale     = directVelocity;
        }
        connectionScaleMode = (byte)(0 - (byte)(connectionScaleMode - 6));
        connectionScale     = (byte)(connectionScale >> connectionScaleMode);
        connectionScale     = (byte)(connectionScale & 0xFE);
        connectionScale     = (byte)(connectionScale + Hi8(connectionShape));
        if (connectionScale > 0x0F) {
            connectionScale = (byte)((connectionScale & 0x0F) | 0x0E);
        }
        connectionScale = (byte)((connectionScale & 0x0F) | (Hi8(connectionShape) & 0x30));
        _channelConnectionCurrent[ch] = connectionScale;
        WriteRelativeGoldRegister(0xC0, connectionScale, unchecked((sbyte)_channelRoutingTable[ch]));
    }

    /// <summary>
    /// PitchBend event handler. Reads bend value and applies pitch modulation.
    /// Mirrors <c>AdgPitchBend_0D86</c>.
    /// </summary>
    private void PitchBend(int ch, ushort eventWord) {
        byte bendValue = Hi8(eventWord);
        ReadWaitValue(ch);
        PitchBendBody(ch, Make16(bendValue, bendValue));
        ChannelEventDispatched?.Invoke(ch, "PBend", $"val={bendValue:X2}", _totalTickCount);
    }

    /// <summary>
    /// Pitch bend computation body. Handles both portamento and non-portamento modes.
    /// Mirrors <c>AdgPitchBendBody_0D8B</c>.
    /// </summary>
    private void PitchBendBody(int ch, ushort input) {
        byte note = _channelNote[ch];
        if (note == 0) { return; }

        ushort ax    = Make16(Lo8(input), 0);
        byte rawNote = note;
        byte quotient  = (byte)((rawNote - 0x18) / 12);
        byte remainder = (byte)((rawNote - 0x18) % 12);
        byte octave    = quotient;
        byte semitone  = remainder;

        if (Lo8(_channelPitchMode[ch]) == 0) {
            // Non-portamento bend
            bool negative = ax < 0x0040;
            ax = (ushort)(ax - 0x0040);
            if (negative) {
                ax    = (ushort)(0 - ax);
                ax    = RotateRight16(ax, 5);
                byte delta = Lo8(ax);
                if (semitone >= delta) {
                    semitone = (byte)(semitone - delta);
                } else {
                    semitone = (byte)(semitone + 12 - delta);
                    octave   = (byte)(octave - 1);
                    if ((octave & 0x80) != 0) { octave = 0; semitone = 0; }
                }
                byte fraction   = ReadPitchBendFraction(semitone);
                ushort mul      = (ushort)(fraction * Hi8(ax));
                byte adjustment = Hi8(mul);
                ushort frequency = ReadFrequencyWord(semitone);
                int result = Lo8(frequency) - adjustment;
                ax = Make16((byte)result, (byte)(Hi8(frequency) - (result < 0 ? 1 : 0)));
            } else {
                ax    = (ushort)(ax + 1);
                ax    = RotateRight16(ax, 5);
                byte delta = Lo8(ax);
                semitone = (byte)(semitone + delta);
                if (semitone >= 12) { semitone = (byte)(semitone - 12); octave = (byte)(octave + 1); }
                byte fraction   = ReadPitchBendFraction((byte)(semitone + 1));
                ushort mul      = (ushort)(fraction * Hi8(ax));
                byte adjustment = Hi8(mul);
                ushort frequency = ReadFrequencyWord(semitone);
                int result = Lo8(frequency) + adjustment;
                ax = Make16((byte)result, (byte)(Hi8(frequency) + (result > 0xFF ? 1 : 0)));
            }
        } else {
            // Portamento bend
            bool negative = ax < 0x0040;
            ax = (ushort)(ax - 0x0040);
            if (negative) {
                ax = (ushort)(0 - ax);
                byte delta        = (byte)(ax / 5);
                byte remainderPort = (byte)(ax % 5);
                if (semitone >= delta) {
                    semitone = (byte)(semitone - delta);
                } else {
                    semitone = (byte)(semitone + 12 - delta);
                    octave   = (byte)(octave - 1);
                    if ((octave & 0x80) != 0) { octave = 0; semitone = 0; }
                }
                int tableBase     = semitone >= 6 ? 5 : 0;
                byte adjustment   = PortamentoFractions[tableBase + remainderPort];
                ushort frequency  = ReadFrequencyWord(semitone);
                int result = Lo8(frequency) - adjustment;
                ax = Make16((byte)result, (byte)(Hi8(frequency) - (result < 0 ? 1 : 0)));
            } else {
                byte delta        = (byte)(ax / 5);
                byte remainderPort = (byte)(ax % 5);
                semitone = (byte)(semitone + delta);
                if (semitone >= 12) { semitone = (byte)(semitone - 12); octave = (byte)(octave + 1); }
                int tableBase     = semitone >= 6 ? 5 : 0;
                byte adjustment   = PortamentoFractions[tableBase + remainderPort];
                ushort frequency  = ReadFrequencyWord(semitone);
                int result = Lo8(frequency) + adjustment;
                ax = Make16((byte)result, (byte)(Hi8(frequency) + (result > 0xFF ? 1 : 0)));
            }
        }

        byte blockBits = (byte)(octave << 2);
        ax = Make16(Lo8(ax), (byte)(Hi8(ax) | blockBits));
        _channelFrequencyWord[ch] = ax;
        ax = Make16(Lo8(ax), (byte)(Hi8(ax) | 0x20));
        WriteFrequencyWord(ch, ax);
    }

    /// <summary>
    /// EndOfTrack event handler. Handles multi-track sequencing and song termination.
    /// Mirrors <c>AdgEndOfTrack_0AF5</c>.
    /// </summary>
    private void EndOfTrack(int ch) {
        _channelWait[ch] = 0xFFFF;
        byte pointerLo = Lo8(_channelEventPointer[ch]);
        _channelEventPointer[ch] = Make16((byte)(pointerLo - 2), Hi8(_channelEventPointer[ch]));

        if (ch != 0) {
            ClearScratchMask(ch);
            return;
        }

        _tickEnabled = (byte)(_tickEnabled - 1);
        if (_tickEnabled == 0) {
            for (int i = 0; i < ChannelCount; i++) {
                _channelWait[i] = 0xFFFF;
            }
            SilenceAllChannels();
            _statusFlags = 0;
            SongFinished?.Invoke();
            return;
        }

        if ((_tickEnabled & 0x80) != 0) {
            _tickEnabled = (byte)(_tickEnabled + 1);
        }

        // Rebuild scheduler state for loop
        _measure     = 1;
        _subdivision = 0x60;
        _fadeScratch  = 0;
        _fadeScratch2 = 0;
        for (int i = 0; i < ChannelCount; i++) {
            ushort ptr = _channelStartOffset[i];
            _channelEventPointer[i]  = ptr;
            _channelWait[i]          = 0xFFFF;
            _channelStateScratch[i]  = 0;
            if (ptr != 0) {
                ReadWaitValue(i);
                _channelWait[i] = (ushort)(_channelWait[i] + 1);
            }
        }

        LoopPointCheck();
        _channelWait[0] = (ushort)(_channelWait[0] - 1);
    }
}
