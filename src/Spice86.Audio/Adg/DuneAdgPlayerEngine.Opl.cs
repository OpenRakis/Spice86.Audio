namespace Spice86.Audio.Adg;

/// <summary>
/// OPL3 Gold register write operations: routing dispatch, note on/off, instrument loading.
/// </summary>
/// <remarks>
/// Routing convention: route &gt;= 0 → primary chip (A0+route);
/// route &lt; 0 → secondary chip ((A0+(byte)route)^0x80, OPL3 reg 0x100+).
/// Faithfully ported from <c>AdgDriverCode.cs</c> OPL-related functions.
/// </remarks>
public sealed partial class DuneAdgPlayerEngine {

    /// <summary>
    /// Core ADG OPL3 Gold routed register write.
    /// Mirrors <c>AdgWriteRelativeGoldRegister</c>: adds route to registerBase,
    /// selects primary or secondary OPL3 chip based on sign of route.
    /// route &gt;= 0 → primary chip; route &lt; 0 → secondary chip (reg | 0x100).
    /// </summary>
    private void WriteRelativeGoldRegister(byte registerBase, byte value, sbyte route) {
        byte routedRegister = (byte)(registerBase + (byte)route);
        ushort oplRegister;
        if (route < 0) {
            routedRegister = (byte)(routedRegister ^ 0x80);
            oplRegister    = (ushort)(0x100 | routedRegister);
        } else {
            oplRegister = routedRegister;
        }
        RegisterWriteRequested?.Invoke(oplRegister, value);
    }

    /// <summary>
    /// Writes a channel-specific OPL3 Gold register using the channel routing table.
    /// Mirrors <c>AdgWriteChannelRegister_10ED</c>.
    /// </summary>
    private void WriteChannelRegister(byte registerBase, byte value, int channelIndex) {
        WriteRelativeGoldRegister(registerBase, value,
            unchecked((sbyte)_channelRoutingTable[channelIndex]));
    }

    /// <summary>
    /// Writes the frequency low byte (A0+route) and high byte (B0+route) for a channel.
    /// Mirrors <c>AdgWriteFrequencyWord_10E0</c>.
    /// </summary>
    private void WriteFrequencyWord(int channelIndex, ushort frequencyWord) {
        WriteChannelRegister(0xA0, Lo8(frequencyWord), channelIndex);
        WriteChannelRegister(0xB0, Hi8(frequencyWord), channelIndex);
    }

    /// <summary>
    /// Note on: converts raw pitch to octave/semitone, looks up OPL3 frequency,
    /// stores it, and writes with key-on (bit 5 of B0 register set).
    /// Mirrors <c>AdgNoteOn_10A9</c>.
    /// </summary>
    private void NoteOnOpl(int channelIndex, ushort rawPitch) {
        ushort noteWord = (ushort)(rawPitch + 0x30);
        if (noteWord >= 0x60) { noteWord = 0; }

        byte octave    = (byte)(noteWord / 12);
        byte semitone  = (byte)(noteWord % 12);
        ushort freqWord = FrequencyLookupTable[semitone];
        freqWord = Make16(Lo8(freqWord), (byte)(Hi8(freqWord) | (octave << 2)));
        _channelFrequencyWord[channelIndex] = freqWord;
        freqWord = Make16(Lo8(freqWord), (byte)(Hi8(freqWord) | 0x20));
        WriteFrequencyWord(channelIndex, freqWord);
    }

    /// <summary>
    /// Note off: writes the stored frequency word without the key-on bit.
    /// Mirrors <c>AdgNoteOff_10D8</c>.
    /// </summary>
    private void NoteOffOpl(int channelIndex) {
        WriteFrequencyWord(channelIndex, _channelFrequencyWord[channelIndex]);
    }

    /// <summary>
    /// Writes one OPL3 Gold operator's registers from instrument patch data.
    /// Mirrors <c>AdgWriteInstrumentOperator_102C</c>.
    /// <paramref name="patchOffset"/> points to the operator block within the 0x28-byte patch.
    /// <paramref name="waveform"/> selects the OPL3 waveform (low 3 bits) for the E0 register.
    /// Returns the TL byte written so callers can seed channel state for <see cref="EnvelopeSetup"/>.
    /// </summary>
    private byte WriteInstrumentOperator(byte routeByte, ushort patchOffset, byte waveform) {
        sbyte route = unchecked((sbyte)routeByte);

        // E0+route: waveform (low 3 bits)
        WriteRelativeGoldRegister(0xE0, (byte)(waveform & 0x07), route);

        // 40+route: TL/KSL — rotate-right-16 by 2 from bytes [0x02](KSL) and [0x0A](TL)
        byte tlValue = (byte)((Make16(SongByte16((ushort)(patchOffset + 0x02)),
                                      (byte)(SongByte16((ushort)(patchOffset + 0x0A)) << 2)) >> 2) & 0xFF);
        WriteRelativeGoldRegister(0x40, tlValue, route);

        // 60+route: attack nibble from [0x08], decay byte from [0x05] — packed
        ushort attackDecay = (ushort)(Make16((byte)(SongByte16((ushort)(patchOffset + 0x08)) << 4),
                                              SongByte16((ushort)(patchOffset + 0x05))) << 4);
        WriteRelativeGoldRegister(0x60, Hi8(attackDecay), route);

        // 80+route: sustain nibble from [0x09], release byte from [0x06]
        ushort sustainRelease = (ushort)(Make16((byte)(SongByte16((ushort)(patchOffset + 0x09)) << 4),
                                                 SongByte16((ushort)(patchOffset + 0x06))) << 4);
        WriteRelativeGoldRegister(0x80, Hi8(sustainRelease), route);

        // 20+route: AM/VIB/EG/KSR/MULT packed flags
        ushort opFlags = 0;
        opFlags = RotateRight16(Make16(SongByte16((ushort)(patchOffset + 0x0B)), Hi8(opFlags)), 1);
        opFlags = RotateRight16(Make16(SongByte16((ushort)(patchOffset + 0x05)), Hi8(opFlags)), 1);
        opFlags = RotateRight16(Make16(SongByte16((ushort)(patchOffset + 0x0A)), Hi8(opFlags)), 1);
        opFlags = RotateRight16(Make16(SongByte16((ushort)(patchOffset + 0x09)), Hi8(opFlags)), 1);
        opFlags = Make16(SongByte16((ushort)(patchOffset + 0x01)), Hi8(opFlags));
        opFlags = (ushort)(opFlags & 0xF00F);
        WriteRelativeGoldRegister(0x20, (byte)(Hi8(opFlags) | Lo8(opFlags)), route);

        return tlValue;
    }

    /// <summary>
    /// Writes a full instrument patch (0x28 bytes) to OPL3 Gold for a channel.
    /// Mirrors <c>AdgWriteInstrumentPatch_0F95</c>.
    /// Uses channel routing table for the connection register;
    /// primary/secondary routes for the two operators.
    /// </summary>
    private void WriteInstrumentPatch(ushort patchOffset, int channelIndex) {
        byte channelRoute   = _channelRoutingTable[channelIndex];
        byte primaryRoute   = _channelPrimaryRoute[channelIndex];
        byte secondaryRoute = _channelSecondaryRoute[channelIndex];

        // C0+channelRoute: connection / feedback
        ushort connValue = Make16(SongByte16((ushort)(patchOffset + 0x0F)),
                                  SongByte16((ushort)(patchOffset + 0x1A)));
        connValue = (ushort)(connValue >> 1);
        connValue = Make16((byte)~Lo8(connValue), SongByte16((ushort)(patchOffset + 0x04)));
        connValue = (ushort)(connValue << 1);
        byte connectionByte = (byte)(Hi8(connValue) & 0x0F);
        _channelConnectionCurrent[channelIndex] = connectionByte;
        WriteRelativeGoldRegister(0xC0, connectionByte, unchecked((sbyte)channelRoute));

        // Modulator (primary route)
        WriteInstrumentOperator(primaryRoute, patchOffset,
            SongByte16((ushort)(patchOffset + 0x1C)));

        // Carrier (secondary route, patch offset + 0x0D)
        WriteInstrumentOperator(secondaryRoute, (ushort)(patchOffset + 0x0D),
            SongByte16((ushort)(patchOffset + 0x1D)));

        if ((secondaryRoute & 0x10) != 0) { return; }

        byte surroundIndex = (byte)(secondaryRoute & 0x03);
        if (unchecked((sbyte)secondaryRoute) < 0) {
            surroundIndex = (byte)(surroundIndex + 3);
        }

        if (SongByte16(patchOffset) == 0x04) { return; }

        byte surroundMask = (byte)~(1 << surroundIndex);
        _surroundMask = (byte)(_surroundMask & surroundMask);
        // Secondary OPL register 0x04 carries the surround channel mask
        RegisterWriteRequested?.Invoke(0x104, _surroundMask);
    }

    /// <summary>
    /// Initialises OPL3 Gold chip registers.
    /// Mirrors the init sequence from <c>AdgInitializeGoldHardware</c> equivalents.
    /// </summary>
    private void InitOplChip() {
        // Primary chip: waveform select enable (reg 0x01, bit 5)
        RegisterWriteRequested?.Invoke(0x01, 0x20);
        // Primary chip: rhythm mode off
        RegisterWriteRequested?.Invoke(0xBD, 0x00);
        // Primary chip: CSM/keyboard split off
        RegisterWriteRequested?.Invoke(0x08, 0x00);

        // Secondary chip: OPL3 mode enable (reg 0x105)
        RegisterWriteRequested?.Invoke(0x105, 0x01);
        // Secondary chip: waveform select enable
        RegisterWriteRequested?.Invoke(0x101, 0x20);
        // Secondary chip: rhythm mode off
        RegisterWriteRequested?.Invoke(0x1BD, 0x00);
    }

    /// <summary>
    /// Silences all 18 OPL3 Gold channels by writing note-off frequency words.
    /// Mirrors <c>AdgResetInternal_0EBA</c>: iterates channels 17..0 skipping percussion slots.
    /// </summary>
    private void SilenceAllChannels() {
        for (int ch = ChannelCount - 1; ch >= 0; ch--) {
            if (ch == 6 || ch == 7 || ch == 14 || ch == 15) { continue; }
            NoteOffOpl(ch);
        }
    }

    /// <summary>
    /// Replays DNADG's Gold surround initialisation from the current song table.
    /// Mirrors <c>AdgUpdateGoldSurround_11C4</c>.
    /// Writes the AdLib Gold surround control register (0x18, secondary bank) using
    /// the serial-shift protocol. No-ops if <see cref="RegisterWriteRequested"/> is not connected.
    /// </summary>
    private void UpdateGoldSurround() {
        ushort surroundPointer = _eventBase;
        byte registerValue = 0;

        for (byte channelIndex = 0; channelIndex < 0x1F; channelIndex++) {
            registerValue = WriteGoldSurroundMask(channelIndex, registerValue);
            // Write with bit 2 set (clock pulse)
            RegisterWriteRequested?.Invoke(0x118, (byte)(registerValue | 0x04));

            byte mask = SongByte16(surroundPointer);
            surroundPointer = (ushort)(surroundPointer + 1);
            registerValue = WriteGoldSurroundMask(mask, registerValue);
            // Write with bit 2 clear
            RegisterWriteRequested?.Invoke(0x118, (byte)(registerValue & 0xFB));
        }
    }

    /// <summary>
    /// Serialises one AdLib Gold surround mask/control value using DNADG's 11F4 sequence.
    /// </summary>
    private byte WriteGoldSurroundMask(byte originalValue, byte registerValue) {
        for (int index = 0; index < 8; index++) {
            registerValue = (byte)(registerValue & 0xFD);
            RegisterWriteRequested?.Invoke(0x118, registerValue);

            byte channelMask = (byte)((originalValue << 1) & 0xFE);
            registerValue = (byte)((registerValue & 0xFE) | channelMask);
            RegisterWriteRequested?.Invoke(0x118, registerValue);

            registerValue = (byte)(registerValue | 0x02);
            RegisterWriteRequested?.Invoke(0x118, registerValue);
        }
        return registerValue;
    }

    /// <summary>
    /// FadeStep: approaches target volume one nibble at a time.
    /// Mirrors <c>AdgFadeStep_0ECC</c>.
    /// </summary>
    private void FadeStep() {
        byte current = _currentVolume;
        byte target  = _targetVolume;

        if (current == target) {
            _fadeBitPattern = 0x0001;
            _statusFlags    = (byte)(_statusFlags & 0xBF);
            return;
        }

        byte updated = current;

        byte currentLow = (byte)(updated & 0x0F);
        byte targetLow  = (byte)(target  & 0x0F);
        if (currentLow != targetLow) {
            updated = (byte)(updated + 1);
            if (currentLow > targetLow) { updated = (byte)(updated - 2); }
        }

        byte currentHigh = (byte)(updated & 0xF0);
        byte targetHigh  = (byte)(target  & 0xF0);
        if (currentHigh != targetHigh) {
            updated = (byte)(updated + 0x10);
            if (currentHigh > targetHigh) { updated = (byte)(updated - 0x20); }
        }

        _currentVolume = updated;
        ApplyVolume(updated);

        if (updated == 0) {
            SilenceAllChannels();
            _statusFlags = 0;
        }
    }
}
